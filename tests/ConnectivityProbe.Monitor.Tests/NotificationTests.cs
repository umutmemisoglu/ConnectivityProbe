using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ConnectivityProbe.Monitor.Tests;

public class NotificationEngineTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Pod'u bildirim göndermeye devam ettirerek zamanı ilerletir, her adımda motoru çalıştırır ve oluşan gelişmeleri toplar.
    private static List<NotifyEvent> Run(Harness h, NotificationEngine engine, AppDefinition app, int steps, TimeSpan step, Action? eachStep = null)
    {
        var all = new List<NotifyEvent>();
        for (var i = 0; i < steps; i++)
        {
            h.Time.Advance(step);
            eachStep?.Invoke();
            all.AddRange(engine.Evaluate(h.Status(app), h.Time.Utc));
        }
        return all;
    }

    [Fact]
    public void App_down_is_reported_once_after_confirmation_and_resolved_when_pods_return()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"));
        Assert.Empty(engine.Evaluate(h.Status(app), h.Time.Utc));

        // Pod susuyor: önce "erişilemiyor" durumu doğrulanana kadar (~90 sn) bildirim yok, sonra tek bildirim.
        var events = Run(h, engine, app, steps: 12, step: TimeSpan.FromSeconds(10));
        var opened = Assert.Single(events, e => e.Rule == "appDown");
        Assert.Equal(NotifyEventKind.Opened, opened.Kind);
        Assert.DoesNotContain(Run(h, engine, app, steps: 12, step: TimeSpan.FromSeconds(10)), e => e.Rule == "appDown"); // tekrar etmez

        // Pod geri geldi: 2 test aralığı sorunsuz geçince "düzeldi".
        var back = Run(h, engine, app, steps: 8, step: TimeSpan.FromSeconds(10), () => h.Report(app, new Pod("a", "10.42.1.15")));
        var resolved = Assert.Single(back, e => e.Rule == "appDown");
        Assert.Equal(NotifyEventKind.Resolved, resolved.Kind);
    }

    [Fact]
    public void A_single_failed_test_is_not_reported_but_three_in_a_row_are()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "db", Host = "sql01", Port = 1433 });
        void Test(bool ok) { h.Time.Advance(Interval); h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", ok)); }
        List<NotifyEvent> Eval() => engine.Evaluate(h.Status(app), h.Time.Utc);

        Test(true); Eval();
        Test(false); Assert.Empty(Eval());          // tek hata
        Test(true); Assert.Empty(Eval());

        Test(false); Assert.Empty(Eval());
        Test(false); Assert.Empty(Eval());
        Test(false);                                  // üst üste 3. hata
        var e = Assert.Single(Eval());
        Assert.Equal("connDown", e.Rule);
        Assert.Equal("1/1", e.Data["failed"]);
        Test(false); Assert.Empty(Eval());          // sürüyor: yeniden bildirilmez
    }

    [Fact]
    public void Failure_on_some_pods_is_a_partial_outage()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "db", Host = "sql01", Port = 1433 });
        var events = new List<NotifyEvent>();
        for (var i = 0; i < 4; i++)
        {
            h.Time.Advance(Interval);
            h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", true));
            h.Report(app, new Pod("b", "10.43.1.21"), null, Harness.Tcp("db", false));
            events.AddRange(engine.Evaluate(h.Status(app), h.Time.Utc));
        }
        var e = Assert.Single(events);
        Assert.Equal("connPartial", e.Rule);
        Assert.Equal("1/2", e.Data["failed"]);
        Assert.Equal("b", e.Data["pods"]);
    }

    [Fact]
    public void Flapping_condition_sends_a_single_unstable_message()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "db", Host = "sql01", Port = 1433 });
        var events = new List<NotifyEvent>();
        void Test(bool ok) { h.Time.Advance(Interval); h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", ok)); events.AddRange(engine.Evaluate(h.Status(app), h.Time.Utc)); }

        // 5 kez: 3 hata (açılır) + 2 başarı (kapanır) — yaklaşık 12 dakikada bir.
        for (var round = 0; round < 5; round++) { Test(false); Test(false); Test(false); Test(true); Test(true); Test(true); }

        var db = events.Where(e => e.Rule == "connDown").ToList();
        Assert.Equal(3, db.Count(e => e.Kind == NotifyEventKind.Opened));
        Assert.Single(db, e => e.Kind == NotifyEventKind.Flapping);
        Assert.Equal(3, db.Count(e => e.Kind == NotifyEventKind.Resolved));   // kararsız olduktan sonra susar
    }

    [Fact]
    public void Certificate_expiry_is_reported_once_per_threshold_and_reset_after_renewal()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "api", Name = "api", Host = "https://api.example.com/" });
        var end = h.Time.Utc.AddDays(10.5);
        List<NotifyEvent> Test(DateTime notAfter)
        {
            h.Time.Advance(Interval);
            h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("api", true, tls: new TlsReport { Handshake = true, Success = true, NotAfterUtc = notAfter }));
            return engine.Evaluate(h.Status(app), h.Time.Utc).Where(e => e.Rule == "certExpiring").ToList();
        }

        Assert.Equal("10", Assert.Single(Test(end)).Data["days"]);     // 14 gün eşiği
        Assert.Empty(Test(end));                                          // aynı eşik tekrar etmez
        h.Time.Advance(TimeSpan.FromDays(4));
        Assert.Equal("6", Assert.Single(Test(end)).Data["days"]);      // 7 gün eşiği
        Assert.Empty(Test(h.Time.Utc.AddDays(90)));                       // yenilendi: sıfırlanır
        Assert.Single(Test(h.Time.Utc.AddDays(13)));                      // yeni sertifika da bitince yine haber verilir
    }

    [Fact]
    public void Deploy_is_not_a_restart_but_a_restart_with_the_same_build_is()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders");
        var t0 = h.Time.Utc;
        h.Report(app, new Pod("a", "10.42.1.15", "1.0.0", StartedAtUtc: t0));
        engine.Evaluate(h.Status(app), h.Time.Utc);

        // Deploy: aynı pod yeni sürümle yeni süreç -> yeniden başlama bildirimi yok, "yeni sürüm yayında" var.
        h.Time.Advance(Interval);
        h.Report(app, new Pod("a", "10.42.1.15", "1.1.0", StartedAtUtc: t0.AddMinutes(1)));
        var deploy = engine.Evaluate(h.Status(app), h.Time.Utc);
        Assert.DoesNotContain(deploy, e => e.Rule == "podRestart");
        var done = Assert.Single(deploy, e => e.Rule == "deployDone");
        Assert.StartsWith("1.0.0", done.Data["from"]);
        Assert.StartsWith("1.1.0", done.Data["to"]);

        // Aynı sürümle yeniden başlama -> bildirim.
        h.Time.Advance(Interval);
        h.Report(app, new Pod("a", "10.42.1.15", "1.1.0", StartedAtUtc: t0.AddMinutes(2)));
        Assert.Single(engine.Evaluate(h.Status(app), h.Time.Utc), e => e.Rule == "podRestart");
    }

    [Fact]
    public void Three_restarts_in_thirty_minutes_is_a_crash_loop()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders");
        var t0 = h.Time.Utc;
        h.Report(app, new Pod("a", "10.42.1.15", StartedAtUtc: t0));
        engine.Evaluate(h.Status(app), h.Time.Utc);

        var events = new List<NotifyEvent>();
        for (var i = 1; i <= 3; i++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(5));
            h.Report(app, new Pod("a", "10.42.1.15", StartedAtUtc: t0.AddMinutes(5 * i)));
            events.AddRange(engine.Evaluate(h.Status(app), h.Time.Utc));
        }
        Assert.Equal(3, events.Count(e => e.Rule == "podRestart"));
        Assert.Equal("3", Assert.Single(events, e => e.Rule == "crashLoop").Data["count"]);
    }

    [Fact]
    public void Memory_must_stay_high_for_five_minutes()
    {
        using var h = new Harness();
        var engine = new NotificationEngine(Interval);
        var app = h.AddApp("orders");
        var hot = new ResourceSample { MemoryBytes = 95, MemoryLimitBytes = 100, WorkingSetBytes = 95 };
        var events = Run(h, engine, app, steps: 9, step: Interval, () => h.Report(app, new Pod("a", "10.42.1.15"), hot)); // 4,5 dk
        Assert.DoesNotContain(events, e => e.Rule == "memHigh");
        events = Run(h, engine, app, steps: 2, step: Interval, () => h.Report(app, new Pod("a", "10.42.1.15"), hot));
        Assert.Equal("95", Assert.Single(events, e => e.Rule == "memHigh").Data["percent"]);
    }

    [Fact]
    public void Open_incidents_survive_a_monitor_restart()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "db", Host = "sql01", Port = 1433 });
        var engine = new NotificationEngine(Interval);
        for (var i = 0; i < 4; i++) { h.Time.Advance(Interval); h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", false)); engine.Evaluate(h.Status(app), h.Time.Utc); }

        // Monitor yeniden başladı: açık durum geri yüklenir, aynı sorun yeniden "başladı" diye bildirilmez.
        var restarted = new NotificationEngine(Interval);
        restarted.Import(JsonSerializer.Deserialize<NotificationEngine.State>(JsonSerializer.Serialize(engine.Export()))!);
        h.Time.Advance(Interval);
        h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", false));
        Assert.DoesNotContain(restarted.Evaluate(h.Status(app), h.Time.Utc), e => e.Kind == NotifyEventKind.Opened);

        // Düzelince "düzeldi" yine gelir.
        var events = new List<NotifyEvent>();
        for (var i = 0; i < 3; i++) { h.Time.Advance(Interval); h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", true)); events.AddRange(restarted.Evaluate(h.Status(app), h.Time.Utc)); }
        Assert.Single(events, e => e.Rule == "connDown" && e.Kind == NotifyEventKind.Resolved);
    }

    [Fact]
    public void Only_selected_rules_are_sent_and_resolved_needs_its_option()
    {
        var events = new[]
        {
            new NotifyEvent("connDown", NotifyEventKind.Opened, Severity.Critical, "db", DateTime.UtcNow, null, new Dictionary<string, string>()),
            new NotifyEvent("connDown", NotifyEventKind.Resolved, Severity.Info, "db", DateTime.UtcNow, null, new Dictionary<string, string>()),
            new NotifyEvent("connSlow", NotifyEventKind.Opened, Severity.Medium, "db", DateTime.UtcNow, null, new Dictionary<string, string>()),
        };
        Assert.Single(NotificationEngine.Filter(events, new[] { "connDown" }));
        Assert.Equal(2, NotificationEngine.Filter(events, new[] { "connDown", "resolved" }).Count);
        Assert.Empty(NotificationEngine.Filter(events, Array.Empty<string>()));
    }

    [Fact]
    public void Default_rules_cover_the_important_cases()
    {
        Assert.Equal(new[] { "appDown", "podMissing", "crashLoop", "connDown", "connPartial", "certExpiring", "certInvalid", "memHigh", "resolved" },
            NotifyRules.Defaults());
        Assert.Equal(new[] { "appDown", "connDown" }, NotifyRules.Normalize(new[] { "connDown", "unknown", "appDown" }));
    }
}

public class NotificationDeliveryTests
{
    private sealed class FakeSender : INotificationSender
    {
        public readonly ConcurrentQueue<(string Url, string Json)> Sent = new();
        public Task<string?> SendAsync(string webhookUrl, object payload, CancellationToken ct)
        {
            Sent.Enqueue((webhookUrl, JsonSerializer.Serialize(payload)));
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task Subscribers_receive_one_card_per_app_in_their_language()
    {
        using var h = new Harness();
        var sender = new FakeSender();
        var service = new NotificationService(h.Store, new SettingsStore(h.Store, Microsoft.Extensions.Options.Options.Create(h.Options)), sender, Microsoft.Extensions.Options.Options.Create(h.Options), NullLogger<NotificationService>.Instance);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "Ana DB", Host = "sql01", Port = 1433 });
        h.Store.Mutate(d =>
        {
            d.People.Add(new PersonDefinition { Id = "p1", Name = "Ayşe", WebhookUrl = "https://teams.example/ayse", Lang = "tr", MonitorUrl = "http://monitor" });
            d.People.Add(new PersonDefinition { Id = "p2", Name = "John", WebhookUrl = "https://teams.example/john", Lang = "en", MonitorUrl = "http://monitor" });
            d.People.Add(new PersonDefinition { Id = "p3", Name = "Not subscribed", WebhookUrl = "https://teams.example/x" });
            var a = d.Apps.Single(x => x.Id == app.Id);
            a.NotifyRules = NotifyRules.Defaults();
            a.SubscriberIds = new List<string> { "p1", "p2" };
            return 0;
        });

        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                h.Time.Advance(TimeSpan.FromSeconds(30));
                h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", false));
                h.Status(app);
                var statuses = h.Monitor.GetSnapshot().Apps.ToDictionary(x => x.AppId);
                service.Process(h.Store.Snapshot(), statuses, h.Time.Utc);
            }
            for (var i = 0; i < 50 && sender.Sent.Count < 2; i++) await Task.Delay(50);
        }
        finally { await service.StopAsync(CancellationToken.None); }

        // JSON Türkçe karakterleri kaçış dizisiyle yazar (Teams doğru gösterir); karşılaştırmadan önce çözüyoruz.
        var sent = sender.Sent.ToDictionary(x => x.Url, x => System.Text.RegularExpressions.Regex.Unescape(x.Json));
        Assert.Equal(2, sent.Count);
        Assert.Contains("Bağlantı koptu: Ana DB", sent["https://teams.example/ayse"]);
        Assert.Contains("Connection down: Ana DB", sent["https://teams.example/john"]);
        Assert.Contains("http://monitor/?app=" + app.Id, sent["https://teams.example/ayse"]);
        Assert.Contains("AdaptiveCard", sent["https://teams.example/john"]);
    }

    [Fact]
    public void Unselected_rule_sends_nothing()
    {
        using var h = new Harness();
        var sender = new FakeSender();
        var service = new NotificationService(h.Store, new SettingsStore(h.Store, Microsoft.Extensions.Options.Options.Create(h.Options)), sender, Microsoft.Extensions.Options.Options.Create(h.Options), NullLogger<NotificationService>.Instance);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "db", Host = "sql01", Port = 1433 });
        h.Store.Mutate(d =>
        {
            d.People.Add(new PersonDefinition { Id = "p1", Name = "Ayşe", WebhookUrl = "https://teams.example/ayse" });
            var a = d.Apps.Single(x => x.Id == app.Id);
            a.NotifyRules = new List<string> { "appDown" };          // bağlantı bildirimleri kapalı
            a.SubscriberIds = new List<string> { "p1" };
            return 0;
        });
        for (var i = 0; i < 4; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", false));
            h.Status(app);
            service.Process(h.Store.Snapshot(), h.Monitor.GetSnapshot().Apps.ToDictionary(x => x.AppId), h.Time.Utc);
        }
        Assert.Equal(0, service.Pending);
    }
}
