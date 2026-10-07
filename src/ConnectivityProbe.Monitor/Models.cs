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
/// Havuzdaki bir hedef (ör. veritabanı sunucusu, harici API). Havuz tektir, birim ve ekiplerden bağımsızdır: her bağlantı
/// her uygulamaya sürükle-bırak ile atanabilir. Uygulamanın her pod'u bu hedefe kendi içinden bağlanarak test eder.
/// (2.0'daki ekip havuzları kalktı; eski dosyadaki "teamId" alanı okunurken yok sayılır, bağlantılar tek havuzda toplanır.)
/// </summary>
public sealed class ConnectionDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Sunucu adı, IP veya tam URL (https://...). URL ise port boş bırakılabilir.</summary>
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    /// <summary>
    /// Hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği. Bağlantı satırında hedef uygulamanın pod sayısı ve durumu da
    /// gösterilir (ör. "hedef: Orders API · 3 pod · Sağlıklı").
    /// </summary>
    public string? TargetAppId { get; set; }
    /// <summary>
    /// TLS / sertifika kontrolü: "auto" (varsayılan; https:// ve 443, 8443, 636, 993, 995, 465, 5671 portlarında), "on" veya "off".
    /// </summary>
    public string? TlsCheck { get; set; }

    private static readonly int[] TlsPorts = { 443, 8443, 636, 993, 995, 465, 5671 };

    /// <summary>Pod'lar bu bağlantıda TCP'ye ek olarak TLS el sıkışması ve sertifika kontrolü yapsın mı.</summary>
    public bool TlsEnabled()
    {
        if (TlsCheck == "on") return true;
        if (TlsCheck == "off") return false;
        return ProbeTarget.TryParse(Host, Port?.ToString(), out _, out var port, out var scheme, out _)
            && (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) || TlsPorts.Contains(port));
    }
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
    /// <summary>Bildirim üreten kurallar (bkz. NotifyRules). Kişiden bağımsızdır; null = varsayılanlar (eski kayıtlar).</summary>
    public List<string>? NotifyRules { get; set; }
    /// <summary>Bu uygulamanın bildirimlerini alan kişiler.</summary>
    public List<string> SubscriberIds { get; set; } = new();
}

/// <summary>
/// Pod'ların otomatik tespit edilen cluster'ı. Anahtar: Kubernetes cluster sertifikasının parmak izi ("k8s:...") ya da
/// Kubernetes dışında pod'un ağı ("net:10.80.0.0/16"). Ad boşsa pod'ların ağından türetilir (ör. "10.42.0.0/16");
/// Monitor'de elle ad verilebilir.
/// </summary>
public sealed class ClusterDefinition
{
    public string Key { get; set; } = "";
    /// <summary>Elle verilen ad; boş = otomatik (pod ağı).</summary>
    public string Name { get; set; } = "";
}

public sealed class DefinitionData
{
    public List<UnitDefinition> Units { get; set; } = new();
    public List<TeamDefinition> Teams { get; set; } = new();
    public List<AppDefinition> Apps { get; set; } = new();
    public List<ConnectionDefinition> Connections { get; set; } = new();
    public List<ClusterDefinition> Clusters { get; set; } = new();
    public List<PersonDefinition> People { get; set; } = new();
}

// ---------------------------------------------------------------------------------------------
// API çıktıları ve girdileri
// ---------------------------------------------------------------------------------------------

public sealed record AppView(string Id, string? TeamId, string Name, string AppKey, DateTime RegisteredAtUtc, List<string> ConnectionIds,
    List<string> NotifyRules, List<string> SubscriberIds)
{
    public static AppView From(AppDefinition a) => new(a.Id, a.TeamId, a.Name, a.AppKey, a.RegisteredAtUtc, a.ConnectionIds.ToList(),
        a.NotifyRules ?? Monitor.NotifyRules.Defaults(), a.SubscriberIds.ToList());
}

/// <summary>Kişinin arayüze giden hali (Teams adresi hiçbir zaman gönderilmez).</summary>
public sealed record PersonView(string Id, string Name)
{
    public static PersonView From(PersonDefinition p) => new(p.Id, p.Name);
}

/// <summary>Bildirim kuralının arayüze giden hali.</summary>
public sealed record RuleView(string Code, string Category, string Kind, string Severity, bool Default)
{
    public static RuleView From(NotifyRule r) => new(r.Code, r.Category, r.Kind.ToString(), r.Severity.ToString(), r.Default);
}

public sealed record NotifyInput(List<string>? Rules);

/// <summary>Ayarlar → Teams bildirimleri. null = değiştirme; iş akışı adresi yalnızca yeni değer yazılınca değişir.</summary>
public sealed record NotifySettingsInput(string? TenantId, string? ClientId, string? WorkflowUrl);

public sealed record NotifyTestInput(string? Email);

/// <summary>Bağlantı tanımının arayüze giden hali.</summary>
public sealed record ConnectionView(string Id, string Name, string Host, int? Port, string? TargetAppId, string TlsCheck, bool Tls)
{
    public static ConnectionView From(ConnectionDefinition c) =>
        new(c.Id, c.Name, c.Host, c.Port, c.TargetAppId, c.TlsCheck ?? "auto", c.TlsEnabled());
}

public sealed record DefinitionsView(
    List<UnitDefinition> Units, List<TeamDefinition> Teams, List<AppView> Apps, List<ConnectionView> Connections, List<ClusterDefinition> Clusters,
    List<PersonView> People, List<RuleView> Rules);

public sealed record UnitInput(string? Name);

public sealed record TeamInput(string? Name, string? UnitId);

/// <summary>Uygulamada değiştirilebilenler: ad ve ekip (anahtar uygulamanın kimliğidir, değişmez).</summary>
public sealed record AppInput(string? Name, string? TeamId = null);

/// <param name="TargetAppId">Hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği (isteğe bağlı).</param>
/// <param name="TlsCheck">auto | on | off (boş = auto).</param>
public sealed record ConnectionInput(string? Name, string? Host, int? Port, string? TargetAppId = null, string? TlsCheck = null);

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

/// <param name="Tls">TCP'ye ek olarak TLS el sıkışması ve sertifika kontrolü yapılsın mı.</param>
public sealed record AgentConnection(string Id, string Name, string Host, int? Port, bool Tls);

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
    /// <summary>Sertifika bitişine kaç gün kala uyarı verildiği (arayüz renklendirmesi için).</summary>
    public int CertificateDays { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public List<AppStatus> Apps { get; set; } = new();
    /// <summary>Görülen cluster'lar (ad ve pod sayısıyla).</summary>
    public List<ClusterView> Clusters { get; set; } = new();
}

/// <param name="Name">Görünen ad: elle verilen ad ya da pod'ların ağı (ör. "10.42.0.0/16").</param>
/// <param name="Networks">Bu cluster'daki pod'ların ağları.</param>
/// <param name="Custom">Ad elle verildiyse true.</param>
public sealed record ClusterView(string Key, string Name, List<string> Networks, bool Custom, int Pods, int Apps);

/// <summary>
/// Uygulama durumunu açıklayan not. Metin arayüzde seçili dilde üretilir.
/// Kodlar: waiting, noReports (AtUtc), missingPods (Count), failedTests (Count), latePods (Count), versions (Count), firstResults.
/// </summary>
public sealed record StatusNote(string Code, int Count = 0, DateTime? AtUtc = null);

public sealed class AppStatus
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string AppKey { get; set; } = "";
    /// <summary>healthy | degraded | down | unknown</summary>
    public string State { get; set; } = "unknown";
    /// <summary>Durumun nedenleri (önce sorunlar, sonra bilgi notları).</summary>
    public List<StatusNote> Notes { get; set; } = new();
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
    /// <summary>Pod'un asıl IP adresi (Monitor'e giderken kullandığı) ve bu adresin ağı (ör. 10.42.0.0/16).</summary>
    public string? PrimaryAddress { get; set; }
    public string? Network { get; set; }
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

    /// <summary>Son kaynak ölçümü (CPU, bellek, limitler, thread'ler, TCP soketleri).</summary>
    public ResourceSample? Resources { get; set; }
    /// <summary>Son ölçümlerin kısa geçmişi (grafikler için; diske yazılmaz).</summary>
    public List<ResourcePoint>? ResourceHistory { get; set; }
    /// <summary>Monitor'ün gördüğü yeniden başlama sayısı (aynı pod, yeni süreç) ve son yeniden başlama.</summary>
    public int Restarts { get; set; }
    public DateTime? LastRestartUtc { get; set; }
    /// <summary>Container'da OOM kill sayısının son arttığı an.</summary>
    public DateTime? LastOomUtc { get; set; }
    /// <summary>Pod uyarıları: memHigh, throttled, restart, oom, portsHigh (arayüz seçili dilde gösterir).</summary>
    public List<string> Alerts { get; set; } = new();
}

/// <summary>Grafikler için kısa kaynak geçmişi noktası.</summary>
public sealed record ResourcePoint(DateTime AtUtc, double? CpuCores, long MemoryBytes, double? ThrottledPercent, long? NetRxBytes, long? NetTxBytes);

public sealed class ConnectionStatus
{
    public string ConnectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>Hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği.</summary>
    public string? TargetAppId { get; set; }
    /// <summary>Bu bağlantıda TLS / sertifika kontrolü yapılıyor mu.</summary>
    public bool Tls { get; set; }
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

    /// <summary>TCP bağlantısının sonucu (Success, TLS kontrolü varsa onu da kapsar).</summary>
    public bool TcpSuccess { get; init; }
    /// <summary>DNS çözümleme süresi (ms).</summary>
    public long? DnsMs { get; init; }
    /// <summary>TLS el sıkışması ve sertifika (bağlantıda TLS kontrolü açıksa).</summary>
    public TlsReport? Tls { get; init; }

    /// <summary>Ad çözümlemesinin döndüğü IP'lerin son değiştiği an ve önceki IP'ler.</summary>
    public DateTime? AddressesChangedUtc { get; init; }
    public List<string>? PreviousAddresses { get; init; }
    /// <summary>Bu bağlantıda şimdiye kadar görülen IP'ler (round-robin DNS'te sahte "IP değişti" uyarısını önler) ve ölçüm sayısı.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<string> KnownAddresses { get; init; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public int Samples { get; init; }

    /// <summary>Son başarılı bağlantı süreleri (ms; en fazla 20) ve olağan süre (ortanca).</summary>
    public List<long> RecentMs { get; init; } = new();
    public long? BaselineMs { get; init; }
    /// <summary>Son ölçümler olağan sürenin 3 katından yavaş.</summary>
    public bool Slow { get; init; }
}

public sealed record HistoryPoint(DateTime AtUtc, string State, int PodCount);
