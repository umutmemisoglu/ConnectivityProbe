using System.Text.Json;
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

    [System.Text.Json.Serialization.JsonIgnore] public bool SignInConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
    [System.Text.Json.Serialization.JsonIgnore] public bool WorkflowConfigured => !string.IsNullOrWhiteSpace(WorkflowUrl);
    /// <summary>Kurulum tamam: "Bana haber ver" Microsoft girişine gider ve bildirimler gönderilir.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Ready => SignInConfigured && WorkflowConfigured;
}

/// <summary>
/// Kurulum ayarlarını veri klasöründe (data/settings.json) tutar. Arayüzden girilen değer, appsettings / ortam değişkenindeki
/// değerin (Monitor:Auth:*, Monitor:Notifications:WorkflowUrl) önüne geçer; arayüzde boş bırakılan alan için o değer kullanılır.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly MonitorOptions _config;
    private NotifySettings _saved;

    public SettingsStore(DefinitionStore definitions, IOptions<MonitorOptions> options)
    {
        _path = Path.Combine(Path.GetDirectoryName(definitions.FilePath)!, "settings.json");
        _config = options.Value;
        _saved = File.Exists(_path) ? JsonSerializer.Deserialize<NotifySettings>(File.ReadAllText(_path), Json) ?? new() : new();
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
                    TenantId = Pick(_saved.TenantId, _config.Auth.TenantId),
                    ClientId = Pick(_saved.ClientId, _config.Auth.ClientId),
                    WorkflowUrl = Pick(_saved.WorkflowUrl, _config.Notifications.WorkflowUrl)
                };
        }
    }

    /// <summary>
    /// Ayarları kaydeder. null = değiştirme; boş metin = temizle (yapılandırmadaki değere dön). İş akışı adresi gizli olduğu için
    /// arayüz onu hiç görmez; yalnızca yeni değer gönderilirse değişir.
    /// </summary>
    public void Save(string? tenantId, string? clientId, string? workflowUrl)
    {
        lock (_gate)
        {
            if (tenantId != null) _saved.TenantId = tenantId.Trim();
            if (clientId != null) _saved.ClientId = clientId.Trim();
            if (workflowUrl != null) _saved.WorkflowUrl = workflowUrl.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_saved, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke();
    }

    private static string? Pick(string? saved, string? configured) =>
        !string.IsNullOrWhiteSpace(saved) ? saved.Trim() : string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
}
