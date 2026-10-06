using System;
using System.Collections.Generic;

namespace ConnectivityProbe
{
    /// <summary>"a=1&amp;b=x%20y" biçimindeki sorgu metnini okur (OWIN ve kendi dinleyicisi için; diğer adaptörler hazırını kullanır).</summary>
    internal static class QueryString
    {
        /// <summary>Her anahtarın ilk değerini döner; anahtarlar büyük/küçük harf duyarsızdır.</summary>
        public static Dictionary<string, string> Parse(string? query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;

            foreach (var part in query!.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                var eq = part.IndexOf('=');
                var key = Decode(eq < 0 ? part : part.Substring(0, eq));
                var value = eq < 0 ? "" : Decode(part.Substring(eq + 1));
                if (!result.ContainsKey(key)) result[key] = value;
            }
            return result;
        }

        private static string Decode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch (UriFormatException) { return s; }
        }
    }
}
