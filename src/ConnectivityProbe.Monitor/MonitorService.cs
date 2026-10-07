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
        public readonly Dictionary<string, List<ResourcePoint>> Resources = new(); // pod -> son kaynak ölçümleri
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

    /// <summary>Saat (testlerde elle ilerletilir).</summary>
    private readonly TimeProvider _time;
    /// <summary>Teams bildirimleri (her turda durumlar verilir).</summary>
    private readonly NotificationService? _notifications;
    /// <summary>Monitor'ün başladığı an: yeniden başladıktan hemen sonra pod'lar bildirim gönderene kadar "eksik" sayılmasın diye.</summary>
    private readonly DateTime _startedUtc;
    /// <summary>"Şimdi test et"e her basıldığında artar; pod'lar değiştiğini görünce beklemeden test eder.</summary>
    private long _runRequestId;
    private DateTime? _lastRunUtc;
    private DateTime _lastSaveUtc;

    public MonitorService(DefinitionStore store, PodStateStore podState, IOptions<MonitorOptions> options, ILogger<MonitorService> log,
        TimeProvider? time = null, NotificationService? notifications = null)
    {
        _time = time ?? TimeProvider.System;
        _notifications = notifications;
        _startedUtc = Now;
        _runRequestId = _startedUtc.Ticks;
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

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

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
        _notifications?.Forget(appId);
        SavePodState();
        Trigger();
    }

    // ---------------------------------------------------------------------------------------------
    // Pod bildirimleri
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Pod bildirimini işler: pod'u canlı olarak işaretler, test sonuçları varsa pod'un hücrelerine yazar ve pod'a uygulamanın
    /// güncel bağlantılarını döner. Pod'un cluster'ı ilk kez görülüyorsa kaydedilir (adı pod ağından türetilir).
    /// </summary>
    /// <param name="sourceIp">Bildirimin geldiği adres; pod kendi adresini bildirmediyse yedek olarak kullanılır.</param>
    public AgentAssignment AcceptAgentReport(AppDefinition app, DefinitionData defs, AgentReport report, string sourceIp)
    {
        var now = Now;
        var pod = report.Pod;

        // Cluster: Kubernetes'te cluster sertifikasının parmak izi (aynı pod ağını kullanan iki cluster da ayrılır);
        // Kubernetes dışında pod'un ağı (ör. 10.80.0.0/16).
        var primary = Network.PrimaryOf(pod.PrimaryAddress, pod.LocalAddresses, sourceIp);
        var network = Network.Of(primary);
        var clusterKey = !string.IsNullOrEmpty(pod.ClusterId) ? "k8s:" + pod.ClusterId : "net:" + (network ?? sourceIp);
        if (defs.Clusters.All(c => c.Key != clusterKey)) EnsureCluster(clusterKey);

        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var connections = app.ConnectionIds
            .Select(id => defs.Connections.FirstOrDefault(c => c.Id == id))
            .Where(c => c != null).Select(c => c!).ToList();

        bool isNew;
        lock (rt)
        {
            // step 1: Pod'u canlı olarak kaydediyoruz (yeni bir pod ise listeye girer).
            //         Aynı pod yeni bir süreçle geldiyse (başlangıç zamanı değişti) yeniden başlamış demektir; OOM kill sayacı
            //         arttıysa container bellek yüzünden süreç öldürmüştür.
            isNew = !rt.Known.TryGetValue(pod.InstanceId, out var previous);
            var restarted = previous?.StartedAtUtc is { } before && pod.StartedAtUtc - before > TimeSpan.FromSeconds(2);
            var oom = report.Resources?.OomKills is { } kills && previous?.Resources?.OomKills is { } killsBefore && kills > killsBefore;
            rt.Known[pod.InstanceId] = new PodStatus
            {
                InstanceId = pod.InstanceId, MachineName = pod.MachineName, Addresses = pod.LocalAddresses,
                PrimaryAddress = primary, Network = network, StartedAtUtc = pod.StartedAtUtc, Details = new Dictionary<string, string>(pod.Environment), LastSeenUtc = now,
                ProbeVersion = pod.ProbeVersion, AppVersion = pod.AppVersion, BuildId = pod.BuildId, BuildDateUtc = pod.BuildDateUtc,
                ClusterKey = clusterKey, Namespace = pod.Namespace, SourceIp = sourceIp,
                Resources = report.Resources,
                Restarts = (previous?.Restarts ?? 0) + (restarted ? 1 : 0),
                LastRestartUtc = restarted ? now : previous?.LastRestartUtc,
                LastOomUtc = oom ? now : previous?.LastOomUtc
            };
            if (restarted) _log.LogWarning("Pod {Pod} of app {App} restarted", pod.InstanceId, app.Name);

            // Grafikler için son 60 ölçüm (varsayılan bildirim aralığıyla ~10 dakika).
            if (report.Resources is { } r)
            {
                if (!rt.Resources.TryGetValue(pod.InstanceId, out var points)) rt.Resources[pod.InstanceId] = points = new List<ResourcePoint>();
                points.Add(new ResourcePoint(now, r.CpuCores, r.MemoryBytes ?? r.WorkingSetBytes, r.CpuThrottledPercent, r.NetRxBytes, r.NetTxBytes));
                if (points.Count > 60) points.RemoveRange(0, points.Count - 60);
            }
            rt.Missed[pod.InstanceId] = 0;
            rt.PollSeconds[pod.InstanceId] = Math.Clamp(report.PollSeconds, 1, 300);

            // step 2: Test sonuçları geldiyse pod'un hücrelerine yazıyoruz.
            foreach (var result in report.Run?.Results ?? new List<AgentResult>())
            {
                var conn = connections.FirstOrDefault(c => c.Id == result.ConnectionId);
                if (conn == null) continue; // tanım bu arada uygulamadan çıkarılmış

                var cell = result.Tcp != null
                    ? FromTcp(result.Tcp, result.Tls, pod.InstanceId, now)
                    : new PodConnectionCell { InstanceId = pod.InstanceId, Error = result.Error ?? "No test result", CheckedAtUtc = now };
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
            connections.Select(c => new AgentConnection(c.Id, c.Name, c.Host, c.Port, c.TlsEnabled())).ToList());
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

    // İlk kez görülen cluster'ı kaydeder. Ad boş kalır: görünen ad pod'ların ağından türetilir, Monitor'de elle verilebilir.
    private void EnsureCluster(string key) =>
        _store.Mutate(d =>
        {
            if (d.Clusters.Any(c => c.Key == key)) return 0;
            d.Clusters.Add(new ClusterDefinition { Key = key });
            return 1;
        });

    /// <summary>
    /// Cluster'ların görünen adları: elle verilen ad, yoksa pod'larının ağları ("10.42.0.0/16"). Aynı ağı kullanan iki
    /// Kubernetes cluster'ı (ör. ikisi de varsayılan 10.42.0.0/16) kimliklerinin kısa haliyle ayrılır.
    /// </summary>
    private Dictionary<string, ClusterView> BuildClusters(DefinitionData defs)
    {
        // step 1: Her cluster'da görülen ağlar (tüm uygulamaların pod'larından).
        var networks = new Dictionary<string, SortedSet<string>>();
        foreach (var rt in _runtime.Values)
            lock (rt)
                foreach (var p in rt.Known.Values.Where(p => p.ClusterKey != null))
                {
                    if (!networks.TryGetValue(p.ClusterKey!, out var set)) networks[p.ClusterKey!] = set = new SortedSet<string>(StringComparer.Ordinal);
                    var net = p.Network ?? Network.Of(Network.PrimaryOf(p.PrimaryAddress, p.Addresses, p.SourceIp));
                    if (net != null) set.Add(net);
                }

        // step 2: Ad: elle verilen ya da ağlar; Kubernetes dışında anahtar zaten ağın kendisidir.
        string AutoName(string key) =>
            networks.TryGetValue(key, out var set) && set.Count > 0 ? string.Join(", ", set)
            : key.StartsWith("net:", StringComparison.Ordinal) ? key[4..] : key;

        var views = defs.Clusters.Select(c => new
        {
            c.Key,
            Custom = !string.IsNullOrWhiteSpace(c.Name),
            Name = string.IsNullOrWhiteSpace(c.Name) ? AutoName(c.Key) : c.Name.Trim(),
            Networks = networks.TryGetValue(c.Key, out var set) ? set.ToList() : new List<string>()
        }).ToList();

        // step 3: Aynı otomatik adı alan cluster'lara kısa kimlik ekliyoruz.
        var duplicate = views.Where(v => !v.Custom).GroupBy(v => v.Name).Where(g => g.Count() > 1).SelectMany(g => g).Select(v => v.Key).ToHashSet();
        static string ShortId(string key)
        {
            var id = key[(key.IndexOf(':') + 1)..];
            return id.Length > 6 ? id[..6] : id;
        }
        return views.ToDictionary(v => v.Key, v => new ClusterView(
            v.Key, duplicate.Contains(v.Key) ? v.Name + " · " + ShortId(v.Key) : v.Name, v.Networks, v.Custom, 0, 0));
    }

    // ---------------------------------------------------------------------------------------------
    // Durum hesaplama
    // ---------------------------------------------------------------------------------------------

    /// <summary>Arayüzün gösterdiği güncel durum.</summary>
    public MonitorSnapshot GetSnapshot()
    {
        var defs = _store.Snapshot();
        var apps = defs.Apps.Select(a => _latest.TryGetValue(a.Id, out var s)
            ? s
            : new AppStatus { AppId = a.Id, Name = a.Name, AppKey = a.AppKey, Notes = { new StatusNote("waiting") } }).ToList();

        // Cluster'lar: görünen ad, canlı pod sayısı ve kaç uygulamanın pod'u olduğu.
        var clusters = BuildClusters(defs).Values.Select(c => c with
        {
            Pods = apps.Sum(a => a.Pods.Count(p => p.ClusterKey == c.Key && p.State == "up")),
            Apps = apps.Count(a => a.Pods.Any(p => p.ClusterKey == c.Key))
        }).ToList();

        return new MonitorSnapshot
        {
            IntervalSeconds = (int)TestInterval.TotalSeconds, MissingAfterCycles = Threshold, LastRunUtc = _lastRunUtc,
            CertificateDays = _opt.Alerts.CertificateDays,
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

    /// <summary>Durumları hemen yeniden hesaplar (arka plan döngüsü her RefreshInterval'da çağırır; testler doğrudan).</summary>
    internal void Refresh()
    {
        var defs = _store.Snapshot();
        var now = Now;

        // Silinmiş uygulamaların durumunu temizliyoruz.
        var ids = defs.Apps.Select(a => a.Id).ToHashSet();
        foreach (var key in _runtime.Keys.Where(k => !ids.Contains(k))) _runtime.TryRemove(key, out _);
        foreach (var key in _latest.Keys.Where(k => !ids.Contains(k))) _latest.TryRemove(key, out _);

        var clusterNames = BuildClusters(defs).ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        foreach (var app in defs.Apps) _latest[app.Id] = Compute(app, clusterNames, defs, now);
        _lastRunUtc = now;

        // Bildirimler: durumlar değerlendirilir, doğrulanan gelişmeler abonelere gönderilir.
        _notifications?.Process(defs, _latest, now);

        // Pod listesini test aralığında bir diske yazıyoruz (yeni pod / kapanış / sıfırlama anında ayrıca yazılır).
        if (now - _lastSaveUtc >= TestInterval) SavePodState();
    }

    /// <summary>Uygulamanın durumunu pod bildirimlerinden hesaplar.</summary>
    private AppStatus Compute(AppDefinition app, Dictionary<string, string> clusterNames, DefinitionData defs, DateTime now)
    {
        var rt = _runtime.GetOrAdd(app.Id, _ => new AppRuntime());
        var interval = TestInterval;
        var threshold = Threshold;
        var status = new AppStatus { AppId = app.Id, Name = app.Name, AppKey = app.AppKey, CheckedAtUtc = now };
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
                    PrimaryAddress = p.PrimaryAddress ?? Network.PrimaryOf(null, p.Addresses, p.SourceIp),
                    Network = p.Network ?? Network.Of(Network.PrimaryOf(null, p.Addresses, p.SourceIp)),
                    Details = p.Details, LastSeenUtc = p.LastSeenUtc, ProbeVersion = p.ProbeVersion, AppVersion = p.AppVersion,
                    BuildId = p.BuildId, BuildDateUtc = p.BuildDateUtc, ClusterKey = p.ClusterKey,
                    ClusterName = p.ClusterKey != null && clusterNames.TryGetValue(p.ClusterKey, out var cn) ? cn : null,
                    Namespace = p.Namespace, SourceIp = p.SourceIp,
                    Seen = states[p.InstanceId] == "up", MissedCycles = rt.Missed.TryGetValue(p.InstanceId, out var mc) ? mc : 0,
                    State = states[p.InstanceId],
                    Resources = p.Resources, Restarts = p.Restarts, LastRestartUtc = p.LastRestartUtc, LastOomUtc = p.LastOomUtc,
                    ResourceHistory = rt.Resources.TryGetValue(p.InstanceId, out var points) ? points.ToList() : null,
                    Alerts = states[p.InstanceId] == "up" ? PodAlerts(p, now) : new List<string>()
                })
                .OrderBy(p => p.ClusterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Details.TryGetValue("POD_NAME", out var n) ? n : p.MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.InstanceId, StringComparer.Ordinal).ToList();

            // step 3: Bağlantı sonuçları: canlı pod'un son iki test aralığından gelenler "taze", diğerleri son bilinen sonuç (soluk).
            var freshAfter = now - interval - interval;
            status.Connections = connections.Select(c => new ConnectionStatus
            {
                ConnectionId = c.Id, Name = c.Name, Target = c.Host + (c.Port.HasValue ? ":" + c.Port : ""), TargetAppId = c.TargetAppId,
                Tls = c.TlsEnabled(),
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
        // Notların metni arayüzde seçili dilde üretilir (bkz. StatusNote).
        if (status.Pods.Count == 0)
        {
            status.State = "unknown";
            status.Notes.Add(new StatusNote("waiting"));
        }
        else if (up == 0)
        {
            status.State = "down";
            status.Notes.Add(new StatusNote("noReports", AtUtc: lastReport));
        }
        else
        {
            var missing = status.Pods.Count(p => p.State == "missing");
            if (missing > 0) status.Notes.Add(new StatusNote("missingPods", missing));
            var failedCells = status.Connections.Sum(c => c.Cells.Count(x => x.Fresh && !x.Success));
            if (failedCells > 0) status.Notes.Add(new StatusNote("failedTests", failedCells));

            // Kaynak ve sertifika uyarıları da uygulamayı "sorunlu" yapar.
            var alerts = _opt.Alerts;
            int PodsWith(string alert) => status.Pods.Count(p => p.Alerts.Contains(alert));
            foreach (var alert in new[] { "memHigh", "restart", "oom", "portsHigh" })
                if (PodsWith(alert) is var n && n > 0) status.Notes.Add(new StatusNote(alert, n));
            var expiring = status.Connections
                .Select(c => c.Cells.Where(x => x.Fresh && x.Tls is { Success: true, NotAfterUtc: not null }).Select(x => x.Tls!.NotAfterUtc!.Value).DefaultIfEmpty(DateTime.MaxValue).Min())
                .Where(end => end != DateTime.MaxValue && end - now <= TimeSpan.FromDays(alerts.CertificateDays)).ToList();
            if (expiring.Count > 0) status.Notes.Add(new StatusNote("certExpiring", expiring.Count, expiring.Min()));
            status.State = status.Notes.Count > 0 ? "degraded" : "healthy";

            // Bilgi notları (durumu değiştirmez).
            var late = status.Pods.Count(p => p.State == "unconfirmed");
            if (late > 0) status.Notes.Add(new StatusNote("latePods", late));
            if (PodsWith("throttled") is var throttled && throttled > 0) status.Notes.Add(new StatusNote("throttled", throttled));
            var slow = status.Connections.Count(c => c.Cells.Any(x => x.Fresh && x.Slow));
            if (slow > 0) status.Notes.Add(new StatusNote("slow", slow));
            var recent = now - TimeSpan.FromMinutes(alerts.RecentMinutes);
            var ipChanged = status.Connections.Count(c => c.Cells.Any(x => x.AddressesChangedUtc >= recent));
            if (ipChanged > 0) status.Notes.Add(new StatusNote("ipChanged", ipChanged));
            var builds = status.Pods.Where(p => p.State == "up").Select(p => p.AppVersion + "|" + p.BuildId).Distinct().Count();
            if (builds > 1) status.Notes.Add(new StatusNote("versions", builds));
            if (connections.Count > 0 && status.Connections.All(c => c.Cells.Count == 0)) status.Notes.Add(new StatusNote("firstResults"));
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

    /// <summary>
    /// Canlı bir pod'un kaynak uyarıları (eşikler: Monitor:Alerts):
    /// memHigh (bellek limite yakın), throttled (CPU limiti yüzünden yavaşlatılıyor), restart / oom (son RecentMinutes içinde),
    /// portsHigh (TCP soketleri yerel port aralığını dolduruyor).
    /// </summary>
    private List<string> PodAlerts(PodStatus p, DateTime now)
    {
        var a = _opt.Alerts;
        var list = new List<string>();
        var r = p.Resources;
        if (r is { MemoryLimitBytes: > 0, MemoryBytes: { } mem } && 100.0 * mem / r.MemoryLimitBytes.Value >= a.MemoryPercent) list.Add("memHigh");
        if (r?.CpuThrottledPercent >= a.CpuThrottledPercent) list.Add("throttled");
        if (r is { EphemeralPorts: > 0, TcpEstablished: { } est, TcpTimeWait: { } tw } && 100.0 * (est + tw) / r.EphemeralPorts.Value >= a.PortsPercent)
            list.Add("portsHigh");
        var recent = now - TimeSpan.FromMinutes(a.RecentMinutes);
        if (p.LastRestartUtc >= recent) list.Add("restart");
        if (p.LastOomUtc >= recent) list.Add("oom");
        return list;
    }

    private static void Forget(AppRuntime rt, string podId)
    {
        rt.Known.Remove(podId);
        rt.Missed.Remove(podId);
        rt.PollSeconds.Remove(podId);
        rt.Resources.Remove(podId);
    }

    /// <summary>
    /// Hücreyi saklar; önceki sonuca bakarak "ne zamandan beri başarısız" ve "son başarılı test" bilgisini taşır.
    /// Çağıran rt üzerinde kilit tutmalıdır.
    /// </summary>
    private static void StoreCell(AppRuntime rt, string connId, PodConnectionCell cell)
    {
        var key = connId + "|" + cell.InstanceId;
        rt.Results.TryGetValue(key, out var prev);

        // Ad çözümlemesi daha önce hiç görülmemiş bir IP döndürdü mü (DNS kaydı değişti, failover, yeni yük dengeleyici...).
        // Round-robin DNS (her sorguda havuzdan farklı IP, ör. github.com) uyarı üretmesin diye görülen IP'ler hatırlanır ve
        // ilk 5 ölçüm yalnızca öğrenmek için kullanılır.
        var addresses = cell.IpResults.Select(r => r.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var previousAddresses = prev?.IpResults.Select(r => r.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var known = new HashSet<string>(prev?.KnownAddresses ?? new List<string>(), StringComparer.Ordinal);
        var samples = (prev?.Samples ?? 0) + 1;
        var changed = samples > 5 && addresses.Any(a => !known.Contains(a));
        known.UnionWith(addresses);

        // Gecikme: son 20 başarılı bağlantının süresi. Son 3 ölçümün ortalaması, öncekilerin ortancasının 3 katını (ve en az
        // 50 ms fazlasını) geçerse "yavaş" sayılır. Hedef kopmadan önce çoğu zaman yavaşlar.
        var recent = (prev?.RecentMs ?? new List<long>()).ToList();
        if (cell.TcpSuccess) recent.Add(cell.ElapsedMs);
        if (recent.Count > 20) recent.RemoveRange(0, recent.Count - 20);
        long? baseline = null;
        var slow = false;
        if (recent.Count >= 8)
        {
            var older = recent.Take(recent.Count - 3).OrderBy(x => x).ToList();
            baseline = older[older.Count / 2];
            var current = recent.Skip(recent.Count - 3).Average();
            slow = cell.TcpSuccess && current >= baseline.Value * 3 && current - baseline.Value >= 50;
        }

        rt.Results[key] = cell with
        {
            LastSuccessUtc = cell.Success ? cell.CheckedAtUtc : prev?.LastSuccessUtc,
            FailingSinceUtc = cell.Success ? null
                : prev is { Success: false } ? prev.FailingSinceUtc ?? prev.CheckedAtUtc
                : cell.CheckedAtUtc,
            AddressesChangedUtc = changed ? cell.CheckedAtUtc : prev?.AddressesChangedUtc,
            PreviousAddresses = changed ? previousAddresses : prev?.PreviousAddresses,
            KnownAddresses = known.Count > 64 ? addresses : known.ToList(),
            Samples = samples,
            RecentMs = recent,
            BaselineMs = baseline,
            Slow = slow
        };
    }

    // Telnet raporunu (isim üzerinden bağlantı + IP bazında sonuçlar) ve varsa TLS sonucunu hücreye çevirir.
    // Hücre ancak TCP bağlantısı kurulduysa ve (TLS kontrolü varsa) sertifika geçerliyse başarılıdır.
    private static PodConnectionCell FromTcp(ProbeReport report, TlsReport? tls, string instanceId, DateTime checkedAtUtc)
    {
        var host = report.HostnameAttempts.FirstOrDefault();
        var tcpOk = host?.Success ?? false;
        return new PodConnectionCell
        {
            InstanceId = instanceId,
            TcpSuccess = tcpOk,
            Success = tcpOk && (tls == null || tls.Success),
            ElapsedMs = host?.ElapsedMs ?? 0,
            ReachedAddress = host?.RemoteAddress,
            Error = host?.Error ?? report.ResolveError ?? (tls is { Success: false } ? "TLS: " + tls.Error : null),
            DnsMs = report.DnsMs,
            Tls = tls,
            CheckedAtUtc = checkedAtUtc,
            IpResults = report.ResolvedAddresses
                .Select(a => new IpResult(a.Address, a.Succeeded > 0, a.Results.FirstOrDefault()?.ElapsedMs ?? 0, a.Results.FirstOrDefault()?.Error))
                .ToList()
        };
    }

    // Her uygulamanın bilinen pod'larını diske yazar.
    private void SavePodState()
    {
        _lastSaveUtc = Now;
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
