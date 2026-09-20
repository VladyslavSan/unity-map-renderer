// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using ExprValueType = MapRenderer.Core.Expressions.ValueType;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S11 — type-assertion operators (spec "Types / Assertion"):
    /// <c>boolean</c> / <c>number</c> / <c>string</c> / <c>object</c> / <c>array</c>.
    ///
    /// These are distinct from the <c>to-*</c> coercions:
    ///   - Assert: return the input if the type matches; error if it does not.
    ///   - Coerce (<c>to-number</c>, <c>to-string</c>, …): convert the value.
    ///
    /// Also verifies that <c>["number",["zoom"]]</c> parses and classifies as Zoom-kind — the key
    /// "legal wrapped zoom" form used by the per-frame interpolate path.
    ///
    /// Allocation test (no-GC sweep) is in <see cref="MapRenderer.Tests.Style.StylePropertyTests"/>
    /// which covers the full zoom-evaluation path including these assertion wrappers.
    /// </summary>
    [TestFixture]
    public class AssertionTests
    {
        // ── boolean ─────────────────────────────────────────────────────────────

        [Test]
        public void Boolean_TrueInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"boolean\", true]").Evaluate(default);
            Assert.AreEqual(ExprValueType.Boolean, v.Type);
            Assert.IsTrue(v.AsBool());
        }

        [Test]
        public void Boolean_FalseInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"boolean\", false]").Evaluate(default);
            Assert.AreEqual(ExprValueType.Boolean, v.Type);
            Assert.IsFalse(v.AsBool());
        }

        [Test]
        public void Boolean_NumberInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"boolean\", 42]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Boolean_MultiArg_FirstMatchWins()
        {
            // Multi-arg first-match form: second arg is boolean.
            var expr = ExpressionParser.Parse("[\"boolean\", 42, true]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Boolean, v.Type);
            Assert.IsTrue(v.AsBool());
        }

        [Test]
        public void Boolean_MultiArg_NoneMatch_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"boolean\", 1, 2, 3]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── number ──────────────────────────────────────────────────────────────

        [Test]
        public void Number_NumberInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"number\", 42]").Evaluate(default);
            Assert.AreEqual(ExprValueType.Number, v.Type);
            Assert.AreEqual(42.0, v.AsNumber(), 1e-15);
        }

        [Test]
        public void Number_StringInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"number\", \"hello\"]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Number_MultiArg_FirstMatchWins()
        {
            var expr = ExpressionParser.Parse("[\"number\", \"hello\", 7]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(7.0, v.AsNumber(), 1e-15);
        }

        // ── string ──────────────────────────────────────────────────────────────

        [Test]
        public void String_StringInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"string\", \"hi\"]").Evaluate(default);
            Assert.AreEqual(ExprValueType.String, v.Type);
            Assert.AreEqual("hi", v.AsString());
        }

        [Test]
        public void String_NumberInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"string\", 99]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── object ──────────────────────────────────────────────────────────────

        [Test]
        public void Object_ObjectInput_ReturnsSame()
        {
            var expr = ExpressionParser.Parse("[\"object\", {\"k\": 1}]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Object, v.Type);
        }

        [Test]
        public void Object_StringInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"object\", \"x\"]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── array ───────────────────────────────────────────────────────────────

        [Test]
        public void Array_AnyElementType_AnyLength_Passes()
        {
            // ["array", v] — any element type, any length.
            var expr = ExpressionParser.Parse("[\"array\", [\"literal\", [1, 2, 3]]]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
            Assert.AreEqual(3, v.AsArray().Count);
        }

        [Test]
        public void Array_AnyElementType_NonArrayInput_Throws()
        {
            var expr = ExpressionParser.Parse("[\"array\", 42]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Array_NumberElementType_AllNumbers_Passes()
        {
            var expr = ExpressionParser.Parse("[\"array\", \"number\", [\"literal\", [1, 2, 3]]]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
        }

        [Test]
        public void Array_NumberElementType_MixedElements_Throws()
        {
            // Evaluates ["array","number", literal [1, "x"]] — second element is string → error.
            var expr = ExpressionParser.Parse("[\"array\", \"number\", [\"literal\", [1, \"x\"]]]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Array_NumberType_ExactLength_Passes()
        {
            var expr = ExpressionParser.Parse("[\"array\", \"number\", 2, [\"literal\", [10, 20]]]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
            Assert.AreEqual(2, v.AsArray().Count);
        }

        [Test]
        public void Array_ExactLength_WrongLength_Throws()
        {
            var expr = ExpressionParser.Parse("[\"array\", \"number\", 3, [\"literal\", [10, 20]]]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── zoom wrapping ────────────────────────────────────────────────────────
        // ["number",["zoom"]] must parse (zoom is legal when inside a ramp input) and classify Zoom.

        [Test]
        public void Number_WrappingZoom_ParsesAsZoomKind()
        {
            // The assertion wraps zoom inside a ramp input slot — valid only with zoomAllowed=true.
            // ParseInputAllowingZoom calls ParseNode with zoomAllowed:true; the "number" assertion
            // must thread that flag through to the inner zoom arg.
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
}
