using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MapRenderer.Core.Json
{
    /// <summary>Thrown only on malformed JSON — never on unknown keys (forward-compat).</summary>
    public sealed class JsonParseException : Exception
    {
        public int Position { get; }

        public JsonParseException(string message, int position)
            : base($"{message} (at offset {position})")
        {
            Position = position;
        }
    }

    /// <summary>
    /// A small, dependency-free recursive-descent JSON parser that produces a <see cref="JsonValue"/>
    /// DOM. It preserves every key, including ones the style model does not type. It is strict about
    /// syntax: malformed input or trailing non-whitespace after the root throws
    /// <see cref="JsonParseException"/>. Numbers parse with <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static class JsonParser
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonParseException("input is null", 0);
            var p = new Cursor(text);
            p.SkipWhitespace();
            JsonValue root = ParseValue(ref p);
            p.SkipWhitespace();
            if (!p.AtEnd)
                throw new JsonParseException("unexpected trailing characters after JSON value", p.Pos);
            return root;
        }

        private struct Cursor
        {
            public readonly string S;
            public int Pos;

            public Cursor(string s) { S = s; Pos = 0; }

            public bool AtEnd => Pos >= S.Length;
            public char Current => S[Pos];

            public void SkipWhitespace()
            {
                while (Pos < S.Length)
                {
                    char c = S[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++;
                    else break;
                }
            }
        }

        private static JsonValue ParseValue(ref Cursor p)
        {
            if (p.AtEnd) throw new JsonParseException("unexpected end of input", p.Pos);
            char c = p.Current;
            switch (c)
            {
                case '{': return ParseObject(ref p);
                case '[': return ParseArray(ref p);
                case '"': return JsonValue.OfString(ParseString(ref p));
                case 't': return ParseLiteral(ref p, "true", JsonValue.OfBool(true));
                case 'f': return ParseLiteral(ref p, "false", JsonValue.OfBool(false));
                case 'n': return ParseLiteral(ref p, "null", JsonValue.Null);
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber(ref p);
                    throw new JsonParseException($"unexpected character '{c}'", p.Pos);
            }
        }

        private static JsonValue ParseLiteral(ref Cursor p, string lit, JsonValue value)
        {
            if (p.Pos + lit.Length > p.S.Length || p.S.Substring(p.Pos, lit.Length) != lit)
                throw new JsonParseException($"invalid literal, expected '{lit}'", p.Pos);
            p.Pos += lit.Length;
            return value;
        }

        private static JsonValue ParseObject(ref Cursor p)
        {
            p.Pos++; // consume '{'
            var members = new Dictionary<string, JsonValue>();
            p.SkipWhitespace();
            if (!p.AtEnd && p.Current == '}') { p.Pos++; return JsonValue.OfObject(members); }

            while (true)
            {
                p.SkipWhitespace();
                if (p.AtEnd || p.Current != '"')
                    throw new JsonParseException("expected object key string", p.Pos);
                string key = ParseString(ref p);
                p.SkipWhitespace();
                if (p.AtEnd || p.Current != ':')
                    throw new JsonParseException("expected ':' after object key", p.Pos);
                p.Pos++; // consume ':'
                p.SkipWhitespace();
                JsonValue val = ParseValue(ref p);
                // Last-wins on duplicate keys (lenient; spec doesn't forbid duplicates).
                members[key] = val;

                p.SkipWhitespace();
                if (p.AtEnd) throw new JsonParseException("unterminated object", p.Pos);
                char c = p.Current;
                if (c == ',') { p.Pos++; continue; }
                if (c == '}') { p.Pos++; break; }
                throw new JsonParseException("expected ',' or '}' in object", p.Pos);
            }
            return JsonValue.OfObject(members);
        }

        private static JsonValue ParseArray(ref Cursor p)
        {
            p.Pos++; // consume '['
            var items = new List<JsonValue>();
            p.SkipWhitespace();
            if (!p.AtEnd && p.Current == ']') { p.Pos++; return JsonValue.OfArray(items); }

            while (true)
            {
                p.SkipWhitespace();
                JsonValue val = ParseValue(ref p);
                items.Add(val);

                p.SkipWhitespace();
                if (p.AtEnd) throw new JsonParseException("unterminated array", p.Pos);
                char c = p.Current;
                if (c == ',') { p.Pos++; continue; }
                if (c == ']') { p.Pos++; break; }
                throw new JsonParseException("expected ',' or ']' in array", p.Pos);
            }
            return JsonValue.OfArray(items);
        }

        private static string ParseString(ref Cursor p)
        {
            // Precondition: p.Current == '"'.
            p.Pos++; // consume opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (p.AtEnd) throw new JsonParseException("unterminated string", p.Pos);
                char c = p.S[p.Pos++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (p.AtEnd) throw new JsonParseException("unterminated escape", p.Pos);
                    char e = p.S[p.Pos++];
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
                            sb.Append(ParseUnicodeEscape(ref p));
                            break;
                        default:
                            throw new JsonParseException($"invalid string escape '\\{e}'", p.Pos - 1);
                    }
                }
                else if (c < 0x20)
                {
                    throw new JsonParseException("unescaped control character in string", p.Pos - 1);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static char ParseUnicodeEscape(ref Cursor p)
        {
            if (p.Pos + 4 > p.S.Length)
                throw new JsonParseException("incomplete \\u escape", p.Pos);
            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                char h = p.S[p.Pos++];
                int d;
                if (h >= '0' && h <= '9') d = h - '0';
                else if (h >= 'a' && h <= 'f') d = h - 'a' + 10;
                else if (h >= 'A' && h <= 'F') d = h - 'A' + 10;
                else throw new JsonParseException($"invalid hex digit '{h}' in \\u escape", p.Pos - 1);
                code = (code << 4) | d;
            }
            return (char)code;
        }

        private static JsonValue ParseNumber(ref Cursor p)
        {
            int start = p.Pos;
            if (!p.AtEnd && p.Current == '-') p.Pos++;
            while (!p.AtEnd && IsNumberChar(p.Current)) p.Pos++;

            string token = p.S.Substring(start, p.Pos - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new JsonParseException($"invalid number '{token}'", start);
            return JsonValue.OfNumber(value);
        }

        private static bool IsNumberChar(char c)
            => (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-';
    }
}
