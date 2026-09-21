using System;
using System.Collections.Generic;

namespace Machine.Core
{
    /// <summary>轻量 JSON 解析器（递归下降）。支持对象/数组/字符串/数字/布尔/null，兼容任意合法 JSON。</summary>
    public class JsonValue
    {
        public string Kind;          // object / array / string / number / bool / null
        public Dictionary<string, JsonValue> Obj;
        public List<JsonValue> Arr;
        public string Str;
        public double Num;
        public bool B;

        public bool IsNull { get { return Kind == "null"; } }

        public JsonValue Get(string key)
        {
            if (Kind != "object" || Obj == null) return null;
            JsonValue v;
            if (Obj.TryGetValue(key, out v)) return v;
            return null;
        }

        public string GetString(string key, string def)
        {
            var v = Get(key);
            if (v == null || v.Kind != "string") return def;
            return v.Str;
        }

        public double GetNumber(string key, double def)
        {
            var v = Get(key);
            if (v == null || v.Kind != "number") return def;
            return v.Num;
        }

        public bool GetBool(string key, bool def)
        {
            var v = Get(key);
            if (v == null || v.Kind != "bool") return def;
            return v.B;
        }

        public string[] GetStringArray(string key)
        {
            var v = Get(key);
            if (v == null || v.Kind != "array" || v.Arr == null) return new string[0];
            var list = new List<string>();
            foreach (var item in v.Arr) if (item != null && item.Kind == "string") list.Add(item.Str);
            return list.ToArray();
        }

        public static JsonValue Parse(string text)
        {
            if (text == null) return null;
            // 容忍 UTF-8/UTF-16 BOM：PowerShell ConvertTo-Json 等工具生成的清单文件常带 BOM，
            // 不剥掉会让根节点被误判为 number，进而报 "not a JSON object"。
            int pos = 0;
            while (pos < text.Length && (text[pos] == '\uFEFF' || text[pos] == '\uFFFE'
                                         || text[pos] == '\u200B' || text[pos] == '\u0000')) pos++;
            var v = ParseValue(text, ref pos);
            return v;
        }

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r'
                                    || s[p] == '\uFEFF' || s[p] == '\u00A0')) p++;
        }

        private static JsonValue ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) return null;
            char c = s[p];
            if (c == '{') return ParseObject(s, ref p);
            if (c == '[') return ParseArray(s, ref p);
            if (c == '"') { var v = new JsonValue(); v.Kind = "string"; v.Str = ParseString(s, ref p); return v; }
            if (c == 't' || c == 'f') { var v = new JsonValue(); v.Kind = "bool"; v.B = ParseBool(s, ref p); return v; }
            if (c == 'n') { ParseNull(s, ref p); var v = new JsonValue(); v.Kind = "null"; return v; }
            // number
            var vn = new JsonValue();
            vn.Kind = "number";
            vn.Num = ParseNumber(s, ref p);
            return vn;
        }

        private static JsonValue ParseObject(string s, ref int p)
        {
            p++; // {
            var v = new JsonValue();
            v.Kind = "object";
            v.Obj = new Dictionary<string, JsonValue>();
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return v; }
            while (p < s.Length)
            {
                SkipWs(s, ref p);
                string key = ParseString(s, ref p);
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ':') p++;
                var val = ParseValue(s, ref p);
                if (val != null) v.Obj[key] = val;
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == '}') { p++; break; }
            }
            return v;
        }

        private static JsonValue ParseArray(string s, ref int p)
        {
            p++; // [
            var v = new JsonValue();
            v.Kind = "array";
            v.Arr = new List<JsonValue>();
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return v; }
            while (p < s.Length)
            {
                var val = ParseValue(s, ref p);
                if (val != null) v.Arr.Add(val);
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == ']') { p++; break; }
            }
            return v;
        }

        private static string ParseString(string s, ref int p)
        {
            p++; // "
            var sb = new System.Text.StringBuilder();
            while (p < s.Length)
            {
                char c = s[p];
                if (c == '"') { p++; break; }
                if (c == '\\' && p + 1 < s.Length)
                {
                    char e = s[p + 1];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == '\\') sb.Append('\\');
                    else if (e == '"') sb.Append('"');
                    else if (e == '/') sb.Append('/');
                    else if (e == 'u' && p + 5 < s.Length)
                    {
                        string hex = s.Substring(p + 2, 4);
                        try { sb.Append((char)Convert.ToInt32(hex, 16)); } catch { }
                        p += 4;
                    }
                    else sb.Append(e);
                    p += 2;
                    continue;
                }
                sb.Append(c);
                p++;
            }
            return sb.ToString();
        }

        private static bool ParseBool(string s, ref int p)
        {
            if (s[p] == 't') { p += 4; return true; }
            p += 5;
            return false;
        }

        private static void ParseNull(string s, ref int p) { p += 4; }

        private static double ParseNumber(string s, ref int p)
        {
            int start = p;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '-' || s[p] == '+' || s[p] == '.' || s[p] == 'e' || s[p] == 'E')) p++;
            double d;
            double.TryParse(s.Substring(start, p - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
            return d;
        }
    }
}
