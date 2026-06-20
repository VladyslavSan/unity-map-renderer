// Unity EditMode only — uses MonoBehaviour, LayerStack, ZoomStyleApplier.
// NOT included in Tools/core-tests.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Unity;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S11 — <see cref="ZoomStyleApplier"/> integration tests.
    ///
    /// Acceptance criteria verified:
    ///   1. No mesh rebuild: line mesh reference unchanged while width uniform changes across zooms.
    ///   2. Premultiplied-alpha color uniform matches spec values (via Core evaluator).
    ///   3. Per-layer distinct Material instances and ordered queues (acceptance #5).
    ///   4. Allocation gate: <c>ApplyZoom</c> with SWEEPING zoom allocates zero GC bytes
    ///      (acceptance #4 — Unity-side full path including Material.SetFloat/SetColor).
    ///
    /// Uses <see cref="LayerStack"/> for real Material instances (the S07 path) rather than mocking,
    /// so the S07 acceptance condition (per-layer material, ordered queues) is exercised.
    /// </summary>
    [TestFixture]
    public class ZoomStyleApplierTests
    {
        private GameObject _go;
        private LayerStack _stack;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ZoomStyleApplierTest");
            _stack = _go.AddComponent<LayerStack>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        // ── Helper: build a two-layer stack with one line layer and one fill layer ──

        private static LayerStack.LayerSpec[] MakeTwoLayers()
            => new[]
            {
                LayerStack.LayerSpec.Fill("fill0",  Color.white, -5, 5, -5, 5),
                LayerStack.LayerSpec.Line("line1",  Color.red,   -5, 5, 0f, 0.5f),
            };

        // ── 1. No mesh rebuild ────────────────────────────────────────────────────

        [Test]
        public void ApplyZoom_LineWidth_MeshReferenceUnchanged_WhileWidthChanges()
        {
            _stack.Build(MakeTwoLayers());
            // Layer 1 is the line layer (index 1).
            Material lineMat = _stack.MaterialAt(1);

            // Capture the mesh object reference before any ApplyZoom call.
            var lineGo = _stack.transform.GetChild(1).gameObject;
            var mf = lineGo.GetComponent<MeshFilter>();
            Mesh meshBefore = mf.sharedMesh;

            // Create a zoom-interpolated width expression.
            var widthEv = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            var applier = new ZoomStyleApplier(lineMat);
            applier.BindFloat(widthEv, "_Width");

            applier.ApplyZoom(5.0);
            float widthAtZ5  = lineMat.GetFloat("_Width");
            applier.ApplyZoom(15.0);
            float widthAtZ15 = lineMat.GetFloat("_Width");

            // Mesh must be the SAME reference after multiple ApplyZoom calls.
            Assert.IsTrue(ReferenceEquals(meshBefore, mf.sharedMesh),
                "sharedMesh reference must not change after ApplyZoom — build-once, restyle path.");

            // Width must have changed.
            Assert.AreEqual(2.0f,  widthAtZ5,  0.01f, "Width at zoom=5 must equal lower stop (2.0).");
            Assert.AreEqual(20.0f, widthAtZ15, 0.01f, "Width at zoom=15 must equal upper stop (20.0).");
        }

        // ── 2. Premultiplied-alpha color matches evaluator ────────────────────────

        [Test]
        public void ApplyZoom_Color_MatchesPremultipliedAlphaEvaluator()
        {
            _stack.Build(MakeTwoLayers());
            Material fillMat = _stack.MaterialAt(0);

            // rgba(0,0,0,0) → rgba(255,255,255,1) — the canonical premult discriminating case.
            var colorEv = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0,[\"rgba\",0,0,0,0],1,[\"rgba\",255,255,255,1]]");
            var applier = new ZoomStyleApplier(fillMat);
            applier.BindColor(colorEv, "_MapColor");

            // Apply at zoom=0.5 (t=0.5 between stops 0 and 1).
            applier.ApplyZoom(0.5);
            Color unity = fillMat.GetColor("_MapColor");

            // Derive the expected value from the Core evaluator with the SAME converter.
            CoreColor core = colorEv.EvaluateColor(0.5);
            Color expected = ZoomStyleApplier.ToUnityColor(core);

            Assert.AreEqual(expected.r, unity.r, 0.01f,
                "R channel from material must match Core evaluator (premultiplied-alpha).");
            Assert.AreEqual(expected.g, unity.g, 0.01f, "G channel.");
            Assert.AreEqual(expected.b, unity.b, 0.01f, "B channel.");
            Assert.AreEqual(expected.a, unity.a, 0.01f, "Alpha channel.");

            // Spec correctness: R must be near 1.0 (premult gives R=255/255=1), not 0.5 (straight lerp).
            Assert.Greater(unity.r, 0.9f,
                "Premultiplied-alpha: R at t=0.5 for transparent→opaque must be near 1.0, not 0.5.");
        }

        // ── 3. Per-layer distinct Material instances (acceptance #5) ─────────────

        [Test]
        public void LayerStack_PerLayerMaterials_AreDistinctInstances()
        {
            _stack.Build(MakeTwoLayers());
            Material m0 = _stack.MaterialAt(0);
            Material m1 = _stack.MaterialAt(1);

            Assert.IsFalse(ReferenceEquals(m0, m1),
                "Each layer must have its own distinct Material instance (never shared).");
        }

        [Test]
        public void LayerStack_DeclaredOrder_QueueIsStrictlyIncreasing()
        {
            _stack.Build(MakeTwoLayers());
            int q0 = _stack.MaterialAt(0).renderQueue;
            int q1 = _stack.MaterialAt(1).renderQueue;

            Assert.Less(q0, q1,
                "Layer 0 must have a lower render queue than layer 1 (painter's algorithm: 0 = bottom).");
            Assert.GreaterOrEqual(q0, LayerDrawOrder.TransparentBandStart,
                "Both queues must be in the transparent band.");
            Assert.GreaterOrEqual(q1, LayerDrawOrder.TransparentBandStart);
        }

        // ── 4. No per-frame GC allocation (SWEEPING zoom — acceptance #4) ─────────
        //
        // The zoom is SWEPT across different values each iteration (not held constant) so the
        // evaluator's full interpolation path is exercised on every call.  A constant zoom would
        // let Unity/JIT detect invariant computation and hoist it, making the gate toothless.

        [Test]
        public void ApplyZoom_SweepingZoom_FloatBinding_AllocatesZeroGCMemory()
        {
            _stack.Build(MakeTwoLayers());
            Material lineMat = _stack.MaterialAt(1);

            var widthEv = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            var applier = new ZoomStyleApplier(lineMat);
            applier.BindFloat(widthEv, "_Width");

            // Warm up: ensure JIT compilation and shader reflection are done before measuring.
            for (int w = 0; w < 20; w++)
                applier.ApplyZoom(5.0 + (w % 10) * 1.0);

            // ── Measure: swept zoom, must be alloc-free. ──
            double sweepZoom = 5.0;
            Assert.That(() =>
            {
                // Use a closure-captured local that changes each call so the sweep is genuinely variable.
                sweepZoom += 0.1;
                if (sweepZoom > 15.0) sweepZoom = 5.0;
                applier.ApplyZoom(sweepZoom);
            }, Is.Not.AllocatingGCMemory(),
                "ZoomStyleApplier.ApplyZoom (float binding, sweeping zoom) must not allocate GC memory. " +
                "A failure indicates a Value[], boxing, or per-frame allocation escaped the hot path.");
        }

        [Test]
        public void ApplyZoom_SweepingZoom_ColorBinding_AllocatesZeroGCMemory()
        {
            _stack.Build(MakeTwoLayers());
            Material fillMat = _stack.MaterialAt(0);

            var colorEv = new PaintPropertyEvaluator(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]");
            var applier = new ZoomStyleApplier(fillMat);
            applier.BindColor(colorEv, "_MapColor");

            for (int w = 0; w < 20; w++)
                applier.ApplyZoom(5.0 + (w % 10) * 1.0);

            double sweepZoom = 5.0;
            Assert.That(() =>
            {
                sweepZoom += 0.1;
                if (sweepZoom > 15.0) sweepZoom = 5.0;
                applier.ApplyZoom(sweepZoom);
            }, Is.Not.AllocatingGCMemory(),
                "ZoomStyleApplier.ApplyZoom (color binding, sweeping zoom) must not allocate GC memory. " +
                "A failure means rgb() stop outputs were not constant-folded to LiteralExpression.");
        }
    }
}
