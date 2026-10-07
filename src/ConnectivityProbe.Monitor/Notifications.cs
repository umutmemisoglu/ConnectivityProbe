using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

namespace ConnectivityProbe.Monitor;

/// <summary>Bildirimi kişiye ileten yol (Teams Workflows adresine POST). Testlerde sahtesi kullanılır.</summary>
public interface INotificationSender
{
    /// <summary>Mesajı gönderir; başarısızsa hata metnini döner (null = başarılı).</summary>
    Task<string?> SendAsync(string webhookUrl, object payload, CancellationToken ct);
}

/// <summary>Teams Workflows iş akışına Adaptive Card gönderir. Sistem proxy'si (HTTPS_PROXY) kullanılır.</summary>
public sealed class TeamsWebhookSender : INotificationSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string?> SendAsync(string webhookUrl, object payload, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(webhookUrl, payload, ct);
            if (response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            return $"HTTP {(int)response.StatusCode} {body[..Math.Min(200, body.Length)]}".Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ex.Message;
        }
    }
}

/// <summary>
/// Bildirimlerin gönderildiği yer: Monitor her turda uygulama durumlarını buraya verir; motor (NotificationEngine)
/// gelişmeleri çıkarır, uygulamanın seçili kurallarına göre süzülür ve abone olan her kişiye kuyruktan gönderilir.
/// Gönderim başarısızsa 3 kez denenir (5 sn, 30 sn sonra). Açık durumlar data/notify-state.json'a yazılır.
/// </summary>
public sealed class NotificationService : BackgroundService
{
    private sealed record Delivery(PersonDefinition Person, NotifyBatch Batch, DateTime AtUtc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30) };

    private readonly NotificationEngine _engine;
    private readonly INotificationSender _sender;
    private readonly ILogger<NotificationService> _log;
    private readonly Channel<Delivery> _queue = Channel.CreateUnbounded<Delivery>();
    private readonly string _statePath;
    private readonly object _gate = new();
    private DateTime _lastSave;

    public NotificationService(DefinitionStore store, INotificationSender sender, Microsoft.Extensions.Options.IOptions<MonitorOptions> options,
        ILogger<NotificationService> log)
    {
        _engine = new NotificationEngine(TimeSpan.FromSeconds(Math.Max(5, options.Value.IntervalSeconds)));
        _sender = sender;
        _log = log;
        _statePath = Path.Combine(Path.GetDirectoryName(store.FilePath)!, "notify-state.json");
        try
        {
            if (File.Exists(_statePath))
                _engine.Import(JsonSerializer.Deserialize<NotificationEngine.State>(File.ReadAllText(_statePath), Json) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.LogWarning(ex, "Notification state could not be read; starting fresh");
        }
    }

    /// <summary>Gönderilmeyi bekleyen mesaj sayısı (testler ve izleme için).</summary>
    public int Pending => _queue.Reader.Count;

    /// <summary>Monitor'ün her turunda çağrılır: gelişmeleri çıkarır ve abonelere gönderilmek üzere kuyruğa alır.</summary>
    public void Process(DefinitionData defs, IReadOnlyDictionary<string, AppStatus> statuses, DateTime now)
    {
        lock (_gate)
        {
            foreach (var app in defs.Apps)
            {
                if (!statuses.TryGetValue(app.Id, out var status)) continue;
                var events = NotificationEngine.Filter(_engine.Evaluate(status, now), app.NotifyRules ?? NotifyRules.Defaults());
                if (events.Count == 0) continue;

                var batch = new NotifyBatch(app.Id, app.Name, events);
                foreach (var person in defs.People.Where(p => app.SubscriberIds.Contains(p.Id)))
                    _queue.Writer.TryWrite(new Delivery(person, batch, now));
                _log.LogInformation("Notification for {App}: {Events} event(s) to {Count} subscriber(s)",
                    app.Name, events.Count, app.SubscriberIds.Count);
            }

            if (_engine.Dirty && now - _lastSave > TimeSpan.FromSeconds(10)) SaveState(now);
        }
    }

    /// <summary>Uygulama silindi veya pod listesi sıfırlandı.</summary>
    public void Forget(string appId)
    {
        lock (_gate) _engine.Forget(appId);
    }

    /// <summary>Kayıt sırasında deneme mesajı gönderir; adres çalışmıyorsa hata metnini döner.</summary>
    public Task<string?> SendWelcomeAsync(PersonDefinition person, CancellationToken ct) =>
        _sender.SendAsync(person.WebhookUrl, NotificationText.Welcome(person), ct);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var d in _queue.Reader.ReadAllAsync(ct))
            {
                var payload = NotificationText.Card(d.Batch, d.Person, d.AtUtc);
                string? error = null;
                for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
                {
                    if (attempt > 0) await Task.Delay(RetryDelays[attempt - 1], ct);
                    error = await _sender.SendAsync(d.Person.WebhookUrl, payload, ct);
                    if (error == null) break;
                }
                if (error != null)
                    _log.LogWarning("Notification to {Person} for {App} failed: {Error}", d.Person.Name, d.Batch.AppName, error);
            }
        }
        catch (OperationCanceledException) { /* kapanış */ }
        finally
        {
            lock (_gate) SaveState(DateTime.UtcNow);
        }
    }

    private void SaveState(DateTime now)
    {
        _lastSave = now;
        try
        {
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_engine.Export(), Json));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Notification state could not be saved");
        }
    }
}
