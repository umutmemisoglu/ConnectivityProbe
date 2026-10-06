using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ConnectivityProbe
{
    /// <summary>Log seviyeleri (bkz. <see cref="ConnectivityProbeOptions.Log"/>).</summary>
    public enum ProbeLogLevel
    {
        Debug,
        Information,
        Warning,
        Error
    }

    /// <summary>
    /// ConnectivityProbe ayarları. Kod yazmadan yapılandırmadan okunur; tüm anahtarlar "ConnectivityProbe:" ön ekiyle yazılır:
    /// <list type="bullet">
    /// <item>ASP.NET Core: appsettings.json içinde "ConnectivityProbe" bölümü veya ortam değişkeni (ConnectivityProbe__AccessKey).</item>
    /// <item>IIS / klasik ASP.NET: web.config appSettings (&lt;add key="ConnectivityProbe:AccessKey" value="..." /&gt;) veya ortam değişkeni.</item>
    /// <item>OWIN / dinleyici: ortam değişkeni (.NET Framework'te ayrıca app.config appSettings) veya kodla verilen nesne.</item>
    /// </list>
    /// </summary>
    public sealed class ConnectivityProbeOptions
    {
        /// <summary>Yapılandırma anahtarlarının ön eki.</summary>
        public const string SectionName = "ConnectivityProbe";

        /// <summary>Erişim anahtarının gönderildiği HTTP başlığı (bkz. <see cref="AccessKey"/>).</summary>
        public const string AccessKeyHeader = "X-ConnectivityProbe-Key";

        /// <summary>
        /// instances ucunda hedefin anahtarı farklıysa: bu başlıkla gönderilen anahtar hedefe iletilir
        /// (verilmezse uygulamanın kendi AccessKey'i kullanılır). Merkezi Monitor, bağlantı tanımındaki anahtarı bununla gönderir.
        /// </summary>
        public const string TargetAccessKeyHeader = "X-ConnectivityProbe-Target-Key";

        /// <summary>
        /// ConnectivityProbe'un tüm yanıtlarına (hata yanıtları dahil) eklenen işaret başlığı. Böylece bir hedefin
        /// ConnectivityProbe kullanıp kullanmadığı, istek reddedilse bile anlaşılır.
        /// </summary>
        public const string MarkerHeader = "X-ConnectivityProbe";

        /// <summary>Uçları tamamen kapatır (false). Ayar: ConnectivityProbe:Enabled. Varsayılan: true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Uçların taban yolu: {Path}/discover (telnet + isteğe bağlı pod keşfi), {Path}/identity.
        /// Ayar: ConnectivityProbe:Path. Varsayılan: /connectivity-probe
        /// </summary>
        public string Path { get; set; } = "/connectivity-probe";

        /// <summary>
        /// Paylaşılan erişim anahtarı. Her istek <see cref="AccessKeyHeader"/> başlığında bu değeri taşımalıdır, yoksa 401.
        /// Merkezi Monitor'de uygulamanın tanımına aynı anahtar girilir. Ayar: ConnectivityProbe:AccessKey.
        /// </summary>
        public string? AccessKey { get; set; }

        /// <summary>
        /// Anahtarsız erişime açıkça izin verir. Paket referans verilince kendiliğinden devreye girdiği için güvenli varsayılan
        /// olarak AccessKey de AllowAnonymous da verilmemişse uçlar her isteği 403 ile reddeder.
        /// Yalnızca güvenilir/iç ağlarda veya geliştirme ortamında true yapın. Ayar: ConnectivityProbe:AllowAnonymous. Varsayılan: false.
        /// </summary>
        public bool AllowAnonymous { get; set; }

        /// <summary>
        /// İzin verilen hedefler (büyük/küçük harf duyarsız). Boşsa her hedefe bağlanılabilir. Biçimler:
        /// "sql01:1433" (tam eşleşme), "sql01:*" (her port), "*.svc.cluster.local:443" (alt alan adları), "*.lan:*".
        /// Ayar: ConnectivityProbe:AllowedTargets (virgülle ayrılmış) veya appsettings'te dizi.
        /// </summary>
        public ICollection<string> AllowedTargets { get; } = new List<string>();

        /// <summary>
        /// Aynı anda en fazla kaç discover isteği işlenir; aşan istekler 429 ile reddedilir. Her discover isteği hedefe çok
        /// sayıda bağlantı açabildiği için bu sınır, uçların yük üretmek için kötüye kullanılmasını önler. identity ucu
        /// (ucuz) bu sınıra dahil değildir. 0: sınırsız. Ayar: ConnectivityProbe:MaxConcurrentDiscover. Varsayılan: 20.
        /// </summary>
        public int MaxConcurrentDiscover { get; set; } = 20;

        /// <summary>
        /// İsteğe bağlı log çıkışı. ASP.NET Core'da verilmezse uygulamanın ILogger'ına bağlanır; diğer platformlarda
        /// verilmezse log yazılmaz. Reddedilen istekler Warning, tamamlanan discover istekleri Information seviyesindedir.
        /// </summary>
        public Action<ProbeLogLevel, string>? Log { get; set; }

        /// <summary>timeoutMs verilmediğinde kullanılır. Ayar: ConnectivityProbe:DefaultTimeoutMs. Varsayılan: 5 sn.</summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>timeoutMs için üst sınır. Ayar: ConnectivityProbe:MaxTimeoutMs. Varsayılan: 30 sn.</summary>
        public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Pod keşfinde hedefe atılacak en fazla identity isteği (attempts üst sınırı). Ayar: ConnectivityProbe:MaxAttempts. Varsayılan: 100.</summary>
        public int MaxAttempts { get; set; } = 100;

        /// <summary>
        /// Tek bir isteğin toplam süre sınırı; dolunca yeni deneme başlatılmaz ve yanıt "truncated: true" olur.
        /// Ayar: ConnectivityProbe:MaxRequestDurationSeconds. Varsayılan: 60 sn.
        /// </summary>
        public TimeSpan MaxRequestDuration { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Bir isim için test edilecek en fazla IP sayısı. Ayar: ConnectivityProbe:MaxAddresses. Varsayılan: 64.</summary>
        public int MaxAddresses { get; set; } = 64;

        /// <summary>{Path}/identity ucunu açar/kapatır. Ayar: ConnectivityProbe:EnableIdentity. Varsayılan: true.</summary>
        public bool EnableIdentity { get; set; } = true;

        /// <summary>InstanceId hash'ine eklenir (aynı makine adını paylaşan kopyalar için). Ayar: ConnectivityProbe:InstanceIdSeed.</summary>
        public string InstanceIdSeed { get; set; } = "";

        /// <summary>
        /// identity yanıtına kopyalanacak ortam değişkenleri. Ayar: ConnectivityProbe:IdentityEnvironmentVariables (virgülle;
        /// verilirse varsayılan listenin yerine geçer). Kubernetes'te POD_NAME, NODE_NAME gibi değerler Downward API ile verilmelidir.
        /// </summary>
        public ICollection<string> IdentityEnvironmentVariables { get; } = new List<string>
        {
            "POD_NAME", "POD_NAMESPACE", "POD_IP", "NODE_NAME", "CLUSTER_NAME", "HOSTNAME",
            "APP_POOL_ID", "ASPNETCORE_ENVIRONMENT"
        };

        /// <summary>
        /// identity yanıtına eklenecek sabit bilgiler (cluster, bölge, sürüm...). Ayar: ConnectivityProbe:Info:&lt;ad&gt;,
        /// ör. ConnectivityProbe:Info:cluster = prod-1.
        /// </summary>
        public IDictionary<string, string> Info { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Yalnızca kendi dinleyicisi (<see cref="ConnectivityProbeListener"/>) için: dinlenecek adresler.
        /// Ayar: ConnectivityProbe:ListenerPrefixes (virgülle). Varsayılan: http://+:8099/
        /// </summary>
        public ICollection<string> ListenerPrefixes { get; } = new List<string>();

        // ------------------------------------------------------------------ yapılandırmadan okuma

        /// <summary>
        /// "ConnectivityProbe:" ön eki atılmış anahtar/değer çiftlerinden (ör. "AccessKey", "AllowedTargets:0", "Info:cluster")
        /// ayarları oluşturur. Bilinmeyen anahtarlar yok sayılır; hatalı değerler varsayılanı bozmaz.
        /// </summary>
        public static ConnectivityProbeOptions FromSettings(IEnumerable<KeyValuePair<string, string?>> settings)
        {
            var o = new ConnectivityProbeOptions();
            var s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in settings)
                if (kv.Value != null) s[kv.Key] = kv.Value;

            // step 1: Basit değerler.
            if (TryBool(s, "Enabled", out var enabled)) o.Enabled = enabled;
            if (s.TryGetValue("Path", out var path) && path.Trim().Length > 0) o.Path = "/" + path.Trim().Trim('/');
            if (s.TryGetValue("AccessKey", out var key) && key.Trim().Length > 0) o.AccessKey = key.Trim();
            if (TryBool(s, "AllowAnonymous", out var anonymous)) o.AllowAnonymous = anonymous;
            if (TryBool(s, "EnableIdentity", out var identity)) o.EnableIdentity = identity;
            if (s.TryGetValue("InstanceIdSeed", out var seed)) o.InstanceIdSeed = seed;

            // step 2: Sayısal sınırlar (yalnızca pozitif değerler kabul edilir).
            if (TryPositive(s, "DefaultTimeoutMs", out var v)) o.DefaultTimeout = TimeSpan.FromMilliseconds(v);
            if (TryPositive(s, "MaxTimeoutMs", out v)) o.MaxTimeout = TimeSpan.FromMilliseconds(v);
            if (TryPositive(s, "MaxAttempts", out v)) o.MaxAttempts = v;
            if (TryPositive(s, "MaxRequestDurationSeconds", out v)) o.MaxRequestDuration = TimeSpan.FromSeconds(v);
            if (TryPositive(s, "MaxAddresses", out v)) o.MaxAddresses = v;
            if (s.TryGetValue("MaxConcurrentDiscover", out var concurrentText)
                && int.TryParse(concurrentText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var concurrent))
                o.MaxConcurrentDiscover = concurrent; // 0 = sınırsız

            // step 3: Listeler: "A,B" biçiminde tek değer veya "Liste:0", "Liste:1" biçiminde dizi.
            foreach (var t in ReadList(s, "AllowedTargets")) o.AllowedTargets.Add(t);
            foreach (var p in ReadList(s, "ListenerPrefixes")) o.ListenerPrefixes.Add(p);
            var envVars = ReadList(s, "IdentityEnvironmentVariables");
            if (envVars.Count > 0)
            {
                o.IdentityEnvironmentVariables.Clear();
                foreach (var e in envVars) o.IdentityEnvironmentVariables.Add(e);
            }

            // step 4: Info:<ad> = değer.
            foreach (var kv in s.Where(kv => kv.Key.StartsWith("Info:", StringComparison.OrdinalIgnoreCase)))
                o.Info[kv.Key.Substring("Info:".Length)] = kv.Value;

            return o;
        }

        /// <summary>
        /// Ayarları ortam değişkenlerinden okur (ConnectivityProbe__AccessKey, ConnectivityProbe__Info__cluster ...).
        /// .NET Framework'te ayrıca web.config / app.config appSettings ("ConnectivityProbe:AccessKey") okunur; ortam
        /// değişkeni aynı anahtarı ezer.
        /// </summary>
        public static ConnectivityProbeOptions FromEnvironment() => FromSettings(ProbeSettings.FromEnvironmentAndAppSettings());

        private static bool TryBool(Dictionary<string, string> s, string key, out bool value)
        {
            value = false;
            return s.TryGetValue(key, out var text) && bool.TryParse(text.Trim(), out value);
        }

        private static bool TryPositive(Dictionary<string, string> s, string key, out int value)
        {
            value = 0;
            return s.TryGetValue(key, out var text)
                   && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private static List<string> ReadList(Dictionary<string, string> s, string key)
        {
            var items = new List<string>();
            if (s.TryGetValue(key, out var single)) items.AddRange(single.Split(',', ';'));
            items.AddRange(s.Where(kv => kv.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value));
            return items.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
