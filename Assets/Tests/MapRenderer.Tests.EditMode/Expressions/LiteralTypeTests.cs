// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S09 — literal / type category: literal values, typeof, and the to-* coercions, per the Style Spec
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
        public void TypeOf_Scalars(string json, string expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"typeof\", {json}]").AsString());
        }

        [Test]
        public void TypeOf_Color()
        {
            Assert.AreEqual("color", Expr.Eval("[\"typeof\", [\"to-color\", \"#ff0000\"]]").AsString());
        }

        [Test]
        public void TypeOf_Array()
        {
            Assert.AreEqual("array", Expr.Eval("[\"typeof\", [\"literal\", [1, 2]]]").AsString());
        }

        // ---- to-number -----------------------------------------------------------------------------

        [Test]
        public void ToNumber_String() => Assert.AreEqual(3.5, Expr.Eval("[\"to-number\", \"3.5\"]").AsNumber());

        [Test]
        public void ToNumber_TrueIsOne() => Assert.AreEqual(1.0, Expr.Eval("[\"to-number\", true]").AsNumber());

        [Test]
        public void ToNumber_FalseAndNullAreZero()
        {
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", false]").AsNumber());
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", null]").AsNumber());
        }

        [Test]
        public void ToNumber_NonNumericString_IsError()
        {
            bool ok = Expr.TryEval("[\"to-number\", \"x\"]", out _, out string error);
            Assert.IsFalse(ok, "to-number of a non-numeric string must be a spec error, not a crash.");
            Assert.IsNotNull(error);
        }

        [Test]
        public void ToNumber_MultiArg_FirstSuccess()
        {
            // First arg fails ("x"), second succeeds ("7").
            Assert.AreEqual(7.0, Expr.Eval("[\"to-number\", \"x\", \"7\"]").AsNumber());
        }

        [Test]
        public void ToNumber_EmptyAndWhitespaceString_IsZero()
        {
            // Spec: a string converts via ECMAScript ToNumber; ToNumber("") and all-whitespace are 0,
            // NOT an error.
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", \"\"]").AsNumber());
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", \"   \"]").AsNumber());
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
        public void ToString_Null_IsEmpty() => Assert.AreEqual("", Expr.Eval("[\"to-string\", null]").AsString());

        [Test]
        public void ToString_Bool() => Assert.AreEqual("true", Expr.Eval("[\"to-string\", true]").AsString());

        [Test]
        public void ToString_IntegerNumber_NoTrailingPointZero()
            => Assert.AreEqual("5", Expr.Eval("[\"to-string\", 5]").AsString());

        [Test]
        public void ToString_FractionalNumber()
            => Assert.AreEqual("3.5", Expr.Eval("[\"to-string\", 3.5]").AsString());

        [Test]
        public void ToString_Color_IsRgba()
        {
            string s = Expr.Eval("[\"to-string\", [\"to-color\", \"#ff0000\"]]").AsString();
            Assert.AreEqual("rgba(255,0,0,1)", s);
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
