// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Color-space expected values below are hand-derived from the SAME citable standards the implementation
// cites (IEC 61966-2-1 sRGB transfer function; sRGB->XYZ matrix; D65 white (0.95047, 1.0, 1.08883);
// CIELAB / CIELCh per CIE 15). Clean-room: there is no MapLibre output to match — the bar is
// self-consistency with the standard math. Numbers were precomputed from those formulae (see the LAB/HCL
// derivations in the stage notes), NOT lifted from any renderer's fixtures.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>S09 — ramps/curves: step, interpolate (linear / exponential / cubic-bezier), -hcl, -lab.</summary>
    [TestFixture]
    public class RampCurveTests
    {
        // ---- step ----------------------------------------------------------------------------------

        [Test]
        public void Step_BelowFirstStop_ReturnsDefault()
        {
            // step: input, default, stop0->out0, stop1->out1
            string e = "[\"step\", -1, \"d\", 0, \"a\", 10, \"b\"]";
            Assert.AreEqual("d", Expr.Eval(e).AsString());
        }

        [Test]
        public void Step_ExactStop_PicksThatOutput()
        {
            string e = "[\"step\", 10, \"d\", 0, \"a\", 10, \"b\"]";
            Assert.AreEqual("b", Expr.Eval(e).AsString());
        }

        [Test]
        public void Step_BetweenStops_PicksLower()
        {
            string e = "[\"step\", 5, \"d\", 0, \"a\", 10, \"b\"]";
            Assert.AreEqual("a", Expr.Eval(e).AsString());
        }

        [Test]
        public void Step_NonAscendingStops_IsParseError()
        {
            Assert.Throws<ExpressionParseException>(
                () => Expr.Parse("[\"step\", 5, \"d\", 10, \"a\", 0, \"b\"]"));
        }

        // ---- interpolate: linear -------------------------------------------------------------------

        [Test]
        public void Interpolate_Linear_Midpoint()
        {
            string e = "[\"interpolate\", [\"linear\"], 5, 0, 0, 10, 100]";
            Assert.AreEqual(50.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Interpolate_Linear_ClampsBelowAndAbove()
        {
            string below = "[\"interpolate\", [\"linear\"], -5, 0, 0, 10, 100]";
            string above = "[\"interpolate\", [\"linear\"], 50, 0, 0, 10, 100]";
            Assert.AreEqual(0.0, Expr.Eval(below).AsNumber(), 1e-9);
            Assert.AreEqual(100.0, Expr.Eval(above).AsNumber(), 1e-9);
        }

        // ---- interpolate: exponential --------------------------------------------------------------

        [Test]
        public void Interpolate_Exponential_Base2_AtSampledInput()
        {
            // base=2, stops x:[0,10] out:[0,100], at x=5:
            //   t = (2^5 - 1) / (2^10 - 1) = 31/1023 ; value = 100*t = 3.0303030303...
            string e = "[\"interpolate\", [\"exponential\", 2], 5, 0, 0, 10, 100]";
            double expected = 100.0 * (31.0 / 1023.0);
            Assert.AreEqual(expected, Expr.Eval(e).AsNumber(), 1e-9);
            Assert.AreEqual(3.0303030303030303, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Interpolate_ExponentialBase1_EqualsLinear()
        {
            string exp = "[\"interpolate\", [\"exponential\", 1], 3, 0, 0, 10, 100]";
            string lin = "[\"interpolate\", [\"linear\"], 3, 0, 0, 10, 100]";
            Assert.AreEqual(Expr.Eval(lin).AsNumber(), Expr.Eval(exp).AsNumber(), 1e-9);
            Assert.AreEqual(30.0, Expr.Eval(exp).AsNumber(), 1e-9);
        }

        // ---- interpolate: cubic-bezier -------------------------------------------------------------

        [Test]
        public void Interpolate_CubicBezier_SymmetricEase_MidpointIsHalf()
        {
            // A symmetric ease (0.42,0,0.58,1) maps progress 0.5 -> 0.5 exactly.
            string e = "[\"interpolate\", [\"cubic-bezier\", 0.42, 0, 0.58, 1], 5, 0, 0, 10, 100]";
            Assert.AreEqual(50.0, Expr.Eval(e).AsNumber(), 1e-6);
        }

        [Test]
        public void Interpolate_CubicBezier_EaseIn_BelowLinear()
        {
            // ease-in (0.42,0,1,1): the curve lies below the diagonal for 0<t<1, so output < linear.
            string e = "[\"interpolate\", [\"cubic-bezier\", 0.42, 0, 1, 1], 5, 0, 0, 10, 100]";
            double v = Expr.Eval(e).AsNumber();
            Assert.Less(v, 50.0);
            Assert.Greater(v, 0.0);
        }

        // ---- interpolate: colors (default = premultiplied-alpha sRGB) ----------------------------

        [Test]
        public void Interpolate_Color_DefaultSpace_Alpha1_ReducesToStraightLerp()
        {
            // black -> white at t=0.5 with alpha=1 on both stops.
            // At alpha=1: premult is identity (R*1=R), unpremult divides by 1 (no-op).
            // Result equals straight sRGB lerp: R=127.5. This is the regression-pin for S09 tests.
            // Stops use alpha=1 so straight-vs-premultiplied cannot diverge.
            string e = "[\"interpolate\", [\"linear\"], 0.5, " +
                       "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]";
            Value c = Expr.Eval(e);
            var rgba = ToRgba(c);
            Assert.AreEqual(127.5, rgba[0], 1e-6,
                "At alpha=1, premultiplied-alpha reduces to straight sRGB lerp (R=127.5).");
            Assert.AreEqual(127.5, rgba[1], 1e-6);
            Assert.AreEqual(127.5, rgba[2], 1e-6);
            Assert.AreEqual(1.0, rgba[3], 1e-9);
        }

        [Test]
        public void Interpolate_Color_DefaultSpace_PremultAlpha_TransparentToOpaque()
        {
            // S11 canonical premult case: rgba(0,0,0,0) → rgba(255,255,255,1) at t=0.5.
            // Premult math: lerp premult (0,0,0,0) and (1,1,1,1) at t=0.5 → (0.5,0.5,0.5,0.5),
            // then unpremult /0.5 → (1.0, 1.0, 1.0, 0.5). So R=255/255=1, A=0.5.
            // Straight sRGB lerp (old behavior) would give R=0.5=127.5/255.
            string e = "[\"interpolate\", [\"linear\"], 0.5, " +
                       "0, [\"rgba\", 0, 0, 0, 0], 1, [\"rgba\", 255, 255, 255, 1]]";
            Value c = Expr.Eval(e);
            var rgba = ToRgba(c);
            Assert.AreEqual(255.0, rgba[0], 0.5,
                "Premult alpha: R at t=0.5 (transparent→opaque) must be 255, not 127.5 (straight lerp).");
            Assert.AreEqual(0.5, rgba[3], 1e-6, "Alpha at t=0.5 must be 0.5.");
        }

        // ---- interpolate-lab -----------------------------------------------------------------------

        [Test]
        public void InterpolateLab_BlackToWhite_MidIsPerceptualGray_NotSrgbHalf()
        {
            // In LAB, black(L=0) -> white(L=100) midpoint is L=50, which is sRGB ~118.91/255 — proving the
            // sRGB->linear gamma step happened (a missing-gamma bug would give 127.5).
            string e = "[\"interpolate-lab\", [\"linear\"], 0.5, " +
                       "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]";
            var rgba = ToRgba(Expr.Eval(e));
            Assert.AreEqual(118.9133, rgba[0], 0.05);
            Assert.AreEqual(118.9133, rgba[1], 0.05);
            Assert.AreEqual(118.9133, rgba[2], 0.05);
            // explicitly not the naive sRGB midpoint (the missing-gamma bug would give 127.5):
            Assert.Greater(System.Math.Abs(rgba[0] - 127.5), 1.0);
        }

        [Test]
        public void InterpolateLab_RedToGreen_Midpoint()
        {
            // LAB midpoint of red(#ff0000) and green(#00ff00) -> sRGB ~ (200.53, 171.01, 0).
            string e = "[\"interpolate-lab\", [\"linear\"], 0.5, " +
                       "0, [\"to-color\", \"#ff0000\"], 1, [\"to-color\", \"#00ff00\"]]";
            var rgba = ToRgba(Expr.Eval(e));
            Assert.AreEqual(200.5281, rgba[0], 0.05);
            Assert.AreEqual(171.0074, rgba[1], 0.05);
            Assert.AreEqual(0.0, rgba[2], 0.05);
        }

        // ---- interpolate-hcl (shortest-arc hue) ----------------------------------------------------

        [Test]
        public void InterpolateHcl_HueWrap_TakesShortestArcThroughZero()
        {
            // Two colors whose HCL hues are ~350 and ~10. The midpoint must pass through hue 0 (short arc),
            // giving a reddish color ~ (209.6,115.9,146.0). The long-arc bug (through 180) would give a
            // cyan ~ (0.2,163.0,143.7) — far away on the red channel, so R is the discriminator.
            string e = "[\"interpolate-hcl\", [\"linear\"], 0.5, " +
                       "0, [\"rgb\", 205, 117, 158], 1, [\"rgb\", 212, 116, 134]]";
            var rgba = ToRgba(Expr.Eval(e));
            Assert.AreEqual(209.6, rgba[0], 1.5, "HCL hue interpolation must take the shortest arc.");
            Assert.AreEqual(115.9, rgba[1], 1.5);
            Assert.AreEqual(146.0, rgba[2], 1.5);
        }

        // helper: read a color value's channels via to-rgba semantics (r,g,b in 0..255, a in 0..1)
        private static double[] ToRgba(Value colorValue)
        {
            Assert.AreEqual(ValueType.Color, colorValue.Type, "expected a color result");
            return colorValue.AsColor().ToRgbaArray();
        }
    }
}
