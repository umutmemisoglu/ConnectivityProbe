using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>Strict mod: pod'un Monitor'e gönderdiği bildirim (her CommandPollSeconds'ta bir; test yapıldıysa sonuçlarla).</summary>
    public sealed class AgentReport
    {
        /// <summary>Bildirimi gönderen pod'un kimliği (identity ucunun döndüğüyle aynı).</summary>
        public InstanceIdentity Identity { get; set; } = new InstanceIdentity();
        /// <summary>Pod'un Monitor'e ne sıklıkla bildirim gönderdiği (sn); Monitor pod'un canlılığını buna göre değerlendirir.</summary>
        public int PollSeconds { get; set; }
        /// <summary>Son test turunun sonuçları; bu bildirimde test sonucu yoksa null.</summary>
        public AgentRun? Run { get; set; }
    }

    /// <summary>Strict mod: bir pod'un bir test turu.</summary>
    public sealed class AgentRun
    {
        public DateTime StartedAtUtc { get; set; }
        public long DurationMs { get; set; }
        public List<AgentResult> Results { get; set; } = new List<AgentResult>();
    }

    /// <summary>Strict mod: bir bağlantının bu pod'daki test sonucu (discover ucunun döndüğü raporla aynı).</summary>
    public sealed class AgentResult
    {
        public string ConnectionId { get; set; } = "";
        public DiscoverReport? Report { get; set; }
        /// <summary>Test yapılamadıysa nedeni (ör. tanımdaki host geçersiz).</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Strict mod: uygulamanın her pod'unda arka planda çalışan iş. <see cref="ConnectivityProbeOptions.MonitorUrl"/> ve
    /// <see cref="ConnectivityProbeOptions.AppKey"/> verildiğinde kendiliğinden başlar (ASP.NET Core, IIS); diğer uygulamalarda
    /// <c>ConnectivityProbeAgent.Start()</c> ile başlatılır.
    /// <list type="number">
    /// <item>Her <see cref="ConnectivityProbeOptions.StrictPollSeconds"/> saniyede Monitor'e bildirim gönderir ("bu pod yaşıyor");
    /// yanıtta uygulamaya ait bağlantı tanımlarını ve test aralığını alır.</item>
    /// <item>Test zamanı geldiğinde (veya Monitor'de "Şimdi test et"e basıldığında) her bağlantıyı bu pod'un içinden test eder
    /// (discover ucuyla aynı mantık) ve sonuçları hemen gönderir.</item>
    /// <item>Uygulama düzgün kapanırken Monitor'e "kapanıyorum" bildirir; böylece deploy / scale-down alarm üretmez.</item>
    /// </list>
    /// İstekler load balancer'dan geçmediği için her pod kendini bildirir: pod sayısı ve her pod'un sonucu kesindir.
    /// </summary>
    public sealed class ConnectivityProbeAgent : IDisposable
    {
        /// <summary>Monitor'deki bildirim ucu.</summary>
        public const string ReportPath = "/api/agent/v1/report";
        /// <summary>Monitor'deki "kapanıyorum" ucu.</summary>
        public const string GoodbyePath = "/api/agent/v1/goodbye";

        /// <summary>Aynı anda test edilen en fazla bağlantı (bir pod'un hedeflere ani yük bindirmemesi için).</summary>
        private const int MaxParallelTests = 4;

        private static readonly object StartLock = new object();
        private static ConnectivityProbeAgent? _current;

        private readonly ConnectivityProbeOptions _options;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _loop;
        private int _stopped;

        private ConnectivityProbeAgent(ConnectivityProbeOptions options)
        {
            _options = options;
            // Monitor'e giden istekler için tek bir istemci (bağlantı yeniden kullanılır; burada pod seçimi söz konusu değil).
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Log(ProbeLogLevel.Information, "ConnectivityProbe strict mode started, reporting to " + options.MonitorUrl);
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
        /// <summary>Monitor'deki uygulama adı (ilk başarılı bildirimden sonra).</summary>
        public string? AppName { get; private set; }

        /// <summary>
        /// Strict mod agent'ını başlatır. <paramref name="options"/> verilmezse ayarlar ortam değişkenlerinden (ve .NET
        /// Framework'te appSettings'ten) okunur. MonitorUrl veya AppKey yoksa hiçbir şey yapmadan null döner. Süreç başına tek
        /// agent çalışır; ikinci çağrı mevcut agent'ı döner.
        /// </summary>
        public static ConnectivityProbeAgent? Start(ConnectivityProbeOptions? options = null)
        {
            options ??= ConnectivityProbeOptions.FromEnvironment();
            if (!options.StrictEnabled) return null;
            lock (StartLock)
            {
                return _current ??= new ConnectivityProbeAgent(options);
            }
        }

        /// <summary>
        /// Agent'ı durdurur ve Monitor'e "kapanıyorum" bildirir (en fazla <paramref name="timeout"/>, varsayılan 5 sn).
        /// Uygulama kapanırken çağrılmalıdır; ASP.NET Core ve IIS'te kendiliğinden çağrılır.
        /// </summary>
        public void Stop(TimeSpan? timeout = null)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
            var wait = timeout ?? TimeSpan.FromSeconds(5);
            _stop.Cancel();
            try { _loop.Wait(wait); } catch (AggregateException) { /* döngü iptalle bitti */ }

            // Senkron bağlamlarda (IIS, ApplicationStopping) kilitlenmemek için iş parçacığı havuzunda bekliyoruz.
            try { Task.Run(SendGoodbyeAsync).Wait(wait); }
            catch (AggregateException ex) { Log(ProbeLogLevel.Warning, "ConnectivityProbe strict mode: goodbye failed: " + ex.InnerException?.Message); }

            _http.Dispose();
            lock (StartLock)
            {
                if (ReferenceEquals(_current, this)) _current = null;
            }
            Log(ProbeLogLevel.Information, "ConnectivityProbe strict mode stopped");
        }

        public void Dispose() => Stop();

        // ------------------------------------------------------------------ döngü

        private async Task LoopAsync(CancellationToken ct)
        {
            var poll = TimeSpan.FromSeconds(Math.Max(1, _options.StrictPollSeconds));
            AgentRun? pending = null;       // gönderilmeyi bekleyen test sonuçları
            DateTime? lastRunStarted = null;
            string? lastRunRequestId = null;
            int failures = 0;

            while (!ct.IsCancellationRequested)
            {
                // step 1: Bildirim gönderiyoruz (varsa son test sonuçlarıyla); yanıtta güncel tanımlar ve test aralığı gelir.
                Assignment? assignment = null;
                try
                {
                    assignment = await ReportAsync(pending, ct).ConfigureAwait(false);
                    pending = null;
                    if (failures > 0) Log(ProbeLogLevel.Information, "ConnectivityProbe strict mode: Monitor reachable again");
                    failures = 0;
                    LastContactUtc = DateTime.UtcNow;
                    LastError = null;
                    AppName = assignment.AppName;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Monitor'e ulaşılamıyor / anahtar yanlış: logu boğmamak için ilk hatayı ve sonra yaklaşık 5 dakikada bir yazıyoruz.
                    failures++;
                    LastError = ex.InnerException?.Message ?? ex.Message;
                    if (failures == 1 || failures % Math.Max(1, 300 / (int)poll.TotalSeconds) == 0)
                        Log(ProbeLogLevel.Warning, "ConnectivityProbe strict mode: cannot report to " + _options.MonitorUrl + ": " + LastError);
                }

                // step 2: Test zamanı geldiyse (ilk tur, aralık doldu veya Monitor'de "Şimdi test et") testleri yapıp sonucu hemen gönderiyoruz.
                if (assignment != null)
                {
                    bool runRequested = lastRunRequestId != null && assignment.RunRequestId != lastRunRequestId;
                    lastRunRequestId = assignment.RunRequestId;
                    var interval = TimeSpan.FromSeconds(Math.Max(5, _options.StrictIntervalSeconds > 0 ? _options.StrictIntervalSeconds : assignment.IntervalSeconds));

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
            using (var gate = new SemaphoreSlim(MaxParallelTests))
            {
                await Task.WhenAll(assignment.Connections.Select(async (connection, i) =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { results[i] = await TestAsync(connection, assignment, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }

            Log(ProbeLogLevel.Debug, "ConnectivityProbe strict mode: tested " + results.Length + " connection(s) in " + clock.ElapsedMilliseconds + " ms");
            return new AgentRun { StartedAtUtc = started, DurationMs = clock.ElapsedMilliseconds, Results = results.ToList() };
        }

        private async Task<AgentResult> TestAsync(AssignedConnection connection, Assignment assignment, CancellationToken ct)
        {
            var result = new AgentResult { ConnectionId = connection.Id };
            if (!ProbeTarget.TryParse(connection.Host, connection.Port?.ToString(CultureInfo.InvariantCulture),
                    out var host, out var port, out var urlScheme, out var error))
            {
                result.Error = "Invalid target: " + error;
                return result;
            }

            try
            {
                var timeout = TimeSpan.FromMilliseconds(Math.Min(Math.Max(500, assignment.TimeoutMs), _options.MaxTimeout.TotalMilliseconds));
                var attempts = Math.Min(Math.Max(1, assignment.Attempts), _options.MaxAttempts);
                result.Report = await DiscoverRunner.RunAsync(_options, host, port, urlScheme ?? "http", connection.UsesConnectivityProbe,
                    timeout, attempts, assignment.Confidence, _options.AccessKey, ct).ConfigureAwait(false);
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
            var report = new AgentReport
            {
                Identity = InstanceIdentityBuilder.Build(new ProbeRequest(), _options),
                PollSeconds = Math.Max(1, _options.StrictPollSeconds),
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
            var body = Json.Serialize(new Dictionary<string, object?> { ["instanceId"] = InstanceIdentityBuilder.GetInstanceId(_options) });
            using (var request = NewRequest(GoodbyePath, body))
            using (var response = await _http.SendAsync(request).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
            }
        }

        private HttpRequestMessage NewRequest(string path, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _options.MonitorUrl!.TrimEnd('/') + path)
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
                return string.IsNullOrEmpty(error) ? "" : ": " + error;
            }
            catch (FormatException)
            {
                return "";
            }
        }

        private void Log(ProbeLogLevel level, string message)
        {
            try { _options.Log?.Invoke(level, message); }
            catch (Exception) { /* log hatası agent'ı durdurmasın */ }
        }

        // ------------------------------------------------------------------ Monitor yanıtı

        /// <summary>Monitor'ün bildirim yanıtı: bu uygulamaya ait tanımlar ve test ayarları.</summary>
        internal sealed class Assignment
        {
            public string? AppName { get; set; }
            public int IntervalSeconds { get; set; } = 30;
            /// <summary>Monitor'de "Şimdi test et"e her basıldığında değişir; değiştiyse pod beklemeden test eder.</summary>
            public string RunRequestId { get; set; } = "";
            public int TimeoutMs { get; set; } = 5000;
            public int Attempts { get; set; } = 60;
            public double Confidence { get; set; } = 0.95;
            public List<AssignedConnection> Connections { get; set; } = new List<AssignedConnection>();

            public static Assignment Parse(string json)
            {
                var o = Json.Parse(json) as IDictionary<string, object?> ?? throw new FormatException("Monitor response is not a JSON object");
                var a = new Assignment
                {
                    AppName = Json.GetString(o, "appName"),
                    RunRequestId = Json.GetString(o, "runRequestId") ?? "",
                    Connections = Json.GetObjectList(o, "connections").Select(c => new AssignedConnection
                    {
                        Id = Json.GetString(c, "id") ?? "",
                        Host = Json.GetString(c, "host") ?? "",
                        Port = Json.GetDouble(c, "port") is double p ? (int)p : (int?)null,
                        UsesConnectivityProbe = Json.GetBool(c, "usesConnectivityProbe")
                    }).Where(c => c.Id.Length > 0).ToList()
                };
                if (Json.GetLong(o, "intervalSeconds") is long interval && interval > 0) a.IntervalSeconds = (int)interval;
                if (Json.GetLong(o, "timeoutMs") is long timeout && timeout > 0) a.TimeoutMs = (int)timeout;
                if (Json.GetLong(o, "attempts") is long attempts && attempts > 0) a.Attempts = (int)attempts;
                if (Json.GetDouble(o, "confidence") is double confidence && confidence >= 0.5 && confidence <= 0.999) a.Confidence = confidence;
                return a;
            }
        }

        internal sealed class AssignedConnection
        {
            public string Id { get; set; } = "";
            public string Host { get; set; } = "";
            public int? Port { get; set; }
            public bool UsesConnectivityProbe { get; set; }
        }
    }
}
