using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>
    /// 3. katman: bulunan adresin gerçekten bir sunucu olup olmadığını ve türünü ölçer.
    /// <list type="bullet">
    /// <item><see cref="ResolvesAsync"/>: ad bir IP'ye çözülüyor mu (her zaman, yalnızca düşük güvenli adaylar için).</item>
    /// <item><see cref="IdentifyAsync"/>: sunucuya bağlanıp o protokolün zararsız "merhaba"sını gönderir ve cevabından türü anlar
    /// (Monitor'de uygulama bazında açılır). Kimlik bilgisi gönderilmez, veri okunmaz/yazılmaz.</item>
    /// </list>
    /// </summary>
    public static class EndpointVerifier
    {
        /// <summary>Sunucu adı bir IP'ye çözülüyor mu (IP ise doğrudan evet).</summary>
        public static async Task<bool> ResolvesAsync(string host, int timeoutMs, CancellationToken ct)
        {
            if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return true;
            try
            {
                var dns = Dns.GetHostAddressesAsync(host);
                if (await Task.WhenAny(dns, Task.Delay(timeoutMs, ct)).ConfigureAwait(false) != dns) return false;
                return (await dns.ConfigureAwait(false)).Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Doğrulama sonucu.</summary>
        public sealed class Result
        {
            /// <summary>TCP bağlantısı kurulabildi mi.</summary>
            public bool Open { get; set; }
            /// <summary>Cevaptan anlaşılan tür (ör. "rabbitmq", "kibana", "http"); anlaşılamadıysa null.</summary>
            public string? Kind { get; set; }
        }

        /// <summary>
        /// Adrese bağlanıp türünü anlamaya çalışır. Tür biliniyorsa yalnızca o protokol denenir; bilinmiyorsa önce sunucunun
        /// kendiliğinden gönderdiği karşılama (MySQL, SMTP, SSH, FTP), sonra HTTP ve diğer protokoller sırayla denenir.
        /// </summary>
        public static async Task<Result> IdentifyAsync(string host, int port, bool tls, string? expectedKind, int timeoutMs, CancellationToken ct)
        {
            var result = new Result();
            var probes = ProbesFor(expectedKind, port);
            foreach (var probe in probes)
            {
                ct.ThrowIfCancellationRequested();
                byte[]? response;
                try
                {
                    response = await ExchangeAsync(host, port, tls && probe.WrapTls, probe.Request?.Invoke(host), timeoutMs, ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    return result;   // bağlantı kurulamadı: diğer protokolleri denemek anlamsız
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    result.Open = true;   // bağlandı ama bu protokolle konuşamadı
                    continue;
                }
                result.Open = true;
                if (response != null && response.Length > 0 && probe.Match(response) is string kind)
                {
                    result.Kind = kind;
                    return result;
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ protokoller

        private sealed class Probe
        {
            public Probe(string name, Func<string, byte[]>? request, Func<byte[], string?> match, bool wrapTls = true)
            {
                Name = name; Request = request; Match = match; WrapTls = wrapTls;
            }
            public string Name { get; }
            /// <summary>Gönderilecek "merhaba"; null ise yalnızca sunucunun karşılaması okunur.</summary>
            public Func<string, byte[]>? Request { get; }
            public Func<byte[], string?> Match { get; }
            /// <summary>TLS'li adreste önce TLS kurulsun mu (PostgreSQL ve SQL Server TLS'i kendi protokolü içinde açar).</summary>
            public bool WrapTls { get; }
        }

        private static readonly int[] HttpPorts = { 80, 443, 3000, 5000, 5601, 8000, 8080, 8081, 8443, 8500, 9000, 9200, 15672 };

        private static List<Probe> ProbesFor(string? kind, int port)
        {
            switch (kind)
            {
                case "http": case "kibana": case "elasticsearch": case "consul": return new List<Probe> { Http };
                case "rabbitmq": return port == 15672 ? new List<Probe> { Http } : new List<Probe> { Amqp };
                case "redis": return new List<Probe> { Redis };
                case "postgresql": return new List<Probe> { Postgres };
                case "mongodb": return new List<Probe> { Mongo };
                case "kafka": return new List<Probe> { Kafka };
                case "sqlserver": return new List<Probe> { SqlServer };
                case "mysql": case "smtp": case "ssh": case "ftp": return new List<Probe> { Banner };
            }
            // Tür bilinmiyor: karşılama, HTTP (bilinen bir HTTP portuysa önce), sonra diğerleri.
            var list = new List<Probe> { Banner };
            if (HttpPorts.Contains(port)) list.Add(Http);
            list.AddRange(new[] { Redis, Postgres, Amqp, Kafka, Mongo, SqlServer });
            if (!HttpPorts.Contains(port)) list.Add(Http);
            return list;
        }

        // MySQL / SMTP / SSH / FTP bağlanınca önce kendileri konuşur.
        private static readonly Probe Banner = new Probe("banner", null, r =>
        {
            var text = Encoding.ASCII.GetString(r, 0, Math.Min(r.Length, 256));
            if (text.StartsWith("SSH-", StringComparison.Ordinal)) return "ssh";
            if (text.StartsWith("220", StringComparison.Ordinal))
                return text.IndexOf("FTP", StringComparison.OrdinalIgnoreCase) >= 0 ? "ftp" : "smtp";
            // MySQL: 3 bayt uzunluk, 1 bayt sıra, sonra protokol sürümü 10 (veya hata paketi 0xFF)
            if (r.Length > 5 && r[3] == 0 && (r[4] == 0x0a || r[4] == 0xff) && r[0] + (r[1] << 8) + (r[2] << 16) == r.Length - 4) return "mysql";
            if (r.Length > 5 && r[3] == 0 && r[4] == 0x0a) return "mysql";
            return null;
        });

        // Redis: PING -> +PONG (şifre istiyorsa -NOAUTH / -ERR ... auth; yine Redis'tir).
        private static readonly Probe Redis = new Probe("redis", _ => Encoding.ASCII.GetBytes("PING\r\n"), r =>
        {
            var text = Encoding.ASCII.GetString(r, 0, Math.Min(r.Length, 128));
            if (text.StartsWith("+PONG", StringComparison.Ordinal) || text.StartsWith("-NOAUTH", StringComparison.Ordinal)
                || (text.StartsWith("-", StringComparison.Ordinal) && text.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)) return "redis";
            return null;
        });

        // AMQP 0-9-1 başlığı -> RabbitMQ Connection.Start (çerçeve türü 1, kanal 0) ya da desteklenen sürümü bildiren "AMQP" cevabı.
        private static readonly Probe Amqp = new Probe("amqp", _ => new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 0, 9, 1 }, r =>
        {
            if (r.Length >= 7 && r[0] == 1 && r[1] == 0 && r[2] == 0 && r.Length >= 11 && r[7] == 0 && r[8] == 10) return "rabbitmq";
            if (r.Length >= 4 && r[0] == 'A' && r[1] == 'M' && r[2] == 'Q' && r[3] == 'P') return "rabbitmq";
            return null;
        });

        // PostgreSQL SSLRequest -> tek bayt 'S' veya 'N'.
        private static readonly Probe Postgres = new Probe("postgresql", _ => new byte[] { 0, 0, 0, 8, 0x04, 0xd2, 0x16, 0x2f },
            r => r.Length == 1 && (r[0] == 'S' || r[0] == 'N') ? "postgresql" : null, wrapTls: false);

        // Kafka ApiVersions v0 (korelasyon kimliği 7) -> aynı korelasyon kimliğiyle cevap.
        private static readonly Probe Kafka = new Probe("kafka", _ => new byte[]
        {
            0, 0, 0, 12,          // uzunluk
            0, 18, 0, 0,          // api_key = 18 (ApiVersions), api_version = 0
            0, 0, 0, 7,           // correlation_id = 7
            0, 2, (byte)'c', (byte)'p' // client_id = "cp"
        }, r => r.Length >= 8 && r[4] == 0 && r[5] == 0 && r[6] == 0 && r[7] == 7 ? "kafka" : null);

        // MongoDB OP_MSG { hello: 1, $db: "admin" } -> OP_MSG (2013) veya OP_REPLY (1) cevabı.
        private static readonly Probe Mongo = new Probe("mongodb", _ => MongoHello(), r =>
        {
            if (r.Length < 16) return null;
            var opCode = r[12] | (r[13] << 8) | (r[14] << 16) | (r[15] << 24);
            var responseTo = r[8] | (r[9] << 8) | (r[10] << 16) | (r[11] << 24);
            return (opCode == 2013 || opCode == 1) && responseTo == 0x0c50 ? "mongodb" : null;
        });

        // SQL Server PRELOGIN -> TDS cevabı (paket türü 4).
        private static readonly Probe SqlServer = new Probe("sqlserver", _ => new byte[]
        {
            0x12, 0x01, 0x00, 0x1a, 0x00, 0x00, 0x01, 0x00,   // TDS başlığı: PRELOGIN, son paket, uzunluk 26
            0x00, 0x00, 0x0b, 0x00, 0x06,                     // VERSION: ofset 11, uzunluk 6
            0x01, 0x00, 0x11, 0x00, 0x01,                     // ENCRYPTION: ofset 17, uzunluk 1
            0xff,                                             // son
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,               // sürüm
            0x02                                              // şifreleme: desteklenmiyor
        }, r => r.Length >= 8 && r[0] == 0x04 ? "sqlserver" : null, wrapTls: false);

        // HTTP GET / -> başlık ve gövdeden Kibana, Elasticsearch, OpenSearch, RabbitMQ yönetim ekranı, Consul; yoksa "http".
        private static readonly Probe Http = new Probe("http", host => Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: " + host + "\r\nUser-Agent: ConnectivityProbe\r\nAccept: */*\r\nConnection: close\r\n\r\n"), r =>
        {
            var text = Encoding.UTF8.GetString(r);
            if (!text.StartsWith("HTTP/", StringComparison.Ordinal)) return null;
            bool Has(string s) => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has("\nkbn-name:") || Has("\nkbn-license-sig:") || Has("\nkbn-xpack-sig:") || Has("/app/kibana") || Has("<title>Elastic</title>") && Has("kbn")) return "kibana";
            if (Has("\nX-elastic-product: Elasticsearch") || Has("You Know, for Search")) return "elasticsearch";
            if (Has("The OpenSearch Project")) return "elasticsearch";
            if (Has("RabbitMQ Management")) return "rabbitmq";
            if (Has("\nX-Consul-")) return "consul";
            return "http";
        });

        private static byte[] MongoHello()
        {
            // BSON { hello: 1, $db: "admin" }
            var doc = new List<byte>();
            doc.Add(0x10); doc.AddRange(Encoding.ASCII.GetBytes("hello")); doc.Add(0); doc.AddRange(BitConverter.GetBytes(1));
            doc.Add(0x02); doc.AddRange(Encoding.ASCII.GetBytes("$db")); doc.Add(0); doc.AddRange(BitConverter.GetBytes(6));
            doc.AddRange(Encoding.ASCII.GetBytes("admin")); doc.Add(0);
            doc.Add(0);
            var bson = new List<byte>(BitConverter.GetBytes(doc.Count + 4));
            bson.AddRange(doc);

            var body = new List<byte>();
            body.AddRange(BitConverter.GetBytes(0));      // flagBits
            body.Add(0);                                   // section kind 0 (body)
            body.AddRange(bson);

            var message = new List<byte>();
            message.AddRange(BitConverter.GetBytes(16 + body.Count));   // messageLength
            message.AddRange(BitConverter.GetBytes(0x0c50));            // requestID
            message.AddRange(BitConverter.GetBytes(0));                 // responseTo
            message.AddRange(BitConverter.GetBytes(2013));              // opCode = OP_MSG
            message.AddRange(body);
            return message.ToArray();   // (BitConverter little-endian: .NET'in desteklediği tüm platformlar)
        }

        // ------------------------------------------------------------------ bağlantı

#if NETFRAMEWORK
        private static readonly SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls;
#else
        private static readonly SslProtocols Protocols = SslProtocols.None;
#endif

        // Bağlanır, (gerekirse TLS kurar), isteği gönderir ve gelen ilk cevabı okur (en fazla 16 KB). Bağlanamazsa SocketException.
        private static async Task<byte[]?> ExchangeAsync(string host, int port, bool tls, byte[]? request, int timeoutMs, CancellationToken ct)
        {
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var client = new TcpClient())
            {
                timer.CancelAfter(timeoutMs);
                var connect = client.ConnectAsync(host.Trim('[', ']'), port);
                if (await Task.WhenAny(connect, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false) != connect)
                    throw new SocketException((int)SocketError.TimedOut);
                await connect.ConfigureAwait(false);   // bağlantı hatası SocketException olarak çıkar

                Stream stream = client.GetStream();
                SslStream? ssl = null;
                try
                {
                    if (tls)
                    {
                        // Yalnızca türü anlamak için: sertifika burada doğrulanmaz (sertifika kontrolü TLS testinin işidir).
                        ssl = new SslStream(stream, false, (s, c, ch, e) => true);
                        var handshake = ssl.AuthenticateAsClientAsync(host.Trim('[', ']'), null, Protocols, false);
                        if (await Task.WhenAny(handshake, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false) != handshake)
                            throw new TimeoutException();
                        await handshake.ConfigureAwait(false);
                        stream = ssl;
                    }

                    if (request != null) await stream.WriteAsync(request, 0, request.Length, timer.Token).ConfigureAwait(false);

                    var buffer = new byte[16 * 1024];
                    var total = 0;
                    // Karşılama beklenen protokollerde (request == null) kısa bekleriz; cevap gelmezse sunucu konuşmuyor demektir.
                    var readTimeout = request == null ? Math.Min(timeoutMs, 1500) : timeoutMs;
                    using (var readTimer = CancellationTokenSource.CreateLinkedTokenSource(timer.Token))
                    {
                        readTimer.CancelAfter(readTimeout);
                        while (total < buffer.Length)
                        {
                            var read = stream.ReadAsync(buffer, total, buffer.Length - total, readTimer.Token);
                            if (await Task.WhenAny(read, Task.Delay(Timeout.Infinite, readTimer.Token)).ConfigureAwait(false) != read) break;
                            var n = await read.ConfigureAwait(false);
                            if (n <= 0) break;
                            total += n;
                            if (request == null || !IsHttpRequest(request)) break;   // HTTP dışında ilk parça yeterli
                            if (total >= 4096) break;
                        }
                    }
                    var result = new byte[total];
                    Array.Copy(buffer, result, total);
                    return result;
                }
                finally
                {
                    ssl?.Dispose();
                }
            }
        }

        private static bool IsHttpRequest(byte[] request) => request.Length > 4 && request[0] == 'G' && request[1] == 'E' && request[2] == 'T';
    }
}
