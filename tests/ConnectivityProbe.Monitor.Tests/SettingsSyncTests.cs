using Xunit;

namespace ConnectivityProbe.Monitor.Tests;

public class SettingsSyncTests
{
    private static DiscoveredEndpoint E(string key, string kind, string host, int port, string confidence = "high", bool tls = false) =>
        new() { Key = key, Kind = kind, Host = host, Port = port, Confidence = confidence, Tls = tls };

    private static DiscoveredSettings S(params DiscoveredEndpoint[] endpoints) => new()
    {
        Signature = SettingsDiscovery.Signature(endpoints), ReadAtUtc = DateTime.UtcNow, Endpoints = endpoints.ToList()
    };

    private static AppDefinition App(Harness h, AppDefinition app) => h.Store.Snapshot().Apps.Single(a => a.Id == app.Id);
    private static List<ConnectionDefinition> Pool(Harness h) => h.Store.Snapshot().Connections;
    private static List<string> Endpoints(Harness h, AppDefinition app)
    {
        var d = h.Store.Snapshot();
        return App(h, app).ConnectionIds.Select(id => d.Connections.Single(c => c.Id == id)).Select(c => $"{c.Host}:{c.Port}").OrderBy(x => x).ToList();
    }

    private static readonly Pod PodA = new("a", "10.42.1.15");

    [Fact]
    public void New_dependencies_are_added_to_the_pool_and_attached()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, PodA, S(
            E("Db:Default", "postgresql", "pg01.corp.test", 5432),
            E("Rabbit:Host", "rabbitmq", "mq01.corp.test", 5672),
            E("Mail:BaseUrl", "http", "mail.corp.test", 443, tls: true)));

        Assert.Equal(new[] { "mail.corp.test:443", "mq01.corp.test:5672", "pg01.corp.test:5432" }, Endpoints(h, app));
        var pg = Pool(h).Single(c => c.Host == "pg01.corp.test");
        Assert.Equal(("PostgreSQL · pg01.corp.test", "settings"), (pg.Name, pg.Origin));
        Assert.Equal("on", Pool(h).Single(c => c.Host == "mail.corp.test").TlsCheck);
        var st = App(h, app).Discovery!;
        Assert.Equal(3, st.Changes.Count(c => c.Type == "added"));

        // Aynı sonuç yeniden gelirse hiçbir şey değişmez.
        h.Report(app, PodA, S(
            E("Db:Default", "postgresql", "pg01.corp.test", 5432),
            E("Rabbit:Host", "rabbitmq", "mq01.corp.test", 5672),
            E("Mail:BaseUrl", "http", "mail.corp.test", 443, tls: true)));
        Assert.Equal(3, App(h, app).Discovery!.Changes.Count);
        Assert.Equal(3, Pool(h).Count);
    }

    [Fact]
    public void Existing_pool_connection_is_reused_and_stays_when_the_setting_goes_away()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "Ana DB", Host = "PG01.corp.test", Port = 5432 });
        h.Report(app, PodA, S(E("Db:Default", "postgresql", "pg01.corp.test", 5432)));
        Assert.Single(Pool(h));                                       // yeni bağlantı açılmadı, "Ana DB" kullanıldı
        Assert.Equal("db", App(h, app).Discovery!.Links.Single().ConnectionId);

        h.Report(app, PodA, S());                                     // ayar kaldırıldı
        Assert.Equal(new[] { "PG01.corp.test:5432" }, Endpoints(h, app).Select(x => x).ToArray()); // elle atanmıştı: kalır
        Assert.Contains(App(h, app).Discovery!.Changes, c => c.Type == "removed");
    }

    [Fact]
    public void Changed_address_replaces_the_old_connection_and_removed_one_is_cleaned_up()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, PodA, S(E("Db:Default", "postgresql", "pg01.corp.test", 5432), E("Cache", "redis", "redis.corp.test", 6379)));
        h.Report(app, PodA, S(E("Db:Default", "postgresql", "pg02.corp.test", 5432)));

        Assert.Equal(new[] { "pg02.corp.test:5432" }, Endpoints(h, app));
        Assert.Single(Pool(h));                                       // kimsenin kullanmadığı otomatik bağlantılar silindi
        var changes = App(h, app).Discovery!.Changes;
        var changed = changes.Single(c => c.Type == "changed");
        Assert.Equal(("postgresql pg01.corp.test:5432", "postgresql pg02.corp.test:5432"), (changed.From, changed.To));
        Assert.Contains(changes, c => c.Type == "removed" && c.Key == "Cache");
    }

    [Fact]
    public void Shared_connection_is_not_deleted_while_another_app_uses_it()
    {
        using var h = new Harness();
        var a = h.AddApp("a");
        var b = h.AddApp("b");
        h.Report(a, PodA, S(E("Db", "postgresql", "pg01.corp.test", 5432)));
        h.Report(b, new Pod("b1", "10.42.1.16"), S(E("Db", "postgresql", "pg01.corp.test", 5432)));
        Assert.Single(Pool(h));                                       // iki uygulama aynı havuz bağlantısını paylaşır

        h.Report(a, PodA, S());
        Assert.Empty(App(h, a).ConnectionIds);
        Assert.Single(Pool(h));                                       // b hâlâ kullanıyor
    }

    [Fact]
    public void Low_confidence_endpoints_wait_as_suggestions_until_accepted()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        var settings = S(E("Endpoint2", "tcp", "srv01.corp.test", 8080, "low"));
        h.Report(app, PodA, settings);
        Assert.Empty(App(h, app).ConnectionIds);
        Assert.Equal("srv01.corp.test", App(h, app).Discovery!.Suggestions.Single().Host);

        h.Store.Mutate(d => { SettingsSync.Accept(d.Apps.Single(x => x.Id == app.Id), "Endpoint2"); return 0; });
        h.Report(app, PodA, settings);                                // aynı imza; onay sonrası yeniden uygulanır
        Assert.Equal(new[] { "srv01.corp.test:8080" }, Endpoints(h, app));
        Assert.Empty(App(h, app).Discovery!.Suggestions);
    }

    [Fact]
    public void Manually_detached_dependency_is_not_added_again()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        var settings = S(E("Db", "postgresql", "pg01.corp.test", 5432));
        h.Report(app, PodA, settings);
        var connId = App(h, app).ConnectionIds.Single();

        h.Store.Mutate(d =>
        {
            var a = d.Apps.Single(x => x.Id == app.Id);
            SettingsSync.ConnectionDetached(d, a, connId, DateTime.UtcNow);
            a.ConnectionIds.Remove(connId);
            return 0;
        });
        h.Report(app, PodA, S(E("Db", "postgresql", "pg01.corp.test", 5432), E("Other", "redis", "r.corp.test", 6379)));

        Assert.Equal(new[] { "r.corp.test:6379" }, Endpoints(h, app));
        Assert.Contains("Db", App(h, app).Discovery!.IgnoredKeys);

        h.Store.Mutate(d => { SettingsSync.Restore(d.Apps.Single(x => x.Id == app.Id), "Db"); return 0; });
        h.Report(app, PodA, S(E("Db", "postgresql", "pg01.corp.test", 5432), E("Other", "redis", "r.corp.test", 6379)));
        Assert.Equal(2, App(h, app).ConnectionIds.Count);             // geri alındı
    }

    [Fact]
    public void Older_pod_cannot_roll_back_settings_during_a_deploy()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        var t0 = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
        var oldPod = new Pod("old", "10.42.1.15", StartedAtUtc: t0);
        var newPod = new Pod("new", "10.42.1.16", Version: "2.0.0", StartedAtUtc: t0.AddHours(1));

        h.Report(app, oldPod, S(E("Db", "postgresql", "pg01.corp.test", 5432)));
        h.Report(app, newPod, S(E("Db", "postgresql", "pg02.corp.test", 5432)));
        h.Report(app, oldPod, S(E("Db", "postgresql", "pg01.corp.test", 5432)));   // eski pod hâlâ eski ayarı bildiriyor

        Assert.Equal(new[] { "pg02.corp.test:5432" }, Endpoints(h, app));
    }

    [Fact]
    public void Read_error_keeps_the_definitions_and_is_shown()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        var good = S(E("Db", "postgresql", "pg01.corp.test", 5432));
        h.Report(app, PodA, good);
        h.Report(app, PodA, new DiscoveredSettings { Signature = good.Signature, Endpoints = good.Endpoints, Error = "settings returned HTTP 503" });

        Assert.Equal(new[] { "pg01.corp.test:5432" }, Endpoints(h, app));
        Assert.Equal("settings returned HTTP 503", App(h, app).Discovery!.Error);
    }

    [Fact]
    public void Verify_setting_is_sent_to_the_pods()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        Assert.False(h.Report(app, PodA).VerifySettings);
        h.Store.Mutate(d => d.Apps.Single(x => x.Id == app.Id).VerifySettings = true);
        Assert.True(h.Report(App(h, app), PodA).VerifySettings);
    }
}
