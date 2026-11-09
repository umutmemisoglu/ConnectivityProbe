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
        /// <summary>Pod'un kaynak kullanımı (CPU, bellek, container limitleri, thread'ler, TCP soketleri).</summary>
        public ResourceSample? Resources { get; set; }
        /// <summary>Ayarlardan bulunan bağımlılıklar (SettingsUrl / SettingsJson / Settings verildiyse; yalnızca tür, host, port).</summary>
        public DiscoveredSettings? Settings { get; set; }
        /// <summary>Bağlantı grupları (ör. "Ödeme servisleri": pay, billing); Monitor detayda bağlantıları bunlara göre gruplar.</summary>
        public List<ConnectionGroup> Groups { get; set; } = new List<ConnectionGroup>();
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
        /// <summary>TLS kontrolü (Monitor bu bağlantı için istediyse ve TCP başarılıysa): el sıkışma ve sertifika.</summary>
        public TlsReport? Tls { get; set; }
        /// <summary>Test yapılamadıysa nedeni (ör. tanımdaki host geçersiz).</summary>
        public string? Error { get; set; }
        /// <summary>
        /// Hedef servisin ConnectivityProbe health ucunun sonucu (Monitor bu bağlantı için istediyse): servis DLL'i kullanıyor mu,
        /// kullanıyorsa durumu. Hedef birden fazla pod'sa her biri için ayrı.
        /// </summary>
        public List<HealthCheckResult>? Health { get; set; }
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
        private readonly HealthServer? _health;
        private volatile int _lastRunTotal = -1;   // -1: henüz test yapılmadı
        private volatile int _lastRunFailed;

        /// <summary>Health ucunun dinlediği port (kapalıysa null).</summary>
        public int? HealthPort => _health?.Port;

        // Health ucunun cevabı: ConnectivityProbe imzası, durum ve uygulama bilgisi. Durum: son testte erişilemeyen bağlantı
        // varsa Degraded, yoksa Healthy. Uygulama anahtarı (kimlik doğrulamada kullanıldığı için) cevaba konmaz.
        private string HealthJson()
        {
            var identity = PodIdentityBuilder.Build(_app, _options.AppName);
            var body = new Dictionary<string, object?>
            {
                ["connectivityProbe"] = ProbeInfo.Version,
                ["status"] = _lastRunFailed > 0 ? "Degraded" : "Healthy",
                ["appId"] = AppId,
                ["appName"] = identity.AppName,
                ["appVersion"] = identity.AppVersion,
                ["buildId"] = identity.BuildId,
                ["instanceId"] = identity.InstanceId,
                ["machineName"] = identity.MachineName,
                ["startedAtUtc"] = identity.StartedAtUtc,
                ["uptimeSeconds"] = (long)(DateTime.UtcNow - identity.StartedAtUtc).TotalSeconds,
                ["monitor"] = new Dictionary<string, object?> { ["lastContactUtc"] = LastContactUtc, ["error"] = LastError },
                ["lastRun"] = _lastRunTotal < 0 ? null : new Dictionary<string, object?>
                {
                    ["atUtc"] = LastRunUtc, ["connections"] = _lastRunTotal, ["failed"] = _lastRunFailed
                }
            };
            return Json.Serialize(body);
        }

        private ConnectivityProbeAgent(ConnectivityProbeOptions options, Assembly? app)
        {
            _options = options;
            _app = app;
            // Monitor'e giden istekler için tek bir istemci (bağlantı yeniden kullanılır).
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // Süreç kapanırken (Kubernetes SIGTERM, Ctrl+C, servis durdurma) ve IIS uygulamayı geri dönüştürürken Monitor'e haber ver.
            AppDomain.CurrentDomain.ProcessExit += OnShutdown;
            AppDomain.CurrentDomain.DomainUnload += OnShutdown;

            // ConnectivityProbe health ucu: ayrı portta (uygulamanın kendi uçlarıyla çakışmaz; worker'larda da çalışır).
            if (options.HealthEnabled)
            {
                _health = HealthServer.Start(options.HealthPort > 0 ? options.HealthPort : HealthServer.DefaultPort, HealthJson);
                if (_health == null) Say("Health endpoint could not be opened.");
                else if (_health.Port != options.HealthPort) Say("Health port " + options.HealthPort + " is in use; health endpoint on port " + _health.Port + ".");
            }

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

            _health?.Dispose();
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
                // step 0: Ayarlar verildiyse zamanı geldiğinde arka planda yeniden okunur (bildirimi geciktirmez).
                StartSettingsRefreshIfDue(ct);

                // step 1: Bildirim (ilkinde kayıt) gönderiyoruz; varsa son test sonuçları da gider. Yanıtta tanımlar ve test aralığı gelir.
                Assignment? assignment = null;
                try
                {
                    assignment = await ReportAsync(pending, ct).ConfigureAwait(false);
                    pending = null;
                    LastContactUtc = DateTime.UtcNow;
                    LastError = null;
                    AppId = assignment.AppId;
                    if (assignment.VerifySettings != _verifySettings)
                    {
                        // Monitor'de "sunucu türünü doğrula" açıldı / kapandı: ayarlar hemen yeniden işlenir.
                        _verifySettings = assignment.VerifySettings;
                        _nextSettingsReadUtc = DateTime.MinValue;
                    }

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
                            _lastRunTotal = pending.Results.Count;
                            _lastRunFailed = pending.Results.Count(r => r.Tcp == null || !r.Tcp.HostnameAttempts.Any(a => a.Success));
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

                // TLS: port açıksa el sıkışıp sertifikayı okuyoruz (bitiş tarihi, ad uyumu, güven zinciri).
                if (connection.Tls && result.Tcp.HostnameAttempts.Any(a => a.Success))
                    result.Tls = await TlsProbe.ProbeAsync(host, port, timeout, ct).ConfigureAwait(false);

                // Health: hedef servisin ConnectivityProbe health ucu (DLL'i kullanıyor mu, kullanıyorsa durumu).
                if (connection.Health.Count > 0)
                {
                    var healthTimeout = (int)Math.Min(timeout.TotalMilliseconds, 5000);
                    var checks = await Task.WhenAll(connection.Health.Take(20).Select(async h =>
                    {
                        var r = await HealthProbe.CheckAsync(h.Host, h.Port, healthTimeout, ct).ConfigureAwait(false);
                        r.Instance = h.Instance;
                        return r;
                    })).ConfigureAwait(false);
                    result.Health = checks.ToList();
                }
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

        // ------------------------------------------------------------------ ayarlardaki bağımlılıklar

        private DiscoveredSettings? _settings;
        private Task? _settingsTask;
        private DateTime _nextSettingsReadUtc = DateTime.MinValue;
        private bool _verifySettings;
        // Doğrulama sonuçları (host:port:tls -> tür); her okumada tüm sunuculara yeniden bağlanmamak için.
        private readonly Dictionary<string, VerifiedEndpoint> _verified = new Dictionary<string, VerifiedEndpoint>(StringComparer.OrdinalIgnoreCase);

        private sealed class VerifiedEndpoint
        {
            public bool Open { get; set; }
            public string? Kind { get; set; }
            public DateTime AtUtc { get; set; }
        }

        private bool SettingsConfigured =>
            !string.IsNullOrWhiteSpace(_options.SettingsUrl) || !string.IsNullOrWhiteSpace(_options.SettingsJson) || _options.Settings != null;

        private void StartSettingsRefreshIfDue(CancellationToken ct)
        {
            if (!SettingsConfigured || DateTime.UtcNow < _nextSettingsReadUtc) return;
            if (_settingsTask != null && !_settingsTask.IsCompleted) return;
            _nextSettingsReadUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SettingsRefreshMinutes));
            _settingsTask = Task.Run(() => RefreshSettingsAsync(ct), ct);
        }

        // Ayarları okur, bağımlılıkları bulur, düşük güvenlileri eler ve (açıksa) türleri doğrular. Okuma başarısızsa önceki
        // sonuç korunur: geçici bir hata Monitor'deki tanımları silmez.
        private async Task RefreshSettingsAsync(CancellationToken ct)
        {
            var previous = Volatile.Read(ref _settings);
            try
            {
                var json = await LoadSettingsJsonAsync(ct).ConfigureAwait(false);
                var endpoints = SettingsDiscovery.Discover(json);
                var timeoutMs = Math.Max(1000, _options.TimeoutMs > 0 ? _options.TimeoutMs : 3000);

                // Düşük güvenli adaylar: ad bir IP'ye çözülmüyorsa sunucu değildir (ör. düz metin). Yüksek güvenliler asla elenmez;
                // "veritabanına erişilemiyor" zaten izlenmesi gereken durumdur.
                var kept = new List<DiscoveredEndpoint>();
                foreach (var e in endpoints)
                    if (e.Confidence == "high" || await EndpointVerifier.ResolvesAsync(e.Host, timeoutMs, ct).ConfigureAwait(false)) kept.Add(e);

                if (_verifySettings) kept = await VerifyAsync(kept, timeoutMs, ct).ConfigureAwait(false);

                var result = new DiscoveredSettings
                {
                    Signature = SettingsDiscovery.Signature(kept), ReadAtUtc = DateTime.UtcNow, Endpoints = kept, Verified = _verifySettings
                };
                if (previous?.Signature != result.Signature)
                    Say("Settings: " + kept.Count + " dependenc" + (kept.Count == 1 ? "y" : "ies") + " found"
                        + (kept.Count > 0 ? " (" + string.Join(", ", kept.GroupBy(e => e.Kind).Select(g => g.Count() + " " + g.Key)) + ")" : "") + ".");
                Volatile.Write(ref _settings, result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // Adres içinde gizli kimlikler olabileceği için mesajda adres yazılmaz.
                var error = Describe(ex);
                if (previous?.Error != error) Say("Settings could not be read: " + error);
                Volatile.Write(ref _settings, new DiscoveredSettings
                {
                    Signature = previous?.Signature, ReadAtUtc = previous?.ReadAtUtc ?? DateTime.UtcNow,
                    Endpoints = previous?.Endpoints ?? new List<DiscoveredEndpoint>(), Verified = previous?.Verified ?? false, Error = error
                });
                _nextSettingsReadUtc = DateTime.UtcNow.AddMinutes(1);   // hata: bir dakika sonra yeniden dene
            }
        }

        private async Task<string> LoadSettingsJsonAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_options.SettingsJson)) return _options.SettingsJson!;
            if (_options.Settings != null) return Json.Serialize(_options.Settings);

            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Get, _options.SettingsUrl!.Trim()))
            {
                timer.CancelAfter(TimeSpan.FromSeconds(15));
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using (var response = await _http.SendAsync(request, timer.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("settings returned HTTP " + (int)response.StatusCode);
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        // 3. katman: her adrese bağlanıp türünü sunucunun cevabından anlar. Tür doğrulanırsa (veya düzelirse) güven "high" olur;
        // düşük güvenli bir aday hiç bağlanılamıyorsa elenir. Sonuçlar 6 saat (bağlanılamayanlar 30 dk) saklanır.
        private async Task<List<DiscoveredEndpoint>> VerifyAsync(List<DiscoveredEndpoint> endpoints, int timeoutMs, CancellationToken ct)
        {
            var results = new VerifiedEndpoint[endpoints.Count];
            using (var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelTests)))
            {
                await Task.WhenAll(endpoints.Select(async (e, i) =>
                {
                    var cacheKey = e.Host + ":" + e.Port + ":" + (e.Tls ? "tls" : "") + ":" + e.Kind;
                    lock (_verified)
                    {
                        if (_verified.TryGetValue(cacheKey, out var cached)
                            && DateTime.UtcNow - cached.AtUtc < (cached.Kind != null ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(30)))
                        {
                            results[i] = cached;
                            return;
                        }
                    }
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var expected = e.Kind == "tcp" ? null : e.Kind;
                        var r = await EndpointVerifier.IdentifyAsync(e.Host, e.Port, e.Tls, expected, timeoutMs, ct).ConfigureAwait(false);
                        // Beklenen türle doğrulanamadıysa ve güven düşükse türü bilinmeyen olarak bir kez daha (tüm protokoller) deneriz.
                        if (r.Open && r.Kind == null && expected != null && e.Confidence == "low")
                            r = await EndpointVerifier.IdentifyAsync(e.Host, e.Port, e.Tls, null, timeoutMs, ct).ConfigureAwait(false);
                        var v = new VerifiedEndpoint { Open = r.Open, Kind = r.Kind, AtUtc = DateTime.UtcNow };
                        lock (_verified) _verified[cacheKey] = v;
                        results[i] = v;
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }

            var kept = new List<DiscoveredEndpoint>();
            for (int i = 0; i < endpoints.Count; i++)
            {
                var e = endpoints[i];
                var v = results[i];
                if (v.Kind != null)
                {
                    e.Verified = v.Kind;
                    // HTTP cevabı yalnızca "http" diyorsa daha özel tahmini (ör. alan adından "kibana") ezmiyoruz.
                    if (!(v.Kind == "http" && e.Kind != "tcp")) e.Kind = v.Kind;
                    e.Confidence = "high";
                }
                else if (!v.Open && e.Confidence == "low")
                {
                    continue;   // düşük güvenli ve hiç bağlanılamıyor: sunucu değil
                }
                kept.Add(e);
            }
            return kept;
        }

        // ------------------------------------------------------------------ Monitor ile iletişim

        private readonly ResourceSampler _resources = new ResourceSampler();
        private string? _primaryAddress;
        private DateTime _primaryAddressAtUtc;

        // Pod'un asıl IP adresi; ağ yapılandırması nadiren değiştiği için 5 dakikada bir yeniden hesaplanır.
        private string? PrimaryAddress(IList<string> localAddresses)
        {
            if (_primaryAddress == null || DateTime.UtcNow - _primaryAddressAtUtc > TimeSpan.FromMinutes(5))
            {
                var uri = new Uri(_options.MonitorUrl);
                _primaryAddress = PodIdentityBuilder.ResolvePrimaryAddress(uri.DnsSafeHost, uri.Port, localAddresses);
                _primaryAddressAtUtc = DateTime.UtcNow;
            }
            return _primaryAddress;
        }

        private async Task<Assignment> ReportAsync(AgentRun? run, CancellationToken ct)
        {
            var identity = PodIdentityBuilder.Build(_app, _options.AppName);
            identity.PrimaryAddress = PrimaryAddress(identity.LocalAddresses);
            identity.HealthPort = _health?.Port;
            var report = new AgentReport
            {
                AppName = identity.AppName,
                Pod = identity,
                PollSeconds = Math.Max(1, _options.PollSeconds),
                Run = run,
                Resources = _resources.Sample(),
                Settings = Volatile.Read(ref _settings),
                Groups = _options.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name) && g.Keywords.Any(k => !string.IsNullOrWhiteSpace(k))).ToList()
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
            /// <summary>Ayarlarda bulunan adreslerin türü sunucuya bağlanıp doğrulansın mı (Monitor'de uygulama bazında).</summary>
            public bool VerifySettings { get; set; }
            public List<AssignedConnection> Connections { get; set; } = new List<AssignedConnection>();

            public static Assignment Parse(string json)
            {
                var o = Json.Parse(json) as IDictionary<string, object?> ?? throw new FormatException("Monitor response is not a JSON object");
                var a = new Assignment
                {
                    AppId = Json.GetString(o, "appId"),
                    RunRequestId = Json.GetString(o, "runRequestId") ?? "",
                    VerifySettings = Json.GetBool(o, "verifySettings"),
                    Connections = Json.GetObjectList(o, "connections").Select(c => new AssignedConnection
                    {
                        Id = Json.GetString(c, "id") ?? "",
                        Host = Json.GetString(c, "host") ?? "",
                        Port = Json.GetDouble(c, "port") is double p ? (int)p : (int?)null,
                        Tls = Json.GetBool(c, "tls"),
                        Health = Json.GetObjectList(c, "health").Select(h => new HealthTarget
                        {
                            Host = Json.GetString(h, "host") ?? "",
                            Port = (int)Json.GetLong(h, "port"),
                            Instance = Json.GetString(h, "instance")
                        }).Where(h => h.Host.Length > 0 && h.Port > 0 && h.Port <= 65535).ToList()
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
            /// <summary>TCP'ye ek olarak TLS el sıkışması ve sertifika kontrolü yapılsın mı (Monitor belirler).</summary>
            public bool Tls { get; set; }
            /// <summary>Health'i sorgulanacak adresler (Monitor belirler: hedef servis ya da hedef uygulamanın pod'ları).</summary>
            public List<HealthTarget> Health { get; set; } = new List<HealthTarget>();
        }

        internal sealed class HealthTarget
        {
            public string Host { get; set; } = "";
            public int Port { get; set; }
            public string? Instance { get; set; }
        }
    }
}
