namespace ConnectivityProbe.Monitor;

/// <summary>appsettings.json içindeki "Monitor" bölümü.</summary>
public sealed class MonitorOptions
{
    /// <summary>
    /// Ortak erişim anahtarı: izlenen uygulamalarda ve "ConnectivityProbe kullanıyor" işaretli hedeflerde tanımlı
    /// ConnectivityProbe:AccessKey ile aynı olmalıdır. Her isteğe X-ConnectivityProbe-Key başlığıyla eklenir.
    /// Boşsa anahtar gönderilmez (o zaman uygulamalar AllowAnonymous=true olmalıdır).
    /// appsettings.json yerine ortam değişkeniyle vermeniz önerilir: Monitor__AccessKey.
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>Monitor arayüzüne giriş için kullanıcı adı. Varsayılan: admin.</summary>
    public string AdminUser { get; set; } = "admin";

    /// <summary>
    /// Monitor arayüzüne giriş şifresi. Verilmezse arayüze yalnızca Monitor'ün çalıştığı makineden (localhost) erişilebilir;
    /// herkesin erişebildiği bir yerde çalışıyorsa mutlaka verin. Ortam değişkeniyle verin: Monitor__AdminPassword.
    /// Pod'ların kullandığı /api/agent uçları şifre değil, uygulama anahtarıyla korunur.
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>Otomatik test döngüsünün aralığı (saniye). Strict modda pod'ların test aralığı da budur. Varsayılan: 30.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Tek bir bağlantı/istek denemesinin zaman aşımı (ms). Varsayılan: 5000.</summary>
    public int ProbeTimeoutMs { get; set; } = 5000;

    /// <summary>Tanımların saklandığı dosya. Göreli ise uygulama klasörüne göre çözülür. Varsayılan: data/definitions.json</summary>
    public string DataFile { get; set; } = "data/definitions.json";

    /// <summary>Aynı anda kaç uygulama kontrol edilir. Varsayılan: 4.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Pod sayımı için bir uygulamaya atılacak en fazla identity isteği. Varsayılan: 60.</summary>
    public int MaxInstanceAttempts { get; set; } = 60;

    /// <summary>Pod sayımı için hedef güven (0.5-0.999). Varsayılan: 0.95.</summary>
    public double InstanceConfidence { get; set; } = 0.95;

    /// <summary>Bir bağlantıyı tüm pod'larda denemek için en fazla probe isteği. Varsayılan: 40.</summary>
    public int MaxProbeCallsPerConnection { get; set; } = 40;

    /// <summary>
    /// Bir uygulamanın bağlantılarından aynı anda kaç tanesi test edilir. Uygulamalardaki eşzamanlı discover sınırını
    /// (ConnectivityProbe:MaxConcurrentDiscover, varsayılan 20) zorlamamak için düşük tutulur. Varsayılan: 4.
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = 4;

    /// <summary>
    /// Bir pod üst üste bu kadar tur görünmezse "eksik" sayılır (alarm). Daha az turda görünmemesi rastgele dağıtımdan
    /// olabileceği için yalnızca "bu turda görülmedi" diye gösterilir. Yerine yeni bir pod geldiyse (deploy/restart)
    /// hiç alarm verilmez. Varsayılan: 3.
    /// </summary>
    public int MissingAfterCycles { get; set; } = 3;
}
