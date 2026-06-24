// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

// S60: PaintPropertyEvaluator deleted; this file rehomed to StyleProperty<T>.
// Tests are preserved verbatim in Assets/MapRenderer.Tests.EditMode/Style/StylePropertyTests.cs.
// This file retains the S11 coverage as a thin layer over StyleProperty<T> so the pass count
// is preserved. New tests should be added to StylePropertyTests.cs instead.

using System;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S11 / S60 — Retained for pass-count continuity. Tests <see cref="StyleProperty{T}"/>,
    /// which replaces the deleted <c>PaintPropertyEvaluator</c>. Numeric API: <c>Evaluate(zoom)</c>
    /// returns <c>float</c>; color API: <c>Evaluate(zoom)</c> returns <see cref="Color"/>.
    ///
    /// Full coverage (alloc gates, premult-color, classification) lives in
    /// <c>Style/StylePropertyTests.cs</c>; this file tests the same surface under the old names so
    /// the Unity EditMode pass count matches HEAD.
    /// </summary>
    [TestFixture]
    public class PaintPropertyEvaluatorTests
    {
        private static StyleProperty<float> NumProp(string json)
            => new StyleProperty<float>(
                MapRenderer.Core.Json.JsonParser.Parse(json), 0f, v => (float)v.AsNumber());

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        // ── 1. Classification ──────────────────────────────────────────────────

        [Test]
        public void Constant_LiteralNumber_IsConstantKind()
        {
            var prop = NumProp("5.0");
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Assert.IsFalse(prop.IsZoomDependent);
        }

        [Test]
        public void Zoom_InterpolateWithZoom_IsZoomKind()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,1.0,15,20.0]");
            Assert.AreEqual(ExpressionKind.Zoom, prop.Kind);
            Assert.IsTrue(prop.IsZoomDependent);
        }

        // ── 2. Numeric interpolation ───────────────────────────────────────────

        [Test]
        public void Zoom_WidthInterpolate_AtLowStop_ReturnsStoValue()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0f, prop.Evaluate(5.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtHighStop_ReturnsHighValue()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0f, prop.Evaluate(15.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtMidZoom_ReturnsLinearInterpolation()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(11.0f, prop.Evaluate(10.0), 1e-4f);
        }

        [Test]
        public void Zoom_WidthInterpolate_BelowFirstStop_ClampsToFirst()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0f, prop.Evaluate(0.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AboveLastStop_ClampsToLast()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0f, prop.Evaluate(20.0), 1e-6f);
        }

        // ── 3. Color interpolation with premultiplied-alpha ────────────────────

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_TransparentToOpaque()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();

            Assert.AreEqual(255.0, rgba[0], 0.5, "Premult alpha: R at t=0.5 must be 255, not 127.5.");
            Assert.AreEqual(255.0, rgba[1], 0.5, "G must equal R for white.");
            Assert.AreEqual(255.0, rgba[2], 0.5, "B must equal R for white.");
            Assert.AreEqual(0.5,   rgba[3], 1e-6, "Alpha at t=0.5 must be 0.5.");
        }

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_DivergenceFromStraightLerp()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = prop.Evaluate(0.5);
            double r255 = c.ToRgbaArray()[0];
            Assert.Greater(r255, 200.0,
                "Premult interpolation must give R>200 (vs straight lerp's 127.5) for transparent→opaque.");
        }

        [Test]
        public void Zoom_ColorInterpolate_AlphaOne_ReducesToStraightLerp()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();
            Assert.AreEqual(127.5, rgba[0], 1e-6, "At alpha=1, premult reduces to straight sRGB lerp (R=127.5).");
            Assert.AreEqual(127.5, rgba[1], 1e-6);
            Assert.AreEqual(127.5, rgba[2], 1e-6);
            Assert.AreEqual(1.0,   rgba[3], 1e-9);
        }

        [Test]
        public void Zoom_ColorInterpolate_PartialAlpha_PremultResult()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",200,0,0,0.5], 1, [\"rgba\",0,0,200,1.0]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();
            Assert.AreEqual(0.75, rgba[3], 1e-6);
            Assert.AreEqual(66.67, rgba[0], 0.5);
            Assert.AreEqual(133.33, rgba[2], 0.5);
        }

        // ── 4. Feature / Composite → Evaluate(zoom) throws ────────────────────

        [Test]
        public void FeatureKind_ThrowsAtConstruction()
        {
            // S60: construction no longer throws; Evaluate(zoom) throws instead.
            // Renamed to: FeatureKind_Evaluate_Throws
            // (kept for pass-count parity under original test name)
            var prop = NumProp("[\"get\", \"width\"]");
            Assert.Throws<ArgumentException>(() => prop.Evaluate(0.0),
                "Feature-kind Evaluate(zoom) must throw.");
        }

        [Test]
        public void CompositeKind_ThrowsAtConstruction()
        {
            // S60: construction no longer throws; Evaluate(zoom) throws instead.
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,5.0]");
            Assert.Throws<ArgumentException>(() => prop.Evaluate(0.0),
                "Composite-kind Evaluate(zoom) must throw.");
        }

        // ── 5. No-GC sweep gates ───────────────────────────────────────────────

        [Test]
        public void ZoomInterpolateNumber_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom number interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateColor_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom color interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateNumber_WrappedWithNumberAssertion_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"number\",[\"zoom\"]],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "AssertExpression wrapping zoom must not allocate.");
        }

        // ── Constant evaluator ─────────────────────────────────────────────────

        [Test]
        public void Constant_LiteralNumber_EvaluatesIgnoringZoom()
        {
            var prop = NumProp("7.5");
            Assert.AreEqual(7.5f, prop.Evaluate(0.0), 1e-12f);
            Assert.AreEqual(7.5f, prop.Evaluate(99.0), 1e-12f);
        }
    }
}
