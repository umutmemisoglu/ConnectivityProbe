using System;
using System.Globalization;
using System.Net;

namespace ConnectivityProbe
{
    /// <summary>
    /// "host" (ve isteğe bağlı "port") metnini bağlanılabilir bir host + porta çevirir. Probe uçları ve merkezi Monitor
    /// aynı kuralı kullansın diye herkese açıktır.
    /// </summary>
    public static class ProbeTarget
    {
        /// <summary>
        /// Kabul edilen biçimler: <c>sql01</c>, <c>sql01:1433</c>, <c>10.0.0.5</c>, <c>::1</c>, <c>[::1]:5078</c>,
        /// <c>https://orders.example.com/</c>, <c>https://x.com:8443/yol</c>. URL'de yol kısmı yok sayılır; Türkçe karakterli
        /// alan adları DNS'in anladığı biçime (punycode) çevrilir. <paramref name="portInput"/> doluysa host içindeki portu ezer;
        /// boşsa host'taki port, o da yoksa URL şemasının varsayılanı (https 443, http 80) kullanılır.
        /// </summary>
        /// <param name="hostInput">Kullanıcının yazdığı host / URL.</param>
        /// <param name="portInput">Ayrıca verilen port (boş olabilir).</param>
        /// <param name="host">Bağlanılacak host (IPv6 köşeli parantezsiz).</param>
        /// <param name="port">Bağlanılacak port.</param>
        /// <param name="scheme">Host bir URL ise şeması (http/https), değilse null.</param>
        /// <param name="error">Geçersizse nedeni.</param>
        public static bool TryParse(
            string? hostInput, string? portInput, out string host, out int port, out string? scheme, out string? error)
        {
            host = "";
            port = 0;
            scheme = null;
            error = null;

            var input = (hostInput ?? "").Trim();
            if (input.Length == 0)
            {
                error = "host is required";
                return false;
            }

            if (input.Contains("://"))
            {
                // step 1a: Tam URL: https://orders.example.com/ -> host + şema + port (açık port yoksa şemanın varsayılanı).
                if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.IdnHost.Length == 0
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    error = "host must be a name, an IP, or an http/https URL";
                    return false;
                }

                host = uri.IdnHost; // IPv6 köşeli parantezsiz, Türkçe karakterli adlar punycode
                port = uri.Port;
                scheme = uri.Scheme;
            }
            else
            {
                input = input.TrimEnd('/');

                if (!input.StartsWith("[", StringComparison.Ordinal) && IPAddress.TryParse(input, out var ip))
                {
                    // step 1b: Çıplak IP (IPv4 veya köşeli parantezsiz IPv6): port ayrıca verilmeli.
                    host = ip.ToString();
                }
                else if (Uri.TryCreate("tcp://" + input, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0
                         && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.UserInfo.Length == 0)
                {
                    // step 1c: sql01, sql01:1433, [::1]:5078 gibi biçimler.
                    host = uri.IdnHost;
                    if (uri.Port > 0) port = uri.Port;
                }
                else
                {
                    error = "host must be a name, an IP, or an http/https URL";
                    return false;
                }
            }

            // step 2: Ayrıca port verildiyse o geçerlidir (host içindeki portu ezer).
            var portText = (portInput ?? "").Trim();
            if (portText.Length > 0 && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                error = "port must be a number between 1 and 65535";
                return false;
            }

            // step 3: Sonuçta geçerli bir port olmalı.
            if (port < 1 || port > 65535)
            {
                error = "port (1-65535) is required unless host contains it (sql01:1433) or is a full URL";
                return false;
            }

            return true;
        }
    }
}
