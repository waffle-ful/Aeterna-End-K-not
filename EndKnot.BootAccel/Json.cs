using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EndKnot.BootAccel
{
    // Minimal hand-rolled JSON. Deliberately not System.Text.Json: first use of STJ costs
    // tens of ms of JIT at boot, which is the very thing this patcher exists to avoid.
    // Numbers are kept as raw token strings so 18-digit tick values survive (double would not).
    internal static class Json
    {
        // ---- writing ----

        internal static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        internal static void Prop(StringBuilder sb, string name, string value)
        {
            Str(sb, name); sb.Append(':'); Str(sb, value);
        }

        internal static void Prop(StringBuilder sb, string name, long value)
        {
            Str(sb, name); sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        internal static void Prop(StringBuilder sb, string name, bool value)
        {
            Str(sb, name); sb.Append(':').Append(value ? "true" : "false");
        }

        // ---- reading ----
        // Returns Dictionary<string,object> | List<object> | string | NumberToken | bool | null.

        internal sealed class NumberToken
        {
            internal readonly string Raw;
            internal NumberToken(string raw) { Raw = raw; }
            internal long AsLong() { long v; return long.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0L; }
        }

        internal static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            return v;
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("eof");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("bad token at " + start);
            return new NumberToken(s.Substring(start, i - start));
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string k = ParseString(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected :");
                i++;
                d[k] = ParseValue(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("eof in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected , or }");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                Ws(s, ref i);
                if (i >= s.Length) throw new FormatException("eof in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected , or ]");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("expected string");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        if (i + 4 > s.Length) throw new FormatException("bad \\u");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape");
                }
            }
            throw new FormatException("eof in string");
        }

        // ---- typed helpers ----

        internal static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        internal static List<object> Arr(object o) { return o as List<object>; }

        internal static string GetStr(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }

        internal static long GetLong(Dictionary<string, object> d, string k, long def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v)) return def;
            var n = v as NumberToken;
            return n != null ? n.AsLong() : def;
        }

        internal static bool GetBool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v)) return def;
            return v is bool ? (bool)v : def;
        }
    }
}
