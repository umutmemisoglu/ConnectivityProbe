using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace ConnectivityProbe.Tests;

public class HealthTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Health_endpoint_answers_with_the_connectivityprobe_signature()
    {
        using var server = HealthServer.Start(FreePort(), () => "{\"connectivityProbe\":\"x\",\"status\":\"Healthy\",\"appId\":\"a1\",\"appName\":\"Orders API\",\"appVersion\":\"1.2.3\"}")!;
        using var http = new HttpClient();

        var ok = await http.GetAsync($"http://127.0.0.1:{server.Port}/connectivityprobe/health");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(ok.Headers.Contains("X-ConnectivityProbe"));
        Assert.Contains("\"status\":\"Healthy\"", await ok.Content.ReadAsStringAsync());

        var other = await http.GetAsync($"http://127.0.0.1:{server.Port}/health");   // uygulamanın kendi ucu değil
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Busy_port_falls_back_to_the_next_one()
    {
        var port = FreePort();
        var busy = new TcpListener(IPAddress.IPv6Any, port);
        busy.Server.DualMode = true;
        busy.Start();
        try
        {
            using var server = HealthServer.Start(port, () => "{}")!;
            Assert.NotEqual(port, server.Port);
            var r = await HealthProbe.CheckAsync("127.0.0.1", server.Port, 3000, CancellationToken.None);
            Assert.Equal("probe", r.Outcome);
        }
        finally { busy.Stop(); }
    }

    [Fact]
    public async Task Probe_reads_status_and_application_of_a_connectivityprobe_service()
    {
        using var server = HealthServer.Start(FreePort(), () => "{\"connectivityProbe\":\"x\",\"status\":\"Degraded\",\"appId\":\"a1\",\"appName\":\"Orders API\",\"appVersion\":\"1.2.3\"}")!;
        var r = await HealthProbe.CheckAsync("127.0.0.1", server.Port, 3000, CancellationToken.None);
        Assert.Equal(("probe", true, "Degraded", "a1", "Orders API", "1.2.3"), (r.Outcome, r.Implemented, r.Status, r.AppId, r.AppName, r.AppVersion));
    }

    [Fact]
    public async Task Closed_health_port_means_the_service_does_not_use_connectivityprobe()
    {
        var r = await HealthProbe.CheckAsync("127.0.0.1", FreePort(), 3000, CancellationToken.None);
        Assert.Equal(("refused", (bool?)false), (r.Outcome, r.Implemented));
    }

    // Gelen isteğe sabit bir cevap veren küçük sunucu.
    private static int FakeServer(byte[] answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(answer);
            client.Client.Shutdown(SocketShutdown.Both);
            listener.Stop();
        });
        return port;
    }

    [Fact]
    public async Task Another_http_service_on_the_port_is_not_mistaken_for_connectivityprobe()
    {
        var port = FakeServer(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 20\r\n\r\n{\"status\":\"Healthy\"}"));
        var r = await HealthProbe.CheckAsync("127.0.0.1", port, 3000, CancellationToken.None);
        Assert.Equal(("other", (bool?)false), (r.Outcome, r.Implemented));
        Assert.Null(r.Status);
    }

    [Fact]
    public async Task Non_http_service_on_the_port_is_not_mistaken_for_connectivityprobe()
    {
        var port = FakeServer(Encoding.ASCII.GetBytes("-ERR unknown command\r\n"));
        var r = await HealthProbe.CheckAsync("127.0.0.1", port, 3000, CancellationToken.None);
        Assert.Equal(("other", (bool?)false), (r.Outcome, r.Implemented));
    }

    [Fact]
    public void Groups_are_built_with_keywords()
    {
        var o = new ConnectivityProbeOptions().AddGroup("Ödeme servisleri", "pay", "billing");
        var g = Assert.Single(o.Groups);
        Assert.Equal("Ödeme servisleri", g.Name);
        Assert.Equal(new[] { "pay", "billing" }, g.Keywords);
    }
}
