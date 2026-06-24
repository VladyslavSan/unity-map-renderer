// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Regression for the "Expected color but found string" crash: production styles (OpenFreeMap "liberty")
// emit color expressions whose branch/stop literals are CSS color STRINGS, not pre-parsed colors. The
// spec's to-color coercion parses strings in color context; we apply it at every color seam
// (StyleProperty<Color> constant + zoom, interpolate/Ramps). These tests pin
// that a constant string, a step over string stops, and an interpolate over string stops all yield colors.

using NUnit.Framework;
using MapRenderer.Core.Style;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
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
}
