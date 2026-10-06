namespace ConnectivityProbe.Monitor;

/// <summary>appsettings.json içindeki "Monitor" bölümü (ortam değişkeniyle: Monitor__Ad).</summary>
public sealed class MonitorOptions
{
    /// <summary>Monitor arayüzüne giriş için kullanıcı adı. Varsayılan: admin.</summary>
    public string AdminUser { get; set; } = "admin";

    /// <summary>
    /// Monitor arayüzüne giriş şifresi. Verilmezse arayüze yalnızca Monitor'ün çalıştığı makineden (localhost) erişilebilir;
    /// başkalarının erişebildiği bir yerde çalışıyorsa mutlaka verin. Ortam değişkeniyle verin: Monitor__AdminPassword.
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
}
