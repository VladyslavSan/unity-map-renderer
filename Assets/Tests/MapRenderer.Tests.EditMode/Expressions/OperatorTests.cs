// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>S09 — color category: rgb/rgba constructors, to-rgba, CSS literal parsing (Style Spec "Color").</summary>
    [TestFixture]
    public class ColorTests
    {
        private static double[] Rgba(Value v)
        {
            Assert.AreEqual(ValueType.Color, v.Type);
            return v.AsColor().ToRgbaArray();
        }

        [Test]
        public void Rgb_Constructs()
        {
            var rgba = Rgba(Expr.Eval("[\"rgb\", 255, 128, 0]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9);
            Assert.AreEqual(128.0, rgba[1], 1e-9);
            Assert.AreEqual(0.0, rgba[2], 1e-9);
            Assert.AreEqual(1.0, rgba[3], 1e-9);
        }

        [Test]
        public void Rgba_ConstructsWithAlpha()
        {
            var rgba = Rgba(Expr.Eval("[\"rgba\", 255, 0, 0, 0.5]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9);
            Assert.AreEqual(0.5, rgba[3], 1e-9);
        }

        [Test]
        public void ToRgba_Decomposes()
        {
            var rgba = Rgba(Expr.Eval("[\"rgba\", 10, 20, 30, 0.5]"));
            // round-trip via to-rgba expression
            Value arr = Expr.Eval("[\"to-rgba\", [\"rgba\", 10, 20, 30, 0.5]]");
            var items = arr.AsArray();
            Assert.AreEqual(10.0, items[0].AsNumber(), 1e-9);
            Assert.AreEqual(20.0, items[1].AsNumber(), 1e-9);
            Assert.AreEqual(30.0, items[2].AsNumber(), 1e-9);
            Assert.AreEqual(0.5, items[3].AsNumber(), 1e-9);
        }

        [Test]
        public void Rgb_OutOfRangeChannel_IsError()
        {
            bool ok = Expr.TryEval("[\"rgb\", 300, 0, 0]", out _, out _);
            Assert.IsFalse(ok, "an out-of-range channel must be a spec error.");
        }

        [Test]
        public void Rgba_OutOfRangeAlpha_IsError()
        {
            bool ok = Expr.TryEval("[\"rgba\", 0, 0, 0, 2]", out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void HexParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"#00ff00\"]"));
            Assert.AreEqual(0.0, rgba[0], 1e-9);
            Assert.AreEqual(255.0, rgba[1], 1e-9);
            Assert.AreEqual(0.0, rgba[2], 1e-9);
        }

        [Test]
        public void ShortHexParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"#0f0\"]"));
            Assert.AreEqual(0.0, rgba[0], 1e-9);
            Assert.AreEqual(255.0, rgba[1], 1e-9);
            Assert.AreEqual(0.0, rgba[2], 1e-9);
        }

        [Test]
        public void NamedColorParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"blue\"]"));
            Assert.AreEqual(0.0, rgba[0], 1e-9);
            Assert.AreEqual(0.0, rgba[1], 1e-9);
            Assert.AreEqual(255.0, rgba[2], 1e-9);
        }

        [Test]
        public void RgbFunctionStringParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"rgb(255, 0, 0)\"]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9);
            Assert.AreEqual(0.0, rgba[1], 1e-9);
        }

        [Test]
        public void ToLab_MidGray_ExercisesCbrtBranch()
        {
            // Belt-and-suspenders for the Math.Cbrt -> math.pow(t, 1.0/3.0) migration in Color.cs.
            // Mid-gray (128,128,128) has t = SrgbToLinear(128/255) ≈ 0.2158 >> delta^3 ≈ 0.00886,
            // so the f(t) = t^(1/3) branch is always exercised.
            // Expected values pinned from the pre-migration Math.Cbrt output: tolerance 1e-6 is
            // far tighter than the sub-ULP (~1e-15) difference between Cbrt and pow(t,1/3).
            var c = Color.From255(128.0, 128.0, 128.0, 1.0);
            var (L, a, b, alpha) = c.ToLab();
            Assert.AreEqual(1.0, alpha, 1e-9, "alpha must be preserved");
            Assert.AreEqual(53.585015771669404, L, 1e-6, "L* for sRGB mid-gray (pre-migration pin)");
            Assert.AreEqual(-9.997846439624425e-06, a, 1e-9, "a* for neutral gray (near 0)");
            Assert.AreEqual(3.99913857584977e-06, b, 1e-9, "b* for neutral gray (near 0)");
        }

        // ---- CSS out-of-range components are CLIPPED, not rejected (CSS Color 4 §4.1) ----------------
        // The counterpart to Rgb_OutOfRangeChannel_IsError / Rgba_OutOfRangeAlpha_IsError above: the
        // EXPRESSION constructors raise on a 300, the CSS-string parse path clamps it. Both are tested so
        // a future "unify them" edit has to break one of these two assertions to land.

        [Test]
        public void CssRgbString_ChannelAboveRange_ClampsTo255()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"rgb(300, 0, 0)\"]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9, "rgb(300,…) must clip to 255, not produce R > 1.0");
            Assert.AreEqual(0.0, rgba[1], 1e-9);
        }

        [Test]
        public void CssRgbString_NegativeChannel_ClampsToZero()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"rgb(0, -20, 0)\"]"));
            Assert.AreEqual(0.0, rgba[1], 1e-9, "a negative channel must clip to 0, not to a negative G");
        }

        [Test]
        public void CssRgbString_PercentAboveRange_ClampsTo255()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"rgb(150%, 0%, 0%)\"]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9, "the percent channel path needs its own clamp");
        }

        [Test]
        public void CssRgbaString_AlphaAboveRange_ClampsToOne()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"rgba(0, 0, 0, 5)\"]"));
            Assert.AreEqual(1.0, rgba[3], 1e-9, "alpha is opacity in [0,1]; 5 must clip to fully opaque");
        }

        [Test]
        public void CssHslString_LightnessAboveRange_ClampsToWhite()
        {
            // Unclamped, l = 1.5 drives q = l + s - l*s past 1 and HueToRgb returns p = 2l - q < 0 for the
            // off-hue channels — the colour comes out with NEGATIVE components, not merely too bright.
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"hsl(0, 100%, 150%)\"]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9, "l > 100% must clip to white");
            Assert.AreEqual(255.0, rgba[1], 1e-9);
            Assert.AreEqual(255.0, rgba[2], 1e-9);
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>S09 — decision category: case/match/coalesce, comparisons, all/any/! (Style Spec "Decision").</summary>
    [TestFixture]
    public class DecisionTests
    {
        [Test]
        public void Case_SelectsFirstTrueBranch()
        {
            string e = "[\"case\", false, \"a\", true, \"b\", \"fallback\"]";
            Assert.AreEqual("b", Expr.Eval(e).AsString());
        }

        [Test]
        public void Case_FallbackWhenNoneMatch()
        {
            string e = "[\"case\", false, \"a\", false, \"b\", \"fallback\"]";
            Assert.AreEqual("fallback", Expr.Eval(e).AsString());
        }

        [Test]
        public void Match_LabelLists_AndDefault()
        {
            string e = "[\"match\", \"b\", [\"a\", \"b\"], \"AB\", \"c\", \"C\", \"DEF\"]";
            Assert.AreEqual("AB", Expr.Eval(e).AsString());
            string e2 = "[\"match\", \"z\", [\"a\", \"b\"], \"AB\", \"c\", \"C\", \"DEF\"]";
            Assert.AreEqual("DEF", Expr.Eval(e2).AsString());
        }

        [Test]
        public void Match_NumericLabels()
        {
            string e = "[\"match\", 2, 1, \"one\", 2, \"two\", \"other\"]";
            Assert.AreEqual("two", Expr.Eval(e).AsString());
        }

        [Test]
        public void Coalesce_SkipsNull()
        {
            string e = "[\"coalesce\", [\"get\", \"missing\"], \"default\"]";
            Assert.AreEqual("default", Expr.Eval(e, Expr.Feature()).AsString());
        }

        [Test]
        public void Coalesce_SkipsErroringBranch()
        {
            // First branch errors (length of a number); coalesce skips it and returns the literal.
            string e = "[\"coalesce\", [\"length\", 5], 99]";
            Assert.AreEqual(99.0, Expr.Eval(e).AsNumber());
        }

        // ---- comparisons ---------------------------------------------------------------------------

        [Test]
        public void Equality_Numbers()
        {
            Assert.IsTrue(Expr.Eval("[\"==\", 1, 1]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"==\", 1, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"!=\", 1, 2]").AsBool());
        }

        [Test]
        public void Equality_Strings()
        {
            Assert.IsTrue(Expr.Eval("[\"==\", \"a\", \"a\"]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"==\", \"a\", \"b\"]").AsBool());
        }

        [Test]
        public void Equality_AcrossTypes_IsFalse_NotError()
        {
            // No implicit coercion: a number and a string are never equal (deliberate — deep value-equality
            // checks the type first). This is a runtime false, not an error.
            Assert.IsFalse(Expr.Eval("[\"==\", 1, \"1\"]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"!=\", 1, \"1\"]").AsBool());
        }

        [Test]
        public void OrderedComparisons_Numbers()
        {
            Assert.IsTrue(Expr.Eval("[\"<\", 1, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"<=\", 2, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\">\", 3, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\">=\", 2, 2]").AsBool());
            Assert.IsFalse(Expr.Eval("[\">\", 1, 2]").AsBool());
        }

        [Test]
        public void OrderedComparisons_Strings()
        {
            Assert.IsTrue(Expr.Eval("[\"<\", \"a\", \"b\"]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"<\", \"b\", \"a\"]").AsBool());
        }

        [Test]
        public void OrderedComparison_TypeMismatch_IsError()
        {
            bool ok = Expr.TryEval("[\"<\", 1, \"a\"]", out _, out _);
            Assert.IsFalse(ok, "comparing a number with a string must be a spec error.");
        }

        // ---- all / any / ! -------------------------------------------------------------------------

        [Test]
        public void All_TruthTable()
        {
            Assert.IsTrue(Expr.Eval("[\"all\", true, true]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"all\", true, false]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"all\"]").AsBool()); // empty all -> true
        }

        [Test]
        public void Any_TruthTable()
        {
            Assert.IsTrue(Expr.Eval("[\"any\", false, true]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"any\", false, false]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"any\"]").AsBool()); // empty any -> false
        }

        [Test]
        public void Not()
        {
            Assert.IsFalse(Expr.Eval("[\"!\", true]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"!\", false]").AsBool());
        }

        [Test]
        public void All_ShortCircuits_SkipsLaterError()
        {
            // Second arg would error, but the first is false so all short-circuits to false.
            string e = "[\"all\", false, [\"<\", 1, \"a\"]]";
            Assert.IsFalse(Expr.Eval(e).AsBool());
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>S09 — variable binding: let/var (Style Spec "Variable binding").</summary>
    [TestFixture]
    public class LetVarTests
    {
        [Test]
        public void Let_BindsAndVarResolves()
        {
            // let x = 5 in x + 1
            string e = "[\"let\", \"x\", 5, [\"+\", [\"var\", \"x\"], 1]]";
            Assert.AreEqual(6.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Let_MultipleBindings()
        {
            string e = "[\"let\", \"a\", 2, \"b\", 3, [\"*\", [\"var\", \"a\"], [\"var\", \"b\"]]]";
            Assert.AreEqual(6.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Let_LaterBindingReferencesEarlier()
        {
            // a=2, b=a+1 -> 3 ; result a+b = 5
            string e = "[\"let\", \"a\", 2, \"b\", [\"+\", [\"var\", \"a\"], 1], " +
                       "[\"+\", [\"var\", \"a\"], [\"var\", \"b\"]]]";
            Assert.AreEqual(5.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Let_NestedShadowing()
        {
            // outer x=1; inner let shadows x=10; inner body uses 10.
            string e = "[\"let\", \"x\", 1, " +
                       "[\"let\", \"x\", 10, [\"var\", \"x\"]]]";
            Assert.AreEqual(10.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Var_Unbound_IsParseError()
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse("[\"var\", \"nope\"]"));
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;
    using MapRenderer.Core.Tiles;

    /// <summary>S09 — lookup category: get/has/at/in/length (Style Spec "Lookup").</summary>
    [TestFixture]
    public class LookupTests
    {
        private static IFeature FeatureWith()
            => Expr.Feature(Expr.Props(
                ("name", Value.String("Berlin")),
                ("pop", Value.Number(3500000)),
                ("capital", Value.Bool(true))),
                TileGeometryType.Point);

        [Test]
        public void Get_Present()
            => Assert.AreEqual("Berlin", Expr.Eval("[\"get\", \"name\"]", FeatureWith()).AsString());

        [Test]
        public void Get_Absent_IsNull()
            => Assert.AreEqual(ValueType.Null, Expr.Eval("[\"get\", \"missing\"]", FeatureWith()).Type);

        [Test]
        public void Has_PresentAndAbsent()
        {
            Assert.IsTrue(Expr.Eval("[\"has\", \"name\"]", FeatureWith()).AsBool());
            Assert.IsFalse(Expr.Eval("[\"has\", \"missing\"]", FeatureWith()).AsBool());
        }

        [Test]
        public void Get_FromObjectLiteral()
        {
            Value v = Expr.Eval("[\"get\", \"a\", [\"literal\", {\"a\": 42}]]");
            Assert.AreEqual(42.0, v.AsNumber());
        }

        [Test]
        public void At_ValidIndex()
        {
            Value v = Expr.Eval("[\"at\", 1, [\"literal\", [10, 20, 30]]]");
            Assert.AreEqual(20.0, v.AsNumber());
        }

        [Test]
        public void At_OutOfRange_IsError()
        {
            bool ok = Expr.TryEval("[\"at\", 5, [\"literal\", [10, 20, 30]]]", out _, out _);
            Assert.IsFalse(ok, "at out-of-range must be a spec error, not a crash.");
        }

        [Test]
        public void At_NegativeIndex_IsError()
        {
            bool ok = Expr.TryEval("[\"at\", -1, [\"literal\", [10]]]", out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void In_Substring()
        {
            Assert.IsTrue(Expr.Eval("[\"in\", \"er\", \"Berlin\"]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"in\", \"xyz\", \"Berlin\"]").AsBool());
        }

        [Test]
        public void In_ArrayMembership()
        {
            Assert.IsTrue(Expr.Eval("[\"in\", 20, [\"literal\", [10, 20, 30]]]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"in\", 99, [\"literal\", [10, 20, 30]]]").AsBool());
        }

        [Test]
        public void Length_String() => Assert.AreEqual(6.0, Expr.Eval("[\"length\", \"Berlin\"]").AsNumber());

        [Test]
        public void Length_Array()
            => Assert.AreEqual(3.0, Expr.Eval("[\"length\", [\"literal\", [1, 2, 3]]]").AsNumber());

        [Test]
        public void Length_OnNumber_IsError()
        {
            bool ok = Expr.TryEval("[\"length\", 5]", out _, out _);
            Assert.IsFalse(ok);
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using System;
    using NUnit.Framework;

    /// <summary>S09 — math category (Style Spec "Math").</summary>
    [TestFixture]
    public class MathOpTests
    {
        private static double N(string json) => Expr.Eval(json).AsNumber();

        [Test]
        public void Arithmetic()
        {
            Assert.AreEqual(6.0, N("[\"+\", 1, 2, 3]"), 1e-9);
            Assert.AreEqual(24.0, N("[\"*\", 2, 3, 4]"), 1e-9);
            Assert.AreEqual(-1.0, N("[\"-\", 2, 3]"), 1e-9);
            Assert.AreEqual(-5.0, N("[\"-\", 5]"), 1e-9);          // unary negation
            Assert.AreEqual(2.5, N("[\"/\", 5, 2]"), 1e-9);
            Assert.AreEqual(1.0, N("[\"%\", 7, 3]"), 1e-9);
            Assert.AreEqual(8.0, N("[\"^\", 2, 3]"), 1e-9);
        }

        [Test]
        public void MinMax()
        {
            Assert.AreEqual(1.0, N("[\"min\", 3, 1, 2]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"max\", 3, 1, 2]"), 1e-9);
        }

        [Test]
        public void Rounding()
        {
            Assert.AreEqual(3.0, N("[\"abs\", -3]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"floor\", 3.7]"), 1e-9);
            Assert.AreEqual(4.0, N("[\"ceil\", 3.2]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"round\", 2.5]"), 1e-9);     // half away from zero
            Assert.AreEqual(-3.0, N("[\"round\", -2.5]"), 1e-9);
        }

        [Test]
        public void Trig()
        {
            Assert.AreEqual(0.0, N("[\"sin\", 0]"), 1e-9);
            Assert.AreEqual(1.0, N("[\"cos\", 0]"), 1e-9);
            Assert.AreEqual(0.0, N("[\"tan\", 0]"), 1e-9);
        }

        [Test]
        public void Logs()
        {
            Assert.AreEqual(0.0, N("[\"ln\", 1]"), 1e-9);
            Assert.AreEqual(2.0, N("[\"log10\", 100]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"log2\", 8]"), 1e-9);
            Assert.AreEqual(2.0, N("[\"sqrt\", 4]"), 1e-9);
        }

        [Test]
        public void Constants()
        {
            Assert.AreEqual(Math.E, N("[\"e\"]"), 1e-12);
            Assert.AreEqual(Math.PI, N("[\"pi\"]"), 1e-12);
            Assert.AreEqual(Math.Log(2.0), N("[\"ln2\"]"), 1e-12);
        }

        [Test]
        public void NonNumberOperand_IsError()
        {
            bool ok = Expr.TryEval("[\"+\", 1, \"x\"]", out _, out _);
            Assert.IsFalse(ok, "math on a non-number must be a spec error.");
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Color-space expected values below are hand-derived from the SAME citable standards the implementation
// cites (IEC 61966-2-1 sRGB transfer function; sRGB->XYZ matrix; D65 white (0.95047, 1.0, 1.08883);
// CIELAB / CIELCh per CIE 15). Clean-room: there is no MapLibre output to match — the bar is
// self-consistency with the standard math. Numbers were precomputed from those formulae (see the LAB/HCL
// derivations in the stage notes), NOT lifted from any renderer's fixtures.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

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

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;

    /// <summary>S09 — string category: concat/upcase/downcase (Style Spec "String").</summary>
    [TestFixture]
    public class StringTests
    {
        [Test]
        public void Concat_JoinsStrings()
            => Assert.AreEqual("foobar", Expr.Eval("[\"concat\", \"foo\", \"bar\"]").AsString());

        [Test]
        public void Concat_CoercesNonStrings()
        {
            // concat coerces each argument to string: 1 -> "1", true -> "true".
            Assert.AreEqual("a1true", Expr.Eval("[\"concat\", \"a\", 1, true]").AsString());
        }

        [Test]
        public void Upcase() => Assert.AreEqual("ABC", Expr.Eval("[\"upcase\", \"abc\"]").AsString());

        [Test]
        public void Downcase() => Assert.AreEqual("abc", Expr.Eval("[\"downcase\", \"ABC\"]").AsString());
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>
    /// S09 — zoom expression. Per spec, <c>zoom</c> is valid only as the direct input of a top-level
    /// <c>step</c>/<c>interpolate</c>; anywhere else is a parse error.
    /// </summary>
    [TestFixture]
    public class ZoomTests
    {
        [Test]
        public void Zoom_AsInterpolateInput_EvaluatesToContextZoom()
        {
            // interpolate over zoom: at z=5 between stops (0->0, 10->100) -> 50.
            string e = "[\"interpolate\", [\"linear\"], [\"zoom\"], 0, 0, 10, 100]";
            Assert.AreEqual(50.0, Expr.Eval(e, zoom: 5.0).AsNumber(), 1e-9);
            Assert.AreEqual(20.0, Expr.Eval(e, zoom: 2.0).AsNumber(), 1e-9);
        }

        [Test]
        public void Zoom_AsStepInput_Works()
        {
            string e = "[\"step\", [\"zoom\"], \"small\", 10, \"big\"]";
            Assert.AreEqual("small", Expr.Eval(e, zoom: 5.0).AsString());
            Assert.AreEqual("big", Expr.Eval(e, zoom: 12.0).AsString());
        }

        [Test]
        public void Zoom_NestedDeeperThanInput_IsParseError()
        {
            // zoom nested inside an arithmetic input (not the direct top-level input) is invalid.
            Assert.Throws<ExpressionParseException>(
                () => Expr.Parse("[\"interpolate\", [\"linear\"], [\"+\", [\"zoom\"], 1], 0, 0, 10, 100]"));
        }

        [Test]
        public void Zoom_AsTopLevelExpression_IsParseError()
        {
            // a bare ["zoom"] outside any ramp is not allowed.
            Assert.Throws<ExpressionParseException>(() => Expr.Parse("[\"zoom\"]"));
        }

        [Test]
        public void Zoom_InGenericOp_IsParseError()
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse("[\"+\", [\"zoom\"], 1]"));
        }
    }
}
