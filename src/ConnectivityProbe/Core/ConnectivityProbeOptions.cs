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
    }
}
