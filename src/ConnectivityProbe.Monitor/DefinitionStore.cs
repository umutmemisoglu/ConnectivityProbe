using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ConnectivityProbe.Monitor;

/// <summary>Uygulama ve bağlantı tanımlarını bellekte tutar ve her değişiklikte JSON dosyasına yazar.</summary>
public sealed class DefinitionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly DefinitionData _data;

    public DefinitionStore(IOptions<MonitorOptions> options, IHostEnvironment env)
    {
        // step 1: Veri dosyasının yolunu belirliyoruz (göreliyse uygulama klasörüne göre).
        var file = options.Value.DataFile;
        _path = Path.IsPathRooted(file) ? file : Path.Combine(env.ContentRootPath, file);

        // step 2: Dosya varsa okuyoruz, yoksa boş tanımlarla başlıyoruz.
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<DefinitionData>(File.ReadAllText(_path), Json) ?? new DefinitionData()
            : new DefinitionData();

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

    /// <summary>Tanımların saklandığı dosyanın tam yolu (diğer kalıcı dosyalar aynı klasöre yazılır).</summary>
    public string FilePath => _path;

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

    private void Save()
    {
        // Önce geçici dosyaya yazıp sonra yer değiştiriyoruz; yazma sırasında kapanırsa dosya bozulmaz.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static DefinitionData Clone(DefinitionData d) =>
        JsonSerializer.Deserialize<DefinitionData>(JsonSerializer.Serialize(d, Json), Json)!;
}
