using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>Pod'un Monitor'e gönderdiği bildirim (her PollSeconds'ta bir; test yapıldıysa sonuçlarla).</summary>
    public sealed class AgentReport
    {
        /// <summary>Uygulama adı: Monitor bu anahtarı ilk kez görüyorsa uygulamayı bu adla kaydeder.</summary>
        public string AppName { get; set; } = "";
        /// <summary>Bildirimi gönderen pod'un kimliği ve uygulamanın sürüm bilgisi.</summary>
        public PodIdentity Pod { get; set; } = new PodIdentity();
        /// <summary>Pod'un bildirim aralığı (sn); Monitor pod'un canlılığını buna göre değerlendirir.</summary>
        public int PollSeconds { get; set; }
        /// <summary>Son test turunun sonuçları; bu bildirimde test sonucu yoksa null.</summary>
        public AgentRun? Run { get; set; }
    }

    /// <summary>Bir pod'un bir test turu.</summary>
    public sealed class AgentRun
    {
        public DateTime StartedAtUtc { get; set; }
        public long DurationMs { get; set; }
        public List<AgentResult> Results { get; set; } = new List<AgentResult>();
    }

    /// <summary>Bir bağlantının bu pod'daki TCP (telnet) test sonucu.</summary>
    public sealed class AgentResult
    {
        public string ConnectionId { get; set; } = "";
        /// <summary>Telnet raporu: ismin çözüldüğü her IP ve isim üzerinden yapılan bağlantının sonucu.</summary>
        public ProbeReport? Tcp { get; set; }
        /// <summary>Test yapılamadıysa nedeni (ör. tanımdaki host geçersiz).</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// ConnectivityProbe agent'ı: uygulamanın her pod'unda arka planda çalışır.
    /// <list type="number">
    /// <item>Başlarken ve her <see cref="ConnectivityProbeOptions.PollSeconds"/> saniyede Monitor'e bildirim gönderir. Monitor bu
    /// anahtarı ilk kez görüyorsa uygulamayı kendiliğinden kaydeder. Yanıtta uygulamaya atanmış bağlantılar ve test aralığı gelir.</item>
    /// <item>Test zamanı geldiğinde (veya Monitor'de "Şimdi test et"e basıldığında) her bağlantıyı bu pod'un içinden test eder
    /// (TCP / telnet) ve sonuçları hemen gönderir.</item>
    /// <item>Süreç kapanırken Monitor'e "kapanıyorum" bildirir; deploy / scale-down alarm üretmez.</item>
    /// </list>
    /// Monitor'e ulaşılamazsa uygulama hiçbir şekilde etkilenmez; konsola kısa bir İngilizce mesaj yazılır ve tekrar denenir.
    /// </summary>
    public sealed class ConnectivityProbeAgent : IDisposable
    {
        /// <summary>Monitor'deki bildirim (ve kayıt) ucu.</summary>
        public const string ReportPath = "/api/agent/v2/report";
        /// <summary>Monitor'deki "kapanıyorum" ucu.</summary>
        public const string GoodbyePath = "/api/agent/v2/goodbye";

        private static readonly object StartLock = new object();
        private static ConnectivityProbeAgent? _current;

        private readonly ConnectivityProbeOptions _options;
        private readonly Assembly? _app;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _loop;
        private int _stopped;

        private ConnectivityProbeAgent(ConnectivityProbeOptions options, Assembly? app)
        {
            _options = options;
            _app = app;
            // Monitor'e giden istekler için tek bir istemci (bağlantı yeniden kullanılır).
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // Süreç kapanırken (Kubernetes SIGTERM, Ctrl+C, servis durdurma) ve IIS uygulamayı geri dönüştürürken Monitor'e haber ver.
            AppDomain.CurrentDomain.ProcessExit += OnShutdown;
            AppDomain.CurrentDomain.DomainUnload += OnShutdown;

            _loop = Task.Run(() => LoopAsync(_stop.Token));
        }

        /// <summary>Bu süreçte çalışan agent (yoksa null).</summary>
        public static ConnectivityProbeAgent? Current => Volatile.Read(ref _current);

        /// <summary>Monitor'le son başarılı iletişim zamanı.</summary>
        public DateTime? LastContactUtc { get; private set; }
        /// <summary>Son test turunun başlangıç zamanı.</summary>
        public DateTime? LastRunUtc { get; private set; }
        /// <summary>Monitor'e ulaşılamıyorsa son hata.</summary>
        public string? LastError { get; private set; }
        /// <summary>Monitor'deki uygulama kimliği (ilk başarılı bildirimden sonra).</summary>
        public string? AppId { get; private set; }

        /// <summary>
        /// ConnectivityProbe'u başlatır. Uygulama açılırken bir kez çağrılır:
        /// ASP.NET Core / Worker / konsol uygulamalarında Program.cs'te, IIS / klasik ASP.NET'te Global.asax Application_Start'ta.
        /// </summary>
        /// <param name="monitorUrl">Monitor'ün adresi (ör. https://monitor.example.com). Zorunlu.</param>
        /// <param name="appKey">Uygulama anahtarı (ör. "orders-api"): uygulamanın Monitor'deki kimliği. Zorunlu.</param>
        /// <param name="appName">Monitor'de görünecek ad; verilmezse bu metodu çağıran projenin assembly adı.</param>
        /// <param name="configure">İsteğe bağlı ince ayarlar (test aralığı, bildirim sıklığı...).</param>
        /// <returns>Çalışan agent; zorunlu değerler eksik veya geçersizse null (uygulama etkilenmez, konsola mesaj yazılır).</returns>
        [MethodImpl(MethodImplOptions.NoInlining)] // Assembly.GetCallingAssembly'nin doğru projeyi bulması için
        public static ConnectivityProbeAgent? Start(string monitorUrl, string appKey, string? appName = null, Action<ConnectivityProbeOptions>? configure = null)
        {
            // Sürüm bilgisi Start'ı çağıran projeden okunur (IIS'te GetEntryAssembly null döner, çağıran proje doğru sonucu verir).
            var app = Assembly.GetCallingAssembly();
            if (app == typeof(ConnectivityProbeAgent).Assembly) app = Assembly.GetEntryAssembly();

            try
            {
                var options = new ConnectivityProbeOptions { MonitorUrl = monitorUrl?.Trim() ?? "", AppKey = appKey?.Trim() ?? "", AppName = appName };
                configure?.Invoke(options);
                return Start(options, app);
            }
            catch (Exception ex)
            {
                // ConnectivityProbe hiçbir koşulda uygulamanın açılmasını engellememeli.
                Say("Could not start: " + ex.Message);
                return null;
            }
        }

        private static ConnectivityProbeAgent? Start(ConnectivityProbeOptions options, Assembly? app)
        {
            if (string.IsNullOrEmpty(options.MonitorUrl) || string.IsNullOrEmpty(options.AppKey))
            {
                Say("MonitorUrl and AppKey are required. Connectivity probe is disabled.");
                return null;
            }
            if (!Uri.TryCreate(options.MonitorUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                Say("Invalid MonitorUrl '" + options.MonitorUrl + "' (expected http:// or https://). Connectivity probe is disabled.");
                return null;
            }
            options.MonitorUrl = options.MonitorUrl.TrimEnd('/');

#if NETFRAMEWORK
            // .NET Framework 4.6.2 - 4.7'de TLS 1.2 varsayılan olarak kapalı olabilir; HTTPS Monitor'e bağlanabilmek için ekliyoruz
            // (mevcut protokoller kaldırılmaz).
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif

            lock (StartLock)
            {
                // Süreç başına tek agent; ikinci çağrı mevcut olanı döner.
                return _current ??= new ConnectivityProbeAgent(options, app);
            }
        }

        /// <summary>
        /// Agent'ı durdurur ve Monitor'e "kapanıyorum" bildirir (en fazla <paramref name="timeout"/>, varsayılan 5 sn).
        /// Süreç kapanırken kendiliğinden çağrılır; elle çağırmak gerekmez.
        /// </summary>
        public void Stop(TimeSpan? timeout = null)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
            var wait = timeout ?? TimeSpan.FromSeconds(5);
            AppDomain.CurrentDomain.ProcessExit -= OnShutdown;
            AppDomain.CurrentDomain.DomainUnload -= OnShutdown;

            _stop.Cancel();
            try { _loop.Wait(wait); } catch (AggregateException) { /* döngü iptalle bitti */ }

            // Monitor'e hiç ulaşılamadıysa veda göndermeye çalışıp kapanışı geciktirmiyoruz.
            if (LastContactUtc != null)
            {
                // Senkron bağlamlarda (IIS, kapanış olayları) kilitlenmemek için iş parçacığı havuzunda bekliyoruz.
                try { Task.Run(SendGoodbyeAsync).Wait(wait); }
                catch (AggregateException) { /* Monitor'e ulaşılamadı; pod birkaç tur sonra "eksik" görünür */ }
            }

            _http.Dispose();
            lock (StartLock)
            {
                if (ReferenceEquals(_current, this)) _current = null;
            }
        }

        public void Dispose() => Stop();

        private void OnShutdown(object? sender, EventArgs e) => Stop(TimeSpan.FromSeconds(3));

        // ------------------------------------------------------------------ döngü

        private async Task LoopAsync(CancellationToken ct)
        {
            var poll = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
            AgentRun? pending = null;       // gönderilmeyi bekleyen test sonuçları
            DateTime? lastRunStarted = null;
            string? lastRunRequestId = null;
            int failures = 0;
            bool everConnected = false;

            while (!ct.IsCancellationRequested)
            {
                // step 1: Bildirim (ilkinde kayıt) gönderiyoruz; varsa son test sonuçları da gider. Yanıtta tanımlar ve test aralığı gelir.
                Assignment? assignment = null;
                try
                {
                    assignment = await ReportAsync(pending, ct).ConfigureAwait(false);
                    pending = null;
                    LastContactUtc = DateTime.UtcNow;
                    LastError = null;
                    AppId = assignment.AppId;

                    if (!everConnected)
                        Say("Registered to monitor " + _options.MonitorUrl + " as \"" + _options.AppKey + "\" ("
                            + assignment.Connections.Count + " connection" + (assignment.Connections.Count == 1 ? "" : "s") + ").");
                    else if (failures > 0)
                        Say("Reconnected to monitor " + _options.MonitorUrl + ".");
                    everConnected = true;
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Uygulama etkilenmez. Konsolu boğmamak için ilk hatayı ve sonra yaklaşık 5 dakikada bir yazıyoruz.
                    failures++;
                    LastError = Describe(ex);
                    if (failures == 1 || failures % Math.Max(1, 300 / (int)poll.TotalSeconds) == 0)
                        Say("Could not connect to monitor " + _options.MonitorUrl + ": " + LastError);
                }

                // step 2: Test zamanı geldiyse (ilk tur, aralık doldu veya "Şimdi test et") testleri yapıp sonucu hemen gönderiyoruz.
                if (assignment != null)
                {
                    bool runRequested = lastRunRequestId != null && assignment.RunRequestId != lastRunRequestId;
                    lastRunRequestId = assignment.RunRequestId;
                    var interval = TimeSpan.FromSeconds(Math.Max(5, _options.IntervalSeconds > 0 ? _options.IntervalSeconds : assignment.IntervalSeconds));

                    if (lastRunStarted == null || DateTime.UtcNow - lastRunStarted.Value >= interval || runRequested)
                    {
                        lastRunStarted = DateTime.UtcNow;
                        LastRunUtc = lastRunStarted;
                        try
                        {
                            pending = await RunTestsAsync(assignment, ct).ConfigureAwait(false);
                            continue; // beklemeden gönder
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                try { await Task.Delay(poll, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Atanan her bağlantıyı bu pod'un içinden test eder (en fazla MaxParallelTests aynı anda).
        private async Task<AgentRun> RunTestsAsync(Assignment assignment, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var results = new AgentResult[assignment.Connections.Count];
            using (var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelTests)))
            {
                await Task.WhenAll(assignment.Connections.Select(async (connection, i) =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { results[i] = await TestAsync(connection, assignment, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }
            return new AgentRun { StartedAtUtc = started, DurationMs = clock.ElapsedMilliseconds, Results = results.ToList() };
        }

        private async Task<AgentResult> TestAsync(AssignedConnection connection, Assignment assignment, CancellationToken ct)
        {
            var result = new AgentResult { ConnectionId = connection.Id };
            if (!ProbeTarget.TryParse(connection.Host, connection.Port?.ToString(CultureInfo.InvariantCulture),
                    out var host, out var port, out _, out var error))
            {
                result.Error = "Invalid target: " + error;
                return result;
            }

            try
            {
                // Telnet: isim çözülür; dönen her IP ve ismin kendisi aynı anda denenir (nedeni: timeout, reddedildi, DNS...).
                var timeout = TimeSpan.FromMilliseconds(Math.Max(500, _options.TimeoutMs > 0 ? _options.TimeoutMs : assignment.TimeoutMs));
                result.Tcp = await TcpProbe.ProbeAllAsync(host, port, 1, timeout, TimeSpan.Zero, Math.Max(1, _options.MaxAddresses),
                    TimeSpan.FromTicks(timeout.Ticks * 3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        // ------------------------------------------------------------------ Monitor ile iletişim

        private async Task<Assignment> ReportAsync(AgentRun? run, CancellationToken ct)
        {
            var identity = PodIdentityBuilder.Build(_app, _options.AppName);
            var report = new AgentReport
            {
                AppName = identity.AppName,
                Pod = identity,
                PollSeconds = Math.Max(1, _options.PollSeconds),
                Run = run
            };

            using (var request = NewRequest(ReportPath, Json.Serialize(report)))
            using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ErrorDetail(body));
                return Assignment.Parse(body);
            }
        }

        private async Task SendGoodbyeAsync()
        {
            var body = Json.Serialize(new Dictionary<string, object?>
            {
                ["instanceId"] = PodIdentityBuilder.ComputeId(PodIdentityBuilder.IdSeed(
                    System.Environment.MachineName, System.Environment.GetEnvironmentVariable("POD_NAME")))
            });
            using (var request = NewRequest(GoodbyePath, body))
            using (var response = await _http.SendAsync(request).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
            }
        }

        private HttpRequestMessage NewRequest(string path, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _options.MonitorUrl + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation(ConnectivityProbeOptions.AppKeyHeader, _options.AppKey);
            return request;
        }

        private static string ErrorDetail(string body)
        {
            try
            {
                var error = Json.GetString(Json.Parse(body) as IDictionary<string, object?>, "error");
                return string.IsNullOrEmpty(error) ? "" : " " + error;
            }
            catch (FormatException)
            {
                return "";
            }
        }

        // İç içe istisnalardan en anlamlı mesajı alır (ör. "No such host is known").
        private static string Describe(Exception ex)
        {
            if (ex is TaskCanceledException) return "timeout";
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        // Konsola (ve IIS gibi konsolu olmayan ortamlar için Trace'e) tek satırlık İngilizce mesaj.
        internal static void Say(string message)
        {
            var line = "[ConnectivityProbe] " + message;
            try { Console.WriteLine(line); } catch (Exception) { /* konsol yoksa */ }
            try { System.Diagnostics.Trace.WriteLine(line); } catch (Exception) { /* yok sayılır */ }
        }

        // ------------------------------------------------------------------ Monitor yanıtı

        /// <summary>Monitor'ün bildirim yanıtı: bu uygulamaya ait bağlantılar ve test ayarları.</summary>
        internal sealed class Assignment
        {
            public string? AppId { get; set; }
            public int IntervalSeconds { get; set; } = 30;
            /// <summary>Monitor'de "Şimdi test et"e her basıldığında değişir; değiştiyse pod beklemeden test eder.</summary>
            public string RunRequestId { get; set; } = "";
            public int TimeoutMs { get; set; } = 5000;
            public List<AssignedConnection> Connections { get; set; } = new List<AssignedConnection>();

            public static Assignment Parse(string json)
            {
                var o = Json.Parse(json) as IDictionary<string, object?> ?? throw new FormatException("Monitor response is not a JSON object");
                var a = new Assignment
                {
                    AppId = Json.GetString(o, "appId"),
                    RunRequestId = Json.GetString(o, "runRequestId") ?? "",
                    Connections = Json.GetObjectList(o, "connections").Select(c => new AssignedConnection
                    {
                        Id = Json.GetString(c, "id") ?? "",
                        Host = Json.GetString(c, "host") ?? "",
                        Port = Json.GetDouble(c, "port") is double p ? (int)p : (int?)null
                    }).Where(c => c.Id.Length > 0).ToList()
                };
                if (Json.GetLong(o, "intervalSeconds") is long interval && interval > 0) a.IntervalSeconds = (int)interval;
                if (Json.GetLong(o, "timeoutMs") is long timeout && timeout > 0) a.TimeoutMs = (int)timeout;
                return a;
            }
        }

        internal sealed class AssignedConnection
        {
            public string Id { get; set; } = "";
            public string Host { get; set; } = "";
            public int? Port { get; set; }
        }
    }
}
