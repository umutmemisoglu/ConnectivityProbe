using System.Reflection;
using Xunit;

namespace ConnectivityProbe.Tests;

public class JsonTests
{
    private sealed class Sample
    {
        public string Name { get; set; } = "a\"b\\c\n<tag>&'";
        public int Count { get; set; } = 3;
        public double Ratio { get; set; } = 0.25;
        public bool Flag { get; set; } = true;
        public string? Missing { get; set; }
        public List<string> Items { get; set; } = new() { "x", "ğüşıöç" };
        public Dictionary<string, string> Map { get; set; } = new() { ["Key"] = "v" };
        public DateTime When { get; set; } = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    }

    [Fact]
    public void Serialize_then_parse_round_trips_with_camel_case()
    {
        var json = Json.Serialize(new Sample());
        var o = Assert.IsAssignableFrom<IDictionary<string, object?>>(Json.Parse(json));

        Assert.Equal("a\"b\\c\n<tag>&'", Json.GetString(o, "name"));
        Assert.Equal(3, Json.GetLong(o, "count"));
        Assert.Equal(0.25, Json.GetDouble(o, "ratio"));
        Assert.True(Json.GetBool(o, "flag"));
        Assert.Equal(new[] { "x", "ğüşıöç" }, Json.GetStringList(o, "items"));
        Assert.Equal("v", Json.GetStringMap(o, "map")["Key"]);   // sözlük anahtarları olduğu gibi kalır
        Assert.Null(Json.GetString(o, "missing"));
        Assert.Equal("2026-01-02T03:04:05.0000000Z", Json.GetString(o, "when"));
        Assert.DoesNotContain("<", json);                        // HTML'e gömülse de güvenli
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"a\": }")]
    [InlineData("[1,]x")]
    public void Parse_rejects_invalid_json(string text) => Assert.Throws<FormatException>(() => Json.Parse(text));
}

public class ProbeTargetTests
{
    [Theory]
    [InlineData("sql01", "1433", "sql01", 1433)]
    [InlineData("sql01:1433", null, "sql01", 1433)]
    [InlineData("sql01:1433", "15", "sql01", 15)]
    [InlineData("https://orders.example.com/path?q=1", null, "orders.example.com", 443)]
    [InlineData("http://x.com:8080/", null, "x.com", 8080)]
    [InlineData("[::1]:5078", null, "::1", 5078)]
    [InlineData("::1", "80", "::1", 80)]
    public void Parses_valid_targets(string host, string? port, string expectedHost, int expectedPort)
    {
        Assert.True(ProbeTarget.TryParse(host, port, out var h, out var p, out _, out var error), error);
        Assert.Equal(expectedHost, h);
        Assert.Equal(expectedPort, p);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "80")]
    [InlineData("sql01", null)]       // port yok
    [InlineData("sql01", "70000")]
    [InlineData("sql01", "abc")]
    public void Rejects_invalid_targets(string? host, string? port) =>
        Assert.False(ProbeTarget.TryParse(host, port, out _, out _, out _, out _));
}

public class IdentityTests
{
    [Fact]
    public void Id_is_stable_short_hex()
    {
        var a = PodIdentityBuilder.ComputeId("pod-a|");
        Assert.Equal(a, PodIdentityBuilder.ComputeId("pod-a|"));
        Assert.Matches("^[0-9a-f]{12}$", a);
        Assert.NotEqual(a, PodIdentityBuilder.ComputeId("pod-b|"));
    }

    [Fact]
    public void Pod_name_is_used_only_when_it_differs_from_machine_name()
    {
        Assert.Equal("web-7f9c-a|", PodIdentityBuilder.IdSeed("web-7f9c-a", "web-7f9c-a"));
        Assert.Equal("web-7f9c-a|", PodIdentityBuilder.IdSeed("web-7f9c-a", null));
        // hostNetwork: makine adı node adıdır; aynı node'daki iki pod farklı kimlik almalı.
        Assert.NotEqual(PodIdentityBuilder.IdSeed("node-1", "web-a"), PodIdentityBuilder.IdSeed("node-1", "web-b"));
    }

    [Theory]
    [InlineData("1.4.0", "1.4.0.0", "1.4.0")]                 // eşit: daha açıklayıcı olan informational
    [InlineData("1.4.0+abc123", "1.4.0.0", "1.4.0")]          // +commit eki gösterilmez
    [InlineData("1.5.0-beta.2", "1.4.0.0", "1.5.0-beta.2")]   // informational büyük
    [InlineData("1.0.0", "2.3.0.0", "2.3.0.0")]               // assembly version büyük
    [InlineData(null, "3.1.0.0", "3.1.0.0")]
    [InlineData("build-42", "1.2.0.0", "1.2.0.0")]            // sayısal olmayan informational
    [InlineData("7", null, "7")]
    public void Picks_the_greater_version(string? informational, string? assembly, string expected) =>
        Assert.Equal(expected, PodIdentityBuilder.PickVersion(informational, assembly == null ? null : Version.Parse(assembly)));

    [Fact]
    public void Build_id_comes_from_the_module_version_id()
    {
        var asm = typeof(IdentityTests).Assembly;
        Assert.Equal(asm.ManifestModule.ModuleVersionId.ToString("N")[..8], PodIdentityBuilder.BuildIdOf(asm));
    }

    [Fact]
    public void Cluster_id_is_the_fingerprint_of_the_service_account_certificate()
    {
        var a = Directory.CreateTempSubdirectory().FullName;
        var b = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(a, "ca.crt"), "-----BEGIN CERTIFICATE-----\nCLUSTER-A\n-----END CERTIFICATE-----\n");
        File.WriteAllText(Path.Combine(b, "ca.crt"), "-----BEGIN CERTIFICATE-----\nCLUSTER-B\n-----END CERTIFICATE-----\n");

        var idA = PodIdentityBuilder.ReadClusterId(a);
        Assert.Matches("^[0-9a-f]{12}$", idA!);
        Assert.Equal(idA, PodIdentityBuilder.ReadClusterId(a));            // aynı cluster -> aynı kimlik
        Assert.NotEqual(idA, PodIdentityBuilder.ReadClusterId(b));         // farklı cluster -> farklı kimlik
        Assert.Null(PodIdentityBuilder.ReadClusterId(Path.Combine(a, "yok"))); // Kubernetes dışı
    }

    [Fact]
    public void Built_identity_contains_application_version_and_selected_environment_only()
    {
        Environment.SetEnvironmentVariable("NODE_NAME", "node-7");
        Environment.SetEnvironmentVariable("CP_TEST_SECRET", "no");
        var asm = typeof(IdentityTests).Assembly;

        var identity = PodIdentityBuilder.Build(asm, null);

        Assert.Equal(asm.GetName().Name, identity.AppName);
        Assert.Equal(PodIdentityBuilder.PickVersion(
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, asm.GetName().Version), identity.AppVersion);
        Assert.Equal(8, identity.BuildId.Length);
        Assert.NotNull(identity.BuildDateUtc);
        Assert.Equal(ProbeInfo.Version, identity.ProbeVersion);
        Assert.Equal("node-7", identity.Environment["NODE_NAME"]);
        Assert.False(identity.Environment.ContainsKey("CP_TEST_SECRET"));
        Assert.Equal("Orders", PodIdentityBuilder.Build(asm, " Orders ").AppName);
    }

    [Fact]
    public void Version_is_a_package_version() => Assert.Matches(@"^\d+\.\d+\.\d+", ProbeInfo.Version);
}

public class TcpProbeTests
{
    [Fact]
    public async Task Open_port_succeeds_and_reports_address()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var result = await TcpProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(2));
            Assert.True(result.Success, result.Error);
            Assert.Equal("127.0.0.1", result.RemoteAddress);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Closed_port_fails_with_reason()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await TcpProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task Unknown_host_reports_resolve_error()
    {
        var report = await TcpProbe.ProbeAllAsync("does-not-exist.invalid", 80, 1, TimeSpan.FromSeconds(3), TimeSpan.Zero);
        Assert.DoesNotContain(report.HostnameAttempts, r => r.Success);
        Assert.False(string.IsNullOrEmpty(report.ResolveError));
    }
}
