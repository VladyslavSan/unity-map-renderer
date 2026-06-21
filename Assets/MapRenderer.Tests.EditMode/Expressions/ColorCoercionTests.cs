// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Regression for the "Expected color but found string" crash: production styles (OpenFreeMap "liberty")
// emit color expressions whose branch/stop literals are CSS color STRINGS, not pre-parsed colors. The
// spec's to-color coercion parses strings in color context; we apply it at every color seam
// (PaintPropertyEvaluator constant + zoom, interpolate/Ramps, DataDrivenPaintEvaluator). These tests pin
// that a constant string, a step over string stops, and an interpolate over string stops all yield colors.

using NUnit.Framework;
using MapRenderer.Core.Style;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    [TestFixture]
    public class ColorCoercionTests
    {
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
            var ev = new PaintPropertyEvaluator("\"#ff0000\"");
            AssertColor(ev.EvaluateColor(0.0), 1, 0, 0);
        }

        [Test]
        public void Step_OverColorStringStops_CoercesToColor()
        {
            // step(zoom): black below 10, white at/above 10 — outputs are STRINGS.
            var ev = new PaintPropertyEvaluator("[\"step\",[\"zoom\"],\"#000000\",10,\"#ffffff\"]");
            AssertColor(ev.EvaluateColor(5.0),  0, 0, 0);
            AssertColor(ev.EvaluateColor(12.0), 1, 1, 1);
        }

        [Test]
        public void Interpolate_OverColorStringStops_CoercesAndLerps()
        {
            // interpolate(linear, zoom): "#000000"→"#ffffff" across [0,10]; midpoint is mid-grey.
            var ev = new PaintPropertyEvaluator("[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#000000\",10,\"#ffffff\"]");
            AssertColor(ev.EvaluateColor(0.0),  0, 0, 0);
            AssertColor(ev.EvaluateColor(10.0), 1, 1, 1);
            AssertColor(ev.EvaluateColor(5.0),  0.5, 0.5, 0.5);
        }
    }
}
