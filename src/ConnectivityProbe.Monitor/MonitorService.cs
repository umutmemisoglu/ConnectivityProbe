using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Belirli aralıklarla kayıtlı her uygulamayı kontrol eder: kaç pod çalıştığını sayar ve ilişkilendirilmiş her bağlantıyı
/// (uygulamanın kendi içinden, her pod için) test ettirir. Sonuçları bellekte tutar; arayüz buradan okur.
/// </summary>
public sealed class MonitorService : BackgroundService
{
    /// <summary>Bir uygulamanın döngüler arasında hatırlanan durumu (görülen pod'lar, son sonuçlar, geçmiş).</summary>
    private sealed class AppRuntime
    {
        public readonly Dictionary<string, PodStatus> Known = new();
        public readonly Dictionary<string, int> Missed = new();                 // pod -> üst üste kaç tur görünmedi
        public readonly Dictionary<string, PodConnectionCell> Results = new();   // anahtar: bağlantıId|podId
        public readonly List<HistoryPoint> History = new();
        /// <summary>Bağlantı -> hedefinde görülen pod'lar (hangi hedef pod'a ne zamandır erişilemediğini bilmek için).</summary>
        public readonly Dictionary<string, TargetMemory> Targets = new();
        /// <summary>Bilinen pod'ların hepsinin görüldüğü son turdaki pod sayısı (deploy ile "pod çöktü"yü ayırmak için).</summary>
        public int ExpectedPods;
        /// <summary>Strict mod: pod -> bildirim sıklığı (sn); pod'un canlı sayılıp sayılmayacağı buna göre değerlendirilir.</summary>
        public readonly Dictionary<string, int> PollSeconds = new();
    }

    /// <summary>Monitor'ün başladığı an: yeniden başladıktan hemen sonra pod'lar bildirim gönderene kadar "eksik" sayılmasın diye.</summary>
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>"Şimdi test et"e her basıldığında artar; Strict pod'lar değiştiğini görünce beklemeden test eder.</summary>
    private long _runRequestId = DateTime.UtcNow.Ticks;

    private readonly DefinitionStore _store;
    private readonly PodStateStore _podState;
    private readonly MonitorOptions _opt;
    private readonly ILogger<MonitorService> _log;
    private readonly ConcurrentDictionary<string, AppRuntime> _runtime = new();
    private readonly ConcurrentDictionary<string, AppStatus> _latest = new();
    private readonly SemaphoreSlim _trigger = new(0, 1);

    private DateTime? _lastRunUtc;
    private DateTime? _nextRunUtc;
    private volatile bool _running;

    public MonitorService(DefinitionStore store, PodStateStore podState, IOptions<MonitorOptions> options, ILogger<MonitorService> log)
    {
        _store = store;
        _podState = podState;
        _opt = options.Value;
        _log = log;

        // Daha önce görülen pod'ları (eksik olanlar dahil) diskten geri yüklüyoruz; Monitor yeniden başlasa bile kaybolmasınlar.
        foreach (var (appId, saved) in _podState.Load())
        {
            var rt = new AppRuntime { ExpectedPods = saved.ExpectedPods };
            foreach (var pod in saved.Known) rt.Known[pod.InstanceId] = pod;
            foreach (var kv in saved.Missed) rt.Missed[kv.Key] = kv.Value;
            foreach (var kv in saved.Targets ?? new()) rt.Targets[kv.Key] = kv.Value;
            _runtime[appId] = rt;
        }
    }

    /// <summary>
    /// Uygulamanın hatırlanan durumunu (görülen pod'lar, eksik pod'lar, bağlantı sonuçları, geçmiş) siler; mevcut pod'lar bir
    /// sonraki turda yeniden keşfedilir. Arayüzdeki "Pod listesini sıfırla" butonu ve uygulamanın adresi değiştiğinde çağrılır
    /// (yeni adres başka bir uygulamadır; eski adresin pod'ları "eksik" diye yanlış alarm vermesin).
    /// </summary>
    public void ForgetApp(string appId)
    {
        _runtime.TryRemove(appId, out _);
        _latest.TryRemove(appId, out _);
        SavePodState();
    }

    // Her uygulamanın bilinen pod'larını diske yazar. Bir tur sürerken (sıfırlama isteği gibi) çağrılırsa ve o an bir uygulamanın
    // listesi güncelleniyorsa kaydı atlıyoruz; tur bitince zaten yeniden kaydediliyor.
    private void SavePodState()
    {
        try
        {
            var state = new Dictionary<string, PersistedAppPods>();
            foreach (var (appId, rt) in _runtime)
            {
                lock (rt)
                {
                    state[appId] = new PersistedAppPods
                    {
                        Known = rt.Known.Values.ToList(),
                        Missed = new Dictionary<string, int>(rt.Missed),
                        ExpectedPods = rt.ExpectedPods,
                        // Kilit dışında diske yazılırken değişmesin diye kopyasını alıyoruz.
                        Targets = JsonSerializer.Deserialize<Dictionary<string, TargetMemory>>(JsonSerializer.Serialize(rt.Targets)) ?? new()
                    };
                }
            }
            _podState.Save(state);
        }
        catch (InvalidOperationException)
        {
            // Liste o an değişiyordu; bir sonraki kayıtta yazılacak.
        }
    }

    /// <summary>Bir sonraki aralığı beklemeden hemen yeni bir döngü başlatır (tanım değişince veya "Şimdi test et" ile).</summary>
    public void Trigger()
    {
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { /* zaten bir tetikleme bekliyor */ }
    }

    /// <summary>Arayüzün gösterdiği güncel durum: tanımlı her uygulama için son sonuç (henüz test edilmediyse "unknown").</summary>
    public MonitorSnapshot GetSnapshot()
    {
        var defs = _store.Snapshot();
        return new MonitorSnapshot
        {
            IntervalSeconds = _opt.IntervalSeconds,
            MissingAfterCycles = Math.Max(1, _opt.MissingAfterCycles),
            LastRunUtc = _lastRunUtc,
            NextRunUtc = _nextRunUtc,
            Running = _running,
            Apps = defs.Apps.Select(a => _latest.TryGetValue(a.Id, out var s)
                ? s
                : new AppStatus
                {
                    AppId = a.Id, Name = a.Name, BaseUrl = a.BaseUrl, Mode = a.Mode,
                    Message = a.Mode == AppModes.Strict ? NoReportMessage : "Henüz test edilmedi"
                }).ToList()
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            // step 1: Tüm uygulamaları bir kez kontrol ediyoruz.
            try { await RunCycleAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Monitor cycle failed"); }

            // step 2: Aralık dolana veya Trigger() çağrılana kadar bekliyoruz.
            _nextRunUtc = DateTime.UtcNow + interval;
            try { await _trigger.WaitAsync(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        _running = true;
        try
        {
            var defs = _store.Snapshot();

            // Silinmiş uygulamaların durumunu temizliyoruz.
            var ids = defs.Apps.Select(a => a.Id).ToHashSet();
            foreach (var key in _runtime.Keys.Where(k => !ids.Contains(k))) _runtime.TryRemove(key, out _);
            foreach (var key in _latest.Keys.Where(k => !ids.Contains(k))) _latest.TryRemove(key, out _);

            // Uygulamaları sınırlı paralellikte kontrol ediyoruz.
            using var gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency));
            await Task.WhenAll(defs.Apps.Select(async app =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var status = app.Mode == AppModes.Strict
                        ? await CheckStrictAppAsync(app, defs, ct).ConfigureAwait(false)
                        : await CheckAppAsync(app, defs, ct).ConfigureAwait(false);
                    status.Mode = app.Mode;
                    // Kontrol sürerken uygulama silindiyse sonucunu yazmıyoruz.
                    if (_store.Snapshot().Apps.Any(a => a.Id == app.Id)) _latest[app.Id] = status;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Check failed for {App}", app.Name);
                    _latest[app.Id] = new AppStatus
                    {
                        AppId = app.Id, Name = app.Name, BaseUrl = app.BaseUrl, Mode = app.Mode, State = "down",
                        Message = "Kontrol hatası: " + ex.Message, CheckedAtUtc = DateTime.UtcNow
                    };
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);

            _lastRunUtc = DateTime.UtcNow;
            SavePodState();
        }
        finally { _running = false; }
    }

    private async Task<AppStatus> CheckAppAsync(AppDefinition app, DefinitionData defs, CancellationToken ct)
    {
        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var cycleStart = DateTime.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(500, _opt.ProbeTimeoutMs));
        var threshold = Math.Max(1, _opt.MissingAfterCycles);
        var status = new AppStatus { AppId = app.Id, Name = app.Name, BaseUrl = app.BaseUrl, CheckedAtUtc = cycleStart };

        if (!Uri.TryCreate(app.BaseUrl, UriKind.Absolute, out var uri))
        {
            status.State = "down";
            status.Message = "Geçersiz uygulama URL'si";
            return status;
        }

        // step 1: Her durumda önce telnet (TCP): uygulamanın portu açık değilse identity'ye hiç HTTP isteği atmıyoruz ve
        //         "erişilemiyor" nedenini (timeout = firewall, reddedildi = servis kapalı, DNS...) doğrudan gösteriyoruz.
        var identityPath = uri.AbsolutePath.TrimEnd('/') + app.ProbePath.TrimEnd('/') + "/identity";
        var budget = Math.Max(1, _opt.MaxInstanceAttempts);
        var tcp = await TcpProbe.ProbeAsync(uri.IdnHost, uri.Port, timeout, ct).ConfigureAwait(false);

        // step 1b: Port açıksa kaç pod çalıştığını buluyoruz. Uygulamanın identity ucuna her seferinde yeni bağlantıyla istek
        //          atılır, dönen instanceId'ler gruplanır; yeni pod çıkmayı kesince (adaptive) durulur.
        DiscoverReport instances;
        if (!tcp.Success)
        {
            instances = new DiscoverReport();
            instances.Errors["telnet (TCP " + uri.IdnHost + ":" + uri.Port + ") başarısız: " + tcp.Error] = 1;
        }
        else
        {
            instances = await InstanceCollector.CollectAsync(
                uri.Scheme, uri.IdnHost, uri.Port, identityPath, budget, timeout, TimeSpan.Zero,
                adaptive: true, confidence: _opt.InstanceConfidence, accessKey: _opt.AccessKey, cancellationToken: ct).ConfigureAwait(false);
        }

        var summaries = instances.Instances.ToDictionary(s => s.InstanceId);
        var succeeded = instances.Succeeded;

        // step 1b: Daha önce bilinen bir pod bu turda görünmediyse, alarm vermeden önce kalan bütçeyle onu özellikle arıyoruz.
        //          Adaptive durma kuralı olasılıksal olduğundan (her turda ~%5 kaçırma ihtimali), bu ek tur yanlış
        //          "pod görünmüyor" uyarılarını büyük ölçüde ortadan kaldırır.
        var remaining = budget - instances.AttemptsMade;
        if (succeeded > 0 && remaining > 0 && rt.Known.Keys.Any(id => !summaries.ContainsKey(id)))
        {
            var extra = await InstanceCollector.CollectAsync(
                uri.Scheme, uri.IdnHost, uri.Port, identityPath, Math.Min(remaining, Math.Max(10, rt.Known.Count * 3)),
                timeout, TimeSpan.Zero, adaptive: false, accessKey: _opt.AccessKey, cancellationToken: ct).ConfigureAwait(false);

            succeeded += extra.Succeeded;
            foreach (var s in extra.Instances)
            {
                if (summaries.TryGetValue(s.InstanceId, out var existing)) existing.Hits += s.Hits;
                else summaries[s.InstanceId] = s;
            }
        }

        // step 2: Görülen pod'ları hatırlanan listeye işliyoruz.
        var seen = summaries.Keys.ToHashSet();
        foreach (var s in summaries.Values)
        {
            var details = new Dictionary<string, string>(s.Identity.Environment);
            foreach (var kv in s.Identity.Info) details[kv.Key] = kv.Value;
            rt.Known[s.InstanceId] = new PodStatus
            {
                InstanceId = s.InstanceId, MachineName = s.Identity.MachineName, Addresses = s.Identity.LocalAddresses,
                StartedAtUtc = s.Identity.StartedAtUtc, Details = details, LastSeenUtc = cycleStart, ProbeVersion = s.Identity.ProbeVersion
            };
            rt.Missed[s.InstanceId] = 0;
        }

        // step 3: Görünmeyen pod'ların sayacını artırıyoruz ve eşiğe TAM BU TURDA ulaşanlara karar veriyoruz:
        //         - Pod sayısı korunduysa (eskilerin yerine yenileri geldiyse: deploy, restart) eskileri alarmsız düşürüyoruz.
        //         - Sayı azaldıysa pod gerçekten eksiktir ve "eksik" olur. Eksik bir pod ASLA kendiliğinden silinmez (sonradan
        //           yerine yeni pod gelse bile); yalnızca "Pod listesini sıfırla" ile silinir. Tekrar cevap verirse normale döner.
        foreach (var id in rt.Known.Keys.Where(id => !seen.Contains(id)))
            rt.Missed[id] = rt.Missed.TryGetValue(id, out var m) ? m + 1 : 1;

        var justCrossed = rt.Known.Keys.Where(id => rt.Missed[id] == threshold).ToList();
        if (justCrossed.Count > 0 && seen.Count > 0 && seen.Count >= rt.ExpectedPods)
            foreach (var id in justCrossed) Forget(rt, id);

        // Bilinen pod'ların hepsi görüldüyse bu, uygulamanın "normal" pod sayısıdır.
        if (seen.Count > 0 && rt.Known.Keys.All(seen.Contains)) rt.ExpectedPods = seen.Count;

        // Artık ilişkili olmayan bağlantıların ve bilinmeyen pod'ların eski sonuçlarını temizliyoruz.
        lock (rt) CleanupResults(rt, app);

        status.PodCount = seen.Count;
        status.Converged = instances.Converged;
        status.Confidence = instances.Confidence;
        status.Pods = rt.Known.Values
            .Select(p =>
            {
                var missed = rt.Missed.TryGetValue(p.InstanceId, out var m) ? m : 0;
                return new PodStatus
                {
                    InstanceId = p.InstanceId, MachineName = p.MachineName, Addresses = p.Addresses, StartedAtUtc = p.StartedAtUtc,
                    Details = p.Details, LastSeenUtc = p.LastSeenUtc, ProbeVersion = p.ProbeVersion, Seen = seen.Contains(p.InstanceId), MissedCycles = missed,
                    State = seen.Contains(p.InstanceId) ? "up" : missed >= threshold ? "missing" : "unconfirmed"
                };
            })
            .OrderBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.InstanceId, StringComparer.Ordinal).ToList();

        // step 4: Hiç pod cevap vermediyse uygulama kapalıdır; bağlantı testi yapılamaz.
        if (succeeded == 0)
        {
            status.State = "down";
            status.Message = "Uygulamaya ulaşılamadı: " + (instances.Errors.Keys.FirstOrDefault() ?? "bilinmeyen hata");
            AddHistory(rt, status);
            status.History = rt.History.ToList();
            return status;
        }

        // Ara sonuç: pod sayısı belli oldu ama bağlantı testleri sürüyor. Arayüz uzun süre eski sonucu göstermesin diye
        // yeni pod bilgisini hemen yayınlıyoruz; bağlantı sonuçları bu turun bitmesine kadar önceki turdan kalır.
        PublishInterim(app, status, rt, seen);

        // step 5: İlişkilendirilmiş her bağlantıyı test ettiriyoruz (bağlantılar birbirine paralel).
        var connections = app.ConnectionIds
            .Select(id => defs.Connections.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null).Select(c => c!).ToList();

        //         Aynı anda en fazla MaxConcurrentConnections bağlantı test edilir; uygulamanın eşzamanlı discover sınırını
        //         (ConnectivityProbe:MaxConcurrentDiscover) zorlamamak ve pod'lara ani yük bindirmemek için.
        using var connGate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrentConnections));
        status.Connections = (await Task.WhenAll(connections.Select(async c =>
        {
            await connGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await CheckConnectionAsync(app, c, rt, seen, cycleStart, timeout, ct).ConfigureAwait(false); }
            finally { connGate.Release(); }
        })).ConfigureAwait(false)).ToList();

        // step 6: Genel durumu belirliyoruz: eksik pod veya başarısız bağlantı varsa "degraded", yoksa "healthy".
        var problems = new List<string>();
        var missing = status.Pods.Count(p => p.State == "missing");
        if (missing > 0) problems.Add($"{missing} pod eksik (üst üste {threshold}+ tur görünmedi)");
        AddConnectionProblems(status, problems);

        var notes = new List<string>();
        var unconfirmed = status.Pods.Count(p => p.State == "unconfirmed");
        if (unconfirmed > 0) notes.Add($"{unconfirmed} pod bu turda görülmedi");
        if (instances.Converged == false) notes.Add("pod sayısı kesin değil (daha fazla istek gerekebilir)");

        status.State = problems.Count > 0 ? "degraded" : "healthy";
        status.Message = problems.Count + notes.Count == 0 ? null : string.Join("; ", problems.Concat(notes));

        AddHistory(rt, status);
        status.History = rt.History.ToList();
        return status;
    }

    // Pod'u tüm hatırlanan listelerden siler (yerine yenisi geldi veya saklama süresi doldu).
    private static void Forget(AppRuntime rt, string podId)
    {
        rt.Known.Remove(podId);
        rt.Missed.Remove(podId);
        rt.PollSeconds.Remove(podId);
    }

    // Artık ilişkili olmayan bağlantıların ve bilinmeyen pod'ların eski sonuçlarını siler. Çağıran rt üzerinde kilit tutmalıdır.
    private static void CleanupResults(AppRuntime rt, AppDefinition app)
    {
        var attached = app.ConnectionIds.ToHashSet();
        foreach (var key in rt.Results.Keys.ToList())
        {
            var sep = key.IndexOf('|');
            if (!attached.Contains(key[..sep]) || !rt.Known.ContainsKey(key[(sep + 1)..])) rt.Results.Remove(key);
        }
        foreach (var connId in rt.Targets.Keys.Where(k => !attached.Contains(k)).ToList()) rt.Targets.Remove(connId);
        foreach (var target in rt.Targets.Values.SelectMany(t => t.Pods.Values))
            foreach (var podId in target.By.Keys.Where(k => !rt.Known.ContainsKey(k)).ToList()) target.By.Remove(podId);
    }

    // Bağlantı sonuçlarındaki sorunları (başarısız test, test edilemeyen bağlantı, erişilemeyen hedef pod'u) özetler.
    private static void AddConnectionProblems(AppStatus status, List<string> problems)
    {
        var failedCells = status.Connections.Sum(c => c.Cells.Count(x => x.Fresh && !x.Success));
        if (failedCells > 0) problems.Add($"{failedCells} bağlantı testi başarısız");
        var callErrors = status.Connections.Count(c => c.CallError != null);
        if (callErrors > 0) problems.Add($"{callErrors} bağlantı test edilemedi");
        // CP hedefinin bir pod'una üst üste birkaç tur erişilemediyse (hedef pod'u çöktü veya bu pod'dan ona yol yok) bu da sorundur.
        foreach (var c in status.Connections)
        {
            var names = c.Cells.Where(x => x.Fresh).SelectMany(x => x.UnreachedTargets ?? new()).Where(u => u.Confirmed)
                .Select(u => u.PodName ?? u.MachineName).Distinct().ToList();
            if (names.Count > 0) problems.Add($"{c.Name}: hedefin {string.Join(", ", names)} pod'una erişilemiyor");
        }
    }

    // =============================================================================================
    // Strict mod: her pod uygulama anahtarıyla kendini bildirir, testleri kendi içinde yapıp sonuçları gönderir.
    // Pod sayısı = bildirim gönderen pod'lar (kesin); "eksik" kuralları Discover ile aynıdır.
    // =============================================================================================

    private const string NoReportMessage =
        "Henüz hiçbir pod bildirim göndermedi. Uygulamada ConnectivityProbe 1.1+ yüklü ve ConnectivityProbe:MonitorUrl / ConnectivityProbe:AppKey tanımlı mı?";

    /// <summary>"Şimdi test et"e her basıldığında değişen kimlik; Strict pod'lar değiştiğini görünce beklemeden test eder.</summary>
    public string RunRequestId => Interlocked.Read(ref _runRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Şimdi test et": Discover uygulamaları için hemen tur başlatır, Strict pod'lara da beklemeden test etmelerini bildirir.</summary>
    public void RequestRun()
    {
        Interlocked.Increment(ref _runRequestId);
        Trigger();
    }

    /// <summary>
    /// Strict pod'un bildirimini işler: pod'u canlı olarak işaretler, test sonuçları varsa pod'un hücrelerine yazar ve pod'a
    /// uygulamanın güncel bağlantı tanımlarını döner.
    /// </summary>
    public AgentAssignment AcceptAgentReport(AppDefinition app, DefinitionData defs, AgentReport report)
    {
        var now = DateTime.UtcNow;
        var identity = report.Identity;
        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var connections = app.ConnectionIds
            .Select(id => defs.Connections.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null).Select(c => c!).ToList();

        bool isNew;
        lock (rt)
        {
            // step 1: Pod'u canlı olarak kaydediyoruz (yeni bir pod ise listeye girer).
            isNew = !rt.Known.ContainsKey(identity.InstanceId);
            var details = new Dictionary<string, string>(identity.Environment);
            foreach (var kv in identity.Info) details[kv.Key] = kv.Value;
            rt.Known[identity.InstanceId] = new PodStatus
            {
                InstanceId = identity.InstanceId, MachineName = identity.MachineName, Addresses = identity.LocalAddresses,
                StartedAtUtc = identity.StartedAtUtc, Details = details, LastSeenUtc = now, ProbeVersion = identity.ProbeVersion
            };
            rt.Missed[identity.InstanceId] = 0;
            rt.PollSeconds[identity.InstanceId] = Math.Clamp(report.PollSeconds, 1, 300);

            // step 2: Test sonuçları geldiyse pod'un hücrelerine yazıyoruz (Discover moddaki hücrelerle aynı biçim).
            foreach (var result in report.Run?.Results ?? new List<AgentResult>())
            {
                var conn = connections.FirstOrDefault(c => c.Id == result.ConnectionId);
                if (conn == null) continue; // tanım bu arada uygulamadan çıkarılmış

                var cell = result.Report?.Tcp != null
                    ? CellFromReport(conn, result.Report, identity.InstanceId) with { CheckedAtUtc = now }
                    : new PodConnectionCell { InstanceId = identity.InstanceId, Error = result.Error ?? "Test sonucu yok", CheckedAtUtc = now };
                cell = StoreCell(rt, conn.Id, cell);

                // CP hedefi: hangi hedef pod'una ne zamandır erişilemediğini güncelliyoruz.
                if (conn.UsesConnectivityProbe && cell.Success && cell.TargetKind == ProbeTargetKind.ConnectivityProbe && cell.TargetPods != null)
                    foreach (var updated in UpdateTargets(rt, conn.Id, new List<PodConnectionCell> { cell }, now))
                        rt.Results[conn.Id + "|" + updated.InstanceId] = updated;
            }
        }

        if (isNew)
        {
            _log.LogInformation("Strict pod {Pod} ({Machine}) joined app {App}", identity.InstanceId, identity.MachineName, app.Name);
            SavePodState();
        }

        return new AgentAssignment(
            app.Id, app.Name, Math.Max(5, _opt.IntervalSeconds), RunRequestId, Math.Max(500, _opt.ProbeTimeoutMs),
            Math.Max(1, _opt.MaxInstanceAttempts), _opt.InstanceConfidence,
            connections.Select(c => new AgentConnection(c.Id, c.Name, c.Host, c.Port, c.UsesConnectivityProbe)).ToList());
    }

    /// <summary>Strict pod düzgün kapanıyor: alarm vermeden listeden çıkarılır (deploy, scale-down).</summary>
    public bool AgentGoodbye(string appId, string instanceId)
    {
        if (!_runtime.TryGetValue(appId, out var rt)) return false;
        lock (rt)
        {
            if (!rt.Known.ContainsKey(instanceId)) return false;
            Forget(rt, instanceId);
            foreach (var key in rt.Results.Keys.Where(k => k.EndsWith("|" + instanceId, StringComparison.Ordinal)).ToList()) rt.Results.Remove(key);
            foreach (var target in rt.Targets.Values.SelectMany(t => t.Pods.Values)) target.By.Remove(instanceId);
        }
        _log.LogInformation("Strict pod {Pod} left app {App}", instanceId, appId);
        SavePodState();
        return true;
    }

    /// <summary>
    /// Strict uygulamanın durumunu pod bildirimlerinden hesaplar. Uygulamanın adresi verildiyse ayrıca Monitor'den dışarıdan
    /// erişim (TCP) kontrol edilir.
    /// </summary>
    private async Task<AppStatus> CheckStrictAppAsync(AppDefinition app, DefinitionData defs, CancellationToken ct)
    {
        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds));
        var threshold = Math.Max(1, _opt.MissingAfterCycles);
        var status = new AppStatus { AppId = app.Id, Name = app.Name, BaseUrl = app.BaseUrl, Mode = AppModes.Strict, CheckedAtUtc = now };

        // step 1: Adres verildiyse Monitor'den dışarıdan erişim kontrolü (yalnızca telnet; pod sayısı bildirimlerden gelir).
        string? urlProblem = null;
        if (!string.IsNullOrWhiteSpace(app.BaseUrl) && Uri.TryCreate(app.BaseUrl, UriKind.Absolute, out var uri))
        {
            var tcp = await TcpProbe.ProbeAsync(uri.IdnHost, uri.Port, TimeSpan.FromMilliseconds(Math.Max(500, _opt.ProbeTimeoutMs)), ct).ConfigureAwait(false);
            if (!tcp.Success) urlProblem = $"Uygulamanın adresine Monitor'den erişilemiyor ({uri.IdnHost}:{uri.Port}): {tcp.Error}";
        }

        var connections = app.ConnectionIds
            .Select(id => defs.Connections.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null).Select(c => c!).ToList();
        int up;
        DateTime? lastReport;

        lock (rt)
        {
            // step 2: Pod durumları bildirimin yaşına göre:
            //   up          -> son bildirim, pod'un bildirim aralığının iki katından yeni
            //   unconfirmed -> bildirim gecikti ama henüz eşik dolmadı
            //   missing     -> üst üste MissingAfterCycles test aralığı boyunca bildirim yok (alarm)
            // Monitor yeni başladıysa eski bildirim zamanları cezalandırılmaz (pod'lara bildirim için süre tanınır).
            var states = new Dictionary<string, string>();
            var justCrossed = new List<string>();
            foreach (var p in rt.Known.Values)
            {
                var poll = rt.PollSeconds.TryGetValue(p.InstanceId, out var s) ? s : 10;
                var age = now - (p.LastSeenUtc > _startedUtc ? p.LastSeenUtc : _startedUtc);
                var isUp = age <= TimeSpan.FromSeconds(poll * 2 + 5);
                var missed = isUp ? 0 : Math.Max(1, (int)(age.TotalSeconds / interval.TotalSeconds));
                var previous = rt.Missed.TryGetValue(p.InstanceId, out var m) ? m : 0;
                rt.Missed[p.InstanceId] = missed;
                if (missed >= threshold && previous < threshold) justCrossed.Add(p.InstanceId);
                states[p.InstanceId] = isUp ? "up" : missed >= threshold ? "missing" : "unconfirmed";
            }
            up = states.Count(kv => kv.Value == "up");

            // step 3: Eşiği bu turda geçen pod'lar: pod sayısı korunduysa (yerine yenisi geldi, kapanış bildirimi gelmeden) alarmsız
            //         düşürüyoruz; azaldıysa "eksik" kalır ve yalnızca "Pod listesini sıfırla" ile silinir (Discover'la aynı kural).
            if (justCrossed.Count > 0 && up > 0 && up >= rt.ExpectedPods)
                foreach (var id in justCrossed) { Forget(rt, id); states.Remove(id); }
            if (up > 0 && states.Values.All(s => s == "up")) rt.ExpectedPods = up;

            CleanupResults(rt, app);
            lastReport = rt.Known.Count > 0 ? rt.Known.Values.Max(p => p.LastSeenUtc) : null;

            status.Pods = rt.Known.Values
                .Select(p => new PodStatus
                {
                    InstanceId = p.InstanceId, MachineName = p.MachineName, Addresses = p.Addresses, StartedAtUtc = p.StartedAtUtc,
                    Details = p.Details, LastSeenUtc = p.LastSeenUtc, ProbeVersion = p.ProbeVersion,
                    Seen = states[p.InstanceId] == "up", MissedCycles = rt.Missed.TryGetValue(p.InstanceId, out var m) ? m : 0,
                    State = states[p.InstanceId]
                })
                .OrderBy(p => p.Details.TryGetValue("POD_NAME", out var n) ? n : p.MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.InstanceId, StringComparer.Ordinal).ToList();

            // step 4: Bağlantı sonuçları: canlı pod'un son test turundan gelenler "taze", diğerleri son bilinen sonuç (soluk).
            var freshAfter = now - interval - interval;
            status.Connections = connections.Select(c => new ConnectionStatus
            {
                ConnectionId = c.Id, Name = c.Name, Target = c.Host + (c.Port.HasValue ? ":" + c.Port : ""),
                Cells = rt.Known.Keys
                    .Where(pod => rt.Results.ContainsKey(c.Id + "|" + pod))
                    .Select(pod =>
                    {
                        var cell = rt.Results[c.Id + "|" + pod];
                        return cell with { Fresh = states[pod] == "up" && cell.CheckedAtUtc >= freshAfter };
                    })
                    .ToList()
            }).ToList();
        }

        status.PodCount = up;
        status.Converged = true; // pod'lar kendini bildirdiği için sayı kesindir

        // step 5: Genel durum.
        if (rt.Known.Count == 0)
        {
            status.State = urlProblem != null ? "down" : "unknown";
            status.Message = NoReportMessage + (urlProblem != null ? "; " + urlProblem : "");
        }
        else if (up == 0)
        {
            status.State = "down";
            status.Message = $"Hiçbir pod bildirim göndermiyor (son bildirim: {lastReport:HH:mm:ss} UTC)" + (urlProblem != null ? "; " + urlProblem : "");
        }
        else
        {
            var problems = new List<string>();
            var missing = status.Pods.Count(p => p.State == "missing");
            if (missing > 0) problems.Add($"{missing} pod eksik (üst üste {threshold}+ tur bildirim göndermedi)");
            if (urlProblem != null) problems.Add(urlProblem);
            AddConnectionProblems(status, problems);

            var notes = new List<string>();
            var late = status.Pods.Count(p => p.State == "unconfirmed");
            if (late > 0) notes.Add($"{late} pod'un bildirimi gecikti");
            if (connections.Count > 0 && status.Connections.All(c => c.Cells.Count == 0)) notes.Add("ilk test sonuçları bekleniyor");

            status.State = problems.Count > 0 ? "degraded" : "healthy";
            status.Message = problems.Count + notes.Count == 0 ? null : string.Join("; ", problems.Concat(notes));
        }

        AddHistory(rt, status);
        status.History = rt.History.ToList();
        return status;
    }

    private void PublishInterim(AppDefinition app, AppStatus status, AppRuntime rt, HashSet<string> seen)
    {
        _latest.TryGetValue(app.Id, out var prev);
        _latest[app.Id] = new AppStatus
        {
            AppId = app.Id, Name = app.Name, BaseUrl = app.BaseUrl, Mode = app.Mode, CheckedAtUtc = status.CheckedAtUtc,
            State = prev?.State ?? "unknown", Message = "Bağlantılar test ediliyor…",
            PodCount = status.PodCount, Converged = status.Converged, Confidence = status.Confidence,
            Pods = status.Pods, History = rt.History.ToList(),
            // Önceki turun sonuçlarını koruyoruz ama artık cevap vermeyen pod'ların hücreleri "eski" (soluk) görünsün.
            Connections = (prev?.Connections ?? new List<ConnectionStatus>()).Select(c => new ConnectionStatus
            {
                ConnectionId = c.ConnectionId, Name = c.Name, Target = c.Target, CallError = c.CallError,
                Cells = c.Cells.Select(x => x with { Fresh = x.Fresh && seen.Contains(x.InstanceId) }).ToList()
            }).ToList()
        };
    }

    private async Task<ConnectionStatus> CheckConnectionAsync(
        AppDefinition app, ConnectionDefinition conn, AppRuntime rt, HashSet<string> seen,
        DateTime cycleStart, TimeSpan timeout, CancellationToken ct)
    {
        var result = new ConnectionStatus
        {
            ConnectionId = conn.Id, Name = conn.Name, Target = conn.Host + (conn.Port.HasValue ? ":" + conn.Port : "")
        };

        // step 1: Her pod'un bu bağlantıyı test ettiğinden emin olmak için, tüm pod'lardan sonuç gelene kadar probe isteği atıyoruz.
        //         Hangi pod'a düşeceğimiz rastgele olduğundan, istek sayısı pod sayısının katı kadardır.
        //         İstekler paralel gruplar halinde atılır; yavaş/zaman aşımına giren bir hedefte bile süre, istek sayısıyla
        //         değil grup sayısıyla artar.
        var got = new HashSet<string>();
        var maxCalls = Math.Clamp(seen.Count * 3, 3, Math.Max(3, _opt.MaxProbeCallsPerConnection));
        var batchSize = Math.Clamp(seen.Count, 2, 10);
        var made = 0;
        var stop = false;

        while (!stop && made < maxCalls && !seen.All(got.Contains))
        {
            var batch = Math.Min(batchSize, maxCalls - made);
            made += batch;

            var replies = await Task.WhenAll(Enumerable.Range(0, batch).Select(_ => CallOnceAsync(app, conn, timeout, ct))).ConfigureAwait(false);

            foreach (var (cell, error) in replies)
            {
                if (cell == null)
                {
                    result.CallError = error;
                    // 4xx hatalar (geçersiz hedef, izin listesinde değil, anahtar yanlış) ve eski sürüm tekrar denemekle düzelmez.
                    //    429 (uygulamanın eşzamanlı discover sınırı doldu) geçicidir; bir sonraki grupta tekrar denenir.
                    if (error != null && ((error.StartsWith("HTTP 4") && !error.StartsWith("HTTP 429")) || error == OldVersionError)) stop = true;
                    continue;
                }

                // step 2: Sonucu, testi yapan pod'un hücresine yazıyoruz.
                lock (rt) StoreCell(rt, conn.Id, cell);
                got.Add(cell.InstanceId);
            }

            // En az bir istek başarılı döndüyse önceki (geçici) hata artık geçerli değil.
            if (got.Count > 0 && !stop) result.CallError = null;
        }

        // step 3: Bilinen her pod için son sonucu hazırlıyoruz; bu döngüde test edilmediyse "Fresh = false" işaretliyoruz.
        lock (rt)
        {
            // CP hedefi: bu turda keşif yapan pod'ların sonuçlarıyla hedef pod hafızasını güncelleyip, her pod'un hangi hedef
            // pod'larına ne zamandır erişemediğini hücresine yazıyoruz.
            if (conn.UsesConnectivityProbe)
            {
                var samples = got
                    .Select(id => rt.Results[conn.Id + "|" + id])
                    .Where(c => c.Success && c.TargetKind == ProbeTargetKind.ConnectivityProbe && c.TargetPods != null)
                    .ToList();
                foreach (var updated in UpdateTargets(rt, conn.Id, samples, cycleStart))
                    rt.Results[conn.Id + "|" + updated.InstanceId] = updated;
            }

            foreach (var pod in rt.Known.Keys)
            {
                if (rt.Results.TryGetValue(conn.Id + "|" + pod, out var cell))
                    result.Cells.Add(cell with { Fresh = cell.CheckedAtUtc >= cycleStart });
            }
        }

        return result;
    }

    private const string OldVersionError =
        "Uygulamadaki ConnectivityProbe sürümü eski (discover ucu yok); kütüphaneyi güncelleyin";

    /// <summary>
    /// Uygulamaya bağlantı için tek bir test yaptırır (/connectivity-probe/discover) ve sonucu, testi yapan pod'un hücresine çevirir.
    /// <list type="bullet">
    /// <item>Hedef ConnectivityProbe kullanmıyorsa (DB, Redis, dış API...): uygulama yalnızca telnet (TCP) atar.</item>
    /// <item>Kullanıyorsa: uygulama önce telnet atar, açıksa hedefin identity ucuyla hedefin pod'larını keşfeder.</item>
    /// </list>
    /// </summary>
    private async Task<(PodConnectionCell? Cell, string? Error)> CallOnceAsync(
        AppDefinition app, ConnectionDefinition conn, TimeSpan timeout, CancellationToken ct)
    {
        // step 1: Uygulamaya testi yaptırıyoruz. Eski kütüphane sürümünde discover ucu yoktur (404) veya testi yapan pod
        //         bilgisi gelmez; o zaman sonucu bir pod'a bağlayamayız.
        var (inst, err) = await RemoteProbe.CallDiscoverAsync(
            app.BaseUrl, app.ProbePath, conn.Host, conn.Port, timeout, _opt.AccessKey, conn.UsesConnectivityProbe,
            Math.Max(1, _opt.MaxInstanceAttempts), _opt.InstanceConfidence, ct).ConfigureAwait(false);
        if (inst == null) return (null, err != null && err.StartsWith("HTTP 404") ? OldVersionError : err);
        if (inst.ExecutedByInstanceId == null || inst.Tcp == null) return (null, OldVersionError);
        return (CellFromReport(conn, inst, inst.ExecutedByInstanceId), null);
    }

    /// <summary>
    /// Bir pod'un bir bağlantı için ürettiği discover raporunu hücreye çevirir. Discover modda uygulamanın discover ucundan,
    /// Strict modda pod'un kendi bildiriminden gelen rapor aynı biçimdedir.
    /// </summary>
    private static PodConnectionCell CellFromReport(ConnectionDefinition conn, DiscoverReport inst, string instanceId)
    {
        // step 2: Telnet sonucu. Hedef ConnectivityProbe kullanmıyorsa sonuç bu kadar.
        var cell = FromTcp(inst.Tcp!, instanceId);
        if (!conn.UsesConnectivityProbe) return cell;

        var firstError = inst.Errors.Keys.FirstOrDefault();

        // step 3: Başarı = telnet açık VE hedefin ConnectivityProbe'u cevap verdi. Değilse nedenini açıkça yazıyoruz.
        string? reason = !cell.Success ? cell.Error
            : inst.TargetKind == ProbeTargetKind.ConnectivityProbe ? null
            : inst.TargetKind == ProbeTargetKind.ConnectivityProbeError
                ? "Telnet başarılı; hedefte ConnectivityProbe var ama istekleri reddetti: " + firstError
            : "Telnet başarılı ama hedefte ConnectivityProbe bulunamadı (bağlantı tanımındaki işareti kontrol edin)"
              + (firstError != null ? ": " + firstError : "");

        return cell with
        {
            Success = cell.Success && inst.TargetKind == ProbeTargetKind.ConnectivityProbe,
            Error = reason,
            TargetKind = inst.TargetKind,
            TargetCountConverged = inst.Converged,
            TargetFailedRequests = inst.Failed,
            TargetPods = inst.Instances
                .Select(s => new TargetPod(s.InstanceId, s.Identity.MachineName, s.Identity.LocalAddresses, s.Hits,
                    s.Identity.Environment.TryGetValue("POD_NAME", out var podName) ? podName : null))
                .OrderBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// Hücreyi saklar; önceki sonuca bakarak "ne zamandan beri başarısız" ve "son başarılı test" bilgisini taşır.
    /// Çağıran rt üzerinde kilit tutmalıdır.
    /// </summary>
    private static PodConnectionCell StoreCell(AppRuntime rt, string connId, PodConnectionCell cell)
    {
        var key = connId + "|" + cell.InstanceId;
        rt.Results.TryGetValue(key, out var prev);
        var stored = cell with
        {
            LastSuccessUtc = cell.Success ? cell.CheckedAtUtc : prev?.LastSuccessUtc,
            FailingSinceUtc = cell.Success ? null
                : prev is { Success: false } ? prev.FailingSinceUtc ?? prev.CheckedAtUtc
                : cell.CheckedAtUtc
        };
        rt.Results[key] = stored;
        return stored;
    }

    /// <summary>
    /// Bir CP bağlantısının hedef pod hafızasını bu turun keşif sonuçlarıyla günceller ve her örnek hücreye, o pod'un erişemediği
    /// hedef pod'larını ekler. Uygulamanın kendi pod'larıyla aynı kurallar geçerlidir:
    /// - Bir hedef pod'u bir pod'dan üst üste MissingAfterCycles tur görülmezse "erişilemiyor" (alarm); daha azı rastlantı olabilir.
    /// - Hiçbir pod'un erişemediği hedef pod'u, hedefin pod sayısı korunuyorsa (deploy/restart: yerine yenisi geldi) sessizce unutulur;
    ///   sayı azaldıysa unutulmaz (pod çöktü) ve yalnızca "Pod listesini sıfırla" ile silinir.
    /// Çağıran rt üzerinde kilit tutmalıdır.
    /// </summary>
    private List<PodConnectionCell> UpdateTargets(AppRuntime rt, string connId, List<PodConnectionCell> samples, DateTime now)
    {
        if (samples.Count == 0) return samples;
        var threshold = Math.Max(1, _opt.MissingAfterCycles);
        if (!rt.Targets.TryGetValue(connId, out var mem)) rt.Targets[connId] = mem = new TargetMemory();

        // step 1: Bu turda en az bir pod'un eriştiği hedef pod'ları hafızaya ekliyoruz.
        var union = new HashSet<string>();
        foreach (var tp in samples.SelectMany(c => c.TargetPods!))
        {
            union.Add(tp.InstanceId);
            if (!mem.Pods.TryGetValue(tp.InstanceId, out var known))
                mem.Pods[tp.InstanceId] = known = new KnownTargetPod { InstanceId = tp.InstanceId, FirstSeenUtc = now };
            known.MachineName = tp.MachineName;
            known.PodName = tp.PodName ?? known.PodName;
            known.LastReachedUtc = now;
        }

        // step 2: Her hedef pod için hem genel hem de test eden pod bazında sayaçları güncelliyoruz.
        foreach (var known in mem.Pods.Values)
        {
            known.Missed = union.Contains(known.InstanceId) ? 0 : known.Missed + 1;
            foreach (var cell in samples)
            {
                if (!known.By.TryGetValue(cell.InstanceId, out var reach)) known.By[cell.InstanceId] = reach = new TargetReach();
                if (cell.TargetPods!.Any(t => t.InstanceId == known.InstanceId))
                {
                    reach.LastReachedUtc = now;
                    reach.MissingSinceUtc = null;
                    reach.Missed = 0;
                }
                else
                {
                    reach.Missed++;
                    reach.MissingSinceUtc ??= now;
                }
            }
        }

        // step 3: Yerine yenisi gelmiş (deploy) hedef pod'larını unutuyoruz; sayı azaldıysa tutuyoruz.
        var crossed = mem.Pods.Values.Where(k => k.Missed == threshold).Select(k => k.InstanceId).ToList();
        if (crossed.Count > 0 && union.Count >= mem.ExpectedTargets)
            foreach (var id in crossed) mem.Pods.Remove(id);
        if (mem.Pods.Keys.All(union.Contains)) mem.ExpectedTargets = union.Count;

        // step 4: Her hücreye, o pod'un erişemediği hedef pod'larını yazıyoruz.
        return samples.Select(cell => cell with
        {
            UnreachedTargets = mem.Pods.Values
                .Where(k => k.By.TryGetValue(cell.InstanceId, out var r) && r.Missed > 0)
                .Select(k =>
                {
                    var r = k.By[cell.InstanceId];
                    return new UnreachedTarget(k.InstanceId, k.MachineName, k.PodName, r.Missed, r.MissingSinceUtc ?? now,
                        r.LastReachedUtc, r.Missed >= threshold);
                })
                .OrderBy(u => u.PodName ?? u.MachineName, StringComparer.OrdinalIgnoreCase).ToList()
        }).ToList();
    }

    // Telnet raporunu (isim üzerinden bağlantı + IP bazında sonuçlar) hücreye çevirir.
    private static PodConnectionCell FromTcp(ProbeReport report, string instanceId)
    {
        var host = report.HostnameAttempts.FirstOrDefault();
        return new PodConnectionCell
        {
            InstanceId = instanceId,
            Success = host?.Success ?? false,
            ElapsedMs = host?.ElapsedMs ?? 0,
            ReachedAddress = host?.RemoteAddress,
            Error = host?.Error ?? report.ResolveError,
            CheckedAtUtc = DateTime.UtcNow,
            IpResults = report.ResolvedAddresses
                .Select(a => new IpResult(a.Address, a.Succeeded > 0, a.Results.FirstOrDefault()?.ElapsedMs ?? 0, a.Results.FirstOrDefault()?.Error))
                .ToList()
        };
    }

    private static void AddHistory(AppRuntime rt, AppStatus status)
    {
        rt.History.Add(new HistoryPoint(status.CheckedAtUtc ?? DateTime.UtcNow, status.State, status.PodCount));
        if (rt.History.Count > 30) rt.History.RemoveRange(0, rt.History.Count - 30);
    }
}
