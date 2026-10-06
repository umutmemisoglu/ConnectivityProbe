using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ConnectivityProbe
{
    /// <summary>
    /// Kütüphanenin kendi küçük JSON yazıcı/okuyucusu. Dış bağımlılık (System.Text.Json, Newtonsoft) kullanmıyoruz ki
    /// .NET Framework uygulamalarında ek DLL ve binding redirect gerekmesin; DLL'i bin'e koymak yeterli olsun.
    /// Yalnızca kütüphanenin kendi modellerini yazmak ve identity yanıtını okumak için yeterli kadarını destekler.
    /// </summary>
    internal static class Json
    {
        // ------------------------------------------------------------------ yazma

        /// <summary>Nesneyi camelCase alan adlarıyla, girintili JSON'a çevirir.</summary>
        public static string Serialize(object? value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object? value, int indent)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime dt: WriteString(sb, dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case int or long or short or byte or uint or ulong or ushort or sbyte:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case IDictionary dict:
                    WriteObject(sb, dict.Keys.Cast<object>().Select(k =>
                        new KeyValuePair<string, object?>(Convert.ToString(k, CultureInfo.InvariantCulture) ?? "", dict[k])), indent, camelCase: false);
                    return;
                case IEnumerable list: WriteArray(sb, list, indent); return;
                default:
                    var props = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);
                    WriteObject(sb, props.Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(value, null))), indent, camelCase: true);
                    return;
            }
        }

        // (Tuple yerine KeyValuePair: ValueTuple .NET Framework 4.6.2'de yerleşik değil, ek paket gerektirirdi.)
        private static void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> members, int indent, bool camelCase)
        {
            var items = members.ToList();
            if (items.Count == 0) { sb.Append("{}"); return; }

            sb.Append('{');
            for (int i = 0; i < items.Count; i++)
            {
                NewLine(sb, indent + 1);
                WriteString(sb, camelCase ? CamelCase(items[i].Key) : items[i].Key);
                sb.Append(": ");
                Write(sb, items[i].Value, indent + 1);
                if (i < items.Count - 1) sb.Append(',');
            }
            NewLine(sb, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, int indent)
        {
            var items = list.Cast<object?>().ToList();
            if (items.Count == 0) { sb.Append("[]"); return; }

            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                NewLine(sb, indent + 1);
                Write(sb, items[i], indent + 1);
                if (i < items.Count - 1) sb.Append(',');
            }
            NewLine(sb, indent);
            sb.Append(']');
        }

        private static void NewLine(StringBuilder sb, int indent) => sb.Append('\n').Append(' ', indent * 2);

        private static string CamelCase(string name) =>
            name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    // HTML içine gömülse bile güvenli olsun diye <, >, & kaçırılır.
                    case '<': case '>': case '&': case '\'':
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------ okuma

        /// <summary>
        /// JSON metnini okur: nesneler büyük/küçük harf duyarsız Dictionary, diziler List, sayılar double,
        /// diğerleri string/bool/null olarak döner. Geçersiz JSON'da FormatException fırlatır.
        /// </summary>
        public static object? Parse(string text)
        {
            int pos = 0;
            var value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length) throw new FormatException("Unexpected data after JSON value at " + pos);
            return value;
        }

        private static object? ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of JSON");

            switch (s[pos])
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static Dictionary<string, object?> ParseObject(string s, ref int pos)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            pos++; // {
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                var key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("Expected ':' at " + pos);
                pos++;
                result[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == '}') { pos++; return result; }
                throw new FormatException("Expected ',' or '}' at " + pos);
            }
        }

        private static List<object?> ParseArray(string s, ref int pos)
        {
            var result = new List<object?>();
            pos++; // [
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == ']') { pos++; return result; }
                throw new FormatException("Expected ',' or ']' at " + pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"') throw new FormatException("Expected string at " + pos);
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) break;
                char e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("Bad escape at " + pos);
                }
            }
            throw new FormatException("Unterminated string");
        }

        private static double ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0) pos++;
            if (start == pos || !double.TryParse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new FormatException("Bad number at " + start);
            return d;
        }

        private static void Expect(string s, ref int pos, string word)
        {
            if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0) throw new FormatException("Unexpected token at " + pos);
            pos += word.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        // ------------------------------------------------------------------ okunan değerden alan çekme yardımcıları

        public static string? GetString(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        public static long GetLong(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is double d ? (long)d : 0;

        public static double? GetDouble(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is double d ? d : (double?)null;

        public static bool GetBool(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is bool b && b;

        /// <summary>Nesne dizisini döner (dizi değilse veya alan yoksa boş liste).</summary>
        public static List<IDictionary<string, object?>> GetObjectList(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is List<object?> list
                ? list.OfType<IDictionary<string, object?>>().ToList()
                : new List<IDictionary<string, object?>>();

        public static IDictionary<string, object?>? GetObject(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) ? v as IDictionary<string, object?> : null;

        public static List<string> GetStringList(IDictionary<string, object?>? o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is List<object?> list
                ? list.Where(x => x != null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToList()
                : new List<string>();

        public static Dictionary<string, string> GetStringMap(IDictionary<string, object?>? o, string key)
        {
            var result = new Dictionary<string, string>();
            if (GetObject(o, key) is { } map)
                foreach (var kv in map)
                    if (kv.Value != null) result[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture)!;
            return result;
        }
    }
}
