using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ConnectivityProbe.Monitor.Tests;

public class NotificationModeTests
{
    [Theory]
    [InlineData(null, null, null, "webhook")]                                   // hiçbir ayar yok: kişi kendi iş akışını kurar
    [InlineData("https://flow.example/hook", null, null, "email")]              // merkezi iş akışı: kişi e-postasını yazar
    [InlineData("https://flow.example/hook", "tenant", "client", "microsoft")]  // + Microsoft girişi: otomatik
    [InlineData(null, "tenant", "client", "webhook")]                           // giriş var ama merkezi iş akışı yok
    [InlineData("https://flow.example/hook", "tenant", "", "email")]            // eksik giriş ayarı
    public void Mode_is_chosen_from_the_settings(string? workflow, string? tenant, string? client, string expected)
    {
        var o = new MonitorOptions { Notifications = { WorkflowUrl = workflow }, Auth = { TenantId = tenant, ClientId = client } };
        Assert.Equal(expected, o.NotifyMode);
    }

    private static readonly Dictionary<string, object?> Card = new() { ["type"] = "AdaptiveCard" };

    [Fact]
    public void Central_workflow_receives_the_recipient_and_the_card()
    {
        var person = new PersonDefinition { Name = "Ayşe", Email = "ayse@example.com", WebhookUrl = "https://own.example" };
        var d = Delivery.For(person, new NotificationOptions { WorkflowUrl = "https://flow.example/hook" }, Card);
        Assert.Equal("https://flow.example/hook", d!.Value.Url);
        var json = JsonSerializer.Serialize(d.Value.Payload);
        Assert.Contains("\"recipient\":\"ayse@example.com\"", json);
        Assert.Contains("\"card\":{\"type\":\"AdaptiveCard\"}", json);
    }

    [Fact]
    public void Personal_workflow_receives_a_teams_message_envelope()
    {
        var person = new PersonDefinition { Name = "Ayşe", WebhookUrl = "https://own.example" };
        var d = Delivery.For(person, new NotificationOptions(), Card);
        Assert.Equal("https://own.example", d!.Value.Url);
        var json = JsonSerializer.Serialize(d.Value.Payload);
        Assert.Contains("\"type\":\"message\"", json);
        Assert.Contains("application/vnd.microsoft.card.adaptive", json);
    }

    [Fact]
    public void Person_without_an_address_for_the_current_mode_is_skipped()
    {
        // Merkezi iş akışı kaldırıldı; Microsoft ile kaydolmuş kişinin kendi iş akışı yok.
        Assert.Null(Delivery.For(new PersonDefinition { Email = "ayse@example.com" }, new NotificationOptions(), Card));
    }

    [Fact]
    public void Microsoft_identity_comes_from_the_id_token_claims()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", "1111-2222"), new Claim("name", "Ayşe Yılmaz"), new Claim("preferred_username", "ayse@example.com")
        }, "test"));
        Assert.Equal(("ms-1111-2222", "Ayşe Yılmaz", "ayse@example.com"), MicrosoftLogin.PersonOf(user));

        var withEmail = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", "x"), new Claim("email", "mail@example.com"), new Claim("preferred_username", "upn@example.com")
        }, "test"));
        Assert.Equal("mail@example.com", MicrosoftLogin.PersonOf(withEmail)!.Value.Email);   // e-posta varsa o kullanılır
        Assert.Equal("mail@example.com", MicrosoftLogin.PersonOf(withEmail)!.Value.Name);    // ad yoksa e-posta

        Assert.Null(MicrosoftLogin.PersonOf(new ClaimsPrincipal(new ClaimsIdentity())));    // giriş yapılmamış
    }

    [Theory]
    [InlineData("/?app=123", "/?app=123")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("https://evil.example/", "/")]   // başka siteye yönlendirme yok
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    public void Return_url_stays_on_the_monitor(string? url, string expected) => Assert.Equal(expected, MicrosoftLogin.SafeReturnUrl(url));

    private sealed class FakeSender : INotificationSender
    {
        public readonly ConcurrentQueue<(string Url, string Json)> Sent = new();
        public Task<string?> SendAsync(string webhookUrl, object payload, CancellationToken ct)
        {
            Sent.Enqueue((webhookUrl, System.Text.RegularExpressions.Regex.Unescape(JsonSerializer.Serialize(payload))));
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task With_a_central_workflow_every_subscriber_gets_a_personal_message()
    {
        using var h = new Harness(o => o.Notifications.WorkflowUrl = "https://flow.example/hook");
        var sender = new FakeSender();
        var service = new NotificationService(h.Store, sender, Microsoft.Extensions.Options.Options.Create(h.Options), NullLogger<NotificationService>.Instance);
        var app = h.AddApp("orders", new ConnectionDefinition { Id = "db", Name = "Ana DB", Host = "sql01", Port = 1433 });
        h.Store.Mutate(d =>
        {
            d.People.Add(new PersonDefinition { Id = "ms-1", Name = "Ayşe", Email = "ayse@example.com", Source = "microsoft", MonitorUrl = "https://monitor" });
            d.People.Add(new PersonDefinition { Id = "ms-2", Name = "John", Email = "john@example.com", Source = "microsoft", Lang = "en" });
            d.Apps.Single(x => x.Id == app.Id).SubscriberIds = new List<string> { "ms-1", "ms-2" };
            return 0;
        });

        // Kayıt sırasındaki deneme mesajı da merkezi iş akışından gider.
        Assert.Null(await service.SendWelcomeAsync(h.Store.Snapshot().People[0], CancellationToken.None));

        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                h.Time.Advance(TimeSpan.FromSeconds(30));
                h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", false));
                h.Status(app);
                service.Process(h.Store.Snapshot(), h.Monitor.GetSnapshot().Apps.ToDictionary(x => x.AppId), h.Time.Utc);
            }
            for (var i = 0; i < 50 && sender.Sent.Count < 3; i++) await Task.Delay(50);
        }
        finally { await service.StopAsync(CancellationToken.None); }

        var sent = sender.Sent.ToList();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, s => Assert.Equal("https://flow.example/hook", s.Url));
        Assert.Contains(sent, s => s.Json.Contains("\"recipient\":\"ayse@example.com\"") && s.Json.Contains("Bağlantı koptu: Ana DB"));
        Assert.Contains(sent, s => s.Json.Contains("\"recipient\":\"john@example.com\"") && s.Json.Contains("Connection down: Ana DB"));
    }
}
