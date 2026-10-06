namespace ConnectivityProbe.Monitor;

// ---------------------------------------------------------------------------------------------
// Tanımlar (kalıcı olarak data/definitions.json içinde saklanır)
// ---------------------------------------------------------------------------------------------

/// <summary>Birim (müdürlük), ör. Efatura. Ekiplerden oluşur.</summary>
public sealed class UnitDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Bir birime bağlı ekip. Uygulamaları ve kendi bağlantı havuzu vardır.</summary>
public sealed class TeamDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string UnitId { get; set; } = "";
}

/// <summary>
/// Havuzdaki bir hedef (ör. veritabanı sunucusu, harici API). Uygulamalara sürükle-bırak ile bağlanır.
/// TeamId doluysa o ekibin havuzundadır ve yalnızca o ekibin uygulamalarına atanabilir; boşsa ortak havuzdadır, herkes kullanır.
/// </summary>
public sealed class ConnectionDefinition
{
    public string Id { get; set; } = "";
    /// <summary>Sahip ekip; null = ortak havuz.</summary>
    public string? TeamId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Sunucu adı, IP veya tam URL (https://...). URL ise port boş bırakılabilir.</summary>
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    /// <summary>
    /// Checkpoint: hedef de ConnectivityProbe kullanıyor mu? false (DB, Redis, dış API...): yalnızca telnet (TCP) testi.
    /// true: önce telnet, açıksa hedefin identity ucuyla pod'ları keşfedilir ("hedefin 10 pod'unu buldum").
    /// </summary>
    public bool UsesConnectivityProbe { get; set; }
}

/// <summary>ConnectivityProbe'u yüklemiş, izlenecek bir uygulama.</summary>
public sealed class AppDefinition
{
    public string Id { get; set; } = "";
    /// <summary>Uygulamanın ekibi; null = atanmamış (eski kayıtlar).</summary>
    public string? TeamId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Uygulamanın adresi (ör. https://orders.example.com). Pod'lara dağıtım yapan adres olmalı.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Uygulamada UseConnectivityProbe'un kullandığı yol. Varsayılan: /connectivity-probe</summary>
    public string ProbePath { get; set; } = "/connectivity-probe";
    /// <summary>Bu uygulamayla ilişkilendirilmiş (sürükle-bırakla eklenmiş) bağlantıların kimlikleri.</summary>
    public List<string> ConnectionIds { get; set; } = new();
}

public sealed class DefinitionData
{
    public List<UnitDefinition> Units { get; set; } = new();
    public List<TeamDefinition> Teams { get; set; } = new();
    public List<AppDefinition> Apps { get; set; } = new();
    public List<ConnectionDefinition> Connections { get; set; } = new();
}

// ---------------------------------------------------------------------------------------------
// API çıktıları (gizli bilgiler olmadan)
// ---------------------------------------------------------------------------------------------

/// <summary>Uygulama tanımının arayüze giden hali.</summary>
public sealed record AppView(string Id, string? TeamId, string Name, string BaseUrl, string ProbePath, List<string> ConnectionIds)
{
    public static AppView From(AppDefinition a) => new(a.Id, a.TeamId, a.Name, a.BaseUrl, a.ProbePath, a.ConnectionIds.ToList());
}

/// <summary>Bağlantı tanımının arayüze giden hali. TeamId null = ortak havuz.</summary>
public sealed record ConnectionView(string Id, string? TeamId, string Name, string Host, int? Port, bool UsesConnectivityProbe)
{
    public static ConnectionView From(ConnectionDefinition c) => new(c.Id, c.TeamId, c.Name, c.Host, c.Port, c.UsesConnectivityProbe);
}

public sealed record DefinitionsView(
    List<UnitDefinition> Units, List<TeamDefinition> Teams, List<AppView> Apps, List<ConnectionView> Connections);

// ---------------------------------------------------------------------------------------------
// API girdileri
// ---------------------------------------------------------------------------------------------

public sealed record UnitInput(string? Name);

public sealed record TeamInput(string? Name, string? UnitId);

/// <param name="TeamId">Uygulamanın ekibi (null = atanmamış).</param>
public sealed record AppInput(string? Name, string? BaseUrl, string? TeamId = null, string? ProbePath = null);

/// <param name="TeamId">Sahip ekip; null = ortak havuz.</param>
/// <param name="UsesConnectivityProbe">Hedef de ConnectivityProbe kullanıyor mu (pod keşfi yapılsın mı).</param>
public sealed record ConnectionInput(string? Name, string? Host, int? Port, bool? UsesConnectivityProbe = null, string? TeamId = null);

// ---------------------------------------------------------------------------------------------
// Monitör sonuçları (bellekte tutulur, her döngüde yenilenir)
// ---------------------------------------------------------------------------------------------

public sealed class MonitorSnapshot
{
    public int IntervalSeconds { get; set; }
    /// <summary>Bir pod'un (veya hedef pod'unun) alarm sayılması için üst üste kaç tur görünmemesi gerektiği.</summary>
    public int MissingAfterCycles { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public bool Running { get; set; }
    public List<AppStatus> Apps { get; set; } = new();
}

public sealed class AppStatus
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    /// <summary>healthy | degraded | down | unknown</summary>
    public string State { get; set; } = "unknown";
    public string? Message { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    /// <summary>Bu döngüde cevap veren farklı pod sayısı.</summary>
    public int PodCount { get; set; }
    /// <summary>Pod sayımı için istenen güvene ulaşıldı mı (false ise sayı "en az" anlamındadır).</summary>
    public bool? Converged { get; set; }
    public double? Confidence { get; set; }
    public List<PodStatus> Pods { get; set; } = new();
    public List<ConnectionStatus> Connections { get; set; } = new();
    public List<HistoryPoint> History { get; set; } = new();
}

public sealed class PodStatus
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    public DateTime? StartedAtUtc { get; set; }
    /// <summary>Pod adı, namespace, node gibi ek bilgiler (identity yanıtındaki environment + info).</summary>
    public Dictionary<string, string> Details { get; set; } = new();
    /// <summary>Bu döngüde cevap verdi mi.</summary>
    public bool Seen { get; set; }
    /// <summary>
    /// up: bu turda cevap verdi. unconfirmed: bu turda denk gelinmedi (rastgele dağıtım yüzünden olabilir, alarm değil).
    /// missing: üst üste MissingAfterCycles tur görünmedi ve yerine yeni pod gelmedi (alarm).
    /// </summary>
    public string State { get; set; } = "up";
    /// <summary>Üst üste kaç turdur görünmüyor.</summary>
    public int MissedCycles { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class ConnectionStatus
{
    public string ConnectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>Probe çağrısının kendisi başarısız olduysa nedeni (ör. HTTP 403 = hedef izin listesinde değil).</summary>
    public string? CallError { get; set; }
    public List<PodConnectionCell> Cells { get; set; } = new();
}

public sealed record IpResult(string Address, bool Success, long ElapsedMs, string? Error);

/// <summary>Bir pod'un bir bağlantıyı test ettiği son sonuç.</summary>
public sealed record PodConnectionCell
{
    public string InstanceId { get; init; } = "";
    /// <summary>true: bu döngüde test edildi. false: bu döngüde bu pod'a denk gelinmedi, son bilinen sonuç gösteriliyor.</summary>
    public bool Fresh { get; init; }
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>İsimle bağlanınca gerçekte ulaşılan IP.</summary>
    public string? ReachedAddress { get; init; }
    /// <summary>İsmin çözüldüğü her IP ve her birinin ayrı test sonucu.</summary>
    public List<IpResult> IpResults { get; init; } = new();
    public string? Error { get; init; }
    public DateTime CheckedAtUtc { get; init; }

    // ---- Yalnızca ConnectivityProbe kullanan hedefler (UsesConnectivityProbe) için ----

    /// <summary>unreachable | connectivityProbe | connectivityProbeError | other (bkz. ConnectivityProbe.ProbeTargetKind).</summary>
    public string? TargetKind { get; init; }
    /// <summary>Bu pod'un kendi içinden keşfettiği hedef pod'lar.</summary>
    public List<TargetPod>? TargetPods { get; init; }
    /// <summary>Hedef pod sayımı istenen güvene ulaştı mı (false: "en az" bu kadar).</summary>
    public bool? TargetCountConverged { get; init; }
    /// <summary>Load balancer üzerinden atılıp cevapsız kalan (hangi pod'a gittiği bilinmeyen) istek sayısı.</summary>
    public int TargetFailedRequests { get; init; }
    /// <summary>
    /// Hedefin daha önce görülmüş ama bu pod'un son turlarda erişemediği pod'ları (ne zamandan beri erişilemediğiyle).
    /// Hedef Monitor'de ayrıca kayıtlı olmasa bile bilinir: Monitor her bağlantının hedef pod'larını kendisi hatırlar.
    /// </summary>
    public List<UnreachedTarget>? UnreachedTargets { get; init; }

    // ---- Tüm bağlantılar için ----

    /// <summary>Bu pod'un bu bağlantıdaki son başarılı testi.</summary>
    public DateTime? LastSuccessUtc { get; init; }
    /// <summary>Başarısızsa: kesintisiz olarak ne zamandan beri başarısız.</summary>
    public DateTime? FailingSinceUtc { get; init; }
}

/// <summary>Hedef uygulamanın bir pod'u: test eden pod onu kendi içinden gördü.</summary>
public sealed record TargetPod(string InstanceId, string MachineName, List<string> Addresses, int Hits, string? PodName = null);

/// <summary>
/// Test eden pod'un erişemediği hedef pod'u.
/// Confirmed=false: yalnızca birkaç turdur görülmedi (rastgele dağıtım olabilir, alarm değil).
/// Confirmed=true: üst üste MissingAfterCycles tur erişilemedi (alarm).
/// </summary>
public sealed record UnreachedTarget(
    string InstanceId, string MachineName, string? PodName, int MissedCycles, DateTime SinceUtc, DateTime? LastReachedUtc, bool Confirmed);

/// <summary>Bir bağlantının hedefinde görülen pod'ların hafızası (kalıcı olarak saklanır).</summary>
public sealed class TargetMemory
{
    public Dictionary<string, KnownTargetPod> Pods { get; set; } = new();
    /// <summary>Bilinen hedef pod'ların hepsine erişilen son turdaki hedef pod sayısı (deploy ile "pod çöktü"yü ayırmak için).</summary>
    public int ExpectedTargets { get; set; }
}

public sealed class KnownTargetPod
{
    public string InstanceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string? PodName { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    /// <summary>Herhangi bir pod'un bu hedef pod'a en son eriştiği an.</summary>
    public DateTime? LastReachedUtc { get; set; }
    /// <summary>Hiçbir pod'un erişemediği ardışık tur sayısı.</summary>
    public int Missed { get; set; }
    /// <summary>Test eden pod -> bu hedef pod'a erişim durumu.</summary>
    public Dictionary<string, TargetReach> By { get; set; } = new();
}

public sealed class TargetReach
{
    public DateTime? LastReachedUtc { get; set; }
    /// <summary>Erişilemiyorsa: ilk erişilemeyen tur.</summary>
    public DateTime? MissingSinceUtc { get; set; }
    public int Missed { get; set; }
}

public sealed record HistoryPoint(DateTime AtUtc, string State, int PodCount);
