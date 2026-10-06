using System;
using System.Reflection;

namespace ConnectivityProbe
{
    /// <summary>Kütüphanenin kendisi hakkında bilgi.</summary>
    public static class ProbeInfo
    {
        /// <summary>
        /// Yüklü ConnectivityProbe sürümü (NuGet paket sürümü, ör. "1.0.0"). identity ve discover yanıtlarında
        /// "probeVersion" olarak döner; böylece hangi uygulamada hangi sürümün çalıştığı uzaktan görülebilir.
        /// </summary>
        public static string Version { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var assembly = typeof(ProbeInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                // SDK, kaynak kontrol bilgisini "+<commit>" olarak ekleyebilir; paket sürümünü göstermek için atıyoruz.
                var plus = informational!.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }
}
