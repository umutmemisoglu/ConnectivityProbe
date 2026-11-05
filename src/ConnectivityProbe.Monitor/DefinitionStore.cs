using System.Text.Json;

namespace ConnectivityProbe.Monitor;

/// <summary>Uygulama ve bağlantı tanımlarını bellekte tutar ve her değişiklikte depolamaya (dosya veya MongoDB) yazar.</summary>
public sealed class DefinitionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly IMonitorStorage _storage;
    private readonly DefinitionData _data;

    public DefinitionStore(IMonitorStorage storage)
    {
        // step 1-2: Kayıtlı tanımları okuyoruz; hiç yoksa boş tanımlarla başlıyoruz.
        _storage = storage;
        _data = storage.LoadDefinitions() ?? new DefinitionData();

        // step 3: 2.0'da Discover modu kalktı; uygulamalar anahtarlarıyla kendini kaydeder. Anahtarı olmayan eski (Discover)
        //         uygulama kayıtlarını kaldırıyoruz. Bağlantı havuzu, birimler ve ekipler olduğu gibi kalır.
        var changed = _data.Apps.RemoveAll(a => string.IsNullOrWhiteSpace(a.AppKey)) > 0;

        // step 4: 2.1'de cluster'lar pod ağıyla (ör. 10.42.0.0/16) kendiliğinden adlandırılıyor. 2.0'ın verdiği "Cluster N"
        //         adlarını boşaltıyoruz (boş ad = otomatik ad); elle verilen adlar kalır.
        foreach (var c in _data.Clusters.Where(c => System.Text.RegularExpressions.Regex.IsMatch(c.Name, @"^Cluster \d+$")))
        {
            c.Name = "";
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>Tanımların bağımsız bir kopyasını verir; çağıran istediği gibi okuyabilir.</summary>
    public DefinitionData Snapshot()
    {
        lock (_gate) return Clone(_data);
    }

    /// <summary>Tanımlar üzerinde kilit altında bir değişiklik yapar ve sonucu dosyaya kaydeder.</summary>
    public T Mutate<T>(Func<DefinitionData, T> change)
    {
        lock (_gate)
        {
            var result = change(_data);
            Save();
            return result;
        }
    }

    private void Save() => _storage.SaveDefinitions(_data);

    private static DefinitionData Clone(DefinitionData d) =>
        JsonSerializer.Deserialize<DefinitionData>(JsonSerializer.Serialize(d, Json), Json)!;
}
