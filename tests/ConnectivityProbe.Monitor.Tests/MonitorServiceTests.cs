using Xunit;

namespace ConnectivityProbe.Monitor.Tests;

public class PodStateTests
{
    [Fact]
    public void Pods_report_and_app_is_healthy()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"));
        h.Report(app, new Pod("b", "10.42.1.16"));

        var s = h.Status(app);
        Assert.Equal("healthy", s.State);
        Assert.Equal(2, s.PodCount);
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void Silent_pod_becomes_missing_when_the_pod_count_drops()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"));
        h.Report(app, new Pod("b", "10.42.1.16"));
        Assert.Equal(2, h.Status(app).PodCount);   // beklenen pod sayısı: 2

        // "b" 100 saniye (3+ test aralığı) bildirim göndermiyor; "a" göndermeye devam ediyor.
        h.Time.Advance(TimeSpan.FromSeconds(100));
        h.Report(app, new Pod("a", "10.42.1.15"));
        var s = h.Status(app);

        Assert.Equal("degraded", s.State);
        Assert.Equal("missing", s.Pods.Single(p => p.InstanceId == "b").State);
        Assert.Contains(s.Notes, n => n.Code == "missingPods" && n.Count == 1);
    }

    [Fact]
    public void Replaced_pod_is_dropped_without_alarm()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"));
        h.Report(app, new Pod("b", "10.42.1.16"));
        h.Status(app);

        // Deploy: "b" kapanış bildirimi göndermeden gitti, yerine "c" geldi; pod sayısı korundu.
        h.Time.Advance(TimeSpan.FromSeconds(100));
        h.Report(app, new Pod("a", "10.42.1.15"));
        h.Report(app, new Pod("c", "10.42.1.17"));
        var s = h.Status(app);

        Assert.Equal("healthy", s.State);
        Assert.DoesNotContain(s.Pods, p => p.InstanceId == "b");
        Assert.Equal(2, s.PodCount);
    }

    [Fact]
    public void Goodbye_removes_the_pod_without_alarm()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"));
        h.Report(app, new Pod("b", "10.42.1.16"));
        h.Status(app);

        Assert.True(h.Monitor.AgentGoodbye(app.Id, "b"));
        var s = h.Status(app);
        Assert.Equal("healthy", s.State);
        Assert.Single(s.Pods);
    }

    [Fact]
    public void No_reports_means_down()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"));
        h.Status(app);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var s = h.Status(app);
        Assert.Equal("down", s.State);
        Assert.Contains(s.Notes, n => n.Code == "noReports");
    }

    [Fact]
    public void Different_versions_are_noted()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15", "1.4.0"));
        h.Report(app, new Pod("b", "10.42.1.16", "1.5.0"));
        var s = h.Status(app);
        Assert.Equal("healthy", s.State);   // bilgi notu; durumu değiştirmez
        Assert.Contains(s.Notes, n => n.Code == "versions" && n.Count == 2);
    }
}

public class ResourceAlertTests
{
    private static ResourceSample Mem(long used, long limit) => new() { WorkingSetBytes = used, MemoryBytes = used, MemoryLimitBytes = limit };

    [Fact]
    public void Memory_close_to_the_limit_makes_the_app_degraded()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"), Mem(95, 100));
        h.Report(app, new Pod("b", "10.42.1.16"), Mem(50, 100));

        var s = h.Status(app);
        Assert.Equal("degraded", s.State);
        Assert.Contains("memHigh", s.Pods.Single(p => p.InstanceId == "a").Alerts);
        Assert.Empty(s.Pods.Single(p => p.InstanceId == "b").Alerts);
        Assert.Contains(s.Notes, n => n.Code == "memHigh" && n.Count == 1);
    }

    [Fact]
    public void Memory_threshold_is_configurable()
    {
        using var h = new Harness(o => o.Alerts.MemoryPercent = 99);
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"), Mem(95, 100));
        Assert.Equal("healthy", h.Status(app).State);
    }

    [Fact]
    public void Throttling_is_a_note_not_a_problem()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"), new ResourceSample { CpuThrottledPercent = 40 });
        var s = h.Status(app);
        Assert.Equal("healthy", s.State);
        Assert.Contains(s.Notes, n => n.Code == "throttled");
    }

    [Fact]
    public void Port_exhaustion_makes_the_app_degraded()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"), new ResourceSample { TcpEstablished = 500, TcpTimeWait = 20000, TcpTotal = 20500, EphemeralPorts = 28232 });
        var s = h.Status(app);
        Assert.Equal("degraded", s.State);
        Assert.Contains(s.Notes, n => n.Code == "portsHigh");
    }

    [Fact]
    public void Restart_is_detected_and_expires_after_the_recent_window()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        var started = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        h.Report(app, new Pod("a", "10.42.1.15", StartedAtUtc: started));
        h.Status(app);

        // Aynı pod yeni bir süreçle geliyor (container yeniden başladı).
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Report(app, new Pod("a", "10.42.1.15", StartedAtUtc: started.AddHours(1)));
        var s = h.Status(app);
        var pod = s.Pods.Single();
        Assert.Equal(1, pod.Restarts);
        Assert.Contains("restart", pod.Alerts);
        Assert.Equal("degraded", s.State);

        // 61 dakika sonra (pod bildirmeye devam ederken) uyarı kalkar, sayı kalır.
        for (var i = 0; i < 61; i++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            h.Report(app, new Pod("a", "10.42.1.15", StartedAtUtc: started.AddHours(1)));
        }
        s = h.Status(app);
        Assert.Equal("healthy", s.State);
        Assert.Equal(1, s.Pods.Single().Restarts);
    }

    [Fact]
    public void Oom_kill_increase_raises_an_alert()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15"), new ResourceSample { OomKills = 0 });
        h.Report(app, new Pod("a", "10.42.1.15"), new ResourceSample { OomKills = 1 });
        var s = h.Status(app);
        Assert.Contains("oom", s.Pods.Single().Alerts);
        Assert.Contains(s.Notes, n => n.Code == "oom");
    }
}

public class ConnectionResultTests
{
    private static ConnectionDefinition Conn(string name, string host = "https://api.example.com/") => new() { Id = name, Name = name, Host = host };

    [Fact]
    public void Failed_test_makes_the_app_degraded_and_tracks_since_when()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("db", "sql01:1433"));
        h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", success: false));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", success: false));

        var s = h.Status(app);
        Assert.Equal("degraded", s.State);
        Assert.Contains(s.Notes, n => n.Code == "failedTests");
        var cell = s.Connections.Single().Cells.Single();
        Assert.Equal(h.Time.Utc.AddSeconds(-30), cell.FailingSinceUtc);
    }

    [Fact]
    public void Invalid_certificate_fails_the_test_even_when_tcp_succeeds()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("api"));
        var tls = new TlsReport { Handshake = true, Success = false, CertificateErrors = "RemoteCertificateNameMismatch", Error = "Certificate: RemoteCertificateNameMismatch" };
        h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("api", success: true, tls: tls));

        var cell = h.Status(app).Connections.Single().Cells.Single();
        Assert.True(cell.TcpSuccess);
        Assert.False(cell.Success);
        Assert.Contains("TLS", cell.Error);
    }

    [Fact]
    public void Certificate_expiring_soon_makes_the_app_degraded()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("api"));
        var tls = new TlsReport { Handshake = true, Success = true, NotAfterUtc = h.Time.Utc.AddDays(5) };
        h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("api", success: true, tls: tls));

        var s = h.Status(app);
        Assert.Equal("degraded", s.State);
        var note = Assert.Single(s.Notes, n => n.Code == "certExpiring");
        Assert.Equal(h.Time.Utc.AddDays(5), note.AtUtc);
    }

    [Fact]
    public void Certificate_far_from_expiry_is_fine()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("api"));
        var tls = new TlsReport { Handshake = true, Success = true, NotAfterUtc = h.Time.Utc.AddDays(60) };
        h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("api", success: true, tls: tls));
        Assert.Equal("healthy", h.Status(app).State);
    }

    [Fact]
    public void Connection_three_times_slower_than_usual_is_marked_slow()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("db", "sql01:1433"));
        void Run(long ms) { h.Time.Advance(TimeSpan.FromSeconds(30)); h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("db", true, ms)); }

        for (var i = 0; i < 8; i++) Run(10);
        Assert.False(h.Status(app).Connections.Single().Cells.Single().Slow);

        for (var i = 0; i < 3; i++) Run(200);
        var s = h.Status(app);
        var cell = s.Connections.Single().Cells.Single();
        Assert.True(cell.Slow);
        Assert.Equal(10, cell.BaselineMs);
        Assert.Equal("healthy", s.State);                   // yavaşlık bilgi notudur
        Assert.Contains(s.Notes, n => n.Code == "slow");
    }

    [Fact]
    public void Round_robin_dns_does_not_raise_ip_change_but_a_new_address_does()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("api"));
        void Run(string ip) { h.Time.Advance(TimeSpan.FromSeconds(30)); h.Report(app, new Pod("a", "10.42.1.15"), null, Harness.Tcp("api", true, 10, ip)); }

        // Havuzdan dönüşümlü iki IP (github.com gibi): öğrenilir, uyarı yok.
        for (var i = 0; i < 8; i++) Run(i % 2 == 0 ? "140.82.121.3" : "140.82.121.4");
        var cell = h.Status(app).Connections.Single().Cells.Single();
        Assert.Null(cell.AddressesChangedUtc);

        // Hiç görülmemiş bir adres: DNS kaydı değişti.
        Run("10.99.0.1");
        var s = h.Status(app);
        cell = s.Connections.Single().Cells.Single();
        Assert.Equal(h.Time.Utc, cell.AddressesChangedUtc);
        Assert.Contains(s.Notes, n => n.Code == "ipChanged");
    }

    [Fact]
    public void Assignment_tells_pods_which_connections_need_tls()
    {
        using var h = new Harness();
        var app = h.AddApp("orders", Conn("api"), Conn("db", "sql01:1433"), new ConnectionDefinition { Id = "ldap", Name = "ldap", Host = "ldap01", Port = 389, TlsCheck = "on" });
        var assignment = h.Report(app, new Pod("a", "10.42.1.15"));
        var tls = assignment.Connections.ToDictionary(c => c.Id, c => c.Tls);
        Assert.True(tls["api"]);    // https:// -> otomatik
        Assert.False(tls["db"]);    // 1433 -> TLS değil
        Assert.True(tls["ldap"]);   // elle açıldı
    }
}

public class ClusterTests
{
    [Fact]
    public void Pods_are_grouped_and_named_by_their_network()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a1", "10.42.1.15"));
        h.Report(app, new Pod("a2", "10.42.2.16"));     // aynı cluster, başka node (/24)
        h.Report(app, new Pod("b1", "10.43.1.21"));

        var s = h.Status(app);
        Assert.Equal("10.42.0.0/16", s.Pods.Single(p => p.InstanceId == "a1").ClusterName);
        Assert.Equal("10.42.0.0/16", s.Pods.Single(p => p.InstanceId == "a2").ClusterName);
        Assert.Equal("10.43.0.0/16", s.Pods.Single(p => p.InstanceId == "b1").ClusterName);
        Assert.Equal(2, h.Monitor.GetSnapshot().Clusters.Count);
    }

    [Fact]
    public void Two_kubernetes_clusters_with_the_same_pod_network_stay_apart()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15", ClusterId: "aaaaaa111111"));
        h.Report(app, new Pod("b", "10.42.1.15", ClusterId: "bbbbbb222222"));   // ikisi de varsayılan 10.42.0.0/16

        var names = h.Status(app).Pods.Select(p => p.ClusterName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "10.42.0.0/16 · aaaaaa", "10.42.0.0/16 · bbbbbb" }, names);
    }

    [Fact]
    public void Custom_name_wins_and_empty_name_goes_back_to_the_network()
    {
        using var h = new Harness();
        var app = h.AddApp("orders");
        h.Report(app, new Pod("a", "10.42.1.15", ClusterId: "aaaaaa111111"));
        h.Store.Mutate(d => d.Clusters.Single().Name = "Prod İstanbul");
        Assert.Equal("Prod İstanbul", h.Status(app).Pods.Single().ClusterName);

        h.Store.Mutate(d => d.Clusters.Single().Name = "");
        Assert.Equal("10.42.0.0/16", h.Status(app).Pods.Single().ClusterName);
    }

    [Fact]
    public void Old_cluster_n_names_are_cleared_but_custom_names_kept()
    {
        using var h = new Harness(definitionsJson: """
            { "clusters": [ { "key": "k8s:1", "name": "Cluster 1" }, { "key": "k8s:2", "name": "Prod Ankara" } ],
              "connections": [ { "id": "c1", "teamId": "old-team", "name": "db", "host": "sql01", "port": 1433 } ] }
            """);
        var d = h.Store.Snapshot();
        Assert.Equal("", d.Clusters.Single(c => c.Key == "k8s:1").Name);
        Assert.Equal("Prod Ankara", d.Clusters.Single(c => c.Key == "k8s:2").Name);
        Assert.Single(d.Connections);   // eski ekip havuzundaki bağlantı tek havuza alındı
    }
}

public class RuleTests
{
    [Theory]
    [InlineData("10.42.1.15", "10.42.0.0/16")]
    [InlineData("10.43.200.1", "10.43.0.0/16")]
    [InlineData("::ffff:10.42.1.15", "10.42.0.0/16")]
    [InlineData("fd00:1:2:3:4:5:6:7", "fd00:1:2:3::/64")]
    [InlineData("not-an-ip", null)]
    public void Network_of_address(string ip, string? expected) => Assert.Equal(expected, Network.Of(ip));

    [Theory]
    [InlineData("sql01:1433", null, "sql01:1433")]
    [InlineData("SQL01", 1433, "sql01:1433")]
    [InlineData("sql01.", 1433, "sql01:1433")]
    [InlineData("https://github.com/", null, "github.com:443")]
    [InlineData("https://GitHub.com/login", null, "github.com:443")]
    [InlineData("http://x.com:8080/a", null, "x.com:8080")]
    [InlineData("sql01:1433", 15, "sql01:15")]          // port alanı host içindeki porttan önce gelir
    [InlineData("sql01", null, null)]                    // port yok: geçersiz
    public void Endpoint_key(string host, int? port, string? expected) => Assert.Equal(expected, ConnectionRules.EndpointKey(host, port));

    [Fact]
    public void Duplicate_address_is_found_except_for_the_connection_itself()
    {
        var d = new DefinitionData { Connections = { new ConnectionDefinition { Id = "1", Name = "GitHub", Host = "https://github.com/" } } };
        Assert.Equal("1", ConnectionRules.DuplicateOf(d, "https://github.com/login", null, exceptId: null)?.Id);
        Assert.Null(ConnectionRules.DuplicateOf(d, "https://github.com/", null, exceptId: "1"));
        Assert.Null(ConnectionRules.DuplicateOf(d, "https://gitlab.com/", null, exceptId: null));
    }

    [Theory]
    [InlineData("https://api.example.com/", null, null, true)]
    [InlineData("api.example.com", 443, null, true)]
    [InlineData("ldap01", 636, null, true)]
    [InlineData("sql01", 1433, null, false)]
    [InlineData("sql01", 1433, "on", true)]
    [InlineData("https://api.example.com/", null, "off", false)]
    public void Tls_check_mode(string host, int? port, string? mode, bool expected) =>
        Assert.Equal(expected, new ConnectionDefinition { Host = host, Port = port, TlsCheck = mode }.TlsEnabled());
}
