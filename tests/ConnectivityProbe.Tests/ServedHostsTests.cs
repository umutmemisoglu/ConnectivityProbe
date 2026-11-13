using System.Diagnostics;
using Xunit;

namespace ConnectivityProbe.Tests;

public class ServedHostsTests
{
    // ASP.NET Core'un HttpContext.Request.Host.Host zincirini taklit eden küçük tipler.
    public sealed class FakeHostString { public string Host { get; init; } = ""; }
    public sealed class FakeRequest { public FakeHostString Host { get; init; } = new(); }
    public sealed class FakeContext { public FakeRequest Request { get; init; } = new(); }
    public sealed class FieldPayload { public readonly object httpContext; public readonly long timestamp; public FieldPayload(object c) { httpContext = c; timestamp = 1; } }

    private static FakeContext Context(string host) => new() { Request = new FakeRequest { Host = new FakeHostString { Host = host } } };

    [Fact]
    public void Addresses_the_application_is_called_at_are_learned_from_incoming_requests()
    {
        using var served = ServedHosts.Start()!;
        Assert.NotNull(served);
        using var aspnet = new DiagnosticListener("Microsoft.AspNetCore");

        // Yalnızca istek başı olayı açılır; Activity (HttpRequestIn) ve diğer olaylar etkilenmez.
        Assert.True(aspnet.IsEnabled("Microsoft.AspNetCore.Hosting.BeginRequest"));
        Assert.False(aspnet.IsEnabled("Microsoft.AspNetCore.Hosting.HttpRequestIn"));
        Assert.False(aspnet.IsEnabled("Microsoft.AspNetCore.Hosting.EndRequest"));

        // Eski sürümler: anonim nesne (özellik); yeni sürümler: httpContext alanı.
        aspnet.Write("Microsoft.AspNetCore.Hosting.BeginRequest", new { httpContext = Context("Orders-test.corp.test"), timestamp = 1L });
        Thread.Sleep(80);
        aspnet.Write("Microsoft.AspNetCore.Hosting.BeginRequest", new FieldPayload(Context("orders-api.ns.svc.cluster.local")));
        Thread.Sleep(80);
        aspnet.Write("Microsoft.AspNetCore.Hosting.BeginRequest", new { httpContext = Context("10.42.1.7"), timestamp = 2L });   // pod IP: alınmaz

        Assert.Equal(new[] { "orders-api.ns.svc.cluster.local", "orders-test.corp.test" }, served.Snapshot());   // sıralı
    }

    [Fact]
    public void Other_listeners_and_unexpected_payloads_are_ignored()
    {
        using var served = ServedHosts.Start()!;
        using var other = new DiagnosticListener("HttpHandlerDiagnosticListener");
        Assert.False(other.IsEnabled("Microsoft.AspNetCore.Hosting.BeginRequest"));
        using var aspnet = new DiagnosticListener("Microsoft.AspNetCore");
        aspnet.Write("Microsoft.AspNetCore.Hosting.BeginRequest", new { something = 1 });   // beklenmeyen içerik: hata yok
        Assert.Empty(served.Snapshot());
    }

    [Theory]
    [InlineData("Orders.Example.com.", "orders.example.com")]
    [InlineData("api.svc:8080", "api.svc")]
    [InlineData("orders", "orders")]
    [InlineData("10.42.1.7", null)]
    [InlineData("[::1]:5000", null)]
    [InlineData("localhost", null)]
    [InlineData("app.localhost", null)]
    [InlineData("bad host", null)]
    [InlineData("", null)]
    public void Addresses_are_normalized(string input, string? expected) => Assert.Equal(expected, ServedHosts.Normalize(input));

    [Fact]
    public void At_most_twenty_addresses_are_kept()
    {
        using var served = ServedHosts.Start()!;
        for (var i = 0; i < 30; i++) served.Add($"host{i}.example.com");
        Assert.Equal(ServedHosts.MaxHosts, served.Snapshot().Count);
    }
}
