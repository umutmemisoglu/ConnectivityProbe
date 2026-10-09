namespace ConnectivityProbe.Monitor;

/// <summary>appsettings.json içindeki "Monitor" bölümü (ortam değişkeniyle: Monitor__Ad).</summary>
public sealed class MonitorOptions
{
    /// <summary>Monitor arayüzüne giriş için kullanıcı adı. Varsayılan: admin.</summary>
    public string AdminUser { get; set; } = "admin";

    /// <summary>
    /// Monitor arayüzüne giriş şifresi (isteğe bağlı). Verilmezse arayüz Monitor'e erişebilen herkese açıktır; verilirse
    /// giriş sayfası çıkar. Ortam değişkeniyle verin: Monitor__AdminPassword.
    /// Pod'ların kullandığı /api/agent uçları şifre değil, uygulama anahtarıyla çalışır.
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>Pod'ların test aralığı (sn); pod'lara bildirim yanıtında gönderilir. Varsayılan: 30.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Pod'ların tek bir bağlantı denemesinde kullandığı zaman aşımı (ms). Varsayılan: 5000.</summary>
    public int ProbeTimeoutMs { get; set; } = 5000;

    /// <summary>Tanımların saklandığı dosya. Göreli ise uygulama klasörüne göre çözülür. Varsayılan: data/definitions.json</summary>
    public string DataFile { get; set; } = "data/definitions.json";

    /// <summary>
    /// Bir pod bu kadar test aralığı boyunca bildirim göndermezse "eksik" sayılır (alarm). Daha kısa gecikmeler "bildirim
    /// gecikti" olarak gösterilir. Düzgün kapanan pod ("kapanıyorum" bildirimi) ve yerine yenisi gelen pod alarm vermez.
    /// Varsayılan: 3.
    /// </summary>
    public int MissingAfterCycles { get; set; } = 3;

    /// <summary>Uyarı eşikleri (bkz. <see cref="AlertOptions"/>).</summary>
    public AlertOptions Alerts { get; set; } = new();

    /// <summary>Teams bildirimleri: arayüzdeki Ayarlar boşsa kullanılan varsayılan (bkz. SettingsStore).</summary>
    public NotificationOptions Notifications { get; set; } = new();

    /// <summary>Microsoft (Entra ID) ile giriş: arayüzdeki Ayarlar boşsa kullanılan varsayılan (bkz. SettingsStore).</summary>
    public AuthOptions Auth { get; set; } = new();

}

/// <summary>"Monitor:Notifications" bölümü.</summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// Merkezi Teams iş akışının (Workflows) adresi: gelen mesajı içindeki e-postaya Flow bot ile özel mesaj olarak gönderir.
    /// Gizlidir; Ayarlar ekranından ya da ortam değişkeniyle (secret) verin: Monitor__Notifications__WorkflowUrl.
    /// </summary>
    public string? WorkflowUrl { get; set; }

    public bool Central => !string.IsNullOrWhiteSpace(WorkflowUrl);
}

/// <summary>"Monitor:Auth" bölümü: Entra ID uygulama kaydının bilgileri (gizli değildir; client secret gerekmez).</summary>
public sealed class AuthOptions
{
    /// <summary>Directory (tenant) ID.</summary>
    public string? TenantId { get; set; }
    /// <summary>Application (client) ID.</summary>
    public string? ClientId { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Kaynak, sertifika ve bağlantı uyarılarının eşikleri ("Monitor:Alerts" bölümü).</summary>
public sealed class AlertOptions
{
    /// <summary>Container bellek kullanımı limitin bu yüzdesini geçerse uyarı (sorunlu). Varsayılan: 90.</summary>
    public int MemoryPercent { get; set; } = 90;
    /// <summary>CPU limiti yüzünden yavaşlatılma oranı bu yüzdeyi geçerse bilgi notu. Varsayılan: 25.</summary>
    public int CpuThrottledPercent { get; set; } = 25;
    /// <summary>TCP soketleri (kurulu + TIME_WAIT) yerel port aralığının bu yüzdesini geçerse uyarı (sorunlu). Varsayılan: 70.</summary>
    public int PortsPercent { get; set; } = 70;
    /// <summary>Sertifikanın bitişine bu kadar gün kala uyarı (sorunlu). Varsayılan: 14.</summary>
    public int CertificateDays { get; set; } = 14;
    /// <summary>Yeniden başlama, OOM ve IP değişikliği bu kadar dakika boyunca gösterilir. Varsayılan: 60.</summary>
    public int RecentMinutes { get; set; } = 60;
}
