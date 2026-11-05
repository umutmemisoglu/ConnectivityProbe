using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace ConnectivityProbe.Monitor.Tests;

/// <summary>
/// MongoDB karşılığının testleri. Yalnızca CP_TEST_MONGO ortam değişkeni verilince çalışır (ör. mongodb://localhost:27017);
/// verilmezse hiçbir şey yapmadan geçer. Her test kendi geçici veritabanını açar ve sonunda siler.
/// </summary>
public sealed class MongoStorageTests : IDisposable
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CP_TEST_MONGO");
    private readonly string _database = "cp_test_" + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        if (Url != null) new MongoClient(Url).DropDatabase(_database);
    }

    private MongoStorage Open() => new(Url!, _database);
    private IMongoCollection<BsonDocument> Collection(string name) => new MongoClient(Url).GetDatabase(_database).GetCollection<BsonDocument>(name);

    [Fact]
    public void Definitions_are_stored_one_document_per_record_and_survive_a_restart()
    {
        if (Url == null) return;
        var storage = Open();
        Assert.Null(storage.LoadDefinitions());

        var data = new DefinitionData
        {
            Units = { new UnitDefinition { Id = "u1", Name = "eLogo" } },
            Teams = { new TeamDefinition { Id = "t1", UnitId = "u1", Name = "ECM" } },
            Apps = { new AppDefinition { Id = "a1", TeamId = "t1", Name = "ECM-API", AppKey = "k1", ConnectionIds = { "c1" } } },
            Connections = { new ConnectionDefinition { Id = "c1", Name = "Ana DB", Host = "sql01", Port = 1433 }, new ConnectionDefinition { Id = "c2", Name = "Cache", Host = "redis", Port = 6379 } },
        };
        storage.SaveDefinitions(data);

        // Her kayıt ayrı belge, alanları okunabilir.
        var c1 = Collection("connections").Find(new BsonDocument("_id", "c1")).Single();
        Assert.Equal("sql01", c1["host"].AsString);
        Assert.Equal(1433, c1["port"].ToInt32());

        var reloaded = Open().LoadDefinitions()!;
        Assert.Equal(new[] { "c1", "c2" }, reloaded.Connections.Select(c => c.Id));     // sıra korunur
        Assert.Equal("ECM-API", reloaded.Apps.Single().Name);
        Assert.Equal(new[] { "c1" }, reloaded.Apps.Single().ConnectionIds);
    }

    [Fact]
    public void Only_changed_records_are_written_and_removed_ones_are_deleted()
    {
        if (Url == null) return;
        var storage = Open();
        var data = new DefinitionData { Connections = { new() { Id = "c1", Name = "A", Host = "h1", Port = 1 }, new() { Id = "c2", Name = "B", Host = "h2", Port = 2 } } };
        storage.SaveDefinitions(data);

        // Değişmeyen kaydın üzerine elle bir işaret koyuyoruz: yeniden yazılırsa işaret kaybolur.
        Collection("connections").UpdateOne(new BsonDocument("_id", "c1"), new BsonDocument("$set", new BsonDocument("marker", 1)));
        data.Connections[1].Name = "B2";
        storage.SaveDefinitions(data);
        Assert.True(Collection("connections").Find(new BsonDocument("_id", "c1")).Single().Contains("marker"));   // dokunulmadı
        Assert.Equal("B2", Collection("connections").Find(new BsonDocument("_id", "c2")).Single()["name"].AsString);

        data.Connections.RemoveAt(1);
        storage.SaveDefinitions(data);
        Assert.Equal(1, Collection("connections").CountDocuments(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public void Pod_state_documents_and_login_keys_round_trip()
    {
        if (Url == null) return;
        var storage = Open();
        storage.SavePods(new Dictionary<string, PersistedAppPods>
        {
            // Pod kimliklerinde "." olabilir; Mongo alan adı olarak değil, JSON metni olarak saklanır.
            ["a1"] = new() { ExpectedPods = 2, Missed = { ["pod-1.ns"] = 3 } },
        });
        storage.SaveDocument("settings", new NotifySettings { TenantId = "t", PublicUrl = "https://monitor.example" });
        storage.KeyRepository.StoreElement(new XElement("key", new XAttribute("id", "k1")), "k1");

        var again = Open();
        Assert.Equal(3, again.LoadPods()["a1"].Missed["pod-1.ns"]);
        Assert.Equal("https://monitor.example", again.LoadDocument<NotifySettings>("settings")!.PublicUrl);
        Assert.Single(again.KeyRepository.GetAllElements());

        again.SavePods(new Dictionary<string, PersistedAppPods>());   // uygulamanın pod listesi sıfırlandı
        Assert.Empty(Open().LoadPods());
    }

    [Fact]
    public void Files_are_imported_once_into_an_empty_database()
    {
        if (Url == null) return;
        using var h = new Harness();
        h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "Ana DB", Host = "sql01", Port = 1433 });
        new SettingsStore(h.Storage, Microsoft.Extensions.Options.Options.Create(h.Options)).Save("tenant", "client", null);

        var storage = Open();
        storage.ImportFromFilesIfEmpty((FileStorage)h.Storage, NullLogger.Instance);
        var defs = storage.LoadDefinitions()!;
        Assert.Equal("orders", defs.Apps.Single().AppKey);
        Assert.Equal("Ana DB", defs.Connections.Single().Name);
        Assert.Equal("client", storage.LoadDocument<NotifySettings>("settings")!.ClientId);

        // İkinci açılışta yeniden aktarılmaz (Mongo'da yapılan değişiklikler korunur).
        defs.Connections.Single().Name = "Mongo'da değişti";
        storage.SaveDefinitions(defs);
        var second = Open();
        second.ImportFromFilesIfEmpty((FileStorage)h.Storage, NullLogger.Instance);
        Assert.Equal("Mongo'da değişti", second.LoadDefinitions()!.Connections.Single().Name);
    }
}
