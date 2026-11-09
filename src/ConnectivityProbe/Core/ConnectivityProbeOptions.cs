using System.Collections.Generic;

namespace ConnectivityProbe
{
    /// <summary>
    /// ConnectivityProbe ayarları. Zorunlu olanlar (<see cref="MonitorUrl"/>, <see cref="AppKey"/>) doğrudan
    /// <see cref="ConnectivityProbeAgent.Start(string, string, string, System.Action{ConnectivityProbeOptions})"/> parametreleriyle
    /// verilir; buradakiler isteğe bağlı ince ayarlardır ve hepsinin makul bir varsayılanı vardır.
    /// </summary>
    public sealed class ConnectivityProbeOptions
    {
        /// <summary>Uygulama anahtarının Monitor'e gönderildiği HTTP başlığı.</summary>
        public const string AppKeyHeader = "X-ConnectivityProbe-AppKey";

        /// <summary>Monitor'ün adresi (ör. https://monitor.example.com). Zorunlu.</summary>
        public string MonitorUrl { get; set; } = "";

        /// <summary>
        /// Uygulama anahtarı: uygulamanın Monitor'deki kimliği (ör. "orders-api"). Geliştirici belirler; Monitor bu anahtarla ilk
        /// kez gelen uygulamayı kendiliğinden kaydeder. Aynı anahtarı kullanan tüm pod'lar aynı uygulama sayılır. Zorunlu.
        /// </summary>
        public string AppKey { get; set; } = "";

        /// <summary>Monitor'de görünecek uygulama adı. Verilmezse Start'ı çağıran projenin assembly adı kullanılır.</summary>
        public string? AppName { get; set; }

        /// <summary>Test aralığı (sn). 0: Monitor'ün belirlediği aralık (varsayılan 30 sn).</summary>
        public int IntervalSeconds { get; set; }

        /// <summary>
        /// Pod'un Monitor'e ne sıklıkla bildirim gönderdiği (sn). Monitor'deki "Şimdi test et" en geç bu sürede etkisini gösterir;
        /// pod'un canlı sayılması da bu bildirimlere bağlıdır. Varsayılan: 10.
        /// </summary>
        public int PollSeconds { get; set; } = 10;

        /// <summary>Tek bir bağlantı denemesinin (ve DNS çözümlemesinin) zaman aşımı (ms). 0: Monitor'ün belirlediği (varsayılan 5000).</summary>
        public int TimeoutMs { get; set; }

        /// <summary>Bir isim için test edilecek en fazla IP. Varsayılan: 64.</summary>
        public int MaxAddresses { get; set; } = 64;

        /// <summary>Bir pod'da aynı anda test edilen en fazla bağlantı (hedeflere ani yük binmesin diye). Varsayılan: 4.</summary>
        public int MaxParallelTests { get; set; } = 4;

        /// <summary>
        /// Uygulamanın ayarlarını JSON olarak veren adres (ör. merkezi ayar servisi). Verilirse ayarlardaki bağımlılıklar
        /// (veritabanları, kuyruklar, servis adresleri) bulunur ve Monitor'de kendiliğinden tanımlanır. Ayarlar yalnızca pod'da
        /// okunur: Monitor'e yalnızca tür, host ve port gider; şifre, kullanıcı adı ve bu adresin kendisi gitmez. GET ile okunur.
        /// </summary>
        public string? SettingsUrl { get; set; }

        /// <summary><see cref="SettingsUrl"/> yerine ayarların kendisi (JSON metni).</summary>
        public string? SettingsJson { get; set; }

        /// <summary><see cref="SettingsUrl"/> yerine uygulamanın zaten okuduğu ayar nesnesi (ör. RemoteSettings); JSON'a çevrilip taranır.</summary>
        public object? Settings { get; set; }

        /// <summary>Ayarların yeniden okunma aralığı (dk); değişen / eklenen / kaldırılan bağımlılıklar böyle fark edilir. Varsayılan: 10.</summary>
        public int SettingsRefreshMinutes { get; set; } = 10;

        /// <summary>
        /// ConnectivityProbe health ucu açılsın mı (http://&lt;pod-ip&gt;:&lt;HealthPort&gt;/connectivityprobe/health). Başka
        /// uygulamaların pod'ları bu servisi test ederken DLL'i kullandığını ve durumunu buradan anlar. Varsayılan: açık.
        /// </summary>
        public bool HealthEnabled { get; set; } = true;

        /// <summary>
        /// Health ucunun portu. Uygulamanın kendi portundan ayrıdır; doluysa sonraki 9 port, o da olmazsa boş bir port kullanılır
        /// ve Monitor'e bildirilir. Varsayılan: 29470.
        /// </summary>
        public int HealthPort { get; set; } = 29470;

        /// <summary>
        /// Monitor'de bağlantıların gruplanması: adresinde (host / URL) veya adında anahtar kelimelerden biri geçen bağlantılar
        /// uygulamanın detayında o grubun altında gösterilir. Örnek: <c>o.AddGroup("Ödeme servisleri", "pay", "billing")</c>.
        /// </summary>
        public List<ConnectionGroup> Groups { get; set; } = new List<ConnectionGroup>();

        /// <summary>Bağlantı grubu ekler (bkz. <see cref="Groups"/>).</summary>
        public ConnectivityProbeOptions AddGroup(string name, params string[] keywords)
        {
            Groups.Add(new ConnectionGroup { Name = name, Keywords = new List<string>(keywords ?? new string[0]) });
            return this;
        }
    }

    /// <summary>Monitor'de bağlantıların gruplanması: ad ve aranacak anahtar kelimeler (büyük / küçük harf duyarsız).</summary>
    public sealed class ConnectionGroup
    {
        public string Name { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }
}
