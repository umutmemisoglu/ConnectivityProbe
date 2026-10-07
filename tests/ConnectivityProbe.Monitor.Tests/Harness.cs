using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ConnectivityProbe.Monitor.Tests;

/// <summary>Elle ilerletilen saat: "pod 2 dakikadır bildirim göndermiyor" gibi durumlar beklemeden test edilir.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public DateTime Utc => _now.UtcDateTime;
}

/// <summary>
/// Geçici bir veri klasörüyle gerçek Monitor servislerini (DefinitionStore, PodStateStore, MonitorService) kurar ve pod
/// bildirimlerini, Monitor'ün /api/agent/v2/report ucunun yaptığı gibi işler.
/// </summary>
internal sealed class Harness : IDisposable
{
    public readonly ManualTime Time = new();
    public readonly string Dir = Directory.CreateTempSubdirectory("cpmon").FullName;
    public readonly MonitorOptions Options = new() { IntervalSeconds = 30, MissingAfterCycles = 3 };
    public DefinitionStore Store { get; }
    public MonitorService Monitor { get; }

    public Harness(Action<MonitorOptions>? configure = null, string? definitionsJson = null)
    {
        configure?.Invoke(Options);
        Options.DataFile = Path.Combine(Dir, "definitions.json");
        if (definitionsJson != null) File.WriteAllText(Options.DataFile, definitionsJson);
        var env = new HostingEnvironment { ContentRootPath = Dir };
        Store = new DefinitionStore(Microsoft.Extensions.Options.Options.Create(Options), env);
        Monitor = new MonitorService(Store, new PodStateStore(Store, NullLogger<PodStateStore>.Instance),
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<MonitorService>.Instance, Time);
    }

    /// <summary>Uygulamayı kaydeder (pod'un ilk bildiriminde olduğu gibi) ve verilen bağlantıları ona atar.</summary>
    public AppDefinition AddApp(string key, params ConnectionDefinition[] connections) =>
        Store.Mutate(d =>
        {
            foreach (var c in connections) { c.Id = c.Id.Length > 0 ? c.Id : c.Name; d.Connections.Add(c); }
            var app = new AppDefinition { Id = "app-" + key, Name = key, AppKey = key, ConnectionIds = connections.Select(c => c.Id).ToList() };
            d.Apps.Add(app);
            return app;
        });

    /// <summary>Pod bildirimi gönderir (test sonuçları ve kaynak ölçümleriyle birlikte olabilir).</summary>
    public AgentAssignment Report(AppDefinition app, Pod pod, ResourceSample? resources = null, params AgentResult[] results)
    {
        var report = new AgentReport
        {
            AppName = app.Name,
            PollSeconds = 10,
            Resources = resources,
            Pod = new PodIdentity
            {
                InstanceId = pod.Id, MachineName = pod.Id, StartedAtUtc = pod.StartedAtUtc ?? new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc),
                LocalAddresses = new List<string> { pod.Ip }, PrimaryAddress = pod.Ip, ClusterId = pod.ClusterId,
                AppVersion = pod.Version, BuildId = "b" + pod.Version.Replace(".", ""), ProbeVersion = "2.1.0"
            },
            Run = results.Length == 0 ? null : new AgentRun { StartedAtUtc = Time.Utc, Results = results.ToList() }
        };
        return Monitor.AcceptAgentReport(app, Store.Snapshot(), report, "192.0.2.1");
    }

    /// <summary>Durumu yeniden hesaplar ve uygulamanın güncel durumunu döner.</summary>
    public AppStatus Status(AppDefinition app)
    {
        Monitor.Refresh();
        return Monitor.GetSnapshot().Apps.Single(a => a.AppId == app.Id);
    }

    public static AgentResult Tcp(string connectionId, bool success, long ms = 10, string ip = "10.0.0.5", TlsReport? tls = null) => new()
    {
        ConnectionId = connectionId,
        Tcp = new ProbeReport
        {
            Host = "target", Port = 443, Attempts = 1, DnsMs = 2,
            HostnameAttempts = { new ProbeResult { Target = "target", Port = 443, Success = success, ElapsedMs = ms,
                RemoteAddress = success ? ip : null, Error = success ? null : "ConnectionRefused" } },
            ResolvedAddresses = { new AddressReport { Address = ip, Succeeded = success ? 1 : 0, Failed = success ? 0 : 1 } }
        },
        Tls = tls
    };

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { /* bir sonraki temizlikte */ }
    }
}

internal sealed record Pod(string Id, string Ip, string Version = "1.0.0", string? ClusterId = null, DateTime? StartedAtUtc = null);
