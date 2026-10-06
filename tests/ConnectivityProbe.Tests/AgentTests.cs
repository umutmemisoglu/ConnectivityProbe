using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ConnectivityProbe.Tests;

/// <summary>Strict modu test etmek için sahte Monitor: pod bildirimlerini toplar ve tanımları döner.</summary>
internal sealed class FakeMonitor : IAsyncDisposable
{
    public const string Key = "cpk_test";
    private readonly WebApplication _app;
    public string Url { get; }
    public ConcurrentQueue<AgentReport> Reports { get; } = new();
    public ConcurrentQueue<string> Goodbyes { get; } = new();
    public string RunRequestId { get; set; } = "1";
    public object[] Connections { get; set; } = Array.Empty<object>();

    private FakeMonitor(WebApplication app)
    {
        _app = app;
        Url = app.Urls.First();
    }

    public static async Task<FakeMonitor> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeMonitor? self = null;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        app.MapPost(ConnectivityProbeAgent.ReportPath, async (HttpContext ctx) =>
        {
            if (ctx.Request.Headers[ConnectivityProbeOptions.AppKeyHeader] != Key)
                return Results.Json(new { error = "bad key" }, statusCode: 401);
            var report = await JsonSerializer.DeserializeAsync<AgentReport>(ctx.Request.Body, json);
            self!.Reports.Enqueue(report!);
            return Results.Json(new
            {
                appId = "a1", appName = "Test App", intervalSeconds = 3600, runRequestId = self.RunRequestId,
                timeoutMs = 2000, attempts = 20, confidence = 0.9, connections = self.Connections
            });
        });
        app.MapPost(ConnectivityProbeAgent.GoodbyePath, async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            self!.Goodbyes.Enqueue(doc.RootElement.GetProperty("instanceId").GetString()!);
            return Results.NoContent();
        });

        await app.StartAsync();
        self = new FakeMonitor(app);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

// Agent süreç başına tek olduğu için bu sınıftaki testler sırayla çalışır (xUnit aynı sınıftaki testleri paralel çalıştırmaz).
public class AgentTests
{
    private static async Task<T> WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } value) return value;
            await Task.Delay(100);
        }
        throw new TimeoutException("condition not met in " + timeout);
    }

    [Fact]
    public void Strict_settings_are_read_from_configuration()
    {
        var o = ConnectivityProbeOptions.FromSettings(new Dictionary<string, string?>
        {
            ["MonitorUrl"] = "https://monitor.example.com/", ["AppKey"] = " cpk_x ",
            ["Strict:IntervalSeconds"] = "45", ["Strict:CommandPollSeconds"] = "7"
        });
        Assert.Equal("https://monitor.example.com", o.MonitorUrl);
        Assert.Equal("cpk_x", o.AppKey);
        Assert.Equal(45, o.StrictIntervalSeconds);
        Assert.Equal(7, o.StrictPollSeconds);
        Assert.True(o.StrictEnabled);
        Assert.False(new ConnectivityProbeOptions { MonitorUrl = "https://m" }.StrictEnabled);
        Assert.Null(ConnectivityProbeAgent.Start(new ConnectivityProbeOptions())); // ayar yoksa başlamaz
    }

    [Fact]
    public async Task Agent_pulls_definitions_tests_them_reports_results_and_says_goodbye()
    {
        await using var target = await TestApp.StartAsync(o => o.AllowAnonymous = true);   // ConnectivityProbe kullanan hedef
        await using var monitor = await FakeMonitor.StartAsync();
        monitor.Connections = new object[]
        {
            new { id = "tcp", name = "tcp", host = "127.0.0.1", port = target.Port, usesConnectivityProbe = false },
            new { id = "cp", name = "cp", host = "127.0.0.1:" + target.Port, port = (int?)null, usesConnectivityProbe = true },
            new { id = "bad", name = "bad", host = "", port = (int?)null, usesConnectivityProbe = false },
        };

        var agent = ConnectivityProbeAgent.Start(new ConnectivityProbeOptions
        {
            MonitorUrl = monitor.Url, AppKey = FakeMonitor.Key, StrictPollSeconds = 1, AllowAnonymous = true
        });
        Assert.NotNull(agent);
        try
        {
            // step 1: İlk bildirimden sonra hemen test yapılıp sonuçlar gönderilir.
            var first = await WaitFor(() => monitor.Reports.FirstOrDefault(r => r.Run != null), TimeSpan.FromSeconds(20));
            Assert.False(string.IsNullOrEmpty(first.Identity.InstanceId));
            Assert.Equal(1, first.PollSeconds);
            Assert.Equal(ProbeInfo.Version, first.Identity.ProbeVersion);

            var results = first.Run!.Results.ToDictionary(r => r.ConnectionId);
            Assert.Equal(ProbeTargetKind.Tcp, results["tcp"].Report!.TargetKind);
            Assert.Equal(first.Identity.InstanceId, results["tcp"].Report!.ExecutedByInstanceId);
            Assert.Equal(ProbeTargetKind.ConnectivityProbe, results["cp"].Report!.TargetKind);
            Assert.Equal(1, results["cp"].Report!.DistinctInstances);
            Assert.Null(results["bad"].Report);
            Assert.Contains("Invalid target", results["bad"].Error);
            Assert.Equal("Test App", agent!.AppName);

            // step 2: Aralık 1 saat olduğu halde "Şimdi test et" (runRequestId değişti) beklemeden yeni tur başlatır.
            var runsBefore = monitor.Reports.Count(r => r.Run != null);
            monitor.RunRequestId = "2";
            await WaitFor(() => monitor.Reports.Count(r => r.Run != null) > runsBefore ? "ok" : null, TimeSpan.FromSeconds(20));

            // step 3: Aralık dolmadıkça yalnızca canlılık bildirimi gönderilir (test sonucu yok).
            var countAfterRun = monitor.Reports.Count;
            await WaitFor(() => monitor.Reports.Count > countAfterRun + 1 ? "ok" : null, TimeSpan.FromSeconds(10));
            Assert.All(monitor.Reports.Skip(countAfterRun), r => Assert.Null(r.Run));
        }
        finally
        {
            agent!.Stop();
        }

        // step 4: Kapanırken Monitor'e "kapanıyorum" bildirilir.
        Assert.Contains(monitor.Reports.First().Identity.InstanceId, monitor.Goodbyes);
        Assert.Null(ConnectivityProbeAgent.Current);
    }

    [Fact]
    public async Task Agent_with_wrong_key_keeps_running_and_reports_the_error()
    {
        await using var monitor = await FakeMonitor.StartAsync();
        var agent = ConnectivityProbeAgent.Start(new ConnectivityProbeOptions { MonitorUrl = monitor.Url, AppKey = "wrong", StrictPollSeconds = 1 });
        try
        {
            var error = await WaitFor(() => agent!.LastError, TimeSpan.FromSeconds(10));
            Assert.Contains("401", error);
            Assert.Empty(monitor.Reports);
            Assert.Null(agent!.LastContactUtc);
        }
        finally
        {
            agent!.Stop();
        }
    }

    [Fact]
    public async Task UseConnectivityProbe_starts_the_agent_with_the_application_and_stops_it_on_shutdown()
    {
        await using var monitor = await FakeMonitor.StartAsync();
        var app = await TestApp.StartAsync(o =>
        {
            o.AllowAnonymous = true; o.MonitorUrl = monitor.Url; o.AppKey = FakeMonitor.Key; o.StrictPollSeconds = 1;
        });
        try
        {
            await WaitFor(() => monitor.Reports.FirstOrDefault(), TimeSpan.FromSeconds(20));
            Assert.NotNull(ConnectivityProbeAgent.Current);
        }
        finally
        {
            await app.DisposeAsync(); // uygulama kapanır -> ApplicationStopping -> agent durur ve "kapanıyorum" der
        }
        Assert.Single(monitor.Goodbyes);
        Assert.Null(ConnectivityProbeAgent.Current);
    }
}
