using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ConnectivityProbe
{
    /// <summary>Bir pod'un (instance'ın) Monitor'e bildirdiği kimlik ve sürüm bilgisi.</summary>
    public sealed class PodIdentity
    {
        /// <summary>Pod başına sabit kısa kimlik (makine / pod adından türetilir; pod yeniden başlasa da aynı kalır).</summary>
        public string InstanceId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public int ProcessId { get; set; }
        /// <summary>Sürecin başlangıç zamanı (restart olunca değişir).</summary>
        public DateTime StartedAtUtc { get; set; }
        public List<string> LocalAddresses { get; set; } = new List<string>();
        public string Os { get; set; } = "";
        public string Framework { get; set; } = "";
        /// <summary>ConnectivityProbe kütüphanesinin sürümü.</summary>
        public string ProbeVersion { get; set; } = "";

        /// <summary>Uygulamanın (ConnectivityProbe'u kullanan projenin) adı.</summary>
        public string AppName { get; set; } = "";
        /// <summary>Uygulamanın sürümü: AssemblyInformationalVersion ile AssemblyVersion'dan büyük olanı.</summary>
        public string AppVersion { get; set; } = "";
        /// <summary>
        /// Build kimliği: derleyicinin her derlemede assembly'ye yazdığı kimliğin (MVID) kısa hali. Kod değişince değişir;
        /// sürüm numarası artırılmamış olsa bile farklı build'leri ayırt eder.
        /// </summary>
        public string BuildId { get; set; } = "";
        /// <summary>Uygulama assembly dosyasının oluşturulma zamanı (biliniyorsa).</summary>
        public DateTime? BuildDateUtc { get; set; }

        /// <summary>
        /// Kubernetes cluster kimliği: cluster'ın kök sertifikasının (service account ca.crt) parmak izi. Aynı cluster'daki tüm
        /// pod'larda aynı, farklı cluster'larda farklıdır. Kubernetes dışında (ör. IIS sunucusu) null; Monitor o zaman pod'ları
        /// bildirimin geldiği ağ adresine göre gruplar.
        /// </summary>
        public string? ClusterId { get; set; }
        /// <summary>Kubernetes namespace'i (service account bilgisinden, ayar gerekmeden).</summary>
        public string? Namespace { get; set; }
        /// <summary>Seçili ortam değişkenleri (pod adı, node, ortam adı, IIS app pool...). Başka değişken okunmaz.</summary>
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Pod kimliğini üretir. Değişmeyen kısımlar bir kez hesaplanır.</summary>
    internal static class PodIdentityBuilder
    {
        /// <summary>Kubernetes'in her pod'a koyduğu service account klasörü (test için ortam değişkeniyle değiştirilebilir).</summary>
        internal static string ServiceAccountDirectory =>
            System.Environment.GetEnvironmentVariable("CONNECTIVITYPROBE_SERVICEACCOUNT_DIR")
            ?? "/var/run/secrets/kubernetes.io/serviceaccount";

        /// <summary>Kimliğe kopyalanan ortam değişkenleri. Gizli bilgi sızmasın diye yalnızca bunlar okunur.</summary>
        private static readonly string[] EnvironmentVariables =
        {
            "POD_NAME", "POD_NAMESPACE", "POD_IP", "NODE_NAME", "HOSTNAME", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "APP_POOL_ID"
        };

        private static readonly DateTime ProcessStartedAtUtc;
        private static readonly int ProcessId;

        static PodIdentityBuilder()
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                ProcessId = process.Id;
                try { ProcessStartedAtUtc = process.StartTime.ToUniversalTime(); }
                catch (Exception) { ProcessStartedAtUtc = DateTime.UtcNow; } // bazı platformlarda okunamayabilir
            }
        }

        /// <param name="app">ConnectivityProbe'u kullanan projenin assembly'si (sürüm ve build bilgisi buradan okunur).</param>
        /// <param name="appName">Uygulama adı; null ise assembly adı.</param>
        public static PodIdentity Build(Assembly? app, string? appName)
        {
            var podName = System.Environment.GetEnvironmentVariable("POD_NAME");
            var identity = new PodIdentity
            {
                InstanceId = ComputeId(IdSeed(System.Environment.MachineName, podName)),
                MachineName = System.Environment.MachineName,
                ProcessId = ProcessId,
                StartedAtUtc = ProcessStartedAtUtc,
                LocalAddresses = GetLocalAddresses(),
#if NETFRAMEWORK
                Os = System.Environment.OSVersion.VersionString,
                Framework = ".NET Framework (CLR " + System.Environment.Version + ")",
#else
                Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
#endif
                ProbeVersion = ProbeInfo.Version,
                AppName = string.IsNullOrWhiteSpace(appName) ? app?.GetName().Name ?? "unknown" : appName!.Trim(),
                ClusterId = ReadClusterId(ServiceAccountDirectory),
                Namespace = ReadText(Path.Combine(ServiceAccountDirectory, "namespace")),
            };

            if (app != null)
            {
                identity.AppVersion = PickVersion(
                    app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, app.GetName().Version);
                identity.BuildId = BuildIdOf(app);
                identity.BuildDateUtc = BuildDateOf(app);
            }

            foreach (var name in EnvironmentVariables)
            {
                var value = System.Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) identity.Environment[name] = value!;
            }
            return identity;
        }

        // ------------------------------------------------------------------ kimlik

        /// <summary>
        /// Kimliğin hash'lenen girdisi. Kubernetes'te POD_NAME (Downward API) verilmişse ve makine adından farklıysa o da eklenir:
        /// hostNetwork: true ile çalışan pod'lar node'un adını taşır, aynı node'daki iki pod aksi halde aynı kimliği alırdı.
        /// </summary>
        internal static string IdSeed(string machineName, string? podName) =>
            string.IsNullOrEmpty(podName) || string.Equals(podName, machineName, StringComparison.OrdinalIgnoreCase)
                ? machineName + "|"
                : machineName + "|" + podName + "|";

        /// <summary>Metnin SHA256 özetinin ilk 6 baytı, 12 karakter hex (aynı girdi -> hep aynı çıktı).</summary>
        internal static string ComputeId(string seed) => Hex(Sha256(Encoding.UTF8.GetBytes(seed)), 6);

        // ------------------------------------------------------------------ sürüm

        private static readonly Regex NumericPrefix = new Regex(@"^\d+(\.\d+){0,3}", RegexOptions.CultureInvariant);

        /// <summary>
        /// AssemblyInformationalVersion ile AssemblyVersion'dan büyük olanı döner. Karşılaştırma sayısal kısımla yapılır
        /// ("1.4.0-beta+abc" -> 1.4.0; eksik kısımlar 0 sayılır, 1.4.0 = 1.4.0.0). Eşitse daha açıklayıcı olan informational
        /// sürüm seçilir; "+commit" eki gösterilmez (build kimliği zaten ayrıca bildirilir).
        /// </summary>
        internal static string PickVersion(string? informational, Version? assembly)
        {
            var info = informational?.Trim();
            if (!string.IsNullOrEmpty(info))
            {
                var plus = info!.IndexOf('+');
                if (plus > 0) info = info.Substring(0, plus);
            }

            var infoNumber = ParseNumeric(info);
            if (infoNumber != null && (assembly == null || Normalize(infoNumber).CompareTo(Normalize(assembly)) >= 0)) return info!;
            if (assembly != null) return assembly.ToString();
            return string.IsNullOrEmpty(info) ? "unknown" : info!;
        }

        private static Version? ParseNumeric(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = NumericPrefix.Match(text);
            if (!m.Success) return null;
            var value = m.Value.IndexOf('.') >= 0 ? m.Value : m.Value + ".0"; // Version "1" kabul etmez
            return Version.TryParse(value, out var v) ? v : null;
        }

        private static Version Normalize(Version v) =>
            new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        /// <summary>Derleyicinin her derlemede ürettiği modül kimliğinin (MVID) ilk 8 karakteri.</summary>
        internal static string BuildIdOf(Assembly app)
        {
            try { return app.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8); }
            catch (Exception) { return ""; }
        }

        private static DateTime? BuildDateOf(Assembly app)
        {
            try
            {
                var location = app.Location;
                return string.IsNullOrEmpty(location) || !File.Exists(location) ? (DateTime?)null : File.GetLastWriteTimeUtc(location);
            }
            catch (Exception)
            {
                return null; // tek dosya yayınlarda veya kısıtlı ortamlarda konum okunamayabilir
            }
        }

        // ------------------------------------------------------------------ cluster

        /// <summary>
        /// Cluster kimliği: service account klasöründeki ca.crt'nin SHA256 parmak izi (12 karakter). Kubernetes her pod'a bu dosyayı
        /// koyar; aynı cluster'daki tüm pod'larda aynıdır. Dosya yoksa (Kubernetes dışı veya automountServiceAccountToken: false) null.
        /// </summary>
        internal static string? ReadClusterId(string serviceAccountDirectory)
        {
            try
            {
                var path = Path.Combine(serviceAccountDirectory, "ca.crt");
                if (!File.Exists(path)) return null;
                return Hex(Sha256(File.ReadAllBytes(path)), 6);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ yardımcılar

        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        private static string Hex(byte[] bytes, int count) =>
            string.Concat(bytes.Take(count).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

        // Pod'un aktif ağ arayüzlerindeki IP adresleri; loopback ve link-local IPv6 hariç.
        private static List<string> GetLocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
                    .Select(a => a.ToString())
                    .Distinct()
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
    }
}
