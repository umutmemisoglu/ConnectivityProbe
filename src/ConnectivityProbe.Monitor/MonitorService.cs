using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Pod bildirimlerini işler ve her uygulamanın durumunu hesaplar. Monitor hiçbir uygulamaya kendisi istek atmaz: her pod
/// (ConnectivityProbeAgent) anahtarıyla kendini bildirir, bağlantılarını kendi içinden test eder ve sonuçları gönderir.
/// Pod sayısı = bildirim gönderen pod'lar (kesin).
/// </summary>
public sealed class MonitorService : BackgroundService
{
    /// <summary>Bir uygulamanın hatırlanan durumu (pod'lar, son sonuçlar, geçmiş).</summary>
    private sealed class AppRuntime
    {
        public readonly Dictionary<string, PodStatus> Known = new();
        public readonly Dictionary<string, int> Missed = new();                 // pod -> kaç test aralığıdır bildirim yok
        public readonly Dictionary<string, int> PollSeconds = new();            // pod -> bildirim sıklığı (sn)
        public readonly Dictionary<string, PodConnectionCell> Results = new();   // anahtar: bağlantıId|podId
        public readonly List<HistoryPoint> History = new();
        /// <summary>Bilinen pod'ların hepsinin canlı olduğu son andaki pod sayısı (deploy ile "pod çöktü"yü ayırmak için).</summary>
        public int ExpectedPods;
        public DateTime LastHistoryUtc;
    }

    /// <summary>Durumların yeniden hesaplanma aralığı: alarm, eşik dolduktan en geç bu kadar sonra görünür.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DefinitionStore _store;
    private readonly PodStateStore _podState;
    private readonly MonitorOptions _opt;
    private readonly ILogger<MonitorService> _log;
    private readonly ConcurrentDictionary<string, AppRuntime> _runtime = new();
    private readonly ConcurrentDictionary<string, AppStatus> _latest = new();
    private readonly SemaphoreSlim _trigger = new(0, 1);

    /// <summary>Monitor'ün başladığı an: yeniden başladıktan hemen sonra pod'lar bildirim gönderene kadar "eksik" sayılmasın diye.</summary>
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    /// <summary>"Şimdi test et"e her basıldığında artar; pod'lar değiştiğini görünce beklemeden test eder.</summary>
    private long _runRequestId = DateTime.UtcNow.Ticks;
    private DateTime? _lastRunUtc;
    private DateTime _lastSaveUtc;

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
            foreach (var kv in saved.PollSeconds ?? new()) rt.PollSeconds[kv.Key] = kv.Value;
            _runtime[appId] = rt;
        }
    }

    private TimeSpan TestInterval => TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds));
    private int Threshold => Math.Max(1, _opt.MissingAfterCycles);

    /// <summary>"Şimdi test et"e her basıldığında değişen kimlik.</summary>
    public string RunRequestId => Interlocked.Read(ref _runRequestId).ToString(CultureInfo.InvariantCulture);

    /// <summary>"Şimdi test et": pod'lar bunu bir sonraki bildirimlerinde görüp beklemeden test eder.</summary>
    public void RequestRun()
    {
        Interlocked.Increment(ref _runRequestId);
        Trigger();
    }

    /// <summary>Durumları beklemeden yeniden hesaplar (tanım değişince).</summary>
    public void Trigger()
    {
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { /* zaten bir tetikleme bekliyor */ }
    }

    /// <summary>
    /// Uygulamanın hatırlanan durumunu (pod'lar, eksik pod'lar, sonuçlar, geçmiş) siler. Arayüzdeki "Pod listesini sıfırla"
    /// butonu çağırır; çalışan pod'lar bir sonraki bildirimlerinde (en geç birkaç saniye içinde) yeniden görünür.
    /// </summary>
    public void ForgetApp(string appId)
    {
        _runtime.TryRemove(appId, out _);
        _latest.TryRemove(appId, out _);
        SavePodState();
        Trigger();
    }

    // ---------------------------------------------------------------------------------------------
    // Pod bildirimleri
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Pod bildirimini işler: pod'u canlı olarak işaretler, test sonuçları varsa pod'un hücrelerine yazar ve pod'a uygulamanın
    /// güncel bağlantılarını döner. Pod'un cluster'ı ilk kez görülüyorsa "Cluster N" adıyla kaydedilir.
    /// </summary>
    /// <param name="sourceIp">Bildirimin geldiği adres; Kubernetes cluster kimliği yoksa pod'lar buna göre gruplanır.</param>
    public AgentAssignment AcceptAgentReport(AppDefinition app, DefinitionData defs, AgentReport report, string sourceIp)
    {
        var now = DateTime.UtcNow;
        var pod = report.Pod;
        var clusterKey = !string.IsNullOrEmpty(pod.ClusterId) ? "k8s:" + pod.ClusterId : "net:" + sourceIp;
        if (defs.Clusters.All(c => c.Key != clusterKey)) EnsureCluster(clusterKey);

        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var connections = app.ConnectionIds
            .Select(id => defs.Connections.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null).Select(c => c!).ToList();

        bool isNew;
        lock (rt)
        {
            // step 1: Pod'u canlı olarak kaydediyoruz (yeni bir pod ise listeye girer).
            isNew = !rt.Known.ContainsKey(pod.InstanceId);
            rt.Known[pod.InstanceId] = new PodStatus
            {
                InstanceId = pod.InstanceId, MachineName = pod.MachineName, Addresses = pod.LocalAddresses,
                StartedAtUtc = pod.StartedAtUtc, Details = new Dictionary<string, string>(pod.Environment), LastSeenUtc = now,
                ProbeVersion = pod.ProbeVersion, AppVersion = pod.AppVersion, BuildId = pod.BuildId, BuildDateUtc = pod.BuildDateUtc,
                ClusterKey = clusterKey, Namespace = pod.Namespace, SourceIp = sourceIp
            };
            rt.Missed[pod.InstanceId] = 0;
            rt.PollSeconds[pod.InstanceId] = Math.Clamp(report.PollSeconds, 1, 300);

            // step 2: Test sonuçları geldiyse pod'un hücrelerine yazıyoruz.
            foreach (var result in report.Run?.Results ?? new List<AgentResult>())
            {
                var conn = connections.FirstOrDefault(c => c.Id == result.ConnectionId);
                if (conn == null) continue; // tanım bu arada uygulamadan çıkarılmış

                var cell = result.Tcp != null
                    ? FromTcp(result.Tcp, pod.InstanceId, now)
                    : new PodConnectionCell { InstanceId = pod.InstanceId, Error = result.Error ?? "Test sonucu yok", CheckedAtUtc = now };
                StoreCell(rt, conn.Id, cell);
            }
        }

        if (isNew)
        {
            _log.LogInformation("Pod {Pod} ({Machine}, {Version}) joined app {App}", pod.InstanceId, pod.MachineName, pod.AppVersion, app.Name);
            SavePodState();
            Trigger();
        }

        return new AgentAssignment(
            app.Id, app.Name, (int)TestInterval.TotalSeconds, RunRequestId, Math.Max(500, _opt.ProbeTimeoutMs),
            connections.Select(c => new AgentConnection(c.Id, c.Name, c.Host, c.Port)).ToList());
    }

    /// <summary>Pod düzgün kapanıyor: alarm vermeden listeden çıkarılır (deploy, scale-down).</summary>
    public bool AgentGoodbye(string appId, string instanceId)
    {
        if (!_runtime.TryGetValue(appId, out var rt)) return false;
        lock (rt)
        {
            if (!rt.Known.ContainsKey(instanceId)) return false;
            Forget(rt, instanceId);
            foreach (var key in rt.Results.Keys.Where(k => k.EndsWith("|" + instanceId, StringComparison.Ordinal)).ToList()) rt.Results.Remove(key);
        }
        _log.LogInformation("Pod {Pod} left app {App}", instanceId, appId);
        SavePodState();
        Trigger();
        return true;
    }

    // İlk kez görülen cluster'ı "Cluster N" adıyla kaydeder (ad Monitor'de değiştirilebilir).
    private void EnsureCluster(string key) =>
        _store.Mutate(d =>
        {
            if (d.Clusters.Any(c => c.Key == key)) return 0;
            d.Clusters.Add(new ClusterDefinition { Key = key, Name = "Cluster " + (d.Clusters.Count + 1) });
            return 1;
        });

    // ---------------------------------------------------------------------------------------------
    // Durum hesaplama
    // ---------------------------------------------------------------------------------------------

    /// <summary>Arayüzün gösterdiği güncel durum.</summary>
    public MonitorSnapshot GetSnapshot()
    {
        var defs = _store.Snapshot();
        var apps = defs.Apps.Select(a => _latest.TryGetValue(a.Id, out var s)
            ? s
            : new AppStatus { AppId = a.Id, Name = a.Name, AppKey = a.AppKey, Message = "Pod bildirimi bekleniyor" }).ToList();

        // Cluster'lar: canlı pod sayısı ve kaç uygulamanın pod'u olduğu.
        var clusters = defs.Clusters.Select(c => new ClusterView(
            c.Key, c.Name,
            apps.Sum(a => a.Pods.Count(p => p.ClusterKey == c.Key && p.State == "up")),
            apps.Count(a => a.Pods.Any(p => p.ClusterKey == c.Key)))).ToList();

        return new MonitorSnapshot
        {
            IntervalSeconds = (int)TestInterval.TotalSeconds, MissingAfterCycles = Threshold, LastRunUtc = _lastRunUtc,
            Apps = apps, Clusters = clusters
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Refresh(); }
            catch (Exception ex) { _log.LogError(ex, "Monitor refresh failed"); }

            try { await _trigger.WaitAsync(RefreshInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Refresh()
    {
        var defs = _store.Snapshot();
        var now = DateTime.UtcNow;

        // Silinmiş uygulamaların durumunu temizliyoruz.
        var ids = defs.Apps.Select(a => a.Id).ToHashSet();
        foreach (var key in _runtime.Keys.Where(k => !ids.Contains(k))) _runtime.TryRemove(key, out _);
        foreach (var key in _latest.Keys.Where(k => !ids.Contains(k))) _latest.TryRemove(key, out _);

        foreach (var app in defs.Apps) _latest[app.Id] = Compute(app, defs, now);
        _lastRunUtc = now;

        // Pod listesini test aralığında bir diske yazıyoruz (yeni pod / kapanış / sıfırlama anında ayrıca yazılır).
        if (now - _lastSaveUtc >= TestInterval) SavePodState();
    }

    /// <summary>Uygulamanın durumunu pod bildirimlerinden hesaplar.</summary>
    private AppStatus Compute(AppDefinition app, DefinitionData defs, DateTime now)
    {
        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var interval = TestInterval;
        var threshold = Threshold;
        var status = new AppStatus { AppId = app.Id, Name = app.Name, AppKey = app.AppKey, CheckedAtUtc = now };
        var clusterNames = defs.Clusters.ToDictionary(c => c.Key, c => c.Name);
        var connections = app.ConnectionIds
            .Select(id => defs.Connections.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null).Select(c => c!).ToList();
        int up;
        DateTime? lastReport;

        lock (rt)
        {
            // step 1: Pod durumları bildirimin yaşına göre:
            //   up          -> son bildirim, pod'un bildirim aralığının iki katından yeni
            //   unconfirmed -> bildirim gecikti ama henüz eşik dolmadı
            //   missing     -> MissingAfterCycles test aralığı boyunca bildirim yok (alarm)
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

            // step 2: Eşiği şimdi geçen pod'lar: pod sayısı korunduysa (yerine yenisi geldi, kapanış bildirimi gelmeden) alarmsız
            //         düşürüyoruz; azaldıysa "eksik" kalır ve yalnızca "Pod listesini sıfırla" ile silinir. Geri gelirse normale döner.
            if (justCrossed.Count > 0 && up > 0 && up >= rt.ExpectedPods)
                foreach (var id in justCrossed) { Forget(rt, id); states.Remove(id); }
            if (up > 0 && states.Values.All(s => s == "up")) rt.ExpectedPods = up;

            // Artık atanmamış bağlantıların ve bilinmeyen pod'ların sonuçlarını temizliyoruz.
            var attached = app.ConnectionIds.ToHashSet();
            foreach (var key in rt.Results.Keys.ToList())
            {
                var sep = key.IndexOf('|');
                if (!attached.Contains(key[..sep]) || !rt.Known.ContainsKey(key[(sep + 1)..])) rt.Results.Remove(key);
            }

            lastReport = rt.Known.Count > 0 ? rt.Known.Values.Max(p => p.LastSeenUtc) : null;

            status.Pods = rt.Known.Values
                .Select(p => new PodStatus
                {
                    InstanceId = p.InstanceId, MachineName = p.MachineName, Addresses = p.Addresses, StartedAtUtc = p.StartedAtUtc,
                    Details = p.Details, LastSeenUtc = p.LastSeenUtc, ProbeVersion = p.ProbeVersion, AppVersion = p.AppVersion,
                    BuildId = p.BuildId, BuildDateUtc = p.BuildDateUtc, ClusterKey = p.ClusterKey,
                    ClusterName = p.ClusterKey != null && clusterNames.TryGetValue(p.ClusterKey, out var cn) ? cn : null,
                    Namespace = p.Namespace, SourceIp = p.SourceIp,
                    Seen = states[p.InstanceId] == "up", MissedCycles = rt.Missed.TryGetValue(p.InstanceId, out var mc) ? mc : 0,
                    State = states[p.InstanceId]
                })
                .OrderBy(p => p.ClusterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Details.TryGetValue("POD_NAME", out var n) ? n : p.MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.InstanceId, StringComparer.Ordinal).ToList();

            // step 3: Bağlantı sonuçları: canlı pod'un son iki test aralığından gelenler "taze", diğerleri son bilinen sonuç (soluk).
            var freshAfter = now - interval - interval;
            status.Connections = connections.Select(c => new ConnectionStatus
            {
                ConnectionId = c.Id, Name = c.Name, Target = c.Host + (c.Port.HasValue ? ":" + c.Port : ""), TargetAppId = c.TargetAppId,
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

        // step 4: Genel durum.
        if (status.Pods.Count == 0)
        {
            status.State = "unknown";
            status.Message = "Pod bildirimi bekleniyor";
        }
        else if (up == 0)
        {
            status.State = "down";
            status.Message = $"Hiçbir pod bildirim göndermiyor (son bildirim: {lastReport:HH:mm:ss} UTC)";
        }
        else
        {
            var problems = new List<string>();
            var missing = status.Pods.Count(p => p.State == "missing");
            if (missing > 0) problems.Add($"{missing} pod eksik (üst üste {threshold}+ test aralığı bildirim göndermedi)");
            var failedCells = status.Connections.Sum(c => c.Cells.Count(x => x.Fresh && !x.Success));
            if (failedCells > 0) problems.Add($"{failedCells} bağlantı testi başarısız");

            var notes = new List<string>();
            var late = status.Pods.Count(p => p.State == "unconfirmed");
            if (late > 0) notes.Add($"{late} pod'un bildirimi gecikti");
            var builds = status.Pods.Where(p => p.State == "up").Select(p => p.AppVersion + "|" + p.BuildId).Distinct().Count();
            if (builds > 1) notes.Add($"{builds} farklı sürüm/build çalışıyor");
            if (connections.Count > 0 && status.Connections.All(c => c.Cells.Count == 0)) notes.Add("ilk test sonuçları bekleniyor");

            status.State = problems.Count > 0 ? "degraded" : "healthy";
            status.Message = problems.Count + notes.Count == 0 ? null : string.Join("; ", problems.Concat(notes));
        }

        // Geçmiş: test aralığında bir nokta.
        lock (rt)
        {
            if (now - rt.LastHistoryUtc >= interval)
            {
                rt.LastHistoryUtc = now;
                rt.History.Add(new HistoryPoint(now, status.State, status.PodCount));
                if (rt.History.Count > 30) rt.History.RemoveRange(0, rt.History.Count - 30);
            }
            status.History = rt.History.ToList();
        }
        return status;
    }

    // ---------------------------------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------------------------------

    private static void Forget(AppRuntime rt, string podId)
    {
        rt.Known.Remove(podId);
        rt.Missed.Remove(podId);
        rt.PollSeconds.Remove(podId);
    }

    /// <summary>
    /// Hücreyi saklar; önceki sonuca bakarak "ne zamandan beri başarısız" ve "son başarılı test" bilgisini taşır.
    /// Çağıran rt üzerinde kilit tutmalıdır.
    /// </summary>
    private static void StoreCell(AppRuntime rt, string connId, PodConnectionCell cell)
    {
        var key = connId + "|" + cell.InstanceId;
        rt.Results.TryGetValue(key, out var prev);
        rt.Results[key] = cell with
        {
            LastSuccessUtc = cell.Success ? cell.CheckedAtUtc : prev?.LastSuccessUtc,
            FailingSinceUtc = cell.Success ? null
                : prev is { Success: false } ? prev.FailingSinceUtc ?? prev.CheckedAtUtc
                : cell.CheckedAtUtc
        };
    }

    // Telnet raporunu (isim üzerinden bağlantı + IP bazında sonuçlar) hücreye çevirir.
    private static PodConnectionCell FromTcp(ProbeReport report, string instanceId, DateTime checkedAtUtc)
    {
        var host = report.HostnameAttempts.FirstOrDefault();
        return new PodConnectionCell
        {
            InstanceId = instanceId,
            Success = host?.Success ?? false,
            ElapsedMs = host?.ElapsedMs ?? 0,
            ReachedAddress = host?.RemoteAddress,
            Error = host?.Error ?? report.ResolveError,
            CheckedAtUtc = checkedAtUtc,
            IpResults = report.ResolvedAddresses
                .Select(a => new IpResult(a.Address, a.Succeeded > 0, a.Results.FirstOrDefault()?.ElapsedMs ?? 0, a.Results.FirstOrDefault()?.Error))
                .ToList()
        };
    }

    // Her uygulamanın bilinen pod'larını diske yazar.
    private void SavePodState()
    {
        _lastSaveUtc = DateTime.UtcNow;
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
                        PollSeconds = new Dictionary<string, int>(rt.PollSeconds),
                        ExpectedPods = rt.ExpectedPods
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
}
