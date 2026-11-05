namespace ConnectivityProbe.Monitor;

/// <summary>Bir uygulamanın kalıcı olarak saklanan pod bilgisi.</summary>
public sealed class PersistedAppPods
{
    /// <summary>Bilinen pod'lar (eksik olanlar dahil).</summary>
    public List<PodStatus> Known { get; set; } = new();
    /// <summary>Pod -> kaç test aralığıdır bildirim göndermiyor.</summary>
    public Dictionary<string, int> Missed { get; set; } = new();
    /// <summary>Pod -> bildirim sıklığı (sn).</summary>
    public Dictionary<string, int> PollSeconds { get; set; } = new();
    /// <summary>Bilinen pod'ların hepsinin canlı olduğu son andaki pod sayısı.</summary>
    public int ExpectedPods { get; set; }
}

/// <summary>
/// Monitor'ün her uygulama için hatırladığı pod'ları depolamaya (dosya veya MongoDB) yazar. Böylece "eksik" pod'lar Monitor
/// yeniden başlasa bile kaybolmaz; yalnızca arayüzdeki "Pod listesini sıfırla" ile silinir.
/// </summary>
public sealed class PodStateStore
{
    private readonly object _gate = new();
    private readonly IMonitorStorage _storage;
    private readonly ILogger<PodStateStore> _log;

    public PodStateStore(IMonitorStorage storage, ILogger<PodStateStore> log)
    {
        _storage = storage;
        _log = log;
    }

    public Dictionary<string, PersistedAppPods> Load()
    {
        try
        {
            return _storage.LoadPods();
        }
        catch (Exception ex)
        {
            // Bozuk kayıt Monitor'ün açılmasını engellemesin; pod'lar bir sonraki bildirimde yeniden görülür.
            _log.LogWarning(ex, "Pod state could not be read from {Storage}", _storage.Description);
            return new();
        }
    }

    public void Save(Dictionary<string, PersistedAppPods> state)
    {
        lock (_gate)
        {
            try
            {
                _storage.SavePods(state);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pod state could not be written to {Storage}", _storage.Description);
            }
        }
    }
}
