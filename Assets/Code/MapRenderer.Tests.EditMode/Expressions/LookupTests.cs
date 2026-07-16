// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Expressions
{
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
