using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ConnectivityProbe
{
    /// <summary>Uygulamanın ayarlarında bulunan bir bağımlılık: veritabanı, kuyruk, servis adresi...</summary>
    public sealed class DiscoveredEndpoint
    {
        /// <summary>Ayardaki yeri (ör. "AppSettings:ConnectionStrings:DefaultConnection"). Bağımlılığın kimliğidir.</summary>
        public string Key { get; set; } = "";
        /// <summary>
        /// Tür: postgresql, sqlserver, mysql, oracle, mongodb, redis, rabbitmq, kafka, elasticsearch, kibana, smtp, ldap, nats,
        /// memcached, zookeeper, consul, ssh, ftp, http, tcp (bilinmiyor).
        /// </summary>
        public string Kind { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        /// <summary>Bağlantı TLS ile kuruluyor (https, amqps, ssl=true...).</summary>
        public bool Tls { get; set; }
        /// <summary>
        /// "high": biçimden kesin (şemalı adres, bağlantı metni, Host + Port alanları, tür doğrulandı) - Monitor otomatik tanımlar.
        /// "low": yalnızca ipucu (ör. "srv01:8080") - Monitor öneri olarak gösterir.
        /// </summary>
        public string Confidence { get; set; } = "high";
        /// <summary>Doğrulama açıksa sunucunun cevabından anlaşılan tür (ör. "rabbitmq"); doğrulanmadıysa null.</summary>
        public string? Verified { get; set; }

        public override string ToString() => Key + " -> " + Kind + " " + Host + ":" + Port + (Tls ? " (tls)" : "") + " [" + Confidence + "]";
    }

    /// <summary>Pod'un ayarlarından bulduğu bağımlılıklar; Monitor'e her bildirimde gönderilir.</summary>
    public sealed class DiscoveredSettings
    {
        /// <summary>Bulunanların imzası: değişmediyse Monitor bir şey yapmaz. Ayarlar hiç okunamadıysa null.</summary>
        public string? Signature { get; set; }
        public DateTime ReadAtUtc { get; set; }
        public List<DiscoveredEndpoint> Endpoints { get; set; } = new List<DiscoveredEndpoint>();
        /// <summary>Son okuma başarısızsa nedeni (önceki başarılı sonuç gönderilmeye devam eder).</summary>
        public string? Error { get; set; }
        /// <summary>Sunucu türleri protokol imzasıyla doğrulandı mı (Monitor'de uygulama bazında açılır).</summary>
        public bool Verified { get; set; }
    }

    /// <summary>
    /// Ayar JSON'undaki bağımlılıkları bulur (1. katman: aday bulma, 2. katman: sınıflandırma). Yalnızca adres bilgisi (tür, host,
    /// port, TLS) çıkarılır; şifre, kullanıcı adı, anahtar gibi bilgiler hiçbir zaman sonuca girmez. Adı şifre / anahtar / token
    /// olan alanlara hiç bakılmaz. Tanınan biçimler:
    /// <list type="bullet">
    /// <item>Bağlantı metinleri: "Host=..;Port=..;Database=.." (PostgreSQL), "Server=..;Initial Catalog=.." (SQL Server),
    /// "(HOST=..)(PORT=..)" (Oracle), "host:6379,ssl=true,..." (Redis).</item>
    /// <item>Adresler: http(s)://, mongodb://, redis(s)://, amqp(s)://, postgres(ql)://, mysql://, kafka://, ldap(s)://, smtp(s)://...</item>
    /// <item>"host:port" ve "h1:9092,h2:9092" listeleri.</item>
    /// <item>Aynı nesnede host ve port alanları: {"Host": "..", "Port": 5672}, {"SmtpServer": "..", "SmtpPort": 25}. Port yoksa
    /// veya 0 ise türün varsayılanı (alan yolundan anlaşılır: RabbitMQ 5672, Redis 6379...).</item>
    /// </list>
    /// Tür; şemadan, bağlantı metninden, porttan, alan adından ve sunucu adından anlaşılır. Kesin biçimler "high", yalnızca
    /// ipucuyla bulunanlar "low" güvenlidir. 3. katman (sunucuya bağlanıp türü doğrulama) <see cref="EndpointVerifier"/>'dadır.
    /// </summary>
    public static class SettingsDiscovery
    {
        private static readonly Regex SecretKey = new Regex(
            "password|passwd|pwd|secret|token|apikey|api_key|privatekey|private_key|credential|passphrase|signingkey|accesskey|access_key",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HostPort = new Regex(@"^(\[[0-9a-fA-F:\.]+\]|[A-Za-z0-9][A-Za-z0-9\-\._]*):(\d{1,5})$", RegexOptions.CultureInvariant);
        private static readonly Regex HostName = new Regex(@"^(\[[0-9a-fA-F:\.]+\]|[A-Za-z0-9][A-Za-z0-9\-\._]*)$", RegexOptions.CultureInvariant);
        // Gerçek bir sunucu adına benzer: IP ya da en az bir nokta ve harfle biten son parça (sql01.sirket.local, 10.0.0.5).
        private static readonly Regex ServerLike = new Regex(@"^(\d{1,3}(\.\d{1,3}){3}|[A-Za-z0-9][A-Za-z0-9\-]*(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})$", RegexOptions.CultureInvariant);
        private static readonly Regex OracleHost = new Regex(@"\(\s*HOST\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OraclePort = new Regex(@"\(\s*PORT\s*=\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // (ValueTuple .NET Framework 4.6.2'de yerleşik değil, ek paket gerektirirdi; küçük bir sınıf kullanıyoruz.)
        private sealed class Kind
        {
            public Kind(string name, int port, bool tls = false) { Name = name; Port = port; Tls = tls; }
            public string Name { get; }
            public int Port { get; }
            public bool Tls { get; }
        }

        // Adres şeması -> tür, varsayılan port, TLS
        private static readonly Dictionary<string, Kind> Schemes = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
        {
            ["http"] = new Kind("http", 80), ["https"] = new Kind("http", 443, true),
            ["ws"] = new Kind("http", 80), ["wss"] = new Kind("http", 443, true),
            ["mongodb"] = new Kind("mongodb", 27017),
            ["redis"] = new Kind("redis", 6379), ["rediss"] = new Kind("redis", 6380, true),
            ["amqp"] = new Kind("rabbitmq", 5672), ["amqps"] = new Kind("rabbitmq", 5671, true), ["rabbitmq"] = new Kind("rabbitmq", 5672),
            ["postgres"] = new Kind("postgresql", 5432), ["postgresql"] = new Kind("postgresql", 5432),
            ["mysql"] = new Kind("mysql", 3306), ["mariadb"] = new Kind("mysql", 3306),
            ["sqlserver"] = new Kind("sqlserver", 1433), ["mssql"] = new Kind("sqlserver", 1433),
            ["kafka"] = new Kind("kafka", 9092), ["nats"] = new Kind("nats", 4222),
            ["ldap"] = new Kind("ldap", 389), ["ldaps"] = new Kind("ldap", 636, true),
            ["smtp"] = new Kind("smtp", 25), ["smtps"] = new Kind("smtp", 465, true),
            ["ftp"] = new Kind("ftp", 21), ["sftp"] = new Kind("ssh", 22), ["ssh"] = new Kind("ssh", 22),
            ["memcached"] = new Kind("memcached", 11211), ["zookeeper"] = new Kind("zookeeper", 2181),
            ["consul"] = new Kind("consul", 8500), ["tcp"] = new Kind("tcp", 0),
        };

        // Ad ipucu (alan yolunda veya sunucu adında geçen) -> tür, varsayılan port. Sıra önemli: özelden genele.
        private static readonly KeyValuePair<string, Kind>[] NameHints =
        {
            Hint("kibana", "kibana", 5601), Hint("elastic", "elasticsearch", 9200), Hint("opensearch", "elasticsearch", 9200),
            Hint("rabbit", "rabbitmq", 5672), Hint("amqp", "rabbitmq", 5672), Hint("masstransit", "rabbitmq", 5672),
            Hint("kafka", "kafka", 9092), Hint("bootstrap", "kafka", 9092),
            Hint("redis", "redis", 6379), Hint("mongo", "mongodb", 27017),
            Hint("postgre", "postgresql", 5432), Hint("npgsql", "postgresql", 5432), Hint("pgsql", "postgresql", 5432),
            Hint("mysql", "mysql", 3306), Hint("maria", "mysql", 3306), Hint("oracle", "oracle", 1521),
            Hint("mssql", "sqlserver", 1433), Hint("sqlserver", "sqlserver", 1433),
            Hint("memcache", "memcached", 11211), Hint("zookeeper", "zookeeper", 2181), Hint("consul", "consul", 8500),
            Hint("smtp", "smtp", 25), Hint("mail", "smtp", 25), Hint("ldap", "ldap", 389), Hint("nats", "nats", 4222),
        };

        // Port -> tür (ad veya şema yoksa)
        private static readonly Dictionary<int, string> Ports = new Dictionary<int, string>
        {
            [5672] = "rabbitmq", [5671] = "rabbitmq", [15672] = "rabbitmq",
            [9092] = "kafka", [9093] = "kafka", [9094] = "kafka",
            [27017] = "mongodb", [27018] = "mongodb", [6379] = "redis", [6380] = "redis",
            [5432] = "postgresql", [1433] = "sqlserver", [3306] = "mysql", [1521] = "oracle",
            [5601] = "kibana", [9200] = "elasticsearch", [9300] = "elasticsearch",
            [25] = "smtp", [587] = "smtp", [465] = "smtp", [389] = "ldap", [636] = "ldap",
            [4222] = "nats", [11211] = "memcached", [2181] = "zookeeper", [8500] = "consul", [22] = "ssh", [21] = "ftp",
        };

        private static readonly string[] HostKeySuffixes = { "hostname", "host", "server", "servername", "address", "endpoint" };

        private static KeyValuePair<string, Kind> Hint(string text, string kind, int port) => new KeyValuePair<string, Kind>(text, new Kind(kind, port));

        /// <summary>Ayar JSON'undaki bağımlılıkları bulur. JSON geçersizse FormatException.</summary>
        public static List<DiscoveredEndpoint> Discover(string json)
        {
            object? root;
            try { root = Json.Parse(json); }
            catch (Exception ex) { throw new FormatException("settings are not valid JSON: " + ex.Message, ex); }

            var found = new List<DiscoveredEndpoint>();
            Walk(root, "", found);

            // Aynı ayar yolundan aynı adres bir kez; birden fazla adres çıkan yollar (#2, #3...) ayrı kimlik alır.
            var result = new List<DiscoveredEndpoint>();
            foreach (var group in found.GroupBy(e => e.Key))
            {
                var unique = group.GroupBy(e => e.Host.ToLowerInvariant() + ":" + e.Port).Select(g => g.First()).ToList();
                for (int i = 0; i < unique.Count; i++)
                {
                    if (i > 0) unique[i].Key = group.Key + "#" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    result.Add(unique[i]);
                }
            }
            return result;
        }

        /// <summary>Bulunanların sıradan bağımsız imzası (değişiklik tespiti için).</summary>
        public static string Signature(IEnumerable<DiscoveredEndpoint> endpoints)
        {
            var text = string.Join("\n", endpoints
                .Select(e => e.Key + "|" + e.Kind + "|" + e.Host.ToLowerInvariant() + "|" + e.Port.ToString(CultureInfo.InvariantCulture)
                             + "|" + (e.Tls ? 1 : 0) + "|" + e.Confidence + "|" + e.Verified)
                .OrderBy(s => s, StringComparer.Ordinal));
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return string.Concat(hash.Take(12).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>Port numarasından tahmin edilen tür (bilinmiyorsa null).</summary>
        internal static string? KindOfPort(int port) => Ports.TryGetValue(port, out var kind) ? kind : null;

        // ------------------------------------------------------------------ JSON'u gezme

        private static void Walk(object? node, string path, List<DiscoveredEndpoint> found)
        {
            switch (node)
            {
                case Dictionary<string, object?> obj:
                    var consumed = FromHostPortFields(obj, path, found);
                    foreach (var pair in obj)
                    {
                        if (consumed.Contains(pair.Key) || SecretKey.IsMatch(pair.Key)) continue;   // şifre vb. alanlara bakılmaz
                        Walk(pair.Value, Join(path, pair.Key), found);
                    }
                    break;
                case List<object?> list:
                    for (int i = 0; i < list.Count; i++) Walk(list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", found);
                    break;
                case string text:
                    FromValue(text.Trim(), path, found);
                    break;
            }
        }

        private static string Join(string path, string key) => path.Length == 0 ? key : path + ":" + key;

        // ------------------------------------------------------------------ host + port alanları

        // Aynı nesnede "Host" + "Port", "SmtpServer" + "SmtpPort", "RedisHost" (port yok, türün varsayılanı) gibi alanlar.
        private static HashSet<string> FromHostPortFields(Dictionary<string, object?> obj, string path, List<DiscoveredEndpoint> found)
        {
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in obj)
            {
                if (!(pair.Value is string hostValue) || SecretKey.IsMatch(pair.Key)) continue;
                var suffix = HostKeySuffixes.FirstOrDefault(s => pair.Key.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                if (suffix == null) continue;
                hostValue = hostValue.Trim();
                if (hostValue.Length == 0 || hostValue.Contains("://") || hostValue.Contains(";") || hostValue.Contains("=") || hostValue.Contains(" ")) continue;

                var prefix = pair.Key.Substring(0, pair.Key.Length - suffix.Length);
                // "SmtpServer" + "SmtpPort"; yoksa aynı nesnedeki düz "Port" ("ServerAddress" + "Port").
                var portKey = obj.Keys.FirstOrDefault(k => string.Equals(k, prefix + "port", StringComparison.OrdinalIgnoreCase))
                              ?? obj.Keys.FirstOrDefault(k => string.Equals(k, "port", StringComparison.OrdinalIgnoreCase));
                int port = portKey != null ? ToPort(obj[portKey]) : 0;
                var flagTls = TlsFlag(obj, prefix);
                var key = Join(path, prefix.Length > 0 ? prefix.TrimEnd(':', '_', '.') : pair.Key);
                var nameHint = HintOf(path + ":" + pair.Key);

                var any = false;
                foreach (var item in hostValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var token = item.Trim();
                    int p = port;
                    var m = HostPort.Match(token);
                    if (m.Success) { token = m.Groups[1].Value; p = ParseInt(m.Groups[2].Value); }
                    else if (!HostName.IsMatch(token)) continue;

                    var hint = nameHint ?? HintOf(token);
                    var kind = hint?.Name ?? (p > 0 ? KindOfPort(p) : null) ?? "tcp";
                    if (p == 0 && kind == "rabbitmq" && flagTls) p = 5671;
                    if (p == 0) p = hint?.Port ?? 0;
                    if (p == 0) continue;   // portu bilinmeyen adres test edilemez

                    // Açık port ya da alan adından anlaşılan tür: kesin. Yalnızca sunucu adından tahmin: öneri.
                    var confident = port > 0 || m.Success || nameHint != null;
                    any |= Add(found, key, kind, token, p, flagTls || IsTlsPort(p), confident);
                }
                if (any)
                {
                    consumed.Add(pair.Key);
                    if (portKey != null) consumed.Add(portKey);
                }
            }
            return consumed;
        }

        private static int ToPort(object? value)
        {
            if (value is double d && d >= 0 && d <= 65535) return (int)d;
            if (value is string s && int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p <= 65535) return p;
            return 0;
        }

        private static int ParseInt(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

        private static bool TlsFlag(Dictionary<string, object?> obj, string prefix)
        {
            foreach (var name in new[] { "ssl", "usessl", "enablessl", "tls", "usetls", "sslenabled", "tlsenabled" })
            {
                var key = obj.Keys.FirstOrDefault(k => string.Equals(k, prefix + name, StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (key == null) continue;
                var v = obj[key];
                if (v is bool b && b) return true;
                if (v is string s && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1")) return true;
                if (v is Dictionary<string, object?> nested && nested.TryGetValue("enabled", out var e) && e is bool eb && eb) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ tek değer

        private static void FromValue(string s, string path, List<DiscoveredEndpoint> found)
        {
            if (s.Length < 3 || s.Length > 4000) return;
            if (s.Contains("://")) FromUrls(s, path, found);
            else if (s.IndexOf("(HOST", StringComparison.OrdinalIgnoreCase) >= 0) FromOracle(s, path, found);
            else if (s.Contains("=") && s.Contains(";")) FromConnectionString(s, path, found);
            else if (s.Contains("=") && s.Contains(",")) FromRedisString(s, path, found);
            else if (!s.Contains(" ") && !s.Contains("@") && !s.Contains("=")) FromBare(s, path, found);
        }

        // https://host/path, mongodb://user:pass@h1:27017,h2/db, amqps://host...
        private static void FromUrls(string s, string path, List<DiscoveredEndpoint> found)
        {
            var idx = s.IndexOf("://", StringComparison.Ordinal);
            var scheme = s.Substring(0, idx);
            if (scheme.Length == 0 || scheme.Contains(" ")) return;
            if (scheme.Equals("mongodb+srv", StringComparison.OrdinalIgnoreCase)) return;   // SRV kaydı: doğrudan test edilecek host yok
            if (!Schemes.TryGetValue(scheme, out var info)) return;

            var rest = s.Substring(idx + 3);
            var end = rest.IndexOfAny(new[] { '/', '?', '#', ' ' });
            var authority = end >= 0 ? rest.Substring(0, end) : rest;
            var at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);   // kullanıcı adı ve şifre atılır

            foreach (var item in authority.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = item.Trim();
                int port = info.Port;
                var m = HostPort.Match(token);
                if (m.Success) { token = m.Groups[1].Value; port = ParseInt(m.Groups[2].Value); }
                else if (!HostName.IsMatch(token)) continue;
                if (port == 0) continue;

                // http adresi: Kibana / Elasticsearch / RabbitMQ yönetim ekranı ayrıca anlaşılır (alan adı, sunucu adı, port).
                var kind = info.Name;
                if (kind == "http")
                {
                    var hinted = HintOf(path)?.Name ?? HintOf(token)?.Name ?? KindOfPort(port);
                    if (hinted is "kibana" or "elasticsearch" or "rabbitmq" or "consul") kind = hinted;
                }
                Add(found, path, kind, token, port, info.Tls || IsTlsPort(port), confident: true);
            }
        }

        // Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=ora01)(PORT=1521))...)
        private static void FromOracle(string s, string path, List<DiscoveredEndpoint> found)
        {
            var hosts = OracleHost.Matches(s);
            var ports = OraclePort.Matches(s);
            for (int i = 0; i < hosts.Count; i++)
            {
                int port = i < ports.Count ? ParseInt(ports[i].Groups[1].Value) : ports.Count > 0 ? ParseInt(ports[0].Groups[1].Value) : 1521;
                Add(found, path, "oracle", hosts[i].Groups[1].Value, port, false, confident: true);
            }
        }

        // Host=pg01;Port=5432;Database=x;Username=u;Password=p   |   Server=tcp:sql01,1433;Initial Catalog=x;...
        private static void FromConnectionString(string s, string path, List<DiscoveredEndpoint> found)
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in s.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part.Substring(0, eq).Trim().Replace(" ", "");
                if (!pairs.ContainsKey(key)) pairs[key] = part.Substring(eq + 1).Trim().Trim('"', '\'');
            }

            string? hostValue = null;
            string? hostKey = null;
            foreach (var k in new[] { "Host", "Server", "DataSource", "Address", "Addr", "NetworkAddress", "HostName" })
                if (pairs.TryGetValue(k, out var v) && v.Length > 0) { hostValue = v; hostKey = k; break; }
            if (hostValue == null) return;

            int port = pairs.TryGetValue("Port", out var portText) ? ToPort(portText) : 0;
            var hint = HintOf(path);
            string kind;
            if (hint != null && hint.Name is "postgresql" or "mysql" or "oracle" or "sqlserver") kind = hint.Name;
            else if (hostKey == "Host") kind = "postgresql";
            else if (pairs.ContainsKey("Uid") || pairs.ContainsKey("SslMode") && !pairs.ContainsKey("InitialCatalog")) kind = "mysql";
            else kind = "sqlserver";

            var defaultPort = kind switch { "postgresql" => 5432, "mysql" => 3306, "oracle" => 1521, _ => 1433 };
            var tls = pairs.TryGetValue("Encrypt", out var enc) && (enc.Equals("true", StringComparison.OrdinalIgnoreCase) || enc.Equals("strict", StringComparison.OrdinalIgnoreCase))
                   || pairs.TryGetValue("SslMode", out var ssl) && (ssl.Equals("Require", StringComparison.OrdinalIgnoreCase) || ssl.StartsWith("Verify", StringComparison.OrdinalIgnoreCase));

            foreach (var item in hostValue.Split(kind == "sqlserver" ? new[] { ';' } : new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = item.Trim();
                int p = port;
                if (kind == "sqlserver")
                {
                    // tcp:sql01,1433  |  sql01\INSTANCE  |  sql01,1433
                    if (token.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) token = token.Substring(4);
                    var comma = token.IndexOf(',');
                    if (comma > 0) { p = ToPort(token.Substring(comma + 1)); token = token.Substring(0, comma); }
                    var slash = token.IndexOf('\\');
                    if (slash > 0) token = token.Substring(0, slash);   // adlandırılmış örnek: varsayılan port denenir
                    if (token == "." || token.StartsWith("(", StringComparison.Ordinal)) continue;   // (localdb) vb.
                }
                else
                {
                    var m = HostPort.Match(token);
                    if (m.Success) { token = m.Groups[1].Value; p = ParseInt(m.Groups[2].Value); }
                }
                if (!HostName.IsMatch(token)) continue;
                Add(found, path, kind, token, p > 0 ? p : defaultPort, tls, confident: true);
            }
        }

        // StackExchange.Redis: redis01:6379,redis02:6379,password=...,ssl=True,abortConnect=False
        private static void FromRedisString(string s, string path, List<DiscoveredEndpoint> found)
        {
            var parts = s.Split(',').Select(p => p.Trim()).ToList();
            var options = parts.Where(p => p.Contains("=")).ToList();
            var known = new[] { "password", "ssl", "abortconnect", "connecttimeout", "synctimeout", "defaultdatabase", "allowadmin", "user", "name", "sslprotocols", "connectretry" };
            if (!options.Any(o => known.Contains(o.Substring(0, o.IndexOf('=')).Trim().ToLowerInvariant()))) return;
            var tls = options.Any(o => o.Replace(" ", "").Equals("ssl=true", StringComparison.OrdinalIgnoreCase));
            foreach (var token in parts.Where(p => !p.Contains("=")))
            {
                var m = HostPort.Match(token);
                if (m.Success) Add(found, path, "redis", m.Groups[1].Value, ParseInt(m.Groups[2].Value), tls, confident: true);
                else if (HostName.IsMatch(token)) Add(found, path, "redis", token, tls ? 6380 : 6379, tls, confident: true);
            }
        }

        // kafka01:9092,kafka02:9092  |  10.0.0.5:5672  |  tek başına sunucu adı (mq01.sirket.local; port alan adından)
        private static void FromBare(string s, string path, List<DiscoveredEndpoint> found)
        {
            var tokens = s.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var pathHint = HintOf(path);
            if (tokens.Count > 0 && tokens.All(t => HostPort.IsMatch(t)))
            {
                foreach (var token in tokens)
                {
                    var m = HostPort.Match(token);
                    var host = m.Groups[1].Value;
                    var port = ParseInt(m.Groups[2].Value);
                    var hint = pathHint ?? HintOf(host);
                    var kind = hint?.Name ?? KindOfPort(port);
                    // "abc:12" gibi rastgele metinleri atlıyoruz: sunucu adına benzemeli ya da bir ipucu olmalı.
                    if (kind == null && !ServerLike.IsMatch(host.Trim('[', ']')) && !host.StartsWith("[", StringComparison.Ordinal)) continue;
                    Add(found, path, kind ?? "tcp", host, port, IsTlsPort(port), confident: pathHint != null);
                }
                return;
            }
            // Portsuz tek sunucu adı: yalnızca alan adından tür ve port anlaşılıyorsa (ör. "KafkaBroker": "kafka01.sirket.local").
            if (tokens.Count == 1 && pathHint != null && ServerLike.IsMatch(tokens[0]) && !IsVersionLike(tokens[0]))
                Add(found, path, pathHint.Name, tokens[0], pathHint.Port, IsTlsPort(pathHint.Port), confident: false);
        }

        // "1.0.0", "2.1.3.4" gibi sürüm numaraları IP / sunucu adı sanılmasın.
        private static bool IsVersionLike(string s) => Regex.IsMatch(s, @"^\d+(\.\d+){1,2}$", RegexOptions.CultureInvariant);

        // ------------------------------------------------------------------ yardımcılar

        private static Kind? HintOf(string text)
        {
            var lower = text.ToLowerInvariant();
            foreach (var h in NameHints)
                if (lower.Contains(h.Key)) return h.Value;
            return null;
        }

        private static bool IsTlsPort(int port) => port == 443 || port == 8443 || port == 5671 || port == 6380 || port == 636 || port == 993 || port == 995 || port == 465;

        // Test edilemeyecek veya anlamsız adresler (yerel makine, örnek alan adları, şema adresleri).
        private static bool Ignored(string host)
        {
            var h = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (h == "localhost" || h == "0.0.0.0" || h == "::1" || h == "::" || h.StartsWith("127.", StringComparison.Ordinal)) return true;
            if (h.Contains("{") || h.Contains("*") || h.Contains("$")) return true;   // yer tutucu: {host}, *.domain
            foreach (var suffix in new[] { "example.com", "example.org", "example.net", "w3.org", "schemas.microsoft.com", "schemas.xmlsoap.org", "json-schema.org", "schema.org", "xmlsoap.org", "purl.org" })
                if (h == suffix || h.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool Add(List<DiscoveredEndpoint> found, string key, string kind, string host, int port, bool tls, bool confident)
        {
            host = host.Trim().Trim('[', ']').TrimEnd('.');
            if (host.Length == 0 || port < 1 || port > 65535 || Ignored(host)) return false;
            found.Add(new DiscoveredEndpoint { Key = key, Kind = kind, Host = host, Port = port, Tls = tls, Confidence = confident ? "high" : "low" });
            return true;
        }
    }
}
