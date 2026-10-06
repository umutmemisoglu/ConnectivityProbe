using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ConnectivityProbe
{
    public sealed class RequestInfo
    {
        public string? RemoteIp { get; set; }
        public string? Scheme { get; set; }
        public string? Host { get; set; }
        /// <summary>Set by proxies / load balancers; shows the path the request took.</summary>
        public string? ForwardedFor { get; set; }
        public string? ForwardedHost { get; set; }
    }

    /// <summary>What one running instance (pod / IIS server) reports about itself.</summary>
    public sealed class InstanceIdentity
    {
        /// <summary>Stable per machine/pod (derived from its name), different between instances.</summary>
        public string InstanceId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public int ProcessId { get; set; }
        /// <summary>Changes when the process restarts, while InstanceId stays the same.</summary>
        public DateTime StartedAtUtc { get; set; }
        public long UptimeSeconds { get; set; }
        public List<string> LocalAddresses { get; set; } = new List<string>();
        public string Os { get; set; } = "";
        public string Framework { get; set; } = "";
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Info { get; set; } = new Dictionary<string, string>();
        public RequestInfo Request { get; set; } = new RequestInfo();
        /// <summary>Bu instance'taki ConnectivityProbe sürümü.</summary>
        public string? ProbeVersion { get; set; }

        /// <summary>Json.Parse ile okunmuş bir identity yanıtını nesneye çevirir; instanceId yoksa null döner.</summary>
        internal static InstanceIdentity? FromJson(object? parsed)
        {
            if (!(parsed is IDictionary<string, object?> o)) return null;
            var id = Json.GetString(o, "instanceId");
            if (string.IsNullOrEmpty(id)) return null;

            var request = Json.GetObject(o, "request");
            DateTime.TryParse(Json.GetString(o, "startedAtUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var started);

            return new InstanceIdentity
            {
                InstanceId = id!,
                MachineName = Json.GetString(o, "machineName") ?? "",
                ProcessId = (int)Json.GetLong(o, "processId"),
                StartedAtUtc = started,
                UptimeSeconds = Json.GetLong(o, "uptimeSeconds"),
                LocalAddresses = Json.GetStringList(o, "localAddresses"),
                Os = Json.GetString(o, "os") ?? "",
                Framework = Json.GetString(o, "framework") ?? "",
                Environment = Json.GetStringMap(o, "environment"),
                Info = Json.GetStringMap(o, "info"),
                ProbeVersion = Json.GetString(o, "probeVersion"),
                Request = new RequestInfo
                {
                    RemoteIp = Json.GetString(request, "remoteIp"),
                    Scheme = Json.GetString(request, "scheme"),
                    Host = Json.GetString(request, "host"),
                    ForwardedFor = Json.GetString(request, "forwardedFor"),
                    ForwardedHost = Json.GetString(request, "forwardedHost")
                }
            };
        }
    }

    internal static class InstanceIdentityBuilder
    {
        // Süreç bilgileri bir kez okunur (her istekte Process nesnesi açıp kapatmamak için). StartedAtUtc sürecin gerçek
        // başlangıç zamanıdır; restart olunca değişir, uptime hesabında kullanılır.
        private static readonly int ProcessId;
        private static readonly DateTime StartedAtUtc;
        private static readonly string Os;
        private static readonly string Framework;

        static InstanceIdentityBuilder()
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                ProcessId = process.Id;
                try { StartedAtUtc = process.StartTime.ToUniversalTime(); }
                catch (Exception) { StartedAtUtc = DateTime.UtcNow; } // bazı platformlarda başlangıç zamanı okunamayabilir
            }

#if NETFRAMEWORK
            // RuntimeInformation .NET Framework 4.6.2'de yok; ek paket gerektirmemek için framework'ün kendi bilgisini kullanıyoruz.
            Os = System.Environment.OSVersion.VersionString;
            Framework = ".NET Framework (CLR " + System.Environment.Version + ")";
#else
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#endif
        }

        /// <summary>
        /// Bu instance'ın kimliği (makine adı + seed'in hash'i); identity ve probe yanıtlarında aynı değer kullanılır.
        /// Kubernetes'te POD_NAME (Downward API) verilmişse ve makine adından farklıysa o da eklenir: hostNetwork: true ile
        /// çalışan pod'lar node'un adını taşır, aynı node'daki iki pod aksi halde aynı kimliği alırdı. Normal pod'larda
        /// POD_NAME makine adıyla aynı olduğu için kimlik değişmez.
        /// </summary>
        public static string GetInstanceId(ConnectivityProbeOptions options) =>
            ComputeId(IdSeed(System.Environment.MachineName, System.Environment.GetEnvironmentVariable("POD_NAME"), options.InstanceIdSeed));

        /// <summary>Kimliğin hash'lenen girdisi (test edilebilsin diye ayrı).</summary>
        internal static string IdSeed(string machineName, string? podName, string seed) =>
            string.IsNullOrEmpty(podName) || string.Equals(podName, machineName, StringComparison.OrdinalIgnoreCase)
                ? machineName + "|" + seed
                : machineName + "|" + podName + "|" + seed;

        /// <param name="request">Gelen istek; load balancer / proxy bilgilerini okumak için kullanılır.</param>
        /// <param name="options">Hangi ortam değişkenlerinin ve özel bilgilerin ekleneceğini belirler.</param>
        public static InstanceIdentity Build(ProbeRequest request, ConnectivityProbeOptions options)
        {
            // step 1: Temel kimliği oluşturuyoruz. instanceId makine/pod adından türetildiği için aynı pod'da hep aynı,
            //         farklı pod'larda farklı olur (Kubernetes'te pod adı benzersizdir).
            var identity = new InstanceIdentity
            {
                InstanceId = GetInstanceId(options),
                MachineName = System.Environment.MachineName,
                ProcessId = ProcessId,
                StartedAtUtc = StartedAtUtc,
                UptimeSeconds = (long)(DateTime.UtcNow - StartedAtUtc).TotalSeconds,
                LocalAddresses = GetLocalAddresses(),
                Os = Os,
                Framework = Framework,
                ProbeVersion = ProbeInfo.Version
            };

            // step 2: Seçili ortam değişkenlerini (pod adı, namespace, node, cluster, IIS app pool...) varsa ekliyoruz.
            //         Yalnızca listedekiler okunur; tüm ortam değişkenlerini dışarı vermiyoruz (gizli bilgi sızmasın diye).
            foreach (var name in options.IdentityEnvironmentVariables)
            {
                var value = System.Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) identity.Environment[name] = value!;
            }

            // step 3: Yapılandırmada tanımlanan sabit bilgileri (cluster adı, bölge, sürüm...) ekliyoruz.
            foreach (var kv in options.Info) identity.Info[kv.Key] = kv.Value;

            // step 4: İsteğin bize nasıl geldiğini kaydediyoruz. Load balancer / IIS / proxy arkasındaysak
            //         X-Forwarded-* başlıkları isteğin izlediği yolu gösterir.
            identity.Request = new RequestInfo
            {
                RemoteIp = request.RemoteIp,
                Scheme = request.Scheme,
                Host = request.Host,
                ForwardedFor = NullIfEmpty(request.Header("X-Forwarded-For")),
                ForwardedHost = NullIfEmpty(request.Header("X-Forwarded-Host"))
            };

            return identity;
        }

        // Verilen metnin SHA256 özetinin ilk 6 baytını 12 karakterlik hex olarak döner (aynı girdi -> hep aynı çıktı).
        internal static string ComputeId(string seed)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                return string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
            }
        }

        // Makinenin (pod'un) aktif ağ arayüzlerindeki IP adreslerini listeler; loopback ve link-local IPv6 hariç.
        private static List<string> GetLocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
                    .Select(a => a.ToString())
                    .Distinct()
                    .ToList();
            }
            catch (Exception)
            {
                // Bazı ortamlarda ağ arayüzleri okunamayabilir; kimlik yanıtı bu yüzden bozulmasın, boş liste dönüyoruz.
                return new List<string>();
            }
        }

        // Boş metni null'a çevirir; JSON'da boş string yerine null görünmesi için.
        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
