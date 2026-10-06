using System.Text.Json;

namespace ConnectivityProbe.Monitor;

/// <summary>Bir uygulamanın kalıcı olarak saklanan pod bilgisi.</summary>
public sealed class PersistedAppPods
{
    /// <summary>Bilinen pod'lar (eksik olanlar dahil).</summary>
    public List<PodStatus> Known { get; set; } = new();
    /// <summary>Pod -> üst üste kaç tur görünmedi.</summary>
    public Dictionary<string, int> Missed { get; set; } = new();
    /// <summary>Bilinen pod'ların hepsinin görüldüğü son turdaki pod sayısı.</summary>
    public int ExpectedPods { get; set; }
    /// <summary>Bağlantı -> o bağlantının hedefinde görülen pod'lar (ConnectivityProbe kullanan hedefler için).</summary>
    public Dictionary<string, TargetMemory> Targets { get; set; } = new();
}

/// <summary>
/// Monitor'ün her uygulama için hatırladığı pod'ları diske yazar (data/pod-state.json). Böylece "eksik" pod'lar Monitor
/// yeniden başlasa bile kaybolmaz; yalnızca arayüzdeki "Pod listesini sıfırla" ile silinir.
/// </summary>
public sealed class PodStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<PodStateStore> _log;

    public PodStateStore(DefinitionStore definitions, ILogger<PodStateStore> log)
    {
        // Tanım dosyasıyla aynı klasörde tutuyoruz.
        _path = Path.Combine(Path.GetDirectoryName(definitions.FilePath)!, "pod-state.json");
        _log = log;
    }

    public Dictionary<string, PersistedAppPods> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, PersistedAppPods>>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (Exception ex)
        {
            // Bozuk dosya Monitor'ün açılmasını engellemesin; pod'lar yeniden keşfedilir.
            _log.LogWarning(ex, "Pod state file could not be read: {Path}", _path);
            return new();
        }
    }

    public void Save(Dictionary<string, PersistedAppPods> state)
    {
        lock (_gate)
        {
            try
            {
                // Önce geçici dosyaya yazıp sonra yer değiştiriyoruz; yazma sırasında kapanırsa dosya bozulmaz.
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pod state file could not be written: {Path}", _path);
            }
        }
    }
}
