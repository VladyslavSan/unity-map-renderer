using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MapRenderer.Core.Json
{
    /// <summary>
    /// A deterministic, value-complete serialization of a <see cref="JsonValue"/>: the same DOM always writes
    /// the same string, and two DOMs write the same string only if they carry the same values. Its only
    /// contract is comparability; it is not a re-parseable JSON writer. Non-obvious why:
    /// <see cref="JsonValue.ToString"/> is a diagnostic (<c>"{3 members}"</c>), and reference identity changes
    /// on every restyle re-parse.
    /// Object members go in ordinal key order, arrays keep their order, only strings are quoted (so <c>1</c> and
    /// <c>"1"</c> differ), and numbers use the invariant round-trip format.
    /// </summary>
    public static class JsonCanonical
    {
        /// <summary>The canonical text of <paramref name="value"/>. A null reference and a
        /// <see cref="JsonKind.Null"/> node both write <c>null</c> — the DOM does not distinguish "absent"
        /// from "present and null" for any caller of this, and a caller that must ought to test for the null
        /// reference before asking.</summary>
        public static string Write(JsonValue value)
        {
            var sb = new StringBuilder();
            Write(value, sb);
            return sb.ToString();
        }

        /// <summary>A short, stable 64-bit hex digest (FNV-1a) of <paramref name="prefix"/>, the canonical
        /// text of <paramref name="value"/>, and <paramref name="extra"/> — a cache key must not re-hash the
        /// whole canonical text on every lookup; computing this digest is the one-time control-plane cost.</summary>
        /// <param name="value">Must not be null: a null <c>Root</c> would collide with any other null-Root
        /// document sharing <paramref name="prefix"/>. Production callers fail earlier.</param>
        /// <param name="extra">A caller-built component folded in after <paramref name="value"/> — e.g. a
        /// layer-numbering signature the JSON content alone cannot see.</param>
        public static string CacheKey(string prefix, JsonValue value, string extra)
        {
            if (value == null) throw new System.ArgumentNullException(nameof(value),
                "a null Root would collide with any other null-Root document sharing this prefix");
            ulong hash = 14695981039346656037UL; // FNV-1a 64-bit offset basis
            Fold(prefix ?? string.Empty, ref hash);
            Fold("\0", ref hash);
            Fold(Write(value), ref hash);
            Fold("\0", ref hash);
            Fold(extra ?? string.Empty, ref hash);
            return hash.ToString("x16");
        }

        private static void Fold(string s, ref ulong hash)
        {
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= 1099511628211UL; // FNV-1a 64-bit prime
            }
        }

        private static void Write(JsonValue value, StringBuilder sb)
        {
            if (value == null) { sb.Append("null"); return; }

            switch (value.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;

                case JsonKind.Bool:
                    sb.Append(value.AsBool() ? "true" : "false");
                    break;

                case JsonKind.Number:
                    // "R" round-trips the double exactly; InvariantCulture keeps '.' as the separator under
                    // every locale, so the key a machine computes is the key every other machine computes.
                    sb.Append(value.AsDouble().ToString("R", CultureInfo.InvariantCulture));
                    break;

                case JsonKind.String:
                    WriteString(value.AsString(string.Empty), sb);
                    break;

                case JsonKind.Array:
                    sb.Append('[');
                    IReadOnlyList<JsonValue> items = value.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(items[i], sb);
                    }
                    sb.Append(']');
                    break;

                case JsonKind.Object:
                    sb.Append('{');
                    var keys = new List<string>(value.Members.Keys);
                    // Ordinal, not culture-aware: a culture-sensitive sort can order the same two keys
                    // differently on two machines.
                    keys.Sort(System.StringComparer.Ordinal);
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(keys[i], sb);
                        sb.Append(':');
                        Write(value.Members[keys[i]], sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(string text, StringBuilder sb)
        {
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        // Control characters must be escaped or the delimiter set stops being unambiguous.
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else          sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
