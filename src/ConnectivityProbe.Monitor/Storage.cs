using System.Text.Json;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Monitor'ün kalıcı verilerinin yazıldığı yer: tanımlar, pod durumu, ayarlar ve bildirim durumu. İki karşılığı vardır:
/// dosyalar (<see cref="FileStorage"/>, varsayılan) ve MongoDB (<see cref="MongoStorage"/>, Monitor:Mongo:ConnectionString
/// verilince). Veriler bellekte tutulur; depolama yalnızca açılışta okunur ve değişiklikte yazılır.
/// </summary>
public interface IMonitorStorage
{
    /// <summary>Günlüğe yazılacak kısa tanım (şifre içermez).</summary>
    string Description { get; }

    /// <summary>Tanımlar; hiç kaydedilmemişse null.</summary>
    DefinitionData? LoadDefinitions();
    void SaveDefinitions(DefinitionData data);

    /// <summary>Uygulama kimliği -> bilinen pod'lar.</summary>
    Dictionary<string, PersistedAppPods> LoadPods();
    void SavePods(Dictionary<string, PersistedAppPods> pods);

    /// <summary>Adıyla saklanan tek parça veri (ör. "settings", "notify-state"); yoksa null.</summary>
    T? LoadDocument<T>(string name) where T : class;
    void SaveDocument<T>(string name, T value);
}

/// <summary>Verileri veri klasöründeki JSON dosyalarına yazar (data/definitions.json, pod-state.json, ...).</summary>
public sealed class FileStorage : IMonitorStorage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _definitionsPath;

    /// <param name="definitionsPath">Tanım dosyasının tam yolu; diğer dosyalar aynı klasöre yazılır.</param>
    public FileStorage(string definitionsPath)
    {
        _definitionsPath = definitionsPath;
        DataDirectory = Path.GetDirectoryName(definitionsPath)!;
    }

    /// <summary>Monitor:DataFile ayarından (göreliyse uygulama klasörüne göre).</summary>
    public static FileStorage From(MonitorOptions options, string contentRoot) =>
        new(Path.IsPathRooted(options.DataFile) ? options.DataFile : Path.Combine(contentRoot, options.DataFile));

    /// <summary>Veri klasörü (oturum anahtarları da burada, keys/ altında tutulur).</summary>
    public string DataDirectory { get; }

    public string Description => $"files ({DataDirectory})";

    public DefinitionData? LoadDefinitions() => Read<DefinitionData>(_definitionsPath);
    public void SaveDefinitions(DefinitionData data) => Write(_definitionsPath, data);

    public Dictionary<string, PersistedAppPods> LoadPods() => Read<Dictionary<string, PersistedAppPods>>(PathOf("pod-state")) ?? new();
    public void SavePods(Dictionary<string, PersistedAppPods> pods) => Write(PathOf("pod-state"), pods);

    public T? LoadDocument<T>(string name) where T : class => Read<T>(PathOf(name));
    public void SaveDocument<T>(string name, T value) => Write(PathOf(name), value);

    private string PathOf(string name) => Path.Combine(DataDirectory, name + ".json");

    private static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;

    private static void Write<T>(string path, T value)
    {
        // Önce geçici dosyaya yazıp sonra yer değiştiriyoruz; yazma sırasında kapanırsa dosya bozulmaz.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        AtomicFile.Replace(tmp, path);
    }
}
