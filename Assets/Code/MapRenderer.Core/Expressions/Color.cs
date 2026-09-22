using System;
using System.Globalization;
using Unity.Mathematics;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// A color in the expression type system: RGBA components in the sRGB color space, each a float in
    /// [0,1] (alpha is opacity, linear in [0,1]). This mirrors the Style Spec "color" type, whose literal
    /// forms are CSS color strings (<c>#rgb</c>/<c>#rrggbb</c>, <c>rgb()/rgba()</c>, <c>hsl()/hsla()</c>,
    /// and the CSS named colors).
    ///
    /// Clean-room color-space math. Constants are taken from citable standards, NOT from MapLibre source:
    ///  - sRGB transfer function (gamma): IEC 61966-2-1, threshold 0.04045 / 12.92 / ((x+0.055)/1.055)^2.4.
    ///  - sRGB → CIE XYZ matrix and D65 reference white (Xn,Yn,Zn) = (0.95047, 1.0, 1.08883): sRGB spec.
    ///  - CIELAB and CIELCh(ab): CIE 15. LCh is LAB in polar form; "HCL" here is LCh(ab) with H in degrees.
    /// Expected values in the tests are hand-derived from these same constants (self-consistency is the
    /// clean-room bar; there is no MapLibre output to match).
    /// </summary>
    public readonly struct Color : IEquatable<Color>
    {
        /// <summary>sRGB red, green, blue, and alpha — each nominally in [0,1].
        ///
        /// <para>The range is enforced by the <i>producers</i>, not by this constructor: every way a color
        /// enters the type clamps or rejects at its own boundary — <see cref="ColorParser"/> clips per CSS,
        /// <see cref="Ops.ColorCtors"/> raises an evaluation error, <see cref="FromLab"/> clamps the
        /// out-of-gamut result of its conversion. The constructor stays a plain field assignment so it can
        /// also carry the intermediate values of a color-space round-trip without clipping them.</para></summary>
        public readonly double R, G, B, A;

        public Color(double r, double g, double b, double a)
        {
            R = r; G = g; B = b; A = a;
        }

        /// <summary>Construct from 0..255 integer channels and a 0..1 alpha (rgb()/rgba() constructors).</summary>
        public static Color From255(double r255, double g255, double b255, double a)
            => new Color(r255 / 255.0, g255 / 255.0, b255 / 255.0, a);

        public bool Equals(Color other)
            => R == other.R && G == other.G && B == other.B && A == other.A;

        public override bool Equals(object obj) => obj is Color c && Equals(c);

        public override int GetHashCode()
            => R.GetHashCode() ^ (G.GetHashCode() << 2) ^ (B.GetHashCode() << 4) ^ (A.GetHashCode() << 6);

        // ---- to-rgba / to-string ------------------------------------------------------------------

        /// <summary>
        /// The <c>to-rgba</c> result: a 4-element array [r, g, b, a] with r,g,b in 0..255 and a in 0..1.
        /// Spec note: to-rgba returns un-premultiplied channels.
        /// </summary>
        public double[] ToRgbaArray()
            => new[] { R * 255.0, G * 255.0, B * 255.0, A };

        /// <summary>
        /// The <c>to-string</c> rendering of a color, per the spec: <c>rgba(r,g,b,a)</c> with r,g,b as
        /// 0..255 integers and a in 0..1.
        /// </summary>
        public string ToRgbaString()
        {
            int r = (int)math.round(R * 255.0);
            int g = (int)math.round(G * 255.0);
            int b = (int)math.round(B * 255.0);
            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3})", r, g, b, A);
        }

        // ---- linear sRGB <-> sRGB (IEC 61966-2-1) -------------------------------------------------

        private static double SrgbToLinear(double c)
            => c <= 0.04045 ? c / 12.92 : math.pow((c + 0.055) / 1.055, 2.4);

        private static double LinearToSrgb(double c)
            => c <= 0.0031308 ? c * 12.92 : 1.055 * math.pow(c, 1.0 / 2.4) - 0.055;

        // ---- sRGB <-> CIE LAB (via linear RGB and XYZ, D65) ---------------------------------------
        // D65 reference white for sRGB.
        private const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;

        private static double LabF(double t)
        {
            const double delta = 6.0 / 29.0;
            return t > delta * delta * delta
                ? math.pow(t, 1.0 / 3.0)   // cbrt(t), written as pow(t, 1/3) — same result for t>0
                : t / (3.0 * delta * delta) + 4.0 / 29.0;
        }

        private static double LabFInv(double t)
        {
            const double delta = 6.0 / 29.0;
            return t > delta
                ? t * t * t
                : 3.0 * delta * delta * (t - 4.0 / 29.0);
        }

        /// <summary>This color as CIELAB (L*, a*, b*) plus the (un-touched) alpha. sRGB→linear→XYZ→LAB.</summary>
        public (double L, double A, double B, double Alpha) ToLab()
        {
            double rl = SrgbToLinear(R), gl = SrgbToLinear(G), bl = SrgbToLinear(B);

            // linear sRGB -> CIE XYZ (D65), sRGB primaries matrix.
            double x = rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375;
            double y = rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750;
            double z = rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041;

            double fx = LabF(x / Xn), fy = LabF(y / Yn), fz = LabF(z / Zn);
            double l = 116.0 * fy - 16.0;
            double a = 500.0 * (fx - fy);
            double b = 200.0 * (fy - fz);
            return (l, a, b, A);
        }

        /// <summary>Build a color from CIELAB (L*, a*, b*) and alpha. LAB→XYZ→linear→sRGB, clamped to [0,1].</summary>
        public static Color FromLab(double l, double a, double b, double alpha)
        {
            double fy = (l + 16.0) / 116.0;
            double fx = fy + a / 500.0;
            double fz = fy - b / 200.0;

            double x = Xn * LabFInv(fx);
            double y = Yn * LabFInv(fy);
            double z = Zn * LabFInv(fz);

            // CIE XYZ (D65) -> linear sRGB.
            double rl = x * 3.2404542 + y * -1.5371385 + z * -0.4985314;
            double gl = x * -0.9692660 + y * 1.8760108 + z * 0.0415560;
            double bl = x * 0.0556434 + y * -0.2040259 + z * 1.0572252;

            double r = Clamp01(LinearToSrgb(rl));
            double g = Clamp01(LinearToSrgb(gl));
            double bb = Clamp01(LinearToSrgb(bl));
            return new Color(r, g, bb, alpha);
        }

        // ---- sRGB <-> HCL  (HCL == CIELCh(ab): LAB in polar form, hue in degrees) ------------------

        /// <summary>This color as HCL: (Hue degrees [0,360), Chroma, Luminance L*) plus alpha.</summary>
        public (double H, double C, double L, double Alpha) ToHcl()
        {
            var (l, a, b, alpha) = ToLab();
            double c = math.sqrt(a * a + b * b);
            double h = math.atan2(b, a) * 180.0 / math.PI_DBL;
            if (h < 0.0) h += 360.0;
            return (h, c, l, alpha);
        }

        /// <summary>Build a color from HCL (Hue degrees, Chroma, Luminance L*) and alpha.</summary>
        public static Color FromHcl(double h, double c, double l, double alpha)
        {
            double rad = h * math.PI_DBL / 180.0;
            double a = math.cos(rad) * c;
            double b = math.sin(rad) * c;
            return FromLab(l, a, b, alpha);
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);

        // ---- premultiplied-alpha mix (Porter-Duff, first-principles) -------------------------------

        /// <summary>
        /// Mix two colors in premultiplied-alpha sRGB space: the same arithmetic
        /// <see cref="Ops.Ramps"/> uses for its default <c>interpolate</c> color space, hoisted here so
        /// a style-transition ease and a zoom ramp share one implementation. At <c>t=0</c>/<c>t=1</c>
        /// this returns <paramref name="a"/>/<paramref name="b"/> exactly; between, RGB is
        /// premultiplied by alpha, mixed, then unpremultiplied by the mixed alpha.
        /// </summary>
        public static Color MixPremultiplied(in Color a, in Color b, double t)
        {
            // Early-exact, not just fast: a multiply immediately followed by a divide (a.R*aA/aA) is not
            // the float identity, so without this the doc's "returns a/b exactly" claim would be false.
            if (t <= 0.0) return a;
            if (t >= 1.0) return b;

            double aA = a.A, bA = b.A;
            double aOut = Lin(aA, bA, t);
            if (aOut <= 0.0)
                return new Color(0.0, 0.0, 0.0, 0.0);
            double rOut = Lin(a.R * aA, b.R * bA, t) / aOut;
            double gOut = Lin(a.G * aA, b.G * bA, t) / aOut;
            double bOut = Lin(a.B * aA, b.B * bA, t) / aOut;
            return new Color(rOut, gOut, bOut, aOut);
        }

        private static double Lin(double a, double b, double t) => a + (b - a) * t;
    }
}
