using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ConnectivityProbe.Tests;

/// <summary>Testler için gerçek bir Kestrel sunucusu (127.0.0.1, rastgele port).</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _logs;
    public string BaseUrl { get; }
    public int Port { get; }
    /// <summary>"ConnectivityProbe" kategorisine yazılan loglar ("Seviye: mesaj").</summary>
    public IEnumerable<string> AllLogs => _logs;

    private TestApp(WebApplication app, string baseUrl, ConcurrentQueue<string> logs)
    {
        _app = app;
        _logs = logs;
        BaseUrl = baseUrl;
        Port = new Uri(baseUrl).Port;
    }

    /// <param name="configure">UseConnectivityProbe'a verilecek ayarlar; null ise UseConnectivityProbe çağrılmaz.</param>
    /// <param name="settings">Yapılandırma ("ConnectivityProbe:..." anahtarları).</param>
    /// <param name="slowPathDelay">"/slow/..." isteklerini bu kadar geciktiren uç (eşzamanlılık testi için).</param>
    public static async Task<TestApp> StartAsync(
        Action<ConnectivityProbeOptions>? configure, Dictionary<string, string?>? settings = null,
        bool useHostingStartup = false, TimeSpan? slowPathDelay = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (settings != null) builder.Configuration.AddInMemoryCollection(settings);
        var logs = new ConcurrentQueue<string>();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(logs));
        if (useHostingStartup) new ConnectivityProbeHostingStartup().Configure(builder.WebHost);

        var app = builder.Build();
        if (slowPathDelay != null)
        {
            // Hedef rolündeki uygulamanın uçlarını yavaşlatan ara katman (discover isteği açık kalsın diye); ConnectivityProbe'dan önce.
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/slow")) await Task.Delay(slowPathDelay.Value);
                await next(ctx);
            });
        }
        if (configure != null) app.UseConnectivityProbe(configure);
        app.MapGet("/", () => "app");
        await app.StartAsync();

        return new TestApp(app, app.Urls.First(), logs);
    }

    public HttpClient Client() => new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _logs;
        public ListLoggerProvider(ConcurrentQueue<string> logs) => _logs = logs;
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _logs);
        public void Dispose() { }

        private sealed class ListLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<string> _logs;
            public ListLogger(string category, ConcurrentQueue<string> logs) { _category = category; _logs = logs; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (_category == "ConnectivityProbe") _logs.Enqueue(logLevel + ": " + formatter(state, exception));
            }
        }
    }
}

public class IntegrationTests
{
    private static IDictionary<string, object?> ParseObject(string json) =>
        Assert.IsAssignableFrom<IDictionary<string, object?>>(Json.Parse(json));

    [Fact]
    public async Task Identity_endpoint_returns_identity_with_marker_header()
    {
        await using var app = await TestApp.StartAsync(o => { o.AllowAnonymous = true; o.Info["app"] = "test"; });
        using var client = app.Client();

        var response = await client.GetAsync("/connectivity-probe/identity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(ConnectivityProbeOptions.MarkerHeader));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var identity = InstanceIdentity.FromJson(Json.Parse(await response.Content.ReadAsStringAsync()));
        Assert.NotNull(identity);
        Assert.Equal("test", identity!.Info["app"]);
        Assert.Equal(ProbeInfo.Version, identity.ProbeVersion);
    }

    [Fact]
    public async Task Other_paths_are_left_to_the_application()
    {
        await using var app = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        using var client = app.Client();
        Assert.Equal("app", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Settings_are_read_from_configuration_and_errors_carry_marker()
    {
        await using var app = await TestApp.StartAsync(_ => { }, new() { ["ConnectivityProbe:AccessKey"] = "k" });
        using var client = app.Client();

        var denied = await client.GetAsync("/connectivity-probe/identity");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True(denied.Headers.Contains(ConnectivityProbeOptions.MarkerHeader));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/connectivity-probe/identity");
        request.Headers.Add(ConnectivityProbeOptions.AccessKeyHeader, "k");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

        Assert.Contains(app.AllLogs, l => l.StartsWith("Warning") && l.Contains("401"));
    }

    [Fact]
    public async Task Hosting_startup_activates_without_code()
    {
        await using var app = await TestApp.StartAsync(null, new() { ["ConnectivityProbe:AllowAnonymous"] = "true" }, useHostingStartup: true);
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/connectivity-probe/identity")).StatusCode);
    }

    [Fact]
    public async Task Discover_tcp_only_target()
    {
        await using var app = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        using var client = app.Client();

        var o = ParseObject(await client.GetStringAsync("/connectivity-probe/discover?host=127.0.0.1:" + app.Port));

        Assert.Equal(ProbeTargetKind.Tcp, Json.GetString(o, "targetKind"));
        Assert.Equal(ProbeInfo.Version, Json.GetString(o, "probeVersion"));
        Assert.False(string.IsNullOrEmpty(Json.GetString(o, "executedByInstanceId")));
        Assert.Equal(0, Json.GetLong(o, "attemptsMade")); // hedefe HTTP isteği gönderilmedi
    }

    [Fact]
    public async Task Discover_unreachable_target_sends_no_http()
    {
        await using var app = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        using var client = app.Client();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var o = ParseObject(await client.GetStringAsync($"/connectivity-probe/discover?host=127.0.0.1&port={closedPort}&usesConnectivityProbe=true"));

        Assert.Equal(ProbeTargetKind.Unreachable, Json.GetString(o, "targetKind"));
        Assert.Equal(0, Json.GetLong(o, "attemptsMade"));
    }

    [Fact]
    public async Task Discover_connectivity_probe_target_finds_its_instance_and_notes_single_instance()
    {
        await using var target = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        await using var app = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        using var client = app.Client();

        // confidence 0.999 ile tek pod için 10 tekrar gerekir (11 başarılı yanıt): tek pod notu tetiklenir.
        var o = ParseObject(await client.GetStringAsync(
            $"/connectivity-probe/discover?host=127.0.0.1:{target.Port}&usesConnectivityProbe=true&confidence=0.999"));

        Assert.Equal(ProbeTargetKind.ConnectivityProbe, Json.GetString(o, "targetKind"));
        Assert.Equal(1, Json.GetLong(o, "distinctInstances"));
        Assert.Equal(true, o["converged"]);
        Assert.Single(Json.GetStringList(o, "notes"));
        Assert.Contains(app.AllLogs, l => l.StartsWith("Information") && l.Contains("connectivityProbe"));
    }

    [Fact]
    public async Task Discover_target_without_connectivity_probe_stops_early()
    {
        await using var target = await TestApp.StartAsync(null); // ConnectivityProbe yok
        await using var app = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        using var client = app.Client();

        var o = ParseObject(await client.GetStringAsync($"/connectivity-probe/discover?host=127.0.0.1:{target.Port}&usesConnectivityProbe=true"));

        Assert.Equal(ProbeTargetKind.Other, Json.GetString(o, "targetKind"));
        Assert.Equal(true, o["stoppedEarly"]);
        Assert.True(Json.GetLong(o, "attemptsMade") <= 3);
    }

    [Fact]
    public async Task Discover_target_rejecting_key_is_reported_as_connectivity_probe_error()
    {
        await using var target = await TestApp.StartAsync(o => o.AccessKey = "target-key");
        await using var app = await TestApp.StartAsync(o => o.AllowAnonymous = true);
        using var client = app.Client();

        var o = ParseObject(await client.GetStringAsync($"/connectivity-probe/discover?host=127.0.0.1:{target.Port}&usesConnectivityProbe=true"));

        Assert.Equal(ProbeTargetKind.ConnectivityProbeError, Json.GetString(o, "targetKind"));
        Assert.Equal(true, o["stoppedEarly"]);
    }

    [Fact]
    public async Task Concurrent_discover_requests_above_the_limit_get_429()
    {
        // Hedefin identity ucu 1,5 sn gecikmeli; ilk discover isteği bu sürede kapıyı tutar.
        await using var target = await TestApp.StartAsync(o => { o.AllowAnonymous = true; o.Path = "/slow/connectivity-probe"; },
            slowPathDelay: TimeSpan.FromMilliseconds(1500));
        await using var app = await TestApp.StartAsync(o => { o.AllowAnonymous = true; o.MaxConcurrentDiscover = 1; o.Path = "/slow/connectivity-probe"; });
        using var client = app.Client();

        var first = client.GetAsync($"/slow/connectivity-probe/discover?host=127.0.0.1:{target.Port}&usesConnectivityProbe=true&attempts=1");
        await Task.Delay(400);
        var second = await client.GetAsync($"/slow/connectivity-probe/discover?host=127.0.0.1:{target.Port}");
        var identity = await client.GetAsync("/slow/connectivity-probe/identity");

        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);   // identity sınıra dahil değil
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/slow/connectivity-probe/discover?host=127.0.0.1:{target.Port}")).StatusCode);
    }
}
