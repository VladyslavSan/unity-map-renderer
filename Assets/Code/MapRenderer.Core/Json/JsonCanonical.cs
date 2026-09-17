using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MapRenderer.Core.Json
{
    /// <summary>
    /// A deterministic, value-complete serialization of a <see cref="JsonValue"/>: the same DOM always writes
    /// the same string, and two DOMs write the same string only if they carry the same values.
    ///
    /// <para><b>Why this exists rather than <see cref="JsonValue.ToString"/>.</b> That method is a
    /// <i>diagnostic</i>, not a serialization — an object renders as <c>"{3 members}"</c> and an array as
    /// <c>"[2 items]"</c>, so two entirely different inline datasets stringify identically. Anything keyed on
    /// it (source identity, cache keys) would silently treat them as the same document.</para>
    ///
    /// <para><b>Why not reference identity either.</b> A restyle re-parses the document, so identity-keying
    /// would make every value-identical source compare as changed and rebuild its whole pipeline on every
    /// restyle. A canonical string is the only form that answers both directions: different data ⇒ different
    /// key, same data ⇒ same key.</para>
    ///
    /// <para><b>The determinism rules, and what each one is for.</b> Object members are written in ORDINAL
    /// key order, because JSON object member order is not semantic and two authorings of the same object must
    /// not differ. Array items keep their order, because array order IS semantic (a polygon ring is not its
    /// reversal). Strings are quoted and escaped and numbers are not, so <c>1</c> and <c>"1"</c> cannot
    /// collide. Numbers use the invariant round-trip format, so the string survives a locale with a comma
    /// decimal separator.</para>
    ///
    /// <para>It is <b>not</b> a general-purpose JSON writer: it makes no promise of being re-parseable into
    /// an equal DOM (a duplicate key in the source dictionary cannot occur, and non-finite numbers — which
    /// <see cref="JsonParser"/> cannot produce — are written as their invariant text rather than rejected).
    /// Its whole contract is comparability.</para>
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
        /// <param name="value">Must not be null — a null <c>Root</c> would otherwise collide with any other
        /// null-Root document sharing <paramref name="prefix"/>. A library-contract guard: production callers
        /// fail loud earlier, so this should never actually be what fires.</param>
        /// <param name="extra">A caller-built component folded in after <paramref name="value"/> — e.g. a
        /// layer-numbering signature the JSON content alone cannot see.</param>
        public static string Digest(string prefix, JsonValue value, string extra)
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
                    // ORDINAL, not culture-aware: a culture-sensitive sort can order the same two keys
                    // differently on two machines, which is exactly the non-determinism this type exists to
                    // remove.
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
