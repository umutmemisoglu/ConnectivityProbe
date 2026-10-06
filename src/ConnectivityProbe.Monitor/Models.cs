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
/// Havuzdaki bir hedef (ör. veritabanı sunucusu, harici API). Uygulamalara sürükle-bırak ile atanır; uygulamanın her pod'u
/// bu hedefe kendi içinden TCP bağlantısı açarak test eder.
/// TeamId doluysa o ekibin havuzundadır ve yalnızca o ekibin uygulamalarına atanabilir; boşsa ortak havuzdadır.
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
    /// Hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği. Bağlantı satırında hedef uygulamanın pod sayısı ve durumu da
    /// gösterilir (ör. "hedef: Orders API · 3 pod · Sağlıklı").
    /// </summary>
    public string? TargetAppId { get; set; }
}

/// <summary>
/// ConnectivityProbe'u kullanan ve Monitor'e anahtarıyla kendini kaydetmiş uygulama. Monitor'de elle eklenmez: bilinmeyen bir
/// anahtarla ilk bildirim geldiğinde kendiliğinden oluşur ("Atanmamış" altında).
/// </summary>
public sealed class AppDefinition
{
    public string Id { get; set; } = "";
    /// <summary>Uygulamanın ekibi; null = atanmamış (yeni kaydolan uygulamalar).</summary>
    public string? TeamId { get; set; }
    /// <summary>İlk kayıtta uygulamanın bildirdiği ad; sonradan Monitor'de değiştirilebilir.</summary>
    public string Name { get; set; } = "";
    /// <summary>Uygulama anahtarı: geliştiricinin ConnectivityProbeAgent.Start'a verdiği değer. Uygulamanın kimliğidir, değişmez.</summary>
    public string AppKey { get; set; } = "";
    public DateTime RegisteredAtUtc { get; set; }
    /// <summary>Bu uygulamaya atanmış bağlantıların kimlikleri.</summary>
    public List<string> ConnectionIds { get; set; } = new();
}

/// <summary>
/// Pod'ların otomatik tespit edilen cluster'ı. Anahtar: Kubernetes cluster sertifikasının parmak izi ("k8s:...") ya da
/// Kubernetes dışında bildirimin geldiği ağ adresi ("net:..."). Ad ilk görüldüğünde "Cluster N" olur, Monitor'de değiştirilebilir.
/// </summary>
public sealed class ClusterDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class DefinitionData
{
    public List<UnitDefinition> Units { get; set; } = new();
    public List<TeamDefinition> Teams { get; set; } = new();
    public List<AppDefinition> Apps { get; set; } = new();
    public List<ConnectionDefinition> Connections { get; set; } = new();
    public List<ClusterDefinition> Clusters { get; set; } = new();
}

// ---------------------------------------------------------------------------------------------
// API çıktıları ve girdileri
// ---------------------------------------------------------------------------------------------

public sealed record AppView(string Id, string? TeamId, string Name, string AppKey, DateTime RegisteredAtUtc, List<string> ConnectionIds)
{
    public static AppView From(AppDefinition a) => new(a.Id, a.TeamId, a.Name, a.AppKey, a.RegisteredAtUtc, a.ConnectionIds.ToList());
}

/// <summary>Bağlantı tanımının arayüze giden hali. TeamId null = ortak havuz.</summary>
public sealed record ConnectionView(string Id, string? TeamId, string Name, string Host, int? Port, string? TargetAppId)
{
    public static ConnectionView From(ConnectionDefinition c) => new(c.Id, c.TeamId, c.Name, c.Host, c.Port, c.TargetAppId);
}

public sealed record DefinitionsView(
    List<UnitDefinition> Units, List<TeamDefinition> Teams, List<AppView> Apps, List<ConnectionView> Connections, List<ClusterDefinition> Clusters);

public sealed record UnitInput(string? Name);

public sealed record TeamInput(string? Name, string? UnitId);

/// <summary>Uygulamada değiştirilebilenler: ad ve ekip (anahtar uygulamanın kimliğidir, değişmez).</summary>
public sealed record AppInput(string? Name, string? TeamId = null);

/// <param name="TeamId">Sahip ekip; null = ortak havuz.</param>
/// <param name="TargetAppId">Hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği (isteğe bağlı).</param>
public sealed record ConnectionInput(string? Name, string? Host, int? Port, string? TeamId = null, string? TargetAppId = null);

public sealed record ClusterInput(string? Name);

/// <summary>Monitor arayüzü girişi.</summary>
public sealed record LoginInput(string? Username, string? Password);

// ---------------------------------------------------------------------------------------------
// Pod'lara (ConnectivityProbeAgent) dönen yanıt
// ---------------------------------------------------------------------------------------------

/// <summary>Pod'un bildirimine yanıt: bu uygulamaya ait bağlantılar ve test ayarları.</summary>
/// <param name="RunRequestId">"Şimdi test et"e her basıldığında değişir; pod değiştiğini görünce beklemeden test eder.</param>
public sealed record AgentAssignment(
    string AppId, string AppName, int IntervalSeconds, string RunRequestId, int TimeoutMs, List<AgentConnection> Connections);

public sealed record AgentConnection(string Id, string Name, string Host, int? Port);

public sealed record AgentGoodbye(string? InstanceId);

// ---------------------------------------------------------------------------------------------
// Monitör sonuçları (bellekte tutulur)
// ---------------------------------------------------------------------------------------------

public sealed class MonitorSnapshot
{
    /// <summary>Pod'ların test aralığı (sn).</summary>
    public int IntervalSeconds { get; set; }
    /// <summary>Bir pod'un "eksik" sayılması için bildirim göndermeden geçmesi gereken test aralığı sayısı.</summary>
    public int MissingAfterCycles { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public List<AppStatus> Apps { get; set; } = new();
    /// <summary>Görülen cluster'lar (ad ve pod sayısıyla).</summary>
    public List<ClusterView> Clusters { get; set; } = new();
}

public sealed record ClusterView(string Key, string Name, int Pods, int Apps);

public sealed class AppStatus
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string AppKey { get; set; } = "";
    /// <summary>healthy | degraded | down | unknown</summary>
    public string State { get; set; } = "unknown";
    public string? Message { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    /// <summary>Şu an bildirim gönderen (canlı) pod sayısı. Pod'lar kendini bildirdiği için kesindir.</summary>
    public int PodCount { get; set; }
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
    /// <summary>Pod adı, node, ortam gibi ek bilgiler (pod'un bildirdiği seçili ortam değişkenleri).</summary>
    public Dictionary<string, string> Details { get; set; } = new();
    /// <summary>Şu an bildirim gönderiyor mu.</summary>
    public bool Seen { get; set; }
    /// <summary>up: canlı. unconfirmed: bildirim gecikti (alarm değil). missing: üst üste MissingAfterCycles aralık bildirim yok (alarm).</summary>
    public string State { get; set; } = "up";
    /// <summary>Kaç test aralığıdır bildirim göndermiyor.</summary>
    public int MissedCycles { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Pod'daki ConnectivityProbe kütüphanesinin sürümü.</summary>
    public string? ProbeVersion { get; set; }
    /// <summary>Uygulamanın sürümü (AssemblyInformationalVersion / AssemblyVersion'dan büyük olanı).</summary>
    public string? AppVersion { get; set; }
    /// <summary>Uygulamanın build kimliği (MVID'nin kısa hali): aynı sürüm numarasındaki farklı build'leri ayırt eder.</summary>
    public string? BuildId { get; set; }
    public DateTime? BuildDateUtc { get; set; }
    /// <summary>Cluster anahtarı (bkz. ClusterDefinition) ve adı.</summary>
    public string? ClusterKey { get; set; }
    public string? ClusterName { get; set; }
    public string? Namespace { get; set; }
    /// <summary>Bildirimin geldiği ağ adresi.</summary>
    public string? SourceIp { get; set; }
}

public sealed class ConnectionStatus
{
    public string ConnectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>Hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği.</summary>
    public string? TargetAppId { get; set; }
    public List<PodConnectionCell> Cells { get; set; } = new();
}

public sealed record IpResult(string Address, bool Success, long ElapsedMs, string? Error);

/// <summary>Bir pod'un bir bağlantıyı test ettiği son sonuç.</summary>
public sealed record PodConnectionCell
{
    public string InstanceId { get; init; } = "";
    /// <summary>true: pod canlı ve sonuç son test turlarından. false: son bilinen sonuç (soluk gösterilir).</summary>
    public bool Fresh { get; init; }
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>İsimle bağlanınca gerçekte ulaşılan IP.</summary>
    public string? ReachedAddress { get; init; }
    /// <summary>İsmin çözüldüğü her IP ve her birinin ayrı test sonucu.</summary>
    public List<IpResult> IpResults { get; init; } = new();
    public string? Error { get; init; }
    public DateTime CheckedAtUtc { get; init; }
    /// <summary>Bu pod'un bu bağlantıdaki son başarılı testi.</summary>
    public DateTime? LastSuccessUtc { get; init; }
    /// <summary>Başarısızsa: kesintisiz olarak ne zamandan beri başarısız.</summary>
    public DateTime? FailingSinceUtc { get; init; }
}

public sealed record HistoryPoint(DateTime AtUtc, string State, int PodCount);
