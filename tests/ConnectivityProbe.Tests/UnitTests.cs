using Xunit;

namespace ConnectivityProbe.Tests;

public class OptionsTests
{
    private static ConnectivityProbeOptions Parse(params (string Key, string? Value)[] settings) =>
        ConnectivityProbeOptions.FromSettings(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));

    [Fact]
    public void Defaults_are_secure()
    {
        var o = new ConnectivityProbeOptions();
        Assert.True(o.Enabled);
        Assert.Null(o.AccessKey);
        Assert.False(o.AllowAnonymous);
        Assert.Empty(o.AllowedTargets);
        Assert.Equal(20, o.MaxConcurrentDiscover);
        Assert.Equal("/connectivity-probe", o.Path);
    }

    [Fact]
    public void Reads_values_case_insensitively()
    {
        var o = Parse(("accesskey", " secret "), ("ALLOWANONYMOUS", "true"), ("Path", "probe/"), ("MaxAttempts", "7"),
            ("MaxConcurrentDiscover", "0"), ("DefaultTimeoutMs", "1500"), ("Info:cluster", "prod-1"));

        Assert.Equal("secret", o.AccessKey);
        Assert.True(o.AllowAnonymous);
        Assert.Equal("/probe", o.Path);
        Assert.Equal(7, o.MaxAttempts);
        Assert.Equal(0, o.MaxConcurrentDiscover);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), o.DefaultTimeout);
        Assert.Equal("prod-1", o.Info["cluster"]);
    }

    [Fact]
    public void Invalid_values_keep_defaults()
    {
        var o = Parse(("MaxAttempts", "-5"), ("DefaultTimeoutMs", "abc"), ("AllowAnonymous", "yes"), ("MaxConcurrentDiscover", "x"));
        Assert.Equal(100, o.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), o.DefaultTimeout);
        Assert.False(o.AllowAnonymous);
        Assert.Equal(20, o.MaxConcurrentDiscover);
    }

    [Fact]
    public void Lists_accept_comma_separated_and_indexed_forms()
    {
        var o = Parse(("AllowedTargets", "sql01:1433, redis:6379"), ("AllowedTargets:0", "a:1"), ("AllowedTargets:1", "SQL01:1433"),
            ("IdentityEnvironmentVariables", "POD_NAME"));
        Assert.Equal(new[] { "sql01:1433", "redis:6379", "a:1" }, o.AllowedTargets);
        Assert.Equal(new[] { "POD_NAME" }, o.IdentityEnvironmentVariables);
    }
}

public class AllowedTargetsTests
{
    [Theory]
    [InlineData("sql01", 1433, true)]
    [InlineData("SQL01", 1433, true)]
    [InlineData("sql01", 1434, false)]
    [InlineData("redis", 1, true)]
    [InlineData("redis", 65535, true)]
    [InlineData("api.prod.svc.cluster.local", 443, true)]
    [InlineData("svc.cluster.local", 443, false)]           // "*.alan" alan adının kendisini değil alt adlarını kapsar
    [InlineData("evil-svc.cluster.local.attacker.com", 443, false)]
    [InlineData("db1.lan", 5432, true)]
    [InlineData("::1", 80, true)]
    [InlineData("10.0.0.5", 22, false)]
    public void Matches_patterns(string host, int port, bool expected)
    {
        var allowed = new List<string> { "sql01:1433", "redis:*", "*.svc.cluster.local:443", "*.lan:*", "[::1]:80" };
        Assert.Equal(expected, ProbeEngine.IsTargetAllowed(allowed, host, port));
    }

    [Fact]
    public void Empty_list_allows_everything() => Assert.True(ProbeEngine.IsTargetAllowed(new List<string>(), "anything", 1));
}

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
    [InlineData("sql01", "1433", "sql01", 1433, null)]
    [InlineData("sql01:1433", null, "sql01", 1433, null)]
    [InlineData("sql01:1433", "15", "sql01", 15, null)]
    [InlineData("https://orders.example.com/path?q=1", null, "orders.example.com", 443, "https")]
    [InlineData("http://x.com:8080/", null, "x.com", 8080, "http")]
    [InlineData("[::1]:5078", null, "::1", 5078, null)]
    [InlineData("::1", "80", "::1", 80, null)]
    public void Parses_valid_targets(string host, string? port, string expectedHost, int expectedPort, string? expectedScheme)
    {
        Assert.True(ProbeTarget.TryParse(host, port, out var h, out var p, out var scheme, out var error), error);
        Assert.Equal(expectedHost, h);
        Assert.Equal(expectedPort, p);
        Assert.Equal(expectedScheme, scheme);
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
    public void Id_is_stable_short_hex_and_seed_dependent()
    {
        var a = InstanceIdentityBuilder.ComputeId("pod-a|");
        Assert.Equal(a, InstanceIdentityBuilder.ComputeId("pod-a|"));
        Assert.Matches("^[0-9a-f]{12}$", a);
        Assert.NotEqual(a, InstanceIdentityBuilder.ComputeId("pod-b|"));
    }

    [Fact]
    public void Pod_name_is_used_only_when_it_differs_from_machine_name()
    {
        // Normal pod: makine adı = pod adı -> kimlik eski sürümle aynı kalır.
        Assert.Equal("web-7f9c-a|", InstanceIdentityBuilder.IdSeed("web-7f9c-a", "web-7f9c-a", ""));
        Assert.Equal("web-7f9c-a|", InstanceIdentityBuilder.IdSeed("web-7f9c-a", null, ""));
        // hostNetwork: makine adı node adıdır; aynı node'daki iki pod farklı kimlik almalı.
        Assert.NotEqual(InstanceIdentityBuilder.IdSeed("node-1", "web-a", ""), InstanceIdentityBuilder.IdSeed("node-1", "web-b", ""));
    }

    [Fact]
    public void Built_identity_contains_version_and_selected_environment_only()
    {
        var options = new ConnectivityProbeOptions();
        options.IdentityEnvironmentVariables.Clear();
        options.IdentityEnvironmentVariables.Add("CP_TEST_VISIBLE");
        Environment.SetEnvironmentVariable("CP_TEST_VISIBLE", "yes");
        Environment.SetEnvironmentVariable("CP_TEST_SECRET", "no");
        options.Info["cluster"] = "test";

        var identity = InstanceIdentityBuilder.Build(new ProbeRequest { Header = h => h == "X-Forwarded-For" ? "1.2.3.4" : null }, options);

        Assert.Equal(ProbeInfo.Version, identity.ProbeVersion);
        Assert.Equal("yes", identity.Environment["CP_TEST_VISIBLE"]);
        Assert.False(identity.Environment.ContainsKey("CP_TEST_SECRET"));
        Assert.Equal("test", identity.Info["cluster"]);
        Assert.Equal("1.2.3.4", identity.Request.ForwardedFor);

        // JSON üzerinden geri okunabilmeli (pod keşfi bunu kullanır).
        var parsed = InstanceIdentity.FromJson(Json.Parse(Json.Serialize(identity)));
        Assert.NotNull(parsed);
        Assert.Equal(identity.InstanceId, parsed!.InstanceId);
        Assert.Equal(identity.ProbeVersion, parsed.ProbeVersion);
    }

    [Fact]
    public void Version_is_a_package_version() => Assert.Matches(@"^\d+\.\d+\.\d+", ProbeInfo.Version);
}

public class AdaptiveTests
{
    [Theory]
    [InlineData(1, 0.99, 7)]
    [InlineData(3, 0.99, 17)]
    [InlineData(10, 0.95, 32)]
    public void Required_streak_matches_formula(int distinct, double confidence, int expected) =>
        Assert.Equal(expected, InstanceCollector.RequiredStreak(distinct, confidence));
}

public class EngineTests
{
    private static ProbeRequest Request(string? key = null, params (string Name, string Value)[] query) => new()
    {
        Query = name => query.FirstOrDefault(q => q.Name == name).Value,
        Header = name => name == ConnectivityProbeOptions.AccessKeyHeader ? key : null,
        RemoteIp = "10.0.0.9"
    };

    [Fact]
    public async Task Unconfigured_endpoints_return_403()
    {
        var engine = new ProbeEngine(new ConnectivityProbeOptions());
        var response = await engine.HandleAsync(Request(), ProbeEndpointKind.Identity);
        Assert.Equal(403, response.StatusCode);
        Assert.Contains("not configured", response.Json);
    }

    [Fact]
    public async Task Access_key_is_required_when_configured()
    {
        var logs = new List<string>();
        var engine = new ProbeEngine(new ConnectivityProbeOptions { AccessKey = "k1", Log = (_, m) => logs.Add(m) });

        Assert.Equal(401, (await engine.HandleAsync(Request(), ProbeEndpointKind.Identity)).StatusCode);
        Assert.Equal(401, (await engine.HandleAsync(Request("k2"), ProbeEndpointKind.Identity)).StatusCode);
        Assert.Equal(200, (await engine.HandleAsync(Request("k1"), ProbeEndpointKind.Identity)).StatusCode);
        Assert.Contains(logs, l => l.Contains("rejected") && l.Contains("10.0.0.9") && l.Contains("401"));
    }

    [Fact]
    public async Task Discover_validates_input_and_allowed_targets()
    {
        var options = new ConnectivityProbeOptions { AllowAnonymous = true };
        options.AllowedTargets.Add("allowed:1");
        var engine = new ProbeEngine(options);

        Assert.Equal(400, (await engine.HandleAsync(Request(), ProbeEndpointKind.Discover)).StatusCode);
        Assert.Equal(403, (await engine.HandleAsync(Request(null, ("host", "other:1")), ProbeEndpointKind.Discover)).StatusCode);
        Assert.Equal(400, (await engine.HandleAsync(Request(null, ("host", "allowed:1"), ("confidence", "2")), ProbeEndpointKind.Discover)).StatusCode);
        Assert.Equal(400, (await engine.HandleAsync(Request(null, ("host", "allowed:1"), ("scheme", "ftp")), ProbeEndpointKind.Discover)).StatusCode);
    }

    [Theory]
    [InlineData("/connectivity-probe/identity", "GET", true)]
    [InlineData("/CONNECTIVITY-PROBE/discover/", "get", true)]
    [InlineData("/connectivity-probe/discover", "POST", false)]
    [InlineData("/connectivity-probe/other", "GET", false)]
    [InlineData("/api/values", "GET", false)]
    public void Matches_only_its_own_paths(string path, string method, bool expected) =>
        Assert.Equal(expected, new ProbeEngine(new ConnectivityProbeOptions()).TryMatch(path, method, out _));
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
        // Boş bir port bulup kapatıyoruz; bağlantı reddedilmeli.
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
