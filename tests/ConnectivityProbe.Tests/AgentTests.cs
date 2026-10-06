using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ConnectivityProbe.Tests;

/// <summary>Sahte Monitor: pod bildirimlerini toplar, bilinmeyen anahtarı "kaydeder" ve bağlantı listesini döner.</summary>
internal sealed class FakeMonitor : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; }
    public ConcurrentQueue<(string Key, AgentReport Report)> Reports { get; } = new();
    public ConcurrentQueue<string> Goodbyes { get; } = new();
    public ConcurrentDictionary<string, string> Registered { get; } = new();   // anahtar -> uygulama adı
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
            var key = ctx.Request.Headers[ConnectivityProbeOptions.AppKeyHeader].ToString();
            var report = (await JsonSerializer.DeserializeAsync<AgentReport>(ctx.Request.Body, json))!;
            self!.Reports.Enqueue((key, report));
            self.Registered.TryAdd(key, report.AppName); // aynı anahtar varsa yeniden kaydedilmez
            return Results.Json(new
            {
                appId = "id-" + key, appName = self.Registered[key], intervalSeconds = 3600, runRequestId = self.RunRequestId,
                timeoutMs = 2000, connections = self.Connections
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

// Agent süreç başına tek olduğu ve konsol çıktısı yakalandığı için bu sınıftaki testler sırayla çalışır.
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

    private static async Task<(string Key, AgentReport Report)> WaitForReport(
        FakeMonitor monitor, Func<(string Key, AgentReport Report), bool> match, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            foreach (var r in monitor.Reports)
                if (match(r)) return r;
            await Task.Delay(100);
        }
        throw new TimeoutException("no matching report in " + timeout);
    }

    // Konsola yazılanları yakalar (Monitor'e bağlanamama mesajları yalnızca konsola gider).
    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _writer = new();
        public ConsoleCapture() => Console.SetOut(TextWriter.Synchronized(_writer));
        public string Text => _writer.ToString();
        public void Dispose() => Console.SetOut(_original);
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("http://monitor", "")]
    [InlineData("   ", "   ")]
    public void Missing_settings_disable_the_agent_without_throwing(string url, string key)
    {
        using var console = new ConsoleCapture();
        Assert.Null(ConnectivityProbeAgent.Start(url, key));
        Assert.Contains("[ConnectivityProbe] MonitorUrl and AppKey are required", console.Text);
    }

    [Fact]
    public void Invalid_monitor_url_disables_the_agent_without_throwing()
    {
        using var console = new ConsoleCapture();
        Assert.Null(ConnectivityProbeAgent.Start("monitor.local", "orders"));
        Assert.Contains("Invalid MonitorUrl", console.Text);
    }

    [Fact]
    public async Task Agent_registers_tests_connections_reports_versions_and_says_goodbye()
    {
        // Kubernetes cluster'ı taklit eden service account klasörü.
        var sa = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(sa, "ca.crt"), "-----BEGIN CERTIFICATE-----\nTEST-CLUSTER\n-----END CERTIFICATE-----\n");
        File.WriteAllText(Path.Combine(sa, "namespace"), "orders-prod\n");
        Environment.SetEnvironmentVariable("CONNECTIVITYPROBE_SERVICEACCOUNT_DIR", sa);

        var target = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;

        await using var monitor = await FakeMonitor.StartAsync();
        monitor.Connections = new object[]
        {
            new { id = "db", name = "db", host = "127.0.0.1", port = targetPort },
            new { id = "bad", name = "bad", host = "", port = (int?)null },
        };

        using var console = new ConsoleCapture();
        var agent = ConnectivityProbeAgent.Start(monitor.Url, "orders-api", "Orders API", o => o.PollSeconds = 1);
        Assert.NotNull(agent);
        try
        {
            // step 1: İlk bildirim = kayıt; hemen ardından test sonuçları gelir.
            var (key, report) = await WaitForReport(monitor, r => r.Report.Run != null, TimeSpan.FromSeconds(20));
            Assert.Equal("orders-api", key);
            Assert.Equal("Orders API", monitor.Registered["orders-api"]);
            Assert.Equal("Orders API", report.AppName);
            Assert.Equal(1, report.PollSeconds);

            // Uygulama sürümü: Start'ı çağıran proje (bu test projesi).
            var asm = typeof(AgentTests).Assembly;
            Assert.Equal(PodIdentityBuilder.PickVersion(
                asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                asm.GetName().Version), report.Pod.AppVersion);
            Assert.Equal(PodIdentityBuilder.BuildIdOf(asm), report.Pod.BuildId);
            Assert.Equal(PodIdentityBuilder.ReadClusterId(sa), report.Pod.ClusterId);
            Assert.Equal("orders-prod", report.Pod.Namespace);

            var results = report.Run!.Results.ToDictionary(r => r.ConnectionId);
            Assert.True(results["db"].Tcp!.HostnameAttempts.Single().Success, results["db"].Tcp!.HostnameAttempts.Single().Error);
            Assert.Null(results["bad"].Tcp);
            Assert.Contains("Invalid target", results["bad"].Error);
            Assert.Equal("id-orders-api", agent!.AppId);
            Assert.Contains("[ConnectivityProbe] Registered to monitor", console.Text);

            // step 2: Aralık 1 saat olduğu halde "Şimdi test et" (runRequestId değişti) beklemeden yeni tur başlatır.
            var runsBefore = monitor.Reports.Count(r => r.Report.Run != null);
            monitor.RunRequestId = "2";
            await WaitFor(() => monitor.Reports.Count(r => r.Report.Run != null) > runsBefore ? "ok" : null, TimeSpan.FromSeconds(20));

            // step 3: Aralık dolmadıkça yalnızca canlılık bildirimi gönderilir.
            var countAfterRun = monitor.Reports.Count;
            await WaitFor(() => monitor.Reports.Count > countAfterRun + 1 ? "ok" : null, TimeSpan.FromSeconds(10));
            Assert.All(monitor.Reports.Skip(countAfterRun), r => Assert.Null(r.Report.Run));
        }
        finally
        {
            agent!.Stop();
            target.Stop();
            Environment.SetEnvironmentVariable("CONNECTIVITYPROBE_SERVICEACCOUNT_DIR", null);
        }

        // step 4: Durdurulunca Monitor'e "kapanıyorum" bildirilir.
        Assert.Contains(monitor.Reports.First().Report.Pod.InstanceId, monitor.Goodbyes);
        Assert.Null(ConnectivityProbeAgent.Current);
    }

    [Fact]
    public async Task Unreachable_monitor_does_not_throw_and_writes_one_console_line()
    {
        // Boş bir port: Monitor yok.
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        using var console = new ConsoleCapture();
        var agent = ConnectivityProbeAgent.Start("http://127.0.0.1:" + port, "orders-api", configure: o => o.PollSeconds = 1);
        try
        {
            await WaitFor(() => agent!.LastError, TimeSpan.FromSeconds(10));
            await Task.Delay(2500); // birkaç deneme daha
            var lines = console.Text.Split('\n').Count(x => x.Contains("Could not connect to monitor"));
            Assert.Equal(1, lines);                     // tekrar eden hatalar konsolu boğmaz
            Assert.Null(agent!.LastContactUtc);
        }
        finally
        {
            agent!.Stop(); // Monitor'e hiç ulaşılmadıysa veda göndermeye çalışıp beklemez
        }
    }

    [Fact]
    public async Task Same_key_from_many_pods_registers_once()
    {
        await using var monitor = await FakeMonitor.StartAsync();
        var agent = ConnectivityProbeAgent.Start(monitor.Url, "shared-key", "First Name", o => o.PollSeconds = 1);
        try
        {
            await WaitFor(() => monitor.Reports.Count >= 2 ? "ok" : null, TimeSpan.FromSeconds(10));
            Assert.Single(monitor.Registered);
            Assert.Same(agent, ConnectivityProbeAgent.Start(monitor.Url, "shared-key", "Other")); // süreç başına tek agent
        }
        finally
        {
            agent!.Stop();
        }
    }
}
