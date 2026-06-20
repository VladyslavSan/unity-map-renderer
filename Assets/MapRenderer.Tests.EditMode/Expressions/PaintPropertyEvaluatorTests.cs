// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S11 — <see cref="PaintPropertyEvaluator"/>: parse, classify, evaluate, and GC-allocation gate.
    ///
    /// Covers:
    ///   1. Constant vs Zoom classification.
    ///   2. Numeric interpolation sampled at stop zooms and a mid-zoom (spec-derived math).
    ///   3. Color interpolation with premultiplied-alpha over zoom.
    ///   4. Feature / Composite input → constructor throws (deferred to S12).
    ///   5. No-GC sweep gate (Core-side): evaluating a zoom sweep in a tight loop allocates 0 bytes
    ///      (measured via <see cref="GC.GetAllocatedBytesForCurrentThread"/> delta). This is the
    ///      discriminating test — a rest-state (constant zoom) gate would be toothless.
    /// </summary>
    [TestFixture]
    public class PaintPropertyEvaluatorTests
    {
        // ── 1. Classification ─────────────────────────────────────────────────

        [Test]
        public void Constant_LiteralNumber_IsConstantKind()
        {
            var ev = new PaintPropertyEvaluator("5.0");
            Assert.AreEqual(ExpressionKind.Constant, ev.Kind);
            Assert.IsFalse(ev.IsZoomDependent);
        }

        [Test]
        public void Zoom_InterpolateWithZoom_IsZoomKind()
        {
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,1.0,15,20.0]");
            Assert.AreEqual(ExpressionKind.Zoom, ev.Kind);
            Assert.IsTrue(ev.IsZoomDependent);
        }

        // ── 2. Numeric interpolation (hand-derived spec math) ─────────────────

        [Test]
        public void Zoom_WidthInterpolate_AtLowStop_ReturnsStoValue()
        {
            // ["interpolate",["linear"],["zoom"], 5, 2.0, 15, 20.0]
            // At zoom=5, exactly at lower stop → returns 2.0.
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0, ev.EvaluateNumber(5.0), 1e-9);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtHighStop_ReturnsHighValue()
        {
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0, ev.EvaluateNumber(15.0), 1e-9);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtMidZoom_ReturnsLinearInterpolation()
        {
            // At zoom=10, t = (10-5)/(15-5) = 0.5, value = 2.0 + 0.5*(20.0-2.0) = 11.0
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(11.0, ev.EvaluateNumber(10.0), 1e-9);
        }

        [Test]
        public void Zoom_WidthInterpolate_BelowFirstStop_ClampsToFirst()
        {
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0, ev.EvaluateNumber(0.0), 1e-9);
        }

        [Test]
        public void Zoom_WidthInterpolate_AboveLastStop_ClampsToLast()
        {
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0, ev.EvaluateNumber(20.0), 1e-9);
        }

        // ── 3. Color interpolation with premultiplied-alpha ───────────────────
        //
        // Canonical premult case: rgba(0,0,0,0) → rgba(255,255,255,1) at t=0.5.
        //
        // Premult math (first-principles):
        //   a = (0,0,0,0),  b = (1,1,1,1)  [normalized to [0,1]]
        //   premult_a = (0,0,0,0),  premult_b = (1,1,1,1)
        //   lerp at t=0.5: (0.5, 0.5, 0.5, 0.5)
        //   unpremult:     /0.5 → (1.0, 1.0, 1.0, 0.5)
        //   → R=255, A=0.5
        //
        // Regression: at alpha=1 premult is identity (R*1=R), unpremult /1 is no-op → same as
        // straight lerp.  So black→white at alpha=1 still gives R=127.5.

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_TransparentToOpaque()
        {
            // rgba(0,0,0,0) → rgba(255,255,255,1) at t=0.5 (zoom=0.5 between stops 0 and 1).
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = ev.EvaluateColor(0.5);
            double[] rgba = c.ToRgbaArray(); // [r*255, g*255, b*255, a]

            // Premultiplied-alpha: R should be 255 (not 127.5 of straight lerp).
            Assert.AreEqual(255.0, rgba[0], 0.5, "Premult alpha: R at t=0.5 must be 255, not 127.5.");
            Assert.AreEqual(255.0, rgba[1], 0.5, "G must equal R for white.");
            Assert.AreEqual(255.0, rgba[2], 0.5, "B must equal R for white.");
            Assert.AreEqual(0.5,   rgba[3], 1e-6, "Alpha at t=0.5 must be 0.5.");
        }

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_DivergenceFromStraightLerp()
        {
            // This test proves the change is discriminating: straight lerp gives R=127.5, premult gives R=255.
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = ev.EvaluateColor(0.5);
            double r255 = c.ToRgbaArray()[0];

            // Discriminating: straight lerp would give 127.5, premult gives 255.
            Assert.Greater(r255, 200.0,
                "Premult interpolation must give R>200 (vs straight lerp's 127.5) for transparent→opaque.");
        }

        [Test]
        public void Zoom_ColorInterpolate_AlphaOne_ReducesToStraightLerp()
        {
            // Regression pin: at alpha=1, premult is identity → same as straight sRGB lerp → R=127.5.
            // This is the EXACT value from the S09 RampCurveTests.Interpolate_Color_DefaultSpace_IsComponentwiseSrgb.
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]");

            Color c = ev.EvaluateColor(0.5); // zoom=0.5, t=(0.5-0)/(1-0)=0.5
            double[] rgba = c.ToRgbaArray();
            Assert.AreEqual(127.5, rgba[0], 1e-6,
                "At alpha=1, premult reduces to straight sRGB lerp (R=127.5 preserved).");
            Assert.AreEqual(127.5, rgba[1], 1e-6);
            Assert.AreEqual(127.5, rgba[2], 1e-6);
            Assert.AreEqual(1.0,   rgba[3], 1e-9);
        }

        [Test]
        public void Zoom_ColorInterpolate_PartialAlpha_PremultResult()
        {
            // rgba(200, 0, 0, 0.5) → rgba(0, 0, 200, 1.0) at t=0.5.
            // Normalized: a=(200/255, 0, 0, 0.5), b=(0, 0, 200/255, 1.0)
            // premult a = (100/255, 0, 0, 0.5), premult b = (0, 0, 200/255, 1.0)
            // lerp at t=0.5: ((50/255), 0, (100/255), 0.75)
            // unpremult /0.75: R=50/(255*0.75)=50/191.25≈0.2614, B=100/(255*0.75)=100/191.25≈0.5229
            // → r255 ≈ 66.7, b255 ≈ 133.3
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",200,0,0,0.5], 1, [\"rgba\",0,0,200,1.0]]");

            Color c = ev.EvaluateColor(0.5);
            double[] rgba = c.ToRgbaArray();

            Assert.AreEqual(0.75, rgba[3], 1e-6, "Alpha must be linear lerp of 0.5 and 1.0 at t=0.5.");
            // R: premult lerp / unpremult = (200*0.5/2 + 0)/0.75 / 255 * 255 = 50/0.75 ≈ 66.67
            Assert.AreEqual(66.67, rgba[0], 0.5, "R channel via premult lerp.");
            // B: premult lerp / unpremult = (0 + 200/2)/0.75 = 100/0.75 ≈ 133.33
            Assert.AreEqual(133.33, rgba[2], 0.5, "B channel via premult lerp.");
        }

        // ── 4. Feature / Composite → constructor throws ───────────────────────

        [Test]
        public void FeatureKind_ThrowsAtConstruction()
        {
            // ["get","width"] is Feature-kind — rejected at construction.
            Assert.Throws<System.ArgumentException>(
                () => new PaintPropertyEvaluator("[\"get\", \"width\"]"),
                "Feature-kind expressions must throw at PaintPropertyEvaluator construction.");
        }

        [Test]
        public void CompositeKind_ThrowsAtConstruction()
        {
            // ["interpolate",["linear"],["zoom"], 5, ["get","w"], 10, 5.0] is Composite-kind.
            Assert.Throws<System.ArgumentException>(
                () => new PaintPropertyEvaluator(
                    "[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,5.0]"),
                "Composite-kind expressions must throw at PaintPropertyEvaluator construction.");
        }

        // ── 5. No-GC sweep gate (Core-side, fast) ─────────────────────────────
        //
        // Key property: the zoom is SWEPT across different values each iteration, not held constant.
        // A rest-state (constant zoom) test would be toothless because the branch predictor / JIT
        // could eliminate redundant work.  The sweep forces the full evaluation path every call:
        //   ZoomExpression → AssertExpression (if wrapped) → InterpolateExpression → LiteralExpression.

        [Test]
        public void ZoomInterpolateNumber_SweepingZoom_AllocatesZeroBytes()
        {
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");

            // Warm up: JIT-compile the hot path before measuring.
            for (int w = 0; w < 50; w++)
                ev.EvaluateNumber(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                // Sweep zoom between 5 and 15 (100 distinct values) to keep the evaluator path live.
                double zoom = 5.0 + (i % 100) * 0.1;
                ev.EvaluateNumber(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom number interpolation must not allocate any GC heap memory per call. " +
                "A non-zero delta means a Value[], delegate, or boxing escaped the hot path. " +
                "Ensure stop outputs are Constant-folded to LiteralExpression at parse time.");
        }

        [Test]
        public void ZoomInterpolateColor_SweepingZoom_AllocatesZeroBytes()
        {
            // Color sweep: proves the constant-fold of rgb() stop outputs removes the Value[] alloc.
            // Without constant-folding, rgb() is a FunctionExpression that allocates new Value[3] per call.
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]");

            // Warm up.
            for (int w = 0; w < 50; w++)
                ev.EvaluateColor(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                ev.EvaluateColor(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom color interpolation must not allocate any GC heap memory per call. " +
                "A non-zero delta proves a FunctionExpression (rgb/rgba stop) was NOT constant-folded. " +
                "Ensure FoldConstant is applied to stop outputs in ExpressionParser.");
        }

        [Test]
        public void ZoomInterpolateNumber_WrappedWithNumberAssertion_AllocatesZeroBytes()
        {
            // ["number",["zoom"]] as the ramp input: the AssertExpression must not allocate.
            var ev = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"number\",[\"zoom\"]],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                ev.EvaluateNumber(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                ev.EvaluateNumber(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "AssertExpression wrapping zoom must not allocate (no Value[] — dedicated node).");
        }

        // ── Constant evaluator ────────────────────────────────────────────────

        [Test]
        public void Constant_LiteralNumber_EvaluatesIgnoringZoom()
        {
            var ev = new PaintPropertyEvaluator("7.5");
            Assert.AreEqual(7.5, ev.EvaluateNumber(0.0), 1e-15);
            Assert.AreEqual(7.5, ev.EvaluateNumber(99.0), 1e-15);
        }
    }
}
