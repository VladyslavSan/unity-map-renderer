using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity.Rendering.Materials;
namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S07 — multi-layer painter's-algorithm "clean composite" snapshot test.
    ///
    /// S54 migration: the retired LayerStack MonoBehaviour drove the original three teeth
    /// (draw-order dominance, reorder-flip, clean composite). The dominance + reorder-flip teeth
    /// are now covered live by <c>BrgBackendSnapshotTests</c> (Tooth 2 pixel — fill-over-line
    /// declared-order flip, falsifiable) and the queue-mechanism tooth by Tooth 2
    /// (ascending renderQueue in declared order). The ONLY tooth without a Gen-2 survivor is the
    /// no-z-fighting clean-composite (low region-color variance), so it is migrated here onto the
    /// live material path (MaterialFactory + LayerDrawOrder + SyntheticLineMesh), with a synthetic
    /// uniform fill quad so the sample region is a single flat colour (a fixture fill would straddle
    /// polygon edges and inflate variance for reasons unrelated to z-fighting).
    ///
    /// GPU-context guard: if renders come back all-black (no GPU context in batch EditMode), the
    /// test goes Inconclusive (not a failure). The variance assertion stays HARD on real pixels.
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
        private static readonly Color FillBottom = new Color(0.10f, 0.85f, 0.10f, 1f); // GREEN
        private static readonly Color LineMid    = new Color(0.10f, 0.10f, 0.90f, 1f); // BLUE
        private static readonly Color FillTop    = new Color(0.90f, 0.10f, 0.10f, 1f); // RED

        // The layers all overlap a central rectangle on the XZ plane. The line is a WIDE ribbon
        // through the centre so it densely covers the sample region.
        private const float OverlapHalf   = 18f;  // overlap rectangle half-extent (world meters)
        private const float LineHalfWidth = 22f;  // line half-width (m) — wider than the overlap

        // Central sample sub-rect (pixels) — well inside the projected overlap, away from edges.
        private const int SX0 = 216, SY0 = 216, SX1 = 296, SY1 = 296;

        // Variance threshold for a clean composite. Calibrated against the measured clean value
        // (logged) with headroom; a coplanar z-fight speckle between two saturated colours far exceeds this.
        private const double CleanVarianceMax = 0.02;

        [Test]
        public void CoplanarLayers_CompositeCleanly_LowVariance()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            // Bright flat ambient so the saturated colours read back strongly without depending on a
            // single light's angle (the clean composite, not lighting, is the signal).
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var sceneGo = new GameObject("CoplanarLayerScene");

            var lightGo = new GameObject("DirLight");
            lightGo.transform.SetParent(sceneGo.transform);
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;

            var disposables = new List<Object>();
            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Declared draw order via renderQueue (painter's algorithm, single transparent band):
                //   [0]=green fill (bottom), [1]=blue line (mid), [2]=red fill (top).
                // FILL-ON-TOP-OF-LINE is present (index 2 fill over index 1 line) — the keystone case.
                int[] queues = LayerDrawOrder.ComputeQueues(3);

                BuildFillQuad(sceneGo, FillBottom, OverlapHalf, queues[0], disposables);
                BuildWideLine(sceneGo, LineMid, LineHalfWidth, queues[1], disposables);
                BuildFillQuad(sceneGo, FillTop, OverlapHalf, queues[2], disposables);

                snap.Render(camera);
                snap.WritePng("layer-order-clean-composite.png");

                // GPU-context guard.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Coplanar-layer render and blank-control are all-black: no GPU context in " +
                            "batch EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                double[] mean = SnapshotCoverage.SampleRegionMeanColor(
                    snap.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);
                double variance = SnapshotCoverage.RegionColorVariance(
                    snap.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);

                Debug.Log($"[LayerOrderSnapshot] clean composite: " +
                          $"meanRGB=({mean[0]:F3},{mean[1]:F3},{mean[2]:F3}), var={variance:F5}");

                // Sanity: the region must actually have rendered something (not background slate).
                if (mean[0] + mean[1] + mean[2] < 0.05)
                {
                    Assert.Inconclusive(
                        "Overlap region is ~background — layers did not render into the sample rect. " +
                        "Likely no GPU context or a framing problem.");
                    return;
                }

                // ── Non-vacuous guard (§7.11): the TOP layer (red fill, queue 3002) must win the composite. ──
                // Before the winding fix the fill quads rendered NOTHING (front-facing under _Cull:1), so only
                // the blue line drew — this region read BLUE and the variance tooth below passed vacuously.
                // Requiring red-dominance proves all three layers render AND that painter order (fill-on-top-
                // of-line, the keystone case) actually puts the top fill on top.
                Assert.That(mean[0], Is.GreaterThan(mean[1]).And.GreaterThan(mean[2]),
                    $"Top layer (red fill) must dominate the composite (meanRGB=" +
                    $"({mean[0]:F3},{mean[1]:F3},{mean[2]:F3})). A blue/green-dominant region means the fill " +
                    "quads did not render (the §7.11 winding bug) or painter order is wrong.");

                // ── No-z-fighting tooth: a clean composite has low colour variance. ──
                // A coplanar ZWrite-On approach would speckle between the saturated layer colours
                // and inflate this far past the threshold.
                Assert.That(variance, Is.LessThan(CleanVarianceMax),
                    $"Coplanar overlap variance ({variance:F5}) must be < {CleanVarianceMax} (clean composite). " +
                    "Coplanar ZWrite-On layers would speckle (z-fight) and inflate this.");
            }
            finally
            {
                foreach (var d in disposables) if (d != null) Object.DestroyImmediate(d);
                Object.DestroyImmediate(sceneGo);
                Object.DestroyImmediate(cameraGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Layer builders ──────────────────────────────────────────────────────────

        /// <summary>
        /// Build a uniform-colour flat fill quad on the XZ plane (±half meters), drawn with a live
        /// Map/Fill material at the given renderQueue. The mesh uses the simple managed Mesh
        /// API (Unity lays attributes out canonically — no non-standard-order warning) with a white
        /// COLOR channel (identity) and a flat +Y normal; the layer colour is the _BaseColor uniform.
        /// </summary>
        private static void BuildFillQuad(GameObject parent, Color color, float half, int renderQueue,
            List<Object> disposables)
        {
            var mesh = new Mesh { name = "LayerOrderFillQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3( half, 0f, -half),
                new Vector3( half, 0f,  half),
                new Vector3(-half, 0f,  half),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.tangents = new[]
            {
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            // This quad is hand-built (it does NOT flow through StyledFillTileBuilder's boundary winding reversal),
            // so it must be wound to be Unity-front under the shipped MapFill.mat _Cull:2 (stock Cull Back).
            // Viewed from above (+Y normal), the Unity-front-facing order is {0,2,1,0,3,2}; {0,1,2,0,2,3} would
            // render INVISIBLE (back-facing → culled), re-creating the vacuous-composite failure. (Pre-flip this
            // was inverted: Cull Front + {0,1,2,0,2,3}. Same render, mirrored convention.)
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();

            var go = new GameObject("FillQuad");
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Opacity", 1f);
            mat.renderQueue = renderQueue;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;

            disposables.Add(mesh);
            disposables.Add(mat);
        }

        /// <summary>
        /// Build a wide horizontal line ribbon through the centre via SyntheticLineMesh, drawn with a
        /// live Map/Line material at the given renderQueue.
        /// </summary>
        private static void BuildWideLine(GameObject parent, Color color, float halfWidthM, int renderQueue,
            List<Object> disposables)
        {
            var mesh = SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-OverlapHalf, 0), new double2(OverlapHalf, 0) },
                JoinType.Miter, CapType.Butt);

            var go = new GameObject("WideLine");
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Width", halfWidthM * 2f);   // full width in meters
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetFloat("_Opacity", 1f);
            mat.renderQueue = renderQueue;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;

            disposables.Add(mesh);
            disposables.Add(mat);
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
