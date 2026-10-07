namespace ConnectivityProbe.Monitor;

public enum NotifyEventKind { Opened, Resolved, Event, Flapping }

/// <summary>
/// Bildirime dönüşecek tek bir gelişme. Data, mesaj metnindeki ayrıntılardır (ör. "failed" = "5/5", "percent" = "94").
/// </summary>
public sealed record NotifyEvent(
    string Rule, NotifyEventKind Kind, Severity Severity, string Subject, DateTime AtUtc, DateTime? SinceUtc,
    IReadOnlyDictionary<string, string> Data);

/// <summary>Bir uygulamanın aynı turda oluşan gelişmeleri (tek mesajda gönderilir).</summary>
public sealed record NotifyBatch(string AppId, string AppName, IReadOnlyList<NotifyEvent> Events);

/// <summary>
/// Uygulama durumlarını (AppStatus) zaman içinde izleyip bildirim üretir. Kurallar:
/// <list type="bullet">
/// <item><b>Doğrulama:</b> bir durum ancak belirli bir süre kesintisiz sürerse "açılır" (ör. bağlantı üst üste 3 testte
/// başarısız, bellek 5 dakika boyunca %90 üstünde). Tek seferlik hatalar bildirim üretmez.</item>
/// <item><b>Tek olay, tek mesaj:</b> açık bir durum sürdükçe yeniden bildirilmez; düzelince (2 test aralığı boyunca sorun
/// görülmezse) kapanır ve istenirse "düzeldi" mesajı gider.</item>
/// <item><b>Gidip gelme:</b> bir durum bir saatte 3'ten fazla açılırsa tek bir "kararsız" mesajı gönderilir; bir saat sakin
/// kalana kadar o durum için yeni mesaj gönderilmez.</item>
/// <item><b>Deploy:</b> yeni sürüme geçen pod'ların yeniden başlaması "pod yeniden başladı" sayılmaz.</item>
/// </list>
/// Tüm durumlar her zaman izlenir; hangi bildirimlerin gönderileceğine uygulamanın seçili kuralları karar verir (Filter).
/// </summary>
public sealed class NotificationEngine
{
    private sealed class Track
    {
        public DateTime? TrueSince { get; set; }
        public DateTime? FalseSince { get; set; }
        public bool Open { get; set; }
        public DateTime OpenedAtUtc { get; set; }
        public bool Flapping { get; set; }
        public List<DateTime> Opens { get; set; } = new();
        public string Subject { get; set; } = "";
        public Dictionary<string, string> Data { get; set; } = new();
    }

    /// <summary>Monitor yeniden başlasa da açık durumlar ve verilmiş sertifika uyarıları unutulmasın diye diske yazılan hal.</summary>
    public sealed class State
    {
        public Dictionary<string, PersistedTrack> Open { get; set; } = new();
        public Dictionary<string, int> CertNotified { get; set; } = new();
        public Dictionary<string, string> StableBuild { get; set; } = new();
    }

    public sealed record PersistedTrack(string Subject, DateTime OpenedAtUtc, Dictionary<string, string> Data);

    private static readonly int[] CertThresholds = { 14, 7, 3, 1 };
    private static readonly TimeSpan FlapWindow = TimeSpan.FromHours(1);
    private const int FlapLimit = 3;

    private readonly TimeSpan _interval;
    private readonly Dictionary<string, Track> _tracks = new();
    private readonly Dictionary<string, int> _restarts = new();           // uygulama|pod -> son görülen yeniden başlama sayısı
    private readonly Dictionary<string, List<DateTime>> _restartTimes = new();
    private readonly Dictionary<string, string> _podBuild = new();         // uygulama|pod -> son görülen sürüm/build
    private readonly Dictionary<string, DateTime> _ipSeen = new();          // uygulama|bağlantı -> son görülen IP değişikliği
    private readonly Dictionary<string, int> _certNotified = new();       // uygulama|bağlantı -> son bildirilen gün eşiği
    private readonly Dictionary<string, string> _stableBuild = new();     // uygulama -> tüm pod'ların çalıştırdığı son sürüm
    private readonly HashSet<string> _seenApps = new();

    public NotificationEngine(TimeSpan testInterval) => _interval = testInterval;

    /// <summary>Durumlar değiştiğinde true (diske yazmak için).</summary>
    public bool Dirty { get; private set; }

    // ---------------------------------------------------------------------------------------------
    // Değerlendirme
    // ---------------------------------------------------------------------------------------------

    /// <summary>Uygulamanın güncel durumunu işler ve bu turda oluşan gelişmeleri döner (tüm kurallar için; bkz. Filter).</summary>
    public List<NotifyEvent> Evaluate(AppStatus s, DateTime now)
    {
        var events = new List<NotifyEvent>();
        var seen = new HashSet<string>();
        var first = _seenApps.Add(s.AppId); // ilk görüşte IP değişikliği için başlangıç değeri alınır, bildirim üretilmez
        var appKey = s.AppId + "|";
        var up = s.Pods.Where(p => p.State == "up").ToList();

        void Condition(string rule, string subjectKey, string subject, bool active, TimeSpan confirm, Dictionary<string, string>? data = null)
        {
            var key = appKey + rule + "|" + subjectKey;
            seen.Add(key);
            Observe(key, rule, subject, active, confirm, data ?? new(), now, events);
        }

        // --- Uygulama ve pod'lar
        Condition("appDown", "", s.Name, s.State == "down" && s.Pods.Count > 0, _interval * 3);

        foreach (var p in s.Pods)
        {
            var name = PodName(p);
            var podKey = appKey + p.InstanceId;
            Condition("podMissing", p.InstanceId, name, p.State == "missing", TimeSpan.Zero,
                new() { ["lastSeen"] = p.LastSeenUtc.ToString("O") });

            // Yeniden başlamalar: sürüm/build değiştiyse deploy'dur, sayılmaz.
            var build = p.AppVersion + "|" + p.BuildId;
            var buildChanged = _podBuild.TryGetValue(podKey, out var prevBuild) && prevBuild != build;
            _podBuild[podKey] = build;
            if (_restarts.TryGetValue(podKey, out var prevRestarts) && p.Restarts > prevRestarts && !buildChanged)
            {
                if (!_restartTimes.TryGetValue(podKey, out var times)) _restartTimes[podKey] = times = new();
                for (var i = prevRestarts; i < p.Restarts; i++) times.Add(now);
                var oom = p.LastOomUtc is { } o && now - o < TimeSpan.FromMinutes(2);
                events.Add(new NotifyEvent("podRestart", NotifyEventKind.Event, Severity.Medium, name, now, null,
                    new Dictionary<string, string> { ["oom"] = oom ? "1" : "0", ["count"] = p.Restarts.ToString() }));
            }
            _restarts[podKey] = p.Restarts;
            // Son 30 dakikadaki yeniden başlamalar (crash loop: 3 veya daha fazla).
            var recent = 0;
            if (_restartTimes.TryGetValue(podKey, out var list))
            {
                list.RemoveAll(t => now - t > TimeSpan.FromMinutes(30));
                recent = list.Count;
            }
            Condition("crashLoop", p.InstanceId, name, recent >= 3, TimeSpan.Zero, new() { ["count"] = recent.ToString() });

            // --- Kaynaklar (eşikler Monitor:Alerts; pod uyarısı olarak gelir)
            Condition("memHigh", p.InstanceId, name, p.Alerts.Contains("memHigh"), TimeSpan.FromMinutes(5),
                new() { ["percent"] = MemPercent(p) });
            Condition("throttled", p.InstanceId, name, p.Alerts.Contains("throttled"), TimeSpan.FromMinutes(10),
                new() { ["percent"] = ((int)Math.Round(p.Resources?.CpuThrottledPercent ?? 0)).ToString() });
            Condition("portsHigh", p.InstanceId, name, p.Alerts.Contains("portsHigh"), TimeSpan.FromMinutes(5),
                new() { ["percent"] = PortPercent(p) });
        }

        // --- Bağlantılar. Üst üste 3 test başarısız = hücre 2 test aralığıdır başarısız.
        var failedFor = _interval * 2;
        foreach (var c in s.Connections)
        {
            var fresh = c.Cells.Where(x => x.Fresh).ToList();
            bool Confirmed(PodConnectionCell x) => !x.Success && x.FailingSinceUtc is { } since && now - since >= failedFor;
            var tcpFailed = fresh.Where(x => Confirmed(x) && !x.TcpSuccess).ToList();
            var tlsFailed = fresh.Where(x => Confirmed(x) && x.TcpSuccess && x.Tls is { Success: false }).ToList();
            var podNames = s.Pods.ToDictionary(p => p.InstanceId, PodName);
            Dictionary<string, string> FailData(List<PodConnectionCell> cells) => new()
            {
                ["failed"] = cells.Count + "/" + fresh.Count,
                ["error"] = cells.Select(x => x.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e)) ?? "",
                ["pods"] = string.Join(", ", cells.Select(x => podNames.TryGetValue(x.InstanceId, out var n) ? n : x.InstanceId).Take(5)),
                ["target"] = c.Target
            };

            Condition("connDown", c.ConnectionId, c.Name, fresh.Count > 0 && tcpFailed.Count == fresh.Count, TimeSpan.Zero, FailData(tcpFailed));
            Condition("connPartial", c.ConnectionId, c.Name, tcpFailed.Count > 0 && tcpFailed.Count < fresh.Count, TimeSpan.Zero, FailData(tcpFailed));
            Condition("certInvalid", c.ConnectionId, c.Name, tlsFailed.Count > 0, TimeSpan.Zero, FailData(tlsFailed));
            Condition("connSlow", c.ConnectionId, c.Name, fresh.Any(x => x.Slow), TimeSpan.FromMinutes(5), new()
            {
                ["baseline"] = fresh.Where(x => x.Slow).Select(x => x.BaselineMs ?? 0).DefaultIfEmpty().Max().ToString(),
                ["now"] = fresh.Where(x => x.Slow).Select(x => x.ElapsedMs).DefaultIfEmpty().Max().ToString(),
                ["target"] = c.Target
            });

            // IP değişikliği (olay): Monitor zaten round-robin DNS'i ayıklıyor, burada yalnızca yeni değişiklik bildirilir.
            var connKey = appKey + c.ConnectionId;
            var changed = c.Cells.Where(x => x.AddressesChangedUtc != null).OrderByDescending(x => x.AddressesChangedUtc).FirstOrDefault();
            if (changed?.AddressesChangedUtc is { } at)
            {
                var known = _ipSeen.TryGetValue(connKey, out var last);
                if (!first && (!known || at > last))
                    events.Add(new NotifyEvent("ipChanged", NotifyEventKind.Event, Severity.Info, c.Name, now, at, new Dictionary<string, string>
                    {
                        ["from"] = string.Join(", ", changed.PreviousAddresses ?? new List<string>()),
                        ["to"] = string.Join(", ", changed.IpResults.Select(r => r.Address)),
                        ["target"] = c.Target
                    }));
                _ipSeen[connKey] = at;
            }

            // Sertifika bitişi (olay): 14, 7, 3 ve 1 gün eşiklerinde birer kez. Sertifika yenilenince sıfırlanır.
            var ends = fresh.Where(x => x.Tls is { Success: true, NotAfterUtc: not null }).Select(x => x.Tls!.NotAfterUtc!.Value).ToList();
            if (ends.Count > 0)
            {
                var end = ends.Min();
                var days = (int)Math.Floor((end - now).TotalDays);
                var threshold = CertThresholds.Where(t => days <= t).DefaultIfEmpty(int.MaxValue).Min();
                if (threshold == int.MaxValue)
                {
                    if (_certNotified.Remove(connKey)) Dirty = true;
                }
                else if (days >= 0 && (!_certNotified.TryGetValue(connKey, out var notified) || threshold < notified))
                {
                    _certNotified[connKey] = threshold;
                    Dirty = true;
                    events.Add(new NotifyEvent("certExpiring", NotifyEventKind.Event, days <= 3 ? Severity.High : Severity.Medium, c.Name, now, null,
                        new Dictionary<string, string> { ["days"] = days.ToString(), ["end"] = end.ToString("O"), ["target"] = c.Target }));
                }
            }
        }

        // --- Sürüm / deploy
        var builds = up.Select(p => p.AppVersion + (string.IsNullOrEmpty(p.BuildId) ? "" : " · " + p.BuildId[..Math.Min(6, p.BuildId.Length)]))
            .Distinct().ToList();
        if (builds.Count == 1)
        {
            if (_stableBuild.TryGetValue(s.AppId, out var stable) && stable != builds[0])
                events.Add(new NotifyEvent("deployDone", NotifyEventKind.Event, Severity.Info, s.Name, now, null,
                    new Dictionary<string, string> { ["from"] = stable, ["to"] = builds[0], ["pods"] = up.Count.ToString() }));
            if (!_stableBuild.TryGetValue(s.AppId, out var current) || current != builds[0]) { _stableBuild[s.AppId] = builds[0]; Dirty = true; }
        }
        Condition("deployStuck", "", s.Name, builds.Count > 1, TimeSpan.FromMinutes(30), new() { ["versions"] = string.Join(", ", builds) });

        // Artık görünmeyen konular (pod gitti, bağlantı çıkarıldı) için durumlar "düzeldi" sayılır.
        foreach (var key in _tracks.Keys.Where(k => k.StartsWith(appKey, StringComparison.Ordinal) && !seen.Contains(k)).ToList())
        {
            var t = _tracks[key];
            Observe(key, key.Split('|')[1], t.Subject, false, TimeSpan.Zero, t.Data, now, events);
            if (!t.Open && t.TrueSince == null && !t.Flapping) _tracks.Remove(key);
        }

        // Olaylar ilk görüşte de doğrudur: yeniden başlama, IP ve sürüm için önce başlangıç değeri alınır (yukarıda); sertifika
        // uyarıları diske yazıldığı için Monitor yeniden başlayınca tekrarlanmaz.
        return events;
    }

    /// <summary>
    /// Uygulamanın seçili kurallarına göre gönderilecek gelişmeler. "Düzeldi" mesajları yalnızca "resolved" seçiliyse ve
    /// ilgili kural da seçiliyse gider.
    /// </summary>
    public static List<NotifyEvent> Filter(IEnumerable<NotifyEvent> events, IReadOnlyCollection<string> rules) =>
        events.Where(e => rules.Contains(e.Rule) && (e.Kind != NotifyEventKind.Resolved || rules.Contains("resolved"))).ToList();

    // Bir durumu bir tur ilerletir: doğrulama süresi dolunca açar, 2 test aralığı sorunsuz geçince kapatır.
    private void Observe(string key, string rule, string subject, bool active, TimeSpan confirm, Dictionary<string, string> data,
        DateTime now, List<NotifyEvent> events)
    {
        if (!_tracks.TryGetValue(key, out var t))
        {
            if (!active) return;
            _tracks[key] = t = new Track();
        }
        t.Subject = subject;
        var severity = NotifyRules.ByCode.TryGetValue(rule, out var r) ? r.Severity : Severity.Medium;

        // Bir saattir yeni açılış yoksa "kararsız" işareti kalkar.
        t.Opens.RemoveAll(o => now - o > FlapWindow);
        if (t.Flapping && t.Opens.Count == 0 && !t.Open) t.Flapping = false;

        if (active)
        {
            t.FalseSince = null;
            t.TrueSince ??= now;
            t.Data = data;
            if (!t.Open && now - t.TrueSince.Value >= confirm)
            {
                t.Open = true;
                t.OpenedAtUtc = t.TrueSince.Value;
                t.Opens.Add(now);
                Dirty = true;
                if (t.Opens.Count > FlapLimit)
                {
                    if (!t.Flapping)
                    {
                        t.Flapping = true;
                        events.Add(new NotifyEvent(rule, NotifyEventKind.Flapping, severity, subject, now, t.OpenedAtUtc, data));
                    }
                }
                else if (!t.Flapping)
                {
                    events.Add(new NotifyEvent(rule, NotifyEventKind.Opened, severity, subject, now, t.OpenedAtUtc, data));
                }
            }
            return;
        }

        t.TrueSince = null;
        if (!t.Open) return;
        t.FalseSince ??= now;
        if (now - t.FalseSince.Value >= _interval * 2)
        {
            t.Open = false;
            t.FalseSince = null;
            Dirty = true;
            if (!t.Flapping)
                events.Add(new NotifyEvent(rule, NotifyEventKind.Resolved, Severity.Info, subject, now, t.OpenedAtUtc, t.Data));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Kalıcılık
    // ---------------------------------------------------------------------------------------------

    public State Export()
    {
        Dirty = false;
        return new State
        {
            Open = _tracks.Where(kv => kv.Value.Open).ToDictionary(kv => kv.Key, kv => new PersistedTrack(kv.Value.Subject, kv.Value.OpenedAtUtc, kv.Value.Data)),
            CertNotified = new Dictionary<string, int>(_certNotified),
            StableBuild = new Dictionary<string, string>(_stableBuild)
        };
    }

    /// <summary>
    /// Önceki çalışmadan kalan açık durumları geri yükler: Monitor yeniden başlayınca sürmekte olan bir sorun yeniden
    /// "başladı" diye bildirilmez; düzelince "düzeldi" mesajı yine gider.
    /// </summary>
    public void Import(State state)
    {
        foreach (var (key, p) in state.Open)
            _tracks[key] = new Track { Open = true, OpenedAtUtc = p.OpenedAtUtc, Subject = p.Subject, Data = p.Data, TrueSince = p.OpenedAtUtc };
        foreach (var kv in state.CertNotified) _certNotified[kv.Key] = kv.Value;
        foreach (var kv in state.StableBuild) _stableBuild[kv.Key] = kv.Value;
    }

    /// <summary>Uygulama silindi veya pod listesi sıfırlandı: o uygulamanın izleme durumunu unutur.</summary>
    public void Forget(string appId)
    {
        var prefix = appId + "|";
        foreach (var k in _tracks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _tracks.Remove(k);
        foreach (var k in _restarts.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _restarts.Remove(k);
        foreach (var k in _restartTimes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _restartTimes.Remove(k);
        foreach (var k in _podBuild.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _podBuild.Remove(k);
        _seenApps.Remove(appId);
        Dirty = true;
    }

    // ---------------------------------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------------------------------

    internal static string PodName(PodStatus p) =>
        p.Details.TryGetValue("POD_NAME", out var n) && !string.IsNullOrEmpty(n) ? n
        : p.Details.TryGetValue("HOSTNAME", out var h) && !string.IsNullOrEmpty(h) ? h
        : p.MachineName;

    private static string MemPercent(PodStatus p) =>
        p.Resources is { MemoryLimitBytes: > 0, MemoryBytes: { } m } r ? ((int)Math.Round(100.0 * m / r.MemoryLimitBytes!.Value)).ToString() : "";

    private static string PortPercent(PodStatus p) =>
        p.Resources is { EphemeralPorts: > 0, TcpEstablished: { } e, TcpTimeWait: { } w } r ? ((int)Math.Round(100.0 * (e + w) / r.EphemeralPorts!.Value)).ToString() : "";
}
