using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Verileri MongoDB'ye yazar (Monitor:Mongo:ConnectionString verilince).
/// <list type="bullet">
/// <item>Tanımlar ayrı koleksiyonlarda, her kayıt ayrı belge: units, teams, apps, connections, clusters, people.</item>
/// <item>pods: uygulama başına pod durumu; documents: ayarlar ve bildirim durumu; dataProtectionKeys: oturum anahtarları.</item>
/// </list>
/// Yalnızca değişen belgeler yazılır. Mongo'ya açılışta erişilemezse Monitor açılmaz (boş veriyle açılıp kayıtların üzerine
/// yazmamak için).
/// </summary>
public sealed class MongoStorage : IMonitorStorage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonWriterSettings Relaxed = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };
    private static readonly FilterDefinitionBuilder<BsonDocument> Filter = Builders<BsonDocument>.Filter;

    private readonly IMongoDatabase _db;
    private readonly object _gate = new();
    // Koleksiyon -> belge kimliği -> son yazılan hali; aynıysa yeniden yazılmaz.
    private readonly Dictionary<string, Dictionary<string, string>> _written = new();

    public MongoStorage(string connectionString, string database)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ApplicationName = "ConnectivityMonitor";
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
        _db = new MongoClient(settings).GetDatabase(database);
        _db.RunCommand<BsonDocument>(new BsonDocument("ping", 1));   // erişilemiyorsa burada hata verir
        Description = $"MongoDB ({database} @ {string.Join(", ", settings.Servers)})";
        KeyRepository = new MongoXmlRepository(_db.GetCollection<BsonDocument>("dataProtectionKeys"));
    }

    public string Description { get; }

    /// <summary>Oturum çerezlerini şifreleyen anahtarların deposu (pod yeniden başlasa da oturumlar sürer).</summary>
    public IXmlRepository KeyRepository { get; }

    // -----------------------------------------------------------------------------------------
    // Tanımlar: her tür ayrı koleksiyon, her kayıt ayrı belge (_id = kaydın kimliği, order = listedeki sırası)
    // -----------------------------------------------------------------------------------------

    public DefinitionData? LoadDefinitions()
    {
        lock (_gate)
        {
            var data = new DefinitionData
            {
                Units = LoadEntities<UnitDefinition>("units"),
                Teams = LoadEntities<TeamDefinition>("teams"),
                Apps = LoadEntities<AppDefinition>("apps"),
                Connections = LoadEntities<ConnectionDefinition>("connections"),
                Clusters = LoadEntities<ClusterDefinition>("clusters"),
                People = LoadEntities<PersonDefinition>("people"),
            };
            var empty = data.Units.Count + data.Teams.Count + data.Apps.Count + data.Connections.Count
                + data.Clusters.Count + data.People.Count == 0;
            return empty ? null : data;
        }
    }

    public void SaveDefinitions(DefinitionData data)
    {
        lock (_gate)
        {
            SyncEntities("units", data.Units, x => x.Id);
            SyncEntities("teams", data.Teams, x => x.Id);
            SyncEntities("apps", data.Apps, x => x.Id);
            SyncEntities("connections", data.Connections, x => x.Id);
            SyncEntities("clusters", data.Clusters, x => x.Key);
            SyncEntities("people", data.People, x => x.Id);
        }
    }

    private List<T> LoadEntities<T>(string collection)
    {
        var written = new Dictionary<string, string>();
        var items = new List<T>();
        var docs = _db.GetCollection<BsonDocument>(collection).Find(Filter.Empty).ToList()
            .OrderBy(d => d.GetValue("order", 0).ToInt32());
        foreach (var doc in docs)
        {
            var id = doc["_id"].ToString()!;
            doc.Remove("_id");
            doc.Remove("order");
            var item = JsonSerializer.Deserialize<T>(doc.ToJson(Relaxed), Json)!;
            items.Add(item);
            written[id] = $"{items.Count - 1}|{JsonSerializer.Serialize(item, Json)}";
        }
        _written[collection] = written;
        return items;
    }

    private void SyncEntities<T>(string collection, List<T> items, Func<T, string> idOf)
    {
        var before = _written.GetValueOrDefault(collection) ?? new();
        var after = new Dictionary<string, string>();
        var ops = new List<WriteModel<BsonDocument>>();
        for (var i = 0; i < items.Count; i++)
        {
            var id = idOf(items[i]);
            var json = JsonSerializer.Serialize(items[i], Json);
            var signature = $"{i}|{json}";
            after[id] = signature;
            if (before.GetValueOrDefault(id) == signature) continue;
            var doc = BsonDocument.Parse(json);
            doc.InsertAt(0, new BsonElement("_id", id));
            doc["order"] = i;
            ops.Add(new ReplaceOneModel<BsonDocument>(Filter.Eq("_id", id), doc) { IsUpsert = true });
        }
        foreach (var gone in before.Keys.Except(after.Keys))
            ops.Add(new DeleteOneModel<BsonDocument>(Filter.Eq("_id", gone)));
        if (ops.Count > 0)
            _db.GetCollection<BsonDocument>(collection).BulkWrite(ops, new BulkWriteOptions { IsOrdered = false });
        _written[collection] = after;   // yazma başarısız olursa önceki hal kalır, sonraki kayıtta yeniden denenir
    }

    // -----------------------------------------------------------------------------------------
    // Pod durumu ve tek parça belgeler: { _id, json, updatedAtUtc }. İçlerinde pod kimliği gibi serbest anahtarlar
    // olduğu için (Mongo alan adlarında "." ve "$" sorun çıkarır) JSON metni olarak saklanır.
    // -----------------------------------------------------------------------------------------

    public Dictionary<string, PersistedAppPods> LoadPods()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, PersistedAppPods>();
            var written = new Dictionary<string, string>();
            foreach (var doc in _db.GetCollection<BsonDocument>("pods").Find(Filter.Empty).ToList())
            {
                var json = doc["json"].AsString;
                result[doc["_id"].AsString] = JsonSerializer.Deserialize<PersistedAppPods>(json, Json)!;
                written[doc["_id"].AsString] = json;
            }
            _written["pods"] = written;
            return result;
        }
    }

    public void SavePods(Dictionary<string, PersistedAppPods> pods)
    {
        lock (_gate)
            SyncBlobs("pods", pods.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value, Json)));
    }

    public T? LoadDocument<T>(string name) where T : class
    {
        lock (_gate)
        {
            var doc = _db.GetCollection<BsonDocument>("documents").Find(Filter.Eq("_id", name)).FirstOrDefault();
            return doc == null ? null : JsonSerializer.Deserialize<T>(doc["json"].AsString, Json);
        }
    }

    public void SaveDocument<T>(string name, T value)
    {
        lock (_gate)
        {
            var doc = new BsonDocument { { "_id", name }, { "json", JsonSerializer.Serialize(value, Json) }, { "updatedAtUtc", DateTime.UtcNow } };
            _db.GetCollection<BsonDocument>("documents").ReplaceOne(Filter.Eq("_id", name), doc, new ReplaceOptions { IsUpsert = true });
        }
    }

    private void SyncBlobs(string collection, Dictionary<string, string> current)
    {
        var before = _written.GetValueOrDefault(collection) ?? new();
        var ops = new List<WriteModel<BsonDocument>>();
        var now = DateTime.UtcNow;
        foreach (var (id, json) in current)
        {
            if (before.GetValueOrDefault(id) == json) continue;
            var doc = new BsonDocument { { "_id", id }, { "json", json }, { "updatedAtUtc", now } };
            ops.Add(new ReplaceOneModel<BsonDocument>(Filter.Eq("_id", id), doc) { IsUpsert = true });
        }
        foreach (var gone in before.Keys.Except(current.Keys))
            ops.Add(new DeleteOneModel<BsonDocument>(Filter.Eq("_id", gone)));
        if (ops.Count > 0)
            _db.GetCollection<BsonDocument>(collection).BulkWrite(ops, new BulkWriteOptions { IsOrdered = false });
        _written[collection] = new Dictionary<string, string>(current);
    }

    // -----------------------------------------------------------------------------------------
    // Dosyalardan ilk aktarım
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Mongo ilk kez kullanılıyorsa (henüz hiç veri yoksa) dosyalardaki verileri aktarır: tanımlar, pod durumu, ayarlar,
    /// bildirim durumu ve oturum anahtarları. Bir kez yapılır; sonra "meta" belgesi bunu kaydeder.
    /// </summary>
    public void ImportFromFilesIfEmpty(FileStorage files, ILogger log)
    {
        if (LoadDocument<object>("meta") != null) return;
        var hasData = LoadDefinitions() != null || LoadPods().Count > 0
            || _db.GetCollection<BsonDocument>("documents").CountDocuments(Filter.Empty) > 0;

        var imported = new List<string>();
        if (!hasData)
        {
            if (files.LoadDefinitions() is { } defs)
            {
                SaveDefinitions(defs);
                imported.Add($"{defs.Apps.Count} apps, {defs.Connections.Count} connections, {defs.People.Count} people");
            }
            if (files.LoadPods() is { Count: > 0 } pods) { SavePods(pods); imported.Add($"pod state of {pods.Count} apps"); }
            if (files.LoadDocument<NotifySettings>("settings") is { } settings) { SaveDocument("settings", settings); imported.Add("settings"); }
            if (files.LoadDocument<NotificationEngine.State>("notify-state") is { } state) { SaveDocument("notify-state", state); imported.Add("notification state"); }
            var keysDir = Path.Combine(files.DataDirectory, "keys");
            if (Directory.Exists(keysDir))
            {
                var keys = Directory.GetFiles(keysDir, "*.xml");
                foreach (var file in keys) KeyRepository.StoreElement(XElement.Load(file), Path.GetFileNameWithoutExtension(file));
                if (keys.Length > 0) imported.Add($"{keys.Length} login keys");
            }
        }
        SaveDocument("meta", new { createdAtUtc = DateTime.UtcNow, importedFromFiles = imported });
        if (imported.Count > 0) log.LogInformation("Imported from {Dir} into MongoDB: {Items}", files.DataDirectory, string.Join("; ", imported));
    }
}

/// <summary>ASP.NET Core oturum anahtarlarını Mongo'da saklar: pod yeniden başlasa da kullanıcıların oturumları sürer.</summary>
internal sealed class MongoXmlRepository : IXmlRepository
{
    private readonly IMongoCollection<BsonDocument> _keys;

    public MongoXmlRepository(IMongoCollection<BsonDocument> keys) => _keys = keys;

    public IReadOnlyCollection<XElement> GetAllElements() =>
        _keys.Find(Builders<BsonDocument>.Filter.Empty).ToList().Select(d => XElement.Parse(d["xml"].AsString)).ToList();

    public void StoreElement(XElement element, string friendlyName)
    {
        var id = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString() : friendlyName;
        var doc = new BsonDocument { { "_id", id }, { "xml", element.ToString(SaveOptions.DisableFormatting) }, { "createdAtUtc", DateTime.UtcNow } };
        _keys.ReplaceOne(Builders<BsonDocument>.Filter.Eq("_id", id), doc, new ReplaceOptions { IsUpsert = true });
    }
}
