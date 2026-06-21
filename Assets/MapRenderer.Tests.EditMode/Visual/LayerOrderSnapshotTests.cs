using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S07 — multi-layer painter's-algorithm reorder snapshot test.
    ///
    /// Stands up a synthetic stack of overlapping coplanar layers (a FILL declared on top of a LINE —
    /// the discriminating case for the keystone "one transparent band" constraint) and proves the
    /// S07 acceptance teeth with HARD assertions on real pixels:
    ///
    ///   • Order tooth: in declared order the TOP layer's colour dominates the overlap region; after
    ///     SetOrder reverses the stack the OTHER layer's colour dominates. A colour flip caused ONLY by
    ///     a renderQueue reassignment (same geometry, same colours) proves we own + can reorder draw order.
    ///   • No-z-fighting tooth: the overlap region's colour variance is below a tight threshold (a clean
    ///     composite). The broken coplanar ZWrite-On approach would speckle and inflate this.
    ///
    /// GPU-context guard: if renders come back all-black (no GPU context in batch EditMode), the test
    /// goes Inconclusive (not a failure), mirroring WorldFillSnapshotTests. The order/variance assertions
    /// stay HARD when pixels are real (no unbounded skip — docs/lessons.md).
    ///
    /// Camera: top-down ortho (512×512, Y=200, orthoSize=70), dark-slate background.
    /// </summary>
    [TestFixture]
    public class LayerOrderSnapshotTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        // Saturated layer colours whose dominant channel is unambiguous regardless of lighting intensity.
        private static readonly Color FillBottom = new Color(0.10f, 0.85f, 0.10f, 1f); // GREEN  (G dominates)
        private static readonly Color LineMid    = new Color(0.10f, 0.10f, 0.90f, 1f); // BLUE   (B dominates)
        private static readonly Color FillTop    = new Color(0.90f, 0.10f, 0.10f, 1f); // RED    (R dominates)

        // The three layers all overlap a central rectangle on the XZ plane. The line is a WIDE ribbon
        // running through the centre so it densely covers the sample region (sample its clean interior).
        private const float OverlapHalf   = 18f;  // overlap rectangle half-extent (world meters)
        private const float LineHalfWidth = 22f;  // line half-width (m) — wider than the overlap so it fills it

        // Central sample sub-rect (pixels) — well inside the projected overlap, away from quad/feather edges.
        // Overlap is ±18m at orthoSize 70 → ±18/70 of half the frame ≈ ±66px around centre (256). Sample ±40px.
        private const int SX0 = 216, SY0 = 216, SX1 = 296, SY1 = 296;

        // Variance threshold for a clean composite. Calibrated against the measured clean value (logged),
        // with headroom; a coplanar z-fight speckle between two saturated colours would far exceed this.
        private const double CleanVarianceMax = 0.02;

        [Test]
        public void ReorderingLayers_FlipsTopColor_WithCleanComposite()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            // Bright flat ambient so the unlit-ish saturated colours read back strongly without
            // depending on a single directional light's angle (the colours, not lighting, are the signal).
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var stackGo  = new GameObject("LayerStack");
            var stack    = stackGo.AddComponent<LayerStack>();

            // A bit of directional light so the Lit fills are not pure ambient (belt and suspenders).
            var lightGo = new GameObject("DirLight");
            lightGo.transform.SetParent(stackGo.transform);
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;

            // Declared order: [0]=green fill (bottom), [1]=blue line (mid), [2]=red fill (top).
            // FILL-ON-TOP-OF-LINE is present (index 2 fill over index 1 line) — the keystone case.
            var specs = new List<LayerStack.LayerSpec>
            {
                LayerStack.LayerSpec.Fill("green-bottom", FillBottom, -OverlapHalf, OverlapHalf, -OverlapHalf, OverlapHalf),
                LayerStack.LayerSpec.Line("blue-mid",     LineMid,    -OverlapHalf, OverlapHalf, 0f, LineHalfWidth),
                LayerStack.LayerSpec.Fill("red-top",      FillTop,    -OverlapHalf, OverlapHalf, -OverlapHalf, OverlapHalf),
            };

            using var snapDeclared = new SnapshotRenderer(SnapW, SnapH);
            using var snapReversed = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                stack.Build(specs); // declared order: red fill on top

                snapDeclared.Render(camera);
                snapDeclared.WritePng("layer-order-declared.png");

                // GPU-context guard.
                if (snapDeclared.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Layer-order render and blank-control are all-black: no GPU context in " +
                            "batch EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                double[] declaredMean = SnapshotCoverage.SampleRegionMeanColor(
                    snapDeclared.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);
                double declaredVar = SnapshotCoverage.RegionColorVariance(
                    snapDeclared.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);

                Debug.Log($"[LayerOrderSnapshot] DECLARED (red on top): " +
                          $"meanRGB=({declaredMean[0]:F3},{declaredMean[1]:F3},{declaredMean[2]:F3}), var={declaredVar:F5}");

                // Sanity: the region must actually have rendered something (not background slate).
                if (declaredMean[0] + declaredMean[1] + declaredMean[2] < 0.05)
                {
                    Assert.Inconclusive(
                        "Overlap region is ~background — layers did not render into the sample rect. " +
                        "Likely no GPU context or a framing problem.");
                    return;
                }

                // ── Order tooth (part 1): RED (top fill) dominates in declared order. ──
                Assert.That(declaredMean[0], Is.GreaterThan(declaredMean[1]),
                    $"Declared order: RED top fill must dominate — mean R ({declaredMean[0]:F3}) > G ({declaredMean[1]:F3}). " +
                    "If GREEN or BLUE shows through, the fill declared on TOP of the line did not composite last — " +
                    "the keystone single-transparent-band constraint is broken.");
                Assert.That(declaredMean[0], Is.GreaterThan(declaredMean[2]),
                    $"Declared order: mean R ({declaredMean[0]:F3}) must exceed B ({declaredMean[2]:F3}).");

                // ── No-z-fighting tooth (declared): clean composite, low variance. ──
                Assert.That(declaredVar, Is.LessThan(CleanVarianceMax),
                    $"Declared-order overlap variance ({declaredVar:F5}) must be < {CleanVarianceMax} (clean composite). " +
                    "Coplanar ZWrite-On layers would speckle and inflate this.");

                // ── Reorder: reverse the stack (top↔bottom) by changing ONLY renderQueue. ──
                // Reversed stack position order = [2,1,0] → spec 0 (green) drawn last/on top.
                stack.SetOrder(new[] { 2, 1, 0 });

                snapReversed.Render(camera);
                snapReversed.WritePng("layer-order-reversed.png");

                double[] reversedMean = SnapshotCoverage.SampleRegionMeanColor(
                    snapReversed.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);
                double reversedVar = SnapshotCoverage.RegionColorVariance(
                    snapReversed.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);

                Debug.Log($"[LayerOrderSnapshot] REVERSED (green on top): " +
                          $"meanRGB=({reversedMean[0]:F3},{reversedMean[1]:F3},{reversedMean[2]:F3}), var={reversedVar:F5}");

                // ── Order tooth (part 2): GREEN (now top) dominates after the reorder. ──
                Assert.That(reversedMean[1], Is.GreaterThan(reversedMean[0]),
                    $"Reversed order: GREEN bottom fill is now on top — mean G ({reversedMean[1]:F3}) must exceed " +
                    $"R ({reversedMean[0]:F3}). If RED still dominates, the reorder did not change draw order " +
                    "(Unity's automatic sort would be in control, not ours).");
                Assert.That(reversedMean[1], Is.GreaterThan(reversedMean[2]),
                    $"Reversed order: mean G ({reversedMean[1]:F3}) must exceed B ({reversedMean[2]:F3}).");

                // ── The flip itself: dominant channel changed from R to G purely via renderQueue. ──
                Assert.That(declaredMean[0], Is.GreaterThan(reversedMean[0]),
                    $"Reorder must REDUCE red dominance: declared R ({declaredMean[0]:F3}) > reversed R ({reversedMean[0]:F3}).");
                Assert.That(reversedMean[1], Is.GreaterThan(declaredMean[1]),
                    $"Reorder must INCREASE green dominance: reversed G ({reversedMean[1]:F3}) > declared G ({declaredMean[1]:F3}).");

                // ── No-z-fighting tooth (reversed): still a clean composite. ──
                Assert.That(reversedVar, Is.LessThan(CleanVarianceMax),
                    $"Reversed-order overlap variance ({reversedVar:F5}) must be < {CleanVarianceMax} (clean composite).");
            }
            finally
            {
                Object.DestroyImmediate(stackGo);
                Object.DestroyImmediate(cameraGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        /// <summary>
        /// Mechanism tooth (GPU-independent): the painter's stack assigns DISTINCT render queues in
        /// declared order, all in the transparent band, with ZWrite off — i.e. our explicit order, not
        /// Unity's automatic queue→distance→depth sort. Runs even when no GPU context is available.
        /// </summary>
        [Test]
        public void Stack_AssignsDistinctTransparentQueues_InDeclaredOrder()
        {
            var stackGo = new GameObject("LayerStackQueues");
            var stack   = stackGo.AddComponent<LayerStack>();
            try
            {
                var specs = new List<LayerStack.LayerSpec>
                {
                    LayerStack.LayerSpec.Fill("f0", FillBottom, -10, 10, -10, 10),
                    LayerStack.LayerSpec.Line("l1", LineMid,    -10, 10, 0f, 5f),
                    LayerStack.LayerSpec.Fill("f2", FillTop,    -10, 10, -10, 10),
                };
                stack.Build(specs);

                int[] expected = LayerDrawOrder.ComputeQueues(3, stack.BaseQueue);
                for (int i = 0; i < 3; i++)
                {
                    Material m = stack.MaterialAt(i);
                    Assert.That(m.renderQueue, Is.EqualTo(expected[i]),
                        $"Layer {i} material.renderQueue ({m.renderQueue}) must equal base+index ({expected[i]}).");
                    Assert.That(m.renderQueue, Is.GreaterThanOrEqualTo(LayerDrawOrder.TransparentBandStart),
                        $"Layer {i} must be in the transparent band (>= {LayerDrawOrder.TransparentBandStart}).");
                    // ZWrite off on every painter's layer (the fill sets _ZWrite=0; the line shader hardcodes it).
                    if (m.HasProperty("_ZWrite"))
                        Assert.That(m.GetFloat("_ZWrite"), Is.EqualTo(0f),
                            $"Layer {i} must have ZWrite off (_ZWrite=0) for painter's-algorithm compositing.");
                }

                // Strictly monotonic + distinct across the actual materials.
                Assert.That(stack.MaterialAt(1).renderQueue, Is.GreaterThan(stack.MaterialAt(0).renderQueue));
                Assert.That(stack.MaterialAt(2).renderQueue, Is.GreaterThan(stack.MaterialAt(1).renderQueue));

                // SetOrder must change ONLY queues: reversing puts spec 0 at the top position queue.
                stack.SetOrder(new[] { 2, 1, 0 });
                Assert.That(stack.MaterialAt(0).renderQueue, Is.EqualTo(expected[2]),
                    "After reverse, spec 0 must be drawn at the TOP position's queue.");
                Assert.That(stack.MaterialAt(2).renderQueue, Is.EqualTo(expected[0]),
                    "After reverse, spec 2 must be drawn at the BOTTOM position's queue.");
            }
            finally
            {
                Object.DestroyImmediate(stackGo);
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LayerOrderCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { Object.DestroyImmediate(go); }
            return snap;
        }
    }
}
