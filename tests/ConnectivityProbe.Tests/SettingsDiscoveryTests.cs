using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace ConnectivityProbe.Tests;

public class SettingsDiscoveryTests
{
    private static DiscoveredEndpoint One(List<DiscoveredEndpoint> all, string key) => Assert.Single(all, e => e.Key == key);

    // Merkezi ayar servisinden gelen tipik bir yapı (değerler uydurma).
    private const string PaasLike = """
    {
      "AppInfo": { "Title": "Orders", "GroupName": "Demo", "IsActive": true },
      "AppSettings": {
        "ConnectionStrings": {
          "DefaultConnection": "Host=pg01.corp.test;Database=ecm;Username=ecm_user;Password=S3cr3t!",
          "LogConnection": "Host=pg02.corp.test;Port=5433;Database=logs;Username=u;Password=p"
        },
        "RabbitMQSettings": { "Host": "mq01.corp.test", "UserName": "guest", "Password": "guest", "VirtualHost": "/", "Port": 0 },
        "MailPaasSettings": { "BaseUrl": "https://mail.corp.test", "ClientId": "abc", "ClientSecret": "xyz", "FromMail": "noreply@corp.test" }
      },
      "Metadata": { "Version": "1.0.0" }
    }
    """;

    [Fact]
    public void Central_settings_structure_is_understood()
    {
        var all = SettingsDiscovery.Discover(PaasLike);

        var db = One(all, "AppSettings:ConnectionStrings:DefaultConnection");
        Assert.Equal(("postgresql", "pg01.corp.test", 5432, "high"), (db.Kind, db.Host, db.Port, db.Confidence));
        Assert.Equal(5433, One(all, "AppSettings:ConnectionStrings:LogConnection").Port);

        var mq = One(all, "AppSettings:RabbitMQSettings:Host");
        Assert.Equal(("rabbitmq", "mq01.corp.test", 5672), (mq.Kind, mq.Host, mq.Port));   // Port: 0 -> RabbitMQ varsayılanı

        var mail = One(all, "AppSettings:MailPaasSettings:BaseUrl");
        Assert.Equal(("http", "mail.corp.test", 443, true), (mail.Kind, mail.Host, mail.Port, mail.Tls));

        Assert.Equal(4, all.Count);   // e-posta adresi, sürüm, başlık bağımlılık sayılmaz
    }

    [Fact]
    public void Secrets_never_appear_in_the_result()
    {
        var json = """
        {
          "Db": "Server=sql01.corp.test;Database=x;User Id=sa;Password=TopSecret1;",
          "Mongo": "mongodb://admin:TopSecret2@mongo01.corp.test:27017/app",
          "Redis": "redis01.corp.test:6379,password=TopSecret3,ssl=True,abortConnect=False",
          "ApiKey": "https://evil-should-not-be-read.corp.test",
          "ClientSecret": "TopSecret4",
          "Token": "srv9.corp.test:8080"
        }
        """;
        var all = SettingsDiscovery.Discover(json);
        var text = string.Join("\n", all.Select(e => e.Key + e.Kind + e.Host + e.Port));
        foreach (var secret in new[] { "TopSecret", "admin", "sa", "evil", "srv9" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Equal(3, all.Count);
    }

    [Theory]
    [InlineData("Server=tcp:sql01.corp.test,1444;Initial Catalog=x;User ID=u;Password=p;Encrypt=True", "sqlserver", "sql01.corp.test", 1444, true)]
    [InlineData("Data Source=sql02.corp.test\\INST1;Initial Catalog=x;Integrated Security=True", "sqlserver", "sql02.corp.test", 1433, false)]
    [InlineData("Server=my01.corp.test;Port=3307;Database=x;Uid=u;Pwd=p;", "mysql", "my01.corp.test", 3307, false)]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=ora01.corp.test)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=X)));User Id=u;Password=p;", "oracle", "ora01.corp.test", 1522, false)]
    [InlineData("amqps://u:p@mq.corp.test/vhost", "rabbitmq", "mq.corp.test", 5671, true)]
    [InlineData("rediss://cache.corp.test", "redis", "cache.corp.test", 6380, true)]
    [InlineData("postgresql://u:p@pg.corp.test:6432/db", "postgresql", "pg.corp.test", 6432, false)]
    [InlineData("ldaps://dc01.corp.test", "ldap", "dc01.corp.test", 636, true)]
    [InlineData("kafka://broker.corp.test:19092", "kafka", "broker.corp.test", 19092, false)]
    public void Connection_strings_and_urls(string value, string kind, string host, int port, bool tls)
    {
        var e = Assert.Single(SettingsDiscovery.Discover("{\"X\":" + System.Text.Json.JsonSerializer.Serialize(value) + "}"));
        Assert.Equal((kind, host, port, tls, "high"), (e.Kind, e.Host, e.Port, e.Tls, e.Confidence));
    }

    [Fact]
    public void Multi_host_values_get_one_endpoint_each()
    {
        var all = SettingsDiscovery.Discover("""
        {
          "Mongo": "mongodb://m1.corp.test:27017,m2.corp.test:27018/?replicaSet=rs0",
          "Kafka": { "BootstrapServers": "k1.corp.test:9092,k2.corp.test:9092" }
        }
        """);
        Assert.Equal(new[] { "m1.corp.test:27017", "m2.corp.test:27018" }, all.Where(e => e.Kind == "mongodb").Select(e => e.Host + ":" + e.Port));
        Assert.Equal(2, all.Count(e => e.Kind == "kafka" && e.Confidence == "high"));
        Assert.Contains(all, e => e.Key == "Mongo#2");   // aynı ayardan ikinci adres ayrı kimlik alır
    }

    [Fact]
    public void Http_services_are_recognized_from_names_and_ports()
    {
        var all = SettingsDiscovery.Discover("""
        {
          "Logging": { "KibanaUrl": "https://logs.corp.test" },
          "Search": { "Uri": "http://es01.corp.test:9200" },
          "Mq": { "ManagementUrl": "http://rabbit.corp.test:15672" }
        }
        """);
        Assert.Equal("kibana", One(all, "Logging:KibanaUrl").Kind);
        Assert.Equal("elasticsearch", One(all, "Search:Uri").Kind);
        Assert.Equal("rabbitmq", One(all, "Mq:ManagementUrl").Kind);
    }

    [Fact]
    public void Host_and_port_fields_with_prefixes()
    {
        var all = SettingsDiscovery.Discover("""
        {
          "Smtp": { "SmtpServer": "smtp.corp.test", "SmtpPort": "587", "EnableSsl": true },
          "Cache": { "RedisHost": "redis.corp.test" },
          "Legacy": { "ServerAddress": "10.20.30.40", "Port": 7001 }
        }
        """);
        var smtp = One(all, "Smtp:Smtp");
        Assert.Equal(("smtp", 587, true), (smtp.Kind, smtp.Port, smtp.Tls));
        Assert.Equal(6379, One(all, "Cache:Redis").Port);                   // port yok: ipucundan
        var legacy = One(all, "Legacy:Server");
        Assert.Equal(("tcp", "10.20.30.40", 7001, "high"), (legacy.Kind, legacy.Host, legacy.Port, legacy.Confidence));
    }

    [Fact]
    public void Bare_values_are_suggestions_and_noise_is_ignored()
    {
        var all = SettingsDiscovery.Discover("""
        {
          "Endpoint2": "srv01.corp.test:8080",
          "KafkaBroker": "kafka01.corp.test",
          "Version": "2.1.0",
          "Ratio": "16:9",
          "Email": "a@corp.test",
          "Local": "http://localhost:5000",
          "Schema": "http://www.w3.org/2001/XMLSchema",
          "Text": "Merhaba dünya"
        }
        """);
        var srv = One(all, "Endpoint2");
        Assert.Equal(("tcp", "low"), (srv.Kind, srv.Confidence));
        var kafka = One(all, "KafkaBroker");
        Assert.Equal(("kafka", 9092, "low"), (kafka.Kind, kafka.Port, kafka.Confidence));
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Signature_ignores_order_and_changes_with_content()
    {
        var a = SettingsDiscovery.Discover(PaasLike);
        var b = a.AsEnumerable().Reverse().ToList();
        Assert.Equal(SettingsDiscovery.Signature(a), SettingsDiscovery.Signature(b));
        var changed = SettingsDiscovery.Discover(PaasLike.Replace("pg01.corp.test", "pg09.corp.test"));
        Assert.NotEqual(SettingsDiscovery.Signature(a), SettingsDiscovery.Signature(changed));
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        Assert.Throws<FormatException>(() => SettingsDiscovery.Discover("{ not json"));
    }

    // ------------------------------------------------------------------ 3. katman: doğrulama (sahte sunucularla)

    // Gelen ilk isteğe verilen cevabı dönen küçük bir TCP sunucusu.
    private static (int Port, Task Server) FakeServer(Func<byte[], byte[]?> reply, byte[]? greeting = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 12; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                if (greeting != null) { await stream.WriteAsync(greeting); }
                var buffer = new byte[4096];
                var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                if (await Task.WhenAny(readTask, Task.Delay(2000)) != readTask) continue;
                var n = await readTask;
                var answer = reply(buffer.Take(n).ToArray());
                if (answer != null) await stream.WriteAsync(answer);
                await Task.Delay(50);
            }
            listener.Stop();
        });
        return (port, server);
    }

    [Fact]
    public async Task Redis_is_identified_by_ping()
    {
        var (port, _) = FakeServer(req => Encoding.ASCII.GetString(req).StartsWith("PING") ? Encoding.ASCII.GetBytes("-NOAUTH Authentication required.\r\n") : null);
        var r = await EndpointVerifier.IdentifyAsync("127.0.0.1", port, false, null, 3000, CancellationToken.None);
        Assert.True(r.Open);
        Assert.Equal("redis", r.Kind);
    }

    [Fact]
    public async Task Kibana_is_identified_from_its_http_headers()
    {
        var (port, _) = FakeServer(req => Encoding.ASCII.GetString(req).StartsWith("GET")
            ? Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nkbn-name: kibana\r\nLocation: /app/home\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            : null);
        var r = await EndpointVerifier.IdentifyAsync("127.0.0.1", port, false, "http", 3000, CancellationToken.None);
        Assert.Equal("kibana", r.Kind);
    }

    [Fact]
    public async Task Postgres_is_identified_by_ssl_request()
    {
        var (port, _) = FakeServer(req => req.Length == 8 && req[4] == 0x04 && req[5] == 0xd2 ? new[] { (byte)'N' } : null);
        var r = await EndpointVerifier.IdentifyAsync("127.0.0.1", port, false, null, 3000, CancellationToken.None);
        Assert.Equal("postgresql", r.Kind);
    }

    [Fact]
    public async Task Rabbitmq_is_identified_by_amqp_header()
    {
        // Connection.Start: çerçeve türü 1, kanal 0, boyut, class 10, method 10
        var start = new byte[] { 1, 0, 0, 0, 0, 0, 4, 0, 10, 0, 10, 0xce };
        var (port, _) = FakeServer(req => Encoding.ASCII.GetString(req, 0, 4) == "AMQP" ? start : null);
        var r = await EndpointVerifier.IdentifyAsync("127.0.0.1", port, false, "rabbitmq", 3000, CancellationToken.None);
        Assert.Equal("rabbitmq", r.Kind);
    }

    [Fact]
    public async Task Server_speaking_first_is_identified_from_its_banner()
    {
        var (port, _) = FakeServer(_ => null, greeting: Encoding.ASCII.GetBytes("SSH-2.0-OpenSSH_9.6\r\n"));
        var r = await EndpointVerifier.IdentifyAsync("127.0.0.1", port, false, null, 3000, CancellationToken.None);
        Assert.Equal("ssh", r.Kind);
    }

    [Fact]
    public async Task Closed_port_is_reported_as_not_open()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var r = await EndpointVerifier.IdentifyAsync("127.0.0.1", port, false, null, 2000, CancellationToken.None);
        Assert.False(r.Open);
        Assert.Null(r.Kind);
    }

    [Fact]
    public async Task Unresolvable_names_are_detected()
    {
        Assert.False(await EndpointVerifier.ResolvesAsync("no-such-host.invalid", 3000, CancellationToken.None));
        Assert.True(await EndpointVerifier.ResolvesAsync("10.1.2.3", 3000, CancellationToken.None));
    }
}
