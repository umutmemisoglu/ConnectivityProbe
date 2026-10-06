using System;
using System.Collections;
using System.Collections.Generic;

namespace ConnectivityProbe
{
    /// <summary>Ayarları farklı kaynaklardan "ConnectivityProbe:" ön eki atılmış anahtar/değer çiftleri olarak toplar.</summary>
    internal static class ProbeSettings
    {
        private const string EnvPrefix = ConnectivityProbeOptions.SectionName + "__";
        private const string KeyPrefix = ConnectivityProbeOptions.SectionName + ":";

        /// <summary>ConnectivityProbe__AccessKey -> "AccessKey", ConnectivityProbe__Info__cluster -> "Info:cluster".</summary>
        public static Dictionary<string, string?> FromEnvironment()
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var name = e.Key as string;
                if (name != null && name.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
                    result[name.Substring(EnvPrefix.Length).Replace("__", ":")] = e.Value as string;
            }
            return result;
        }

#if NETFRAMEWORK
        /// <summary>web.config / app.config appSettings: &lt;add key="ConnectivityProbe:AccessKey" value="..." /&gt;</summary>
        public static Dictionary<string, string?> FromAppSettings()
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var appSettings = System.Configuration.ConfigurationManager.AppSettings;
            foreach (string? key in appSettings.AllKeys)
            {
                if (key != null && key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                    result[key.Substring(KeyPrefix.Length)] = appSettings[key];
            }
            return result;
        }
#endif

        /// <summary>
        /// Ortam değişkenleri (ve .NET Framework'te appSettings). Aynı anahtar ikisinde de varsa ortam değişkeni kazanır;
        /// böylece aynı paket farklı ortamlarda (test/prod) yeniden derlenmeden yapılandırılabilir.
        /// </summary>
        public static Dictionary<string, string?> FromEnvironmentAndAppSettings()
        {
#if NETFRAMEWORK
            var result = FromAppSettings();
#else
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
#endif
            foreach (var kv in FromEnvironment()) result[kv.Key] = kv.Value;
            return result;
        }
    }
}
