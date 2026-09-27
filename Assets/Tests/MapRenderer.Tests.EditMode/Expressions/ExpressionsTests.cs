// The expression engine's per-category operators and semantics. Engine-free: Tools/core-tests also compiles
// it. MathOpTests.cs is separate because its `using System;` makes the bare ValueType here ambiguous.
//
// Contents:
//   ColorTests                             — rgb/rgba constructors, to-rgba, CSS literal parsing.
//   DecisionTests                          — case/match/coalesce, comparisons, all/any/!.
//   LetVarTests                            — let/var variable binding.
//   LookupTests                            — get/has/at/in/length.
//   RampCurveTests                         — step, interpolate (linear/exponential/cubic-bezier/-hcl/-lab).
//   StringTests                            — concat/upcase/downcase.
//   ZoomTests                              — the zoom expression's ramp-only placement rule.
//   AssertionTests                         — the boolean/number/string/object/array type-assertion ops.
//   ClassificationTests                    — each expression classifies Constant/Zoom/Feature/Composite.
//   ColorCoercionTests                     — a CSS color string coerces to a color at every color seam.
//   ExpressionErrorTests                   — evaluation errors surface as TryEvaluate=false, never a crash;
//                                             parse-time structural errors throw.
//   FeatureDataTests                       — properties/geometry-type/id (Style Spec "Feature data").
//   FeatureKeyExpressionCapabilityTests    — a constant-key get/has node stays on the string path for a
//                                             feature that is not IIndexedFeature-capable.
//   FeatureKeyExpressionTests              — the int-keyed IIndexedFeature path fires only with a binding
//                                             AND an indexed feature; otherwise the string path fires.
//   LiteralTypeTests                       — literal values, typeof, and the to-* coercions.
//   LegacyFunctionTests                    — the deprecations-page legacy stops object: type/property/
//                                             default/colorSpace, single-stop, categorical, identity,
//                                             zoom-and-property.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using ExprValueType = MapRenderer.Core.Expressions.ValueType;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Expressions
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // ColorTests — rgb/rgba constructors, to-rgba, CSS literal parsing (Style Spec "Color")
    // ───────────────────────────────────────────────────────────────────────────────────

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
        [TestCase("#00ff00", 0.0, 255.0, 0.0, TestName = "ToColor_ParsesStringFormat(Hex)")]
        [TestCase("#0f0", 0.0, 255.0, 0.0, TestName = "ToColor_ParsesStringFormat(ShortHex)")]
        [TestCase("blue", 0.0, 0.0, 255.0, TestName = "ToColor_ParsesStringFormat(NamedColor)")]
        [TestCase("rgb(255, 0, 0)", 255.0, 0.0, 0.0, TestName = "ToColor_ParsesStringFormat(RgbFunction)")]
        public void ToColor_ParsesStringFormat(string cssString, double expectedR, double expectedG, double expectedB)
        {
            var rgba = Rgba(Expr.Eval($"[\"to-color\", \"{cssString}\"]"));
            Assert.AreEqual(expectedR, rgba[0], 1e-9);
            Assert.AreEqual(expectedG, rgba[1], 1e-9);
            Assert.AreEqual(expectedB, rgba[2], 1e-9);
        }

        [Test]
        public void ToLab_MidGray_ExercisesCbrtBranch()
        {
            // Mid-gray has t ≈ 0.2158 >> delta^3, so Color.cs's f(t) = pow(t, 1/3) branch runs. The expected
            // values come from a Math.Cbrt reference; 1e-6 is far wider than their sub-ULP difference.
            var c = Color.From255(128.0, 128.0, 128.0, 1.0);
            var (L, a, b, alpha) = c.ToLab();
            Assert.AreEqual(1.0, alpha, 1e-9, "alpha must be preserved");
            Assert.AreEqual(53.585015771669404, L, 1e-6, "L* for sRGB mid-gray (pre-migration pin)");
            Assert.AreEqual(-9.997846439624425e-06, a, 1e-9, "a* for neutral gray (near 0)");
            Assert.AreEqual(3.99913857584977e-06, b, 1e-9, "b* for neutral gray (near 0)");
        }

        // ---- CSS out-of-range components are CLIPPED, not rejected (CSS Color 4 §4.1) ----------------
        // The EXPRESSION constructors raise on a 300 (tests above); the CSS-string path clamps it.

        /// <summary>Every CSS-string clamp case, all four channels asserted (rgb/rgba channels 0-255,
        /// alpha 0-1). The hsl row is the sharpest: unclamped, l=1.5 drives q = l+s-l*s past 1 and HueToRgb
        /// returns p = 2l-q &lt; 0 for the off-hue channels, so the colour would otherwise come out with
        /// NEGATIVE components, not merely too bright.</summary>
        [Test]
        [TestCase("rgb(300, 0, 0)", 255.0, 0.0, 0.0, 1.0, "rgb(300,…) must clip to 255, not produce R > 1.0",
            TestName = "CssColorString_OutOfRangeComponent_Clamps(Rgb_ChannelAboveRange)")]
        [TestCase("rgb(0, -20, 0)", 0.0, 0.0, 0.0, 1.0, "a negative channel must clip to 0, not to a negative G",
            TestName = "CssColorString_OutOfRangeComponent_Clamps(Rgb_NegativeChannel)")]
        [TestCase("rgb(150%, 0%, 0%)", 255.0, 0.0, 0.0, 1.0, "the percent channel path needs its own clamp",
            TestName = "CssColorString_OutOfRangeComponent_Clamps(Rgb_PercentAboveRange)")]
        [TestCase("rgba(0, 0, 0, 5)", 0.0, 0.0, 0.0, 1.0, "alpha is opacity in [0,1]; 5 must clip to fully opaque",
            TestName = "CssColorString_OutOfRangeComponent_Clamps(Rgba_AlphaAboveRange)")]
        [TestCase("hsl(0, 100%, 150%)", 255.0, 255.0, 255.0, 1.0, "l > 100% must clip to white",
            TestName = "CssColorString_OutOfRangeComponent_Clamps(Hsl_LightnessAboveRange)")]
        public void CssColorString_OutOfRangeComponent_Clamps(
            string cssString, double expectedR, double expectedG, double expectedB, double expectedA, string message)
        {
            var rgba = Rgba(Expr.Eval($"[\"to-color\", \"{cssString}\"]"));
            Assert.AreEqual(expectedR, rgba[0], 1e-9, message);
            Assert.AreEqual(expectedG, rgba[1], 1e-9, message);
            Assert.AreEqual(expectedB, rgba[2], 1e-9, message);
            Assert.AreEqual(expectedA, rgba[3], 1e-9, message);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DecisionTests — case/match/coalesce, comparisons, all/any/! (Style Spec "Decision")
    // ───────────────────────────────────────────────────────────────────────────────────

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

        /// <summary>== and != across numbers, strings, and mismatched types: no implicit coercion, so a
        /// number and a string are never equal (deliberate — deep value-equality checks the type first) —
        /// a runtime false, not an error.</summary>
        [Test]
        public void Equality_ByOperandTypes()
        {
            Assert.IsTrue(Expr.Eval("[\"==\", 1, 1]").AsBool(), "numbers: 1 == 1");
            Assert.IsFalse(Expr.Eval("[\"==\", 1, 2]").AsBool(), "numbers: 1 != 2");
            Assert.IsTrue(Expr.Eval("[\"!=\", 1, 2]").AsBool(), "numbers: 1 != 2");

            Assert.IsTrue(Expr.Eval("[\"==\", \"a\", \"a\"]").AsBool(), "strings: a == a");
            Assert.IsFalse(Expr.Eval("[\"==\", \"a\", \"b\"]").AsBool(), "strings: a != b");

            Assert.IsFalse(Expr.Eval("[\"==\", 1, \"1\"]").AsBool(), "across types: 1 != \"1\", not an error");
            Assert.IsTrue(Expr.Eval("[\"!=\", 1, \"1\"]").AsBool(), "across types: 1 != \"1\"");
        }

        /// <summary>&lt; &lt;= &gt; &gt;= across numbers and strings; comparing mismatched types is a spec
        /// error, not a crash.</summary>
        [Test]
        public void OrderedComparison_ByOperandTypes()
        {
            Assert.IsTrue(Expr.Eval("[\"<\", 1, 2]").AsBool(), "numbers: 1 < 2");
            Assert.IsTrue(Expr.Eval("[\"<=\", 2, 2]").AsBool(), "numbers: 2 <= 2");
            Assert.IsTrue(Expr.Eval("[\">\", 3, 2]").AsBool(), "numbers: 3 > 2");
            Assert.IsTrue(Expr.Eval("[\">=\", 2, 2]").AsBool(), "numbers: 2 >= 2");
            Assert.IsFalse(Expr.Eval("[\">\", 1, 2]").AsBool(), "numbers: 1 is not > 2");

            Assert.IsTrue(Expr.Eval("[\"<\", \"a\", \"b\"]").AsBool(), "strings: a < b");
            Assert.IsFalse(Expr.Eval("[\"<\", \"b\", \"a\"]").AsBool(), "strings: b is not < a");

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

    // ───────────────────────────────────────────────────────────────────────────────────
    // LetVarTests — let/var variable binding (Style Spec "Variable binding")
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LetVarTests
    {
        [Test]
        // let x = 5 in x + 1
        [TestCase("[\"let\", \"x\", 5, [\"+\", [\"var\", \"x\"], 1]]", 6.0, TestName = "Let_BindingSemantics(BindsAndVarResolves)")]
        [TestCase("[\"let\", \"a\", 2, \"b\", 3, [\"*\", [\"var\", \"a\"], [\"var\", \"b\"]]]", 6.0, TestName = "Let_BindingSemantics(MultipleBindings)")]
        // a=2, b=a+1 -> 3 ; result a+b = 5
        [TestCase("[\"let\", \"a\", 2, \"b\", [\"+\", [\"var\", \"a\"], 1], [\"+\", [\"var\", \"a\"], [\"var\", \"b\"]]]", 5.0, TestName = "Let_BindingSemantics(LaterBindingReferencesEarlier)")]
        // outer x=1; inner let shadows x=10; inner body uses 10.
        [TestCase("[\"let\", \"x\", 1, [\"let\", \"x\", 10, [\"var\", \"x\"]]]", 10.0, TestName = "Let_BindingSemantics(NestedShadowing)")]
        public void Let_BindingSemantics(string json, double expected)
        {
            Assert.AreEqual(expected, Expr.Eval(json).AsNumber(), 1e-9);
        }

        [Test]
        public void Var_Unbound_IsParseError()
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse("[\"var\", \"nope\"]"));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LookupTests — get/has/at/in/length (Style Spec "Lookup")
    // ───────────────────────────────────────────────────────────────────────────────────

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
        [TestCase("[\"at\", 5, [\"literal\", [10, 20, 30]]]", TestName = "At_IndexValidity(OutOfRange_IsError)")]
        [TestCase("[\"at\", -1, [\"literal\", [10]]]", TestName = "At_IndexValidity(NegativeIndex_IsError)")]
        public void At_IndexValidity(string json)
        {
            bool ok = Expr.TryEval(json, out _, out _);
            Assert.IsFalse(ok, "an invalid at index must be a spec error, not a crash.");
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
        [TestCase("\"Berlin\"", 6.0, TestName = "Length_ByOperandType(String)")]
        [TestCase("[\"literal\", [1, 2, 3]]", 3.0, TestName = "Length_ByOperandType(Array)")]
        public void Length_ByOperandType(string operand, double expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"length\", {operand}]").AsNumber());
        }

        [Test]
        public void Length_OnNumber_IsError()
        {
            bool ok = Expr.TryEval("[\"length\", 5]", out _, out _);
            Assert.IsFalse(ok);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RampCurveTests — step, interpolate (linear/exponential/cubic-bezier), -hcl, -lab
    // ───────────────────────────────────────────────────────────────────────────────────

    // Expected colours derive from IEC 61966-2-1 sRGB, D65 white (0.95047, 1.0, 1.08883) and CIE 15 LAB/LCh.
    // There is no MapLibre output to match, so no parity oracle: the bar is self-consistency with that math.
    [TestFixture]
    public class RampCurveTests
    {
        // ---- step ----------------------------------------------------------------------------------

        // step: input, default, stop0->out0, stop1->out1 (stops fixed at 0->"a", 10->"b", default "d").
        [Test]
        [TestCase(-1.0, "d", TestName = "Step_PicksOutputForInput(BelowFirstStop_ReturnsDefault)")]
        [TestCase(10.0, "b", TestName = "Step_PicksOutputForInput(ExactStop_PicksThatOutput)")]
        [TestCase(5.0, "a", TestName = "Step_PicksOutputForInput(BetweenStops_PicksLower)")]
        public void Step_PicksOutputForInput(double input, string expected)
        {
            // Invariant culture: some cultures format a negative number with U+2212 MINUS SIGN instead of
            // ASCII '-', which would emit invalid JSON for the -1.0 case.
            string e = $"[\"step\", {input.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"d\", 0, \"a\", 10, \"b\"]";
            Assert.AreEqual(expected, Expr.Eval(e).AsString());
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

        // ---- interpolate: number arrays -------------------------------------------------------------

        [Test]
        public void Interpolate_ThreeEntryNumberArray_LerpsElementwise_NotColorCoerced()
        {
            // A 3-number array ALSO looks like an rgb() triple; the array branch must win so this lerps
            // element-wise instead of being coerced to a Color. Input is ["zoom"], not a literal 5, so the
            // SAME parsed expression can be re-evaluated at a different zoom below, for the aliasing check.
            string e = "[\"interpolate\", [\"linear\"], [\"zoom\"], " +
                       "0, [\"literal\", [0, 10, 20]], 10, [\"literal\", [10, 30, 40]]]";
            var expr = Expr.Parse(e);
            Value v = expr.Evaluate(new EvaluationContext(5.0));
            Assert.AreEqual(ExprValueType.Array, v.Type, "a number-array stop must stay an array");
            var items = v.AsArray();
            Assert.AreEqual(3, items.Count);
            Assert.AreEqual(5.0, items[0].AsNumber(), 1e-9);
            Assert.AreEqual(20.0, items[1].AsNumber(), 1e-9);
            Assert.AreEqual(30.0, items[2].AsNumber(), 1e-9);

            // UMR-222: a Value returned by Evaluate must never alias a buffer a LATER call overwrites — a
            // second, different-zoom evaluation on the SAME expression must leave the first result untouched.
            double firstX = items[0].AsNumber(), firstY = items[1].AsNumber(), firstZ = items[2].AsNumber();
            Value second = expr.Evaluate(new EvaluationContext(8.0));
            Assert.AreNotEqual(firstX, second.AsArray()[0].AsNumber(),
                "precondition: zoom 5 and zoom 8 must actually produce different results, or this proves nothing.");
            Assert.AreEqual(firstX, items[0].AsNumber(), 1e-9,
                "the FIRST result's array must be unchanged after a second, different evaluation.");
            Assert.AreEqual(firstY, items[1].AsNumber(), 1e-9);
            Assert.AreEqual(firstZ, items[2].AsNumber(), 1e-9);
        }

        // ---- interpolate: colors (default = premultiplied-alpha sRGB) ----------------------------

        [Test]
        public void Interpolate_Color_DefaultSpace_Alpha1_ReducesToStraightLerp()
        {
            // black -> white at t=0.5, alpha=1 on both stops: premultiplying is the identity, so the result is
            // the straight sRGB lerp, R=127.5.
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
            // rgba(0,0,0,0) → rgba(255,255,255,1) at t=0.5: premultiplied lerp (0.5,0.5,0.5,0.5), unpremult
            // → R=1, A=0.5. A straight sRGB lerp would give R=0.5.
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
            // HCL hues ~350 and ~10: the short-arc midpoint is reddish (209.6,115.9,146.0). A long arc through
            // 180 gives cyan (0.2,163.0,143.7), so R discriminates.
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // LegacyFunctionTests — the deprecations-page legacy stops object: type/property/default/colorSpace,
    // single-stop, categorical, identity, zoom-and-property
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A legacy function object (<c>{"stops":…}</c>, or a bare <c>"type":"identity"</c>) synthesises the
    /// equivalent modern expression and re-parses it. Each tooth also states one hand-computed value, so an
    /// implementation wrong in both the synthesis and the check cannot pass.
    /// </summary>
    [TestFixture]
    public class LegacyFunctionTests
    {
        [Test]
        public void Interval_ZoomFunction_StepsAtTheSecondStop()
        {
            // interval steps: below the second stop the first stop's output holds, unlike a ramp.
            string json = "{\"type\":\"interval\",\"stops\":[[10,1],[14,5]]}";
            Assert.AreEqual(1.0, Expr.Eval(json, zoom: 13.9).AsNumber());
            Assert.AreEqual(5.0, Expr.Eval(json, zoom: 14.1).AsNumber());
        }

        [Test]
        public void Categorical_MatchesLabelOrFallsToDefault()
        {
            string json = "{\"property\":\"class\",\"type\":\"categorical\"," +
                          "\"stops\":[[\"motorway\",8],[\"trunk\",5]],\"default\":1}";
            var expr = Expr.Parse(json);
            Assert.AreEqual(ExpressionKind.Feature, expr.Kind);

            var trunk = Expr.Feature(Expr.Props(("class", Value.String("trunk"))));
            Assert.AreEqual(5.0, expr.Evaluate(new EvaluationContext(0.0, trunk)).AsNumber());

            var primary = Expr.Feature(Expr.Props(("class", Value.String("primary"))));
            Assert.AreEqual(1.0, expr.Evaluate(new EvaluationContext(0.0, primary)).AsNumber(), "an unmatched label takes \"default\"");

            var missing = Expr.Feature();
            Assert.AreEqual(1.0, expr.Evaluate(new EvaluationContext(0.0, missing)).AsNumber(), "a missing property also takes \"default\"");
        }

        [Test]
        public void Categorical_BooleanDomain_MatchesByEquality()
        {
            string json = "{\"property\":\"oneway\",\"type\":\"categorical\",\"stops\":[[true,3],[false,1]]}";
            var expr = Expr.Parse(json);
            var f = Expr.Feature(Expr.Props(("oneway", Value.Bool(false))));
            Assert.AreEqual(1.0, expr.Evaluate(new EvaluationContext(0.0, f)).AsNumber());
        }

        [Test]
        public void Identity_ReadsThePropertyVerbatim()
        {
            // "identity" needs no "stops" key at all.
            string json = "{\"type\":\"identity\",\"property\":\"w\"}";
            var expr = Expr.Parse(json);
            var f = Expr.Feature(Expr.Props(("w", Value.Number(7.0))));
            Assert.AreEqual(7.0, expr.Evaluate(new EvaluationContext(0.0, f)).AsNumber());
        }

        [Test]
        public void ExponentialProperty_RampsNumericFeatureValue_FallsToDefaultOtherwise()
        {
            string json = "{\"property\":\"pop\",\"base\":2,\"stops\":[[0,1],[10,11]],\"default\":4}";
            var expr = Expr.Parse(json);

            var numeric = Expr.Feature(Expr.Props(("pop", Value.Number(5.0))));
            // t = (2^5-1)/(2^10-1) = 31/1023; value = 1 + 10*t.
            Assert.AreEqual(1.0 + 10.0 * 31.0 / 1023.0, expr.Evaluate(new EvaluationContext(0.0, numeric)).AsNumber(), 1e-4);

            var nonNumeric = Expr.Feature(Expr.Props(("pop", Value.String("n/a"))));
            Assert.AreEqual(4.0, expr.Evaluate(new EvaluationContext(0.0, nonNumeric)).AsNumber(),
                "a non-numeric feature value takes \"default\" instead of throwing inside interpolate");
        }

        [Test]
        public void ZoomAndProperty_InterpolatesBothAxes()
        {
            // z10: rank 1->2, 5->6. z14: rank 1->4, 5->12. At rank=3, z10 gives 4 and z14 gives 8;
            // interpolating those over zoom at z12 (halfway) gives 6.
            string json = "{\"property\":\"rank\",\"stops\":[" +
                          "[{\"zoom\":10,\"value\":1},2],[{\"zoom\":10,\"value\":5},6]," +
                          "[{\"zoom\":14,\"value\":1},4],[{\"zoom\":14,\"value\":5},12]]}";
            var expr = Expr.Parse(json);
            Assert.AreEqual(ExpressionKind.Composite, expr.Kind);

            var rank3 = Expr.Feature(Expr.Props(("rank", Value.Number(3.0))));
            Assert.AreEqual(6.0, expr.Evaluate(new EvaluationContext(12.0, rank3)).AsNumber(), 1e-6);
        }

        [Test]
        public void ColorSpace_Lab_MatchesTheModernInterpolateLabForm()
        {
            string legacy = "{\"colorSpace\":\"lab\",\"stops\":[[0,\"#ff0000\"],[10,\"#0000ff\"]]}";
            string modern = "[\"interpolate-lab\",[\"linear\"],[\"zoom\"],0,\"#ff0000\",10,\"#0000ff\"]";
            var legacyResult = Expr.Eval(legacy, zoom: 5.0).AsColor().ToRgbaArray();
            var modernResult = Expr.Eval(modern, zoom: 5.0).AsColor().ToRgbaArray();
            CollectionAssert.AreEqual(modernResult, legacyResult);

            string rgb = "{\"stops\":[[0,\"#ff0000\"],[10,\"#0000ff\"]]}"; // default colorSpace: rgb
            var rgbResult = Expr.Eval(rgb, zoom: 5.0).AsColor().ToRgbaArray();
            CollectionAssert.AreNotEqual(rgbResult, legacyResult, "lab and rgb must blend differently");
        }

        [Test]
        public void ColorSpace_Hcl_MatchesTheModernInterpolateHclForm()
        {
            string legacy = "{\"colorSpace\":\"hcl\",\"stops\":[[0,\"#ff0000\"],[10,\"#0000ff\"]]}";
            string modern = "[\"interpolate-hcl\",[\"linear\"],[\"zoom\"],0,\"#ff0000\",10,\"#0000ff\"]";
            var legacyResult = Expr.Eval(legacy, zoom: 5.0).AsColor().ToRgbaArray();
            var modernResult = Expr.Eval(modern, zoom: 5.0).AsColor().ToRgbaArray();
            CollectionAssert.AreEqual(modernResult, legacyResult);
        }

        [Test]
        public void Identity_WithDefault_MissingPropertyTakesIt()
        {
            string json = "{\"type\":\"identity\",\"property\":\"w\",\"default\":9}";
            var expr = Expr.Parse(json);
            Assert.AreEqual(9.0, expr.Evaluate(new EvaluationContext(0.0, Expr.Feature())).AsNumber());
        }

        [Test]
        public void SingleStop_IsConstant_RegardlessOfInput()
        {
            // A single-stop function returns that one output at every input.
            string json = "{\"stops\":[[5,3]]}";
            Assert.AreEqual(3.0, Expr.Eval(json, zoom: 0.0).AsNumber());
            Assert.AreEqual(3.0, Expr.Eval(json, zoom: 20.0).AsNumber());
        }

        [Test]
        public void ZoomFunction_Base1_StaysZoomKind_AndInterpolatesLinearly()
        {
            // Byte-identity regression: a plain zoom function (no type/property) keeps producing a Zoom-kind
            // InterpolateExpression, the same Zoom-kind InterpolateExpression as a modern
            // ["interpolate",["linear"],["zoom"],…].
            var expr = Expr.Parse("{\"stops\":[[0,0],[10,100]],\"base\":1}");
            Assert.AreEqual(ExpressionKind.Zoom, expr.Kind);
            Assert.AreEqual(50.0, expr.Evaluate(new EvaluationContext(5.0)).AsNumber(), 1e-9);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // StringTests — concat/upcase/downcase (Style Spec "String")
    // ───────────────────────────────────────────────────────────────────────────────────

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

    // ───────────────────────────────────────────────────────────────────────────────────
    // ZoomTests — the zoom expression's ramp-only placement rule
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zoom expression. Per spec, <c>zoom</c> is valid only as the direct input of a top-level
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

        // zoom is legal ONLY as a ramp's direct top-level input — nested inside another op, bare at
        // top level, or inside a generic op are all a parse error.
        [Test]
        [TestCase("[\"interpolate\", [\"linear\"], [\"+\", [\"zoom\"], 1], 0, 0, 10, 100]", TestName = "Zoom_MisplacedOutsideRampInput_IsParseError(NestedDeeperThanInput)")]
        [TestCase("[\"zoom\"]", TestName = "Zoom_MisplacedOutsideRampInput_IsParseError(AsTopLevelExpression)")]
        [TestCase("[\"+\", [\"zoom\"], 1]", TestName = "Zoom_MisplacedOutsideRampInput_IsParseError(InGenericOp)")]
        public void Zoom_MisplacedOutsideRampInput_IsParseError(string json)
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse(json));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // AssertionTests — the boolean/number/string/object/array type-assertion ops
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Type-assertion operators (spec "Types / Assertion"): <c>boolean</c> / <c>number</c> / <c>string</c> /
    /// <c>object</c> / <c>array</c> return the input when the type matches and error otherwise, unlike the
    /// <c>to-*</c> coercions. <c>["number",["zoom"]]</c> classifies as Zoom-kind. The no-GC sweep over
    /// these wrappers is in <see cref="MapRenderer.Tests.Style.StylePropertyTests"/>.
    /// </summary>
    [TestFixture]
    public class AssertionTests
    {
        // ── boolean / number / string / object: matching-type input returns the same value ────────

        private static IEnumerable<TestCaseData> MatchingTypeCases()
        {
            yield return new TestCaseData("[\"boolean\", true]", ExprValueType.Boolean, Value.Bool(true))
                .SetName("AssertionOp_MatchingType_ReturnsInput(Boolean_True)");
            yield return new TestCaseData("[\"boolean\", false]", ExprValueType.Boolean, Value.Bool(false))
                .SetName("AssertionOp_MatchingType_ReturnsInput(Boolean_False)");
            yield return new TestCaseData("[\"number\", 42]", ExprValueType.Number, Value.Number(42.0))
                .SetName("AssertionOp_MatchingType_ReturnsInput(Number)");
            yield return new TestCaseData("[\"string\", \"hi\"]", ExprValueType.String, Value.String("hi"))
                .SetName("AssertionOp_MatchingType_ReturnsInput(String)");
            yield return new TestCaseData("[\"object\", {\"k\": 1}]", ExprValueType.Object,
                    Value.Object(new Dictionary<string, Value> { ["k"] = Value.Number(1) }))
                .SetName("AssertionOp_MatchingType_ReturnsInput(Object)");
        }

        [Test]
        [TestCaseSource(nameof(MatchingTypeCases))]
        public void AssertionOp_MatchingType_ReturnsInput(string json, ExprValueType expectedType, Value expected)
        {
            Value v = ExpressionParser.Parse(json).Evaluate(default);
            Assert.AreEqual(expectedType, v.Type);
            Assert.AreEqual(expected, v, json);
        }

        // ── boolean / number: the multi-arg first-match form ──────────────────────────────────────

        private static IEnumerable<TestCaseData> MultiArgFirstMatchCases()
        {
            // Multi-arg first-match form: the first argument that matches the asserted type wins.
            yield return new TestCaseData("[\"boolean\", 42, true]", Value.Bool(true))
                .SetName("AssertionOp_MultiArg_FirstMatchWins(Boolean)");
            yield return new TestCaseData("[\"number\", \"hello\", 7]", Value.Number(7.0))
                .SetName("AssertionOp_MultiArg_FirstMatchWins(Number)");
        }

        [Test]
        [TestCaseSource(nameof(MultiArgFirstMatchCases))]
        public void AssertionOp_MultiArg_FirstMatchWins(string json, Value expected)
        {
            Value v = ExpressionParser.Parse(json).Evaluate(default);
            Assert.AreEqual(expected, v, json);
        }

        // ── array ───────────────────────────────────────────────────────────────

        /// <summary>The array assert form, with and without an element-type/length constraint: it resolves
        /// to the Array type and reports the expected element count.</summary>
        [Test]
        [TestCase("[\"array\", [\"literal\", [1, 2, 3]]]", 3, TestName = "Array_Assertion_PassesWithCount(AnyElementType_AnyLength)")]
        [TestCase("[\"array\", \"number\", 2, [\"literal\", [10, 20]]]", 2, TestName = "Array_Assertion_PassesWithCount(NumberType_ExactLength)")]
        [TestCase("[\"array\", \"number\", [\"literal\", [1, 2, 3]]]", 3, TestName = "Array_Assertion_PassesWithCount(NumberType_AnyLength)")]
        public void Array_Assertion_PassesWithCount(string json, int expectedCount)
        {
            Value v = ExpressionParser.Parse(json).Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
            Assert.AreEqual(expectedCount, v.AsArray().Count);
        }

        // ── assert: a wrong-typed/malformed value throws at evaluation ───────────

        /// <summary>Each malformed <c>assert</c> form throws at evaluation: a boolean multi-arg none-match,
        /// a wrong-typed value for boolean/number/string/object, and each array element-type/length
        /// mismatch.</summary>
        [Test]
        [TestCase("[\"boolean\", 42]", TestName = "TypeAssertion_WrongInput_Throws(Boolean_NumberInput)")]
        [TestCase("[\"boolean\", 1, 2, 3]", TestName = "TypeAssertion_WrongInput_Throws(Boolean_MultiArg_NoneMatch)")]
        [TestCase("[\"number\", \"hello\"]", TestName = "TypeAssertion_WrongInput_Throws(Number_StringInput)")]
        [TestCase("[\"string\", 99]", TestName = "TypeAssertion_WrongInput_Throws(String_NumberInput)")]
        [TestCase("[\"object\", \"x\"]", TestName = "TypeAssertion_WrongInput_Throws(Object_StringInput)")]
        [TestCase("[\"array\", 42]", TestName = "TypeAssertion_WrongInput_Throws(Array_AnyElementType_NonArrayInput)")]
        // Evaluates ["array","number", literal [1, "x"]] — second element is string -> error.
        [TestCase("[\"array\", \"number\", [\"literal\", [1, \"x\"]]]", TestName = "TypeAssertion_WrongInput_Throws(Array_NumberElementType_MixedElements)")]
        [TestCase("[\"array\", \"number\", 3, [\"literal\", [10, 20]]]", TestName = "TypeAssertion_WrongInput_Throws(Array_ExactLength_WrongLength)")]
        public void TypeAssertion_WrongInput_Throws(string json)
        {
            var expr = ExpressionParser.Parse(json);
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── zoom wrapping ────────────────────────────────────────────────────────
        // ["number",["zoom"]] must parse (zoom is legal when inside a ramp input) and classify Zoom.

        [Test]
        public void Number_WrappingZoom_ParsesAsZoomKind()
        {
            // Zoom inside a ramp input slot is valid only with zoomAllowed=true; the "number" assertion must
            // pass that flag through to the inner zoom arg.
            var expr = ExpressionParser.Parse(
                "[\"interpolate\", [\"linear\"], [\"number\", [\"zoom\"]], 5, 0.0, 10, 1.0]");
            Assert.AreEqual(ExpressionKind.Zoom, expr.Kind,
                "An interpolate whose input is [\"number\",[\"zoom\"]] must classify as Zoom-kind.");
        }

        [Test]
        public void Number_WrappingZoom_EvaluatesAtGivenZoom()
        {
            var expr = ExpressionParser.Parse(
                "[\"interpolate\", [\"linear\"], [\"number\", [\"zoom\"]], 5, 0.0, 10, 100.0]");
            // At zoom=7.5, t = (7.5-5)/(10-5) = 0.5, so value = 50.
            double v = expr.Evaluate(new EvaluationContext(7.5)).AsNumber();
            Assert.AreEqual(50.0, v, 1e-9);
        }

        [Test]
        public void Number_TopLevelZoom_WithoutRamp_ThrowsParseError()
        {
            // A bare ["number",["zoom"]] NOT inside a ramp input is NOT legal.
            Assert.Throws<ExpressionParseException>(
                () => ExpressionParser.Parse("[\"number\", [\"zoom\"]]"),
                "\"zoom\" must throw at parse when not inside a ramp input.");
        }

        // ── classification ────────────────────────────────────────────────────────

        [Test]
        public void Assert_Constant_ClassifiesConstant()
        {
            var expr = ExpressionParser.Parse("[\"number\", 5]");
            Assert.AreEqual(ExpressionKind.Constant, expr.Kind);
        }

        [Test]
        public void Assert_ResultType_IsAssertedType()
        {
            Assert.AreEqual(ExprValueType.Number,  ExpressionParser.Parse("[\"number\", 5]").ResultType);
            Assert.AreEqual(ExprValueType.Boolean, ExpressionParser.Parse("[\"boolean\", true]").ResultType);
            Assert.AreEqual(ExprValueType.String,  ExpressionParser.Parse("[\"string\", \"x\"]").ResultType);
            Assert.AreEqual(ExprValueType.Array,   ExpressionParser.Parse("[\"array\", [\"literal\", [1]]]").ResultType);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ClassificationTests — each expression classifies Constant/Zoom/Feature/Composite
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classification: each parsed expression reports Constant / Zoom / Feature / Composite. The
    /// brief's "constant / zoom / data-driven" maps to Constant / Zoom / Feature; Composite (depends on
    /// BOTH zoom and feature) is the fourth corner needed to distinguish a per-frame uniform from a
    /// per-vertex attribute.
    /// </summary>
    [TestFixture]
    public class ClassificationTests
    {
        private static ExpressionKind Kind(string json) => Expr.Parse(json).Kind;

        [Test]
        [TestCase("5", ExpressionKind.Constant, TestName = "ExpressionKind_ClassifiesAsExpected(Literal)")]
        [TestCase("[\"+\", 1, 2]", ExpressionKind.Constant, TestName = "ExpressionKind_ClassifiesAsExpected(PureArithmetic)")]
        // data-driven == "zoom" classification per the brief (camera expression).
        [TestCase("[\"interpolate\", [\"linear\"], [\"zoom\"], 0, 0, 10, 100]", ExpressionKind.Zoom, TestName = "ExpressionKind_ClassifiesAsExpected(ZoomRamp)")]
        // data-driven == "Feature" classification.
        [TestCase("[\"get\", \"x\"]", ExpressionKind.Feature, TestName = "ExpressionKind_ClassifiesAsExpected(Get)")]
        [TestCase("[\"has\", \"x\"]", ExpressionKind.Feature, TestName = "ExpressionKind_ClassifiesAsExpected(Has)")]
        [TestCase("[\"geometry-type\"]", ExpressionKind.Feature, TestName = "ExpressionKind_ClassifiesAsExpected(GeometryType)")]
        [TestCase("[\"id\"]", ExpressionKind.Feature, TestName = "ExpressionKind_ClassifiesAsExpected(Id)")]
        // ["+", ["zoom-ramp"], ["get"]] would be invalid (zoom not at ramp input); instead use a ramp over
        // zoom whose stop OUTPUTS depend on a feature -> composite.
        [TestCase("[\"interpolate\", [\"linear\"], [\"zoom\"], 0, [\"get\", \"a\"], 10, [\"get\", \"b\"]]", ExpressionKind.Composite, TestName = "ExpressionKind_ClassifiesAsExpected(MixedZoomAndFeature)")]
        [TestCase("[\"==\", [\"get\", \"x\"], 5]", ExpressionKind.Feature, TestName = "ExpressionKind_ClassifiesAsExpected(FeatureDrivenComparison)")]
        // body uses a feature get -> Feature.
        [TestCase("[\"let\", \"x\", [\"get\", \"a\"], [\"var\", \"x\"]]", ExpressionKind.Feature, TestName = "ExpressionKind_ClassifiesAsExpected(Let_InheritsBodyKind)")]
        public void ExpressionKind_ClassifiesAsExpected(string json, ExpressionKind expected)
        {
            Assert.AreEqual(expected, Kind(json), json);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ColorCoercionTests — a CSS color string coerces to a color at every color seam
    // ───────────────────────────────────────────────────────────────────────────────────

    // Real styles (OpenFreeMap "liberty") use CSS color STRINGS as branch/stop literals. A constant string, a
    // step over string stops and an interpolate over string stops must all yield colors.
    [TestFixture]
    public class ColorCoercionTests
    {
        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        private static void AssertColor(Color c, double r, double g, double b, double a = 1.0, double tol = 1e-6)
        {
            Assert.That(c.R, Is.EqualTo(r).Within(tol), "R");
            Assert.That(c.G, Is.EqualTo(g).Within(tol), "G");
            Assert.That(c.B, Is.EqualTo(b).Within(tol), "B");
            Assert.That(c.A, Is.EqualTo(a).Within(tol), "A");
        }

        [Test]
        public void ConstantColorString_CoercesToColor()
        {
            // A bare CSS color string as a constant paint value (parsed as a String literal).
            var prop = ColProp("\"#ff0000\"");
            AssertColor(prop.Evaluate(0.0), 1, 0, 0);
        }

        [Test]
        public void Step_OverColorStringStops_CoercesToColor()
        {
            // step(zoom): black below 10, white at/above 10 — outputs are STRINGS.
            var prop = ColProp("[\"step\",[\"zoom\"],\"#000000\",10,\"#ffffff\"]");
            AssertColor(prop.Evaluate(5.0),  0, 0, 0);
            AssertColor(prop.Evaluate(12.0), 1, 1, 1);
        }

        [Test]
        public void Interpolate_OverColorStringStops_CoercesAndLerps()
        {
            // interpolate(linear, zoom): "#000000"→"#ffffff" across [0,10]; midpoint is mid-grey.
            var prop = ColProp("[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#000000\",10,\"#ffffff\"]");
            AssertColor(prop.Evaluate(0.0),  0, 0, 0);
            AssertColor(prop.Evaluate(10.0), 1, 1, 1);
            AssertColor(prop.Evaluate(5.0),  0.5, 0.5, 0.5);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ExpressionErrorTests — evaluation errors are TryEvaluate=false; parse errors throw
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Error model. Acceptance: coercion / lookup / type failures surface as spec-defined errors
    /// through the evaluation boundary (TryEvaluate returns false), NEVER as an unhandled crash. Parse-time
    /// structural problems throw <see cref="ExpressionParseException"/> from the parser.
    /// </summary>
    [TestFixture]
    public class ExpressionErrorTests
    {
        // EVALUATION errors: TryEvaluate returns false with a message and never throws. Only ops with no
        // per-op `_IsError` test elsewhere are listed here.
        [TestCase("[\"!\", 5]")]                 // ! on a non-boolean
        [TestCase("[\"upcase\", 5]")]            // upcase on a non-string
        public void EvaluationError_ReturnsFalse_NeverThrows(string json)
        {
            // Must not throw an ExpressionEvaluationException out of the boundary.
            bool ok = true;
            string error = null;
            Assert.DoesNotThrow(() =>
            {
                ok = Expr.TryEval(json, out _, out error);
            });
            Assert.IsFalse(ok, $"expected a spec error result for: {json}");
            Assert.IsNotNull(error);
        }

        // Each of these is a PARSE error: thrown from the parser (structurally invalid).
        [TestCase("[\"unknown-op\", 1]")]
        [TestCase("[\"var\", \"x\"]")]            // unbound var
        [TestCase("[\"zoom\"]")]                  // mis-placed zoom
        [TestCase("[\"get\"]")]                   // wrong arity
        [TestCase("[]")]                          // empty array
        [TestCase("[\"step\", 1, \"d\", 5, \"a\", 5, \"b\"]")] // non-ascending stops
        public void ParseError_Throws(string json)
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse(json));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FeatureDataTests — properties/geometry-type/id (Style Spec "Feature data")
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Feature-data category: properties/geometry-type/id (Style Spec "Feature data"). Tested over
    /// synthetic <see cref="DictionaryFeature"/> features. Real decoded features are covered in
    /// <c>MapRenderer.Tests.Mvt.MvtPropertyDecodeTests</c> and <c>MapRenderer.Tests.Filters.PropertyFilterTests</c>.
    /// </summary>
    [TestFixture]
    public class FeatureDataTests
    {
        [TestCase(TileGeometryType.Point, "Point")]
        [TestCase(TileGeometryType.LineString, "LineString")]
        [TestCase(TileGeometryType.Polygon, "Polygon")]
        [TestCase(TileGeometryType.Unknown, "Unknown")]
        public void GeometryType(TileGeometryType geom, string expected)
        {
            var f = Expr.Feature(geom: geom);
            Assert.AreEqual(expected, Expr.Eval("[\"geometry-type\"]", f).AsString());
        }

        [Test]
        public void Id_Present()
        {
            var f = Expr.Feature(hasId: true, id: Value.Number(42));
            Assert.AreEqual(42.0, Expr.Eval("[\"id\"]", f).AsNumber());
        }

        [Test]
        public void Id_Absent_IsNull()
        {
            var f = Expr.Feature(hasId: false);
            Assert.AreEqual(ValueType.Null, Expr.Eval("[\"id\"]", f).Type);
        }

        [Test]
        public void Properties_ReturnsObject_And_GetReadsIt()
        {
            var f = Expr.Feature(Expr.Props(("k", Value.String("v"))));
            Value props = Expr.Eval("[\"properties\"]", f);
            Assert.AreEqual(ValueType.Object, props.Type);
            Assert.IsTrue(props.AsObject().ContainsKey("k"));
            // get via properties: ["get", "k", ["properties"]]
            Assert.AreEqual("v", Expr.Eval("[\"get\", \"k\", [\"properties\"]]", f).AsString());
        }

        [Test]
        public void Get_NoFeature_IsError()
        {
            bool ok = Expr.TryEval("[\"get\", \"x\"]", out _, out _, feature: null);
            Assert.IsFalse(ok, "feature-data with no feature in context must be a spec error, not a crash.");
        }

        [Test]
        public void GeometryType_DrivesMatch()
        {
            var f = Expr.Feature(geom: TileGeometryType.Polygon);
            string e = "[\"match\", [\"geometry-type\"], \"Polygon\", \"fill\", \"Point\", \"point\", \"other\"]";
            Assert.AreEqual("fill", Expr.Eval(e, f).AsString());
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FeatureKeyExpressionCapabilityTests — a constant-key get/has stays on the string path
    // for a non-IIndexedFeature feature
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A constant-key <c>get</c>/<c>has</c> node takes the string path for a feature that is NOT
    /// <see cref="IIndexedFeature"/>-capable (<see cref="GeoJsonFeature"/>, <c>DictionaryFeature</c>). Their
    /// layers never build a <see cref="EvaluationContext.KeyBinding"/>, so the tests pass a nonsense binding
    /// to prove the node's own <c>is IIndexedFeature</c> gate.
    /// </summary>
    [TestFixture]
    public class FeatureKeyExpressionCapabilityTests
    {
        private static GeoJsonFeature MakeGeoJsonFeature(IReadOnlyDictionary<string, Value> properties)
            => new GeoJsonFeature
            {
                Id = Value.Null,
                Properties = properties,
                GeometryType = TileGeometryType.Point,
                Paths = null,
                PolygonRingCounts = null,
            };

        [Test]
        public void GeoJsonFeature_PresentKey_Get_ReturnsValue()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value> { ["name"] = Value.String("Aruba") });
            Value result = ExpressionParser.Parse("[\"get\",\"name\"]").Evaluate(new EvaluationContext(0.0, feature));
            Assert.That(result.AsString(), Is.EqualTo("Aruba"));
        }

        [Test]
        public void GeoJsonFeature_AbsentKey_Get_ReturnsNull()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value>());
            Value result = ExpressionParser.Parse("[\"get\",\"name\"]").Evaluate(new EvaluationContext(0.0, feature));
            Assert.That(result.Type, Is.EqualTo(ValueType.Null));
        }

        [Test]
        public void GeoJsonFeature_Has_PresentAndAbsent()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value> { ["name"] = Value.String("Aruba") });
            Assert.That(
                ExpressionParser.Parse("[\"has\",\"name\"]").Evaluate(new EvaluationContext(0.0, feature)).AsBool(),
                Is.True);
            Assert.That(
                ExpressionParser.Parse("[\"has\",\"missing\"]").Evaluate(new EvaluationContext(0.0, feature)).AsBool(),
                Is.False);
        }

        /// <summary>
        /// RED-verify target: drop <c>FeatureKeyExpression.Evaluate</c>'s <c>is IIndexedFeature</c> gate
        /// (take the int path whenever <see cref="EvaluationContext.KeyBinding"/> is non-null, ignoring
        /// feature capability) and this reds — <see cref="GeoJsonFeature"/> is not
        /// <see cref="IIndexedFeature"/>, so the int branch has nothing to call.
        /// </summary>
        [Test]
        public void GeoJsonFeature_EvenWithANonNullBinding_StaysOnTheStringPath()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value> { ["name"] = Value.String("Aruba") });
            Assert.That(feature, Is.Not.InstanceOf<IIndexedFeature>(),
                "precondition: GeoJsonFeature must not be index-capable, or this tooth proves nothing");

            // A nonsense binding (slot 0 -> key 999), so a wrongly taken int path misbehaves visibly instead of
            // answering right by chance.
            var nonsenseBinding = new[] { 999 };
            Value result = ExpressionParser.Parse("[\"get\",\"name\"]")
                .Evaluate(new EvaluationContext(0.0, feature, nonsenseBinding));

            Assert.That(result.AsString(), Is.EqualTo("Aruba"),
                "the string path must still answer correctly even when (contrary to production) a binding is present");
        }

        [Test]
        public void DictionaryFeature_IsNotIndexCapable()
        {
            Assert.That(new DictionaryFeature(), Is.Not.InstanceOf<IIndexedFeature>(),
                "production never builds a binding for a DictionaryFeature-backed source");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FeatureKeyExpressionTests — the int-keyed path fires only with a binding + an indexed feature
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A constant-key <c>get</c>/<c>has</c> node takes the int-keyed <see cref="IIndexedFeature"/> path when
    /// it can, and <see cref="IFeature.TryGetProperty"/> only when it can't. A call-counting double answers
    /// the same value on both paths, so only the call counts catch a node that always takes the string path.
    /// </summary>
    [TestFixture]
    public class FeatureKeyExpressionTests
    {
        /// <summary>Parses a JSON expression string, also yielding its key layout — the
        /// <see cref="ExpressionParser"/> layout-surfacing overload only takes a <see cref="JsonValue"/>.</summary>
        private static Expression ParseWithLayout(string json, out IReadOnlyList<string> keyLayout)
            => ExpressionParser.Parse(JsonParser.Parse(json), out keyLayout);

        /// <summary>An <see cref="IFeature"/> that also implements <see cref="IIndexedFeature"/>, counting
        /// which method actually fired.</summary>
        private sealed class RecordingIndexedFeature : IFeature, IIndexedFeature
        {
            public int ByNameCalls { get; private set; }
            public int ByIndexCalls { get; private set; }

            public TileGeometryType GeometryType => TileGeometryType.Unknown;
            public Value Id => Value.Null;
            public IReadOnlyDictionary<string, Value> Properties => EmptyProperties;
            private static readonly Dictionary<string, Value> EmptyProperties = new Dictionary<string, Value>();

            public bool TryGetProperty(string name, out Value value)
            {
                ByNameCalls++;
                value = Value.String("by-name:" + name);
                return true;
            }

            public bool TryGetPropertyByKeyIndex(int keyIndex, out Value value)
            {
                ByIndexCalls++;
                value = Value.String("by-index:" + keyIndex);
                return true;
            }
        }

        [Test]
        public void Evaluate_WithBindingAndIndexedFeature_TakesTheIntPath_NotTheStringPath()
        {
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"get\",\"k\"]", out var layout);
            Assert.That(layout, Has.Count.EqualTo(1), "precondition: one constant-key node -> one layout slot");

            var binding = new[] { 7 }; // slot 0 -> key index 7 (the value a bind site would have resolved)
            var ctx = new EvaluationContext(0.0, feature, binding);
            Value result = expr.Evaluate(ctx);

            Assert.That(feature.ByIndexCalls, Is.EqualTo(1),
                "TryGetPropertyByKeyIndex must fire when a binding and an IIndexedFeature are both present");
            Assert.That(feature.ByNameCalls, Is.EqualTo(0),
                "TryGetProperty(string) must NOT fire when the int path is taken");
            Assert.That(result.AsString(), Is.EqualTo("by-index:7"));
        }

        [Test]
        public void Evaluate_WithNullBinding_TakesTheStringPath_NotTheIntPath()
        {
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"get\",\"k\"]", out _);
            var ctx = new EvaluationContext(0.0, feature, keyBinding: null);
            Value result = expr.Evaluate(ctx);

            Assert.That(feature.ByNameCalls, Is.EqualTo(1),
                "TryGetProperty(string) must fire when no binding is supplied, even for an index-capable feature");
            Assert.That(feature.ByIndexCalls, Is.EqualTo(0),
                "TryGetPropertyByKeyIndex must NOT fire without a binding");
            Assert.That(result.AsString(), Is.EqualTo("by-name:k"));
        }

        [Test]
        public void Has_WithBinding_TakesTheIntPath()
        {
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"has\",\"k\"]", out var layout);
            var binding = new[] { 3 };
            var ctx = new EvaluationContext(0.0, feature, binding);
            Value result = expr.Evaluate(ctx);

            Assert.That(layout, Has.Count.EqualTo(1));
            Assert.That(feature.ByIndexCalls, Is.EqualTo(1));
            Assert.That(feature.ByNameCalls, Is.EqualTo(0));
            Assert.That(result.AsBool(), Is.True);
        }

        [Test]
        public void Evaluate_NegativeBoundIndex_MeansAbsent_AndNeverCallsTheStore()
        {
            // -1 is the bind site's "layer has no such key" sentinel (mirrors TryResolveKey returning
            // false) — the node must short-circuit to absent without ever calling TryGetPropertyByKeyIndex.
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"get\",\"k\"]", out _);
            var ctx = new EvaluationContext(0.0, feature, new[] { -1 });
            Value result = expr.Evaluate(ctx);

            Assert.That(feature.ByIndexCalls, Is.EqualTo(0));
            Assert.That(feature.ByNameCalls, Is.EqualTo(0));
            Assert.That(result.Type, Is.EqualTo(ValueType.Null));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LiteralTypeTests — literal values, typeof, and the to-* coercions
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Literal / type category: literal values, typeof, and the to-* coercions, per the Style Spec
    /// "Types" section.
    /// </summary>
    [TestFixture]
    public class LiteralTypeTests
    {
        [Test]
        public void Literal_Number_PassesThrough()
        {
            Value v = Expr.Eval("[\"literal\", 5]");
            Assert.AreEqual(ValueType.Number, v.Type);
            Assert.AreEqual(5.0, v.AsNumber());
        }

        [Test]
        public void Literal_Array_IsArrayValue()
        {
            Value v = Expr.Eval("[\"literal\", [1, 2, 3]]");
            Assert.AreEqual(ValueType.Array, v.Type);
            Assert.AreEqual(3, v.AsArray().Count);
            Assert.AreEqual(2.0, v.AsArray()[1].AsNumber());
        }

        [Test]
        public void BareLiteralsParse()
        {
            Assert.AreEqual(true, Expr.Eval("true").AsBool());
            Assert.AreEqual("hi", Expr.Eval("\"hi\"").AsString());
            Assert.AreEqual(ValueType.Null, Expr.Eval("null").Type);
        }

        [TestCase("5", "number")]
        [TestCase("\"x\"", "string")]
        [TestCase("true", "boolean")]
        [TestCase("null", "null")]
        [TestCase("[\"to-color\", \"#ff0000\"]", "color")]
        [TestCase("[\"literal\", [1, 2]]", "array")]
        public void TypeOf_ReturnsSpecName(string json, string expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"typeof\", {json}]").AsString());
        }

        // ---- to-number -----------------------------------------------------------------------------

        [Test]
        [TestCase("\"3.5\"", 3.5, TestName = "ToNumber_CoercesInput(String)")]
        [TestCase("true", 1.0, TestName = "ToNumber_CoercesInput(TrueIsOne)")]
        [TestCase("false", 0.0, TestName = "ToNumber_CoercesInput(FalseIsZero)")]
        [TestCase("null", 0.0, TestName = "ToNumber_CoercesInput(NullIsZero)")]
        // First arg fails ("x"), second succeeds ("7") — multi-arg first-success form.
        [TestCase("\"x\", \"7\"", 7.0, TestName = "ToNumber_CoercesInput(MultiArg_FirstSuccess)")]
        // Spec: a string converts via ECMAScript ToNumber; ToNumber("") and all-whitespace are 0, NOT an error.
        [TestCase("\"\"", 0.0, TestName = "ToNumber_CoercesInput(EmptyString)")]
        [TestCase("\"   \"", 0.0, TestName = "ToNumber_CoercesInput(WhitespaceString)")]
        public void ToNumber_CoercesInput(string args, double expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"to-number\", {args}]").AsNumber());
        }

        [Test]
        public void ToNumber_NonNumericString_IsError()
        {
            bool ok = Expr.TryEval("[\"to-number\", \"x\"]", out _, out string error);
            Assert.IsFalse(ok, "to-number of a non-numeric string must be a spec error, not a crash.");
            Assert.IsNotNull(error);
        }

        // ---- to-boolean ----------------------------------------------------------------------------

        [TestCase("0", false)]
        [TestCase("2", true)]
        [TestCase("\"\"", false)]
        [TestCase("\"a\"", true)]
        [TestCase("null", false)]
        [TestCase("false", false)]
        [TestCase("true", true)]
        public void ToBoolean(string json, bool expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"to-boolean\", {json}]").AsBool());
        }

        // ---- to-string -----------------------------------------------------------------------------

        [Test]
        [TestCase("null", "", TestName = "ToString_ConvertsInput(Null_IsEmpty)")]
        [TestCase("true", "true", TestName = "ToString_ConvertsInput(Bool)")]
        [TestCase("5", "5", TestName = "ToString_ConvertsInput(IntegerNumber_NoTrailingPointZero)")]
        [TestCase("3.5", "3.5", TestName = "ToString_ConvertsInput(FractionalNumber)")]
        [TestCase("[\"to-color\", \"#ff0000\"]", "rgba(255,0,0,1)", TestName = "ToString_ConvertsInput(Color_IsRgba)")]
        public void ToString_ConvertsInput(string arg, string expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"to-string\", {arg}]").AsString());
        }

        // ---- to-color / to-rgba round-trip ---------------------------------------------------------

        [Test]
        public void ToColor_Hex_RoundTrips_Via_ToRgba()
        {
            Value rgba = Expr.Eval("[\"to-rgba\", [\"to-color\", \"#ff0000\"]]");
            var arr = rgba.AsArray();
            Assert.AreEqual(255.0, arr[0].AsNumber(), 1e-9);
            Assert.AreEqual(0.0, arr[1].AsNumber(), 1e-9);
            Assert.AreEqual(0.0, arr[2].AsNumber(), 1e-9);
            Assert.AreEqual(1.0, arr[3].AsNumber(), 1e-9);
        }

        [Test]
        public void ToColor_NonColorString_IsError()
        {
            bool ok = Expr.TryEval("[\"to-color\", \"not a color\"]", out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void ToRgba_OnNonColor_IsError()
        {
            bool ok = Expr.TryEval("[\"to-rgba\", 5]", out _, out _);
            Assert.IsFalse(ok);
        }
    }
}
