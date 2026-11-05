using Microsoft.Extensions.Options;

namespace ConnectivityProbe.Monitor;

/// <summary>Teams bildirimlerinin tek seferlik kurulum ayarları (Monitor → Ayarlar → Teams bildirimleri).</summary>
public sealed class NotifySettings
{
    /// <summary>Entra ID uygulama kaydı: Directory (tenant) ID.</summary>
    public string? TenantId { get; set; }
    /// <summary>Entra ID uygulama kaydı: Application (client) ID.</summary>
    public string? ClientId { get; set; }
    /// <summary>Merkezi Teams iş akışının adresi (gizli; arayüze geri gönderilmez).</summary>
    public string? WorkflowUrl { get; set; }
    /// <summary>
    /// Monitor'ün kullanıcıların açtığı adresi (ör. https://monitor.sirket.com). Verilirse Microsoft girişinin dönüş adresi
    /// ve Teams mesajlarındaki bağlantılar bununla kurulur; TLS'i önde sonlandıran bir yük dengeleyici "https" bilgisini
    /// iletmese bile doğru adres kullanılır.
    /// </summary>
    public string? PublicUrl { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool SignInConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
    [System.Text.Json.Serialization.JsonIgnore] public bool WorkflowConfigured => !string.IsNullOrWhiteSpace(WorkflowUrl);
    /// <summary>Kurulum tamam: "Bana haber ver" Microsoft girişine gider ve bildirimler gönderilir.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Ready => SignInConfigured && WorkflowConfigured;
}

/// <summary>
/// Kurulum ayarlarını depolamada ("settings": dosya veya MongoDB) tutar. Arayüzden girilen değer, appsettings / ortam
/// değişkenindeki değerin (Monitor:Auth:*, Monitor:Notifications:WorkflowUrl) önüne geçer; arayüzde boş bırakılan alan için o
/// değer kullanılır.
/// </summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly IMonitorStorage _storage;
    private readonly MonitorOptions _config;
    private NotifySettings _saved;

    public SettingsStore(IMonitorStorage storage, IOptions<MonitorOptions> options)
    {
        _storage = storage;
        _config = options.Value;
        _saved = storage.LoadDocument<NotifySettings>("settings") ?? new();
    }

    /// <summary>Ayar değiştiğinde (ör. Microsoft girişinin yeniden yapılandırılması için).</summary>
    public event Action? Changed;

    /// <summary>Geçerli ayarlar: arayüzde girilen, yoksa yapılandırmadaki değer.</summary>
    public NotifySettings Current
    {
        get
        {
            lock (_gate)
                return new NotifySettings
                {
                    // GUID'ler küçük harfe çevrilir: Entra ID belirteçteki "aud" değerini küçük harfle yazar ve doğrulama
                    // büyük/küçük harfe duyarlıdır (büyük harfle girilen Client ID girişi bozardı).
                    TenantId = Guid(Pick(_saved.TenantId, _config.Auth.TenantId)),
                    ClientId = Guid(Pick(_saved.ClientId, _config.Auth.ClientId)),
                    WorkflowUrl = Pick(_saved.WorkflowUrl, _config.Notifications.WorkflowUrl),
                    PublicUrl = Pick(_saved.PublicUrl, _config.PublicUrl)?.TrimEnd('/')
                };
        }
    }

    /// <summary>
    /// Ayarları kaydeder. null = değiştirme; boş metin = temizle (yapılandırmadaki değere dön). İş akışı adresi gizli olduğu için
    /// arayüz onu hiç görmez; yalnızca yeni değer gönderilirse değişir.
    /// </summary>
    public void Save(string? tenantId, string? clientId, string? workflowUrl, string? publicUrl = null)
    {
        lock (_gate)
        {
            if (tenantId != null) _saved.TenantId = tenantId.Trim();
            if (clientId != null) _saved.ClientId = clientId.Trim();
            if (workflowUrl != null) _saved.WorkflowUrl = workflowUrl.Trim();
            if (publicUrl != null) _saved.PublicUrl = publicUrl.Trim().TrimEnd('/');
            _storage.SaveDocument("settings", _saved);
        }
        Changed?.Invoke();
    }

    private static string? Guid(string? value) =>
        value != null && System.Guid.TryParse(value, out var g) ? g.ToString("D") : value;

    private static string? Pick(string? saved, string? configured) =>
        !string.IsNullOrWhiteSpace(saved) ? saved.Trim() : string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
}
