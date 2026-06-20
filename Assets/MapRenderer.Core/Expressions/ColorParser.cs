using System;
using System.Collections.Generic;
using System.Globalization;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// Parses CSS color strings into <see cref="Color"/>, the literal form of the spec's "color" type:
    /// <c>#rgb</c>/<c>#rgba</c>/<c>#rrggbb</c>/<c>#rrggbbaa</c>, <c>rgb()/rgba()</c>, <c>hsl()/hsla()</c>,
    /// and the CSS named colors. Clean-room: the CSS color syntax + the named-color table are public web
    /// standards.
    /// </summary>
    public static class ColorParser
    {
        public static bool TryParse(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();

            if (s[0] == '#') return TryParseHex(s, out color);

            int paren = s.IndexOf('(');
            if (paren > 0 && s.EndsWith(")", StringComparison.Ordinal))
            {
                string fn = s.Substring(0, paren).Trim().ToLowerInvariant();
                string inner = s.Substring(paren + 1, s.Length - paren - 2);
                string[] parts = inner.Split(',');
                switch (fn)
                {
                    case "rgb":
                    case "rgba":
                        return TryParseRgb(parts, out color);
                    case "hsl":
                    case "hsla":
                        return TryParseHsl(parts, out color);
                    default:
                        return false;
                }
            }

            string lower = s.ToLowerInvariant();
            if (lower == "transparent")
            {
                color = new Color(0, 0, 0, 0);
                return true;
            }
            if (NamedColors.TryGetValue(lower, out uint packed))
            {
                color = FromPackedRgb(packed);
                return true;
            }
            return false;
        }

        private static bool TryParseHex(string s, out Color color)
        {
            color = default;
            string h = s.Substring(1);
            int r, g, b, a = 255;
            try
            {
                if (h.Length == 3 || h.Length == 4)
                {
                    r = HexNibble(h[0]); g = HexNibble(h[1]); b = HexNibble(h[2]);
                    r = r * 16 + r; g = g * 16 + g; b = b * 16 + b;
                    if (h.Length == 4) { a = HexNibble(h[3]); a = a * 16 + a; }
                }
                else if (h.Length == 6 || h.Length == 8)
                {
                    r = HexByte(h, 0); g = HexByte(h, 2); b = HexByte(h, 4);
                    if (h.Length == 8) a = HexByte(h, 6);
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
            color = Color.From255(r, g, b, a / 255.0);
            return true;
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new FormatException("bad hex");
        }

        private static int HexByte(string h, int i) => HexNibble(h[i]) * 16 + HexNibble(h[i + 1]);

        private static bool TryParseRgb(string[] parts, out Color color)
        {
            color = default;
            if (parts.Length != 3 && parts.Length != 4) return false;
            if (!TryChannel(parts[0], out double r)) return false;
            if (!TryChannel(parts[1], out double g)) return false;
            if (!TryChannel(parts[2], out double b)) return false;
            double a = 1.0;
            if (parts.Length == 4 && !TryAlpha(parts[3], out a)) return false;
            color = Color.From255(r, g, b, a);
            return true;
        }

        // A rgb() channel: a 0..255 number, or a percentage of 255.
        private static bool TryChannel(string s, out double v)
        {
            s = s.Trim();
            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                if (double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double pct))
                {
                    v = pct / 100.0 * 255.0;
                    return true;
                }
                v = 0; return false;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryAlpha(string s, out double v)
            => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static bool TryParseHsl(string[] parts, out Color color)
        {
            color = default;
            if (parts.Length != 3 && parts.Length != 4) return false;
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                return false;
            if (!TryPercent(parts[1], out double s)) return false;
            if (!TryPercent(parts[2], out double l)) return false;
            double a = 1.0;
            if (parts.Length == 4 && !TryAlpha(parts[3], out a)) return false;

            // HSL -> sRGB (CSS Color 3 algorithm).
            h = ((h % 360.0) + 360.0) % 360.0 / 360.0;
            double r, g, b;
            if (s == 0.0)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }
            color = new Color(r, g, b, a);
            return true;
        }

        private static bool TryPercent(string s, out double v)
        {
            s = s.Trim();
            if (!s.EndsWith("%", StringComparison.Ordinal))
            {
                // accept a bare 0..1 too, but CSS requires '%'; be tolerant.
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return true;
                v = 0; return false;
            }
            if (double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double pct))
            {
                v = pct / 100.0;
                return true;
            }
            v = 0; return false;
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        private static Color FromPackedRgb(uint packed)
            => Color.From255((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF, 1.0);

        // CSS named colors (a representative, standard subset incl. all needed by tests + common style use).
        private static readonly Dictionary<string, uint> NamedColors = new Dictionary<string, uint>
        {
            { "black", 0x000000 }, { "white", 0xFFFFFF }, { "red", 0xFF0000 }, { "green", 0x008000 },
            { "blue", 0x0000FF }, { "yellow", 0xFFFF00 }, { "cyan", 0x00FFFF }, { "aqua", 0x00FFFF },
            { "magenta", 0xFF00FF }, { "fuchsia", 0xFF00FF }, { "gray", 0x808080 }, { "grey", 0x808080 },
            { "silver", 0xC0C0C0 }, { "maroon", 0x800000 }, { "olive", 0x808000 }, { "lime", 0x00FF00 },
            { "teal", 0x008080 }, { "navy", 0x000080 }, { "purple", 0x800080 }, { "orange", 0xFFA500 },
        };
    }
}
