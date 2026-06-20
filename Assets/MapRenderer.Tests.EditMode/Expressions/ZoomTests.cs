// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
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
