// Json.cs - minimal JSON writer (.NET Framework 4.8 doesn't include System.Text.Json).
// Same format as JSON.stringify(obj, null, 2).
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CustomOutfits
{
    /// <summary>JSON object that keeps key order.</summary>
    public sealed class JObj : List<KeyValuePair<string, object>>
    {
        public JObj Set(string key, object value)
        {
            Add(new KeyValuePair<string, object>(key, value));
            return this;
        }
    }

    public static class Json
    {
        public static string Stringify(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);

        private static void Write(StringBuilder sb, object v, int level)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case JObj o:
                    if (o.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    for (int k = 0; k < o.Count; k++)
                    {
                        Indent(sb, level + 1);
                        WriteString(sb, o[k].Key);
                        sb.Append(": ");
                        Write(sb, o[k].Value, level + 1);
                        if (k < o.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    Indent(sb, level);
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    var items = new List<object>();
                    foreach (var x in list) items.Add(x);
                    if (items.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (int k = 0; k < items.Count; k++)
                    {
                        Indent(sb, level + 1);
                        Write(sb, items[k], level + 1);
                        if (k < items.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    Indent(sb, level);
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, v.ToString());
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
