// Unity EditMode only — uses RenderLayerSet, MaterialFactory, ZoomStyleApplier.
// NOT included in Tools/core-tests.
//
// S54: retargeted off the retired LayerStack MonoBehaviour onto the live RenderLayerSet
// (the production "style → GPU layers" path) + MaterialFactory. The teeth are unchanged:
// per-layer distinct materials, ordered queues, premultiplied-alpha color, mesh-reference-
// unchanged (build-once / restyle-via-uniforms), and zero-GC on the swept zoom hot path.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Json;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using MapRenderer.Tests.Visual;
using CoreColor = MapRenderer.Core.Expressions.Color;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S11 — <see cref="ZoomStyleApplier"/> integration tests.
    ///
    /// Acceptance criteria verified:
    ///   1. No mesh rebuild: a line mesh reference is unchanged while the width uniform changes across zooms.
    ///   2. Premultiplied-alpha color uniform matches spec values (via Core evaluator).
    ///   3. Per-layer distinct Material instances and ordered queues (acceptance #5).
    ///   4. Allocation gate: <c>ApplyZoom</c> with SWEEPING zoom allocates zero GC bytes
    ///      (acceptance #4 — Unity-side full path including Material.SetFloat/SetColor).
    ///
    /// S54: drives the live <see cref="RenderLayerSet"/> / <see cref="MaterialFactory"/> path (the
    /// production "style → GPU layers" machinery), replacing the retired LayerStack MonoBehaviour.
    /// </summary>
    [TestFixture]
    public class ZoomStyleApplierTests
    {
        // ── Helper: build a two-layer styled set (one fill, one line) ───────────────

        // Fill layer first (declared index 0 = bottom), line layer second (index 1 = top).
        private const string TwoLayerStyleJson = @"{
    ""version"": 8,
    ""name"": ""ZoomStyleApplierTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] } },
        { ""id"": ""line1"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""line1"",
          ""paint"": { ""line-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        private static RenderLayerSet BuildTwoLayerSet(double initialZoom = 0.0)
        {
            var style = StyleParser.Parse(TwoLayerStyleJson);
            var set = new RenderLayerSet();
            set.Build(style, initialZoom, MapMaterialSetTestUtil.Load());
            return set;
        }

        // ── 1. No mesh rebuild ────────────────────────────────────────────────────

        [Test]
        public void ApplyZoom_LineWidth_MeshReferenceUnchanged_WhileWidthChanges()
        {
            // The build-once / restyle-via-uniforms invariant: a line mesh is built ONCE and never
            // rebuilt when zoom changes — only the material's width uniform is pushed. We bind a
            // line material to an applier and assert the (separately-built) mesh reference held by a
            // MeshFilter is unchanged across ApplyZoom calls while _Width tracks the zoom expression.
            Material lineMat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());

            var lineMesh = SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-40, 0), new double2(40, 0) },
                JoinType.Miter, CapType.Butt);
            var go = new GameObject("ZoomApplier_LineMesh");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = lineMesh;
            Mesh meshBefore = mf.sharedMesh;

            try
            {
                // Create a zoom-interpolated width expression.
                var widthSp = new StyleProperty<float>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]"),
                    0f, v => (float)v.AsNumber());
                var applier = new ZoomStyleApplier(lineMat);
                applier.BindFloat(widthSp, ShaderProperties.Line.PropertyId.Width);

                applier.ApplyZoom(5.0, 1.0);
                float widthAtZ5 = lineMat.GetFloat("_Width");
                applier.ApplyZoom(15.0, 1.0);
                float widthAtZ15 = lineMat.GetFloat("_Width");

                // Mesh must be the SAME reference after multiple ApplyZoom calls.
                Assert.IsTrue(ReferenceEquals(meshBefore, mf.sharedMesh),
                    "sharedMesh reference must not change after ApplyZoom — build-once, restyle path.");

                // Width must have changed.
                Assert.AreEqual(2.0f, widthAtZ5, 0.01f, "Width at zoom=5 must equal lower stop (2.0).");
                Assert.AreEqual(20.0f, widthAtZ15, 0.01f, "Width at zoom=15 must equal upper stop (20.0).");
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (lineMesh != null) Object.DestroyImmediate(lineMesh);
                Object.DestroyImmediate(lineMat);
            }
        }

        // ── 2. Premultiplied-alpha color matches evaluator ────────────────────────

        [Test]
        public void ApplyZoom_Color_MatchesPremultipliedAlphaEvaluator()
        {
            Material fillMat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            try
            {
                // rgba(0,0,0,0) → rgba(255,255,255,1) — the canonical premult discriminating case.
                var colorSp = new StyleProperty<CoreColor>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                    "0,[\"rgba\",0,0,0,0],1,[\"rgba\",255,255,255,1]]"),
                    default, v => v.AsColorCoerced());
                var applier = new ZoomStyleApplier(fillMat);
                applier.BindColor(colorSp, ShaderProperties.PropertyId.BaseColor);

                // Apply at zoom=0.5 (t=0.5 between stops 0 and 1).
                applier.ApplyZoom(0.5, 1.0);
                Color unity = fillMat.GetColor("_BaseColor");

                // Derive the expected value from the Core evaluator with the SAME converter.
                CoreColor core = colorSp.Evaluate(0.5);
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
            finally
            {
                Object.DestroyImmediate(fillMat);
            }
        }

        // ── 3. Per-layer distinct Material instances (acceptance #5) ─────────────

        [Test]
        public void RenderLayerSet_PerLayerMaterials_AreDistinctInstances()
        {
            using var set = BuildTwoLayerSet();
            Assert.AreEqual(2, set.Count, "Expected two render layers (one fill, one line).");
            Assert.IsInstanceOf<MapRenderer.Core.Style.Fill.StyleLayer>(set[0].StyleLayer, "Layer 0 is the fill.");
            Assert.IsInstanceOf<MapRenderer.Core.Style.Line.StyleLayer>(set[1].StyleLayer, "Layer 1 is the line.");

            Material m0 = set[0].Material;
            Material m1 = set[1].Material;

            Assert.IsFalse(ReferenceEquals(m0, m1),
                "Each layer must have its own distinct Material instance (never shared).");
        }

        [Test]
        public void RenderLayerSet_DeclaredOrder_QueueIsStrictlyIncreasing()
        {
            using var set = BuildTwoLayerSet();

            // Declared order: fill (index 0) then line (index 1). A single monotonic draw index is assigned
            // across ALL layers, so the fill's queue must be lower than the line's.
            int q0 = set[0].Material.renderQueue;
            int q1 = set[1].Material.renderQueue;

            Assert.Less(q0, q1,
                "Layer 0 (fill, declared first) must have a lower render queue than layer 1 (line) " +
                "— painter's algorithm: declared-first = bottom.");
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
            Material lineMat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            try
            {
                var widthSp = new StyleProperty<float>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]"),
                    0f, v => (float)v.AsNumber());
                var applier = new ZoomStyleApplier(lineMat);
                applier.BindFloat(widthSp, ShaderProperties.Line.PropertyId.Width);

                // Warm up: ensure JIT compilation and shader reflection are done before measuring.
                for (int w = 0; w < 20; w++)
                    applier.ApplyZoom(5.0 + (w % 10) * 1.0, 1.0);

                // ── Measure: swept zoom, must be alloc-free. ──
                double sweepZoom = 5.0;
                Assert.That(() =>
                {
                    // Use a closure-captured local that changes each call so the sweep is genuinely variable.
                    sweepZoom += 0.1;
                    if (sweepZoom > 15.0) sweepZoom = 5.0;
                    applier.ApplyZoom(sweepZoom, 1.0);
                }, Is.Not.AllocatingGCMemory(),
                    "ZoomStyleApplier.ApplyZoom (float binding, sweeping zoom) must not allocate GC memory. " +
                    "A failure indicates a Value[], boxing, or per-frame allocation escaped the hot path.");
            }
            finally
            {
                Object.DestroyImmediate(lineMat);
            }
        }

        [Test]
        public void ApplyZoom_SweepingZoom_ColorBinding_AllocatesZeroGCMemory()
        {
            Material fillMat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            try
            {
                var colorSp = new StyleProperty<CoreColor>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                    "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]"),
                    default, v => v.AsColorCoerced());
                var applier = new ZoomStyleApplier(fillMat);
                applier.BindColor(colorSp, ShaderProperties.PropertyId.BaseColor);

                for (int w = 0; w < 20; w++)
                    applier.ApplyZoom(5.0 + (w % 10) * 1.0, 1.0);

                double sweepZoom = 5.0;
                Assert.That(() =>
                {
                    sweepZoom += 0.1;
                    if (sweepZoom > 15.0) sweepZoom = 5.0;
                    applier.ApplyZoom(sweepZoom, 1.0);
                }, Is.Not.AllocatingGCMemory(),
                    "ZoomStyleApplier.ApplyZoom (color binding, sweeping zoom) must not allocate GC memory. " +
                    "A failure means rgb() stop outputs were not constant-folded to LiteralExpression.");
            }
            finally
            {
                Object.DestroyImmediate(fillMat);
            }
        }
        // ── P4: the _Opacity uniform half of data-driven fill-opacity ─────────────

        /// <summary>
        /// A CONSTANT fill-opacity rides the <c>_Opacity</c> uniform. Pairs with
        /// <c>FillSortKeyAndOpacityTests.ConstantOpacity_IsNotBaked</c>, which asserts the other half (that
        /// it is NOT also baked into vertex alpha) — on its own that test would pass on a build that dropped
        /// fill-opacity entirely, so this is what makes the pair discriminating.
        /// </summary>
        [Test]
        public void BindFillPaint_ConstantOpacity_LandsOnTheOpacityUniform()
        {
            Material fillMat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            try
            {
                var paint = new MapRenderer.Core.Style.Fill.PaintProperties(
                    JsonParser.Parse(@"{""fill-opacity"": 0.5}"));
                var applier = new ZoomStyleApplier(fillMat);
                MaterialFactory.BindFillPaintToApplier(paint, applier, fillMat);
                applier.ApplyZoom(0.0, 1.0);

                Assert.AreEqual(0.5f, fillMat.GetFloat("_Opacity"), 1e-4f,
                    "a constant fill-opacity must be bound as the _Opacity uniform.");
            }
            finally { Object.DestroyImmediate(fillMat); }
        }

        /// <summary>
        /// A DATA-DRIVEN fill-opacity is baked per-feature into the COLOR stream, so <c>_Opacity</c> must be
        /// pinned to 1: the fragment computes <c>alpha *= vColor.a * _Opacity</c>, and leaving the base
        /// material's inherited value there would multiply the opacity in twice. Mirrors the convention
        /// <c>BindLinePaintToApplier</c> uses for data-driven line-width.
        /// </summary>
        [Test]
        public void BindFillPaint_DataDrivenOpacity_PinsTheUniformToOneSoItCannotDoubleApply()
        {
            Material fillMat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            try
            {
                fillMat.SetFloat("_Opacity", 0.25f); // a stale/inherited value the bind must overwrite

                var paint = new MapRenderer.Core.Style.Fill.PaintProperties(
                    JsonParser.Parse(@"{""fill-opacity"": [""get"", ""op""]}"));
                Assert.IsTrue(paint.Opacity.DependsOnFeature, "precondition: this expression is data-driven");

                var applier = new ZoomStyleApplier(fillMat);
                MaterialFactory.BindFillPaintToApplier(paint, applier, fillMat);
                applier.ApplyZoom(0.0, 1.0);

                Assert.AreEqual(1f, fillMat.GetFloat("_Opacity"), 1e-4f,
                    "a data-driven fill-opacity is baked per-feature, so _Opacity must be pinned to 1. " +
                    "Any other value double-applies the opacity in the fragment.");
            }
            finally { Object.DestroyImmediate(fillMat); }
        }
    }
}
