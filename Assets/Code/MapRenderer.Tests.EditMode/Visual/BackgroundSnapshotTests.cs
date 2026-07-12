using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// E3 — tooth §6.5: a style's <c>background</c> layer renders at its declared draw slot (the style's
    /// colour, not the camera clear) and composites mid-stack (occludes a layer declared below it, is
    /// occluded by one declared above it). Setup (camera, quality level, ambient, GPU-context guard) copied
    /// VERBATIM from <see cref="LayerOrderSnapshotTests"/> — the proven headless-Lit-material recipe.
    ///
    /// Layers come from a real <see cref="RenderLayerSet.Build"/> over a parsed style — the production path
    /// end-to-end (<see cref="RenderLayerFactory"/> → <see cref="BackgroundRenderLayer.Create"/> → the quad
    /// GameObject lands in the scene → <c>renderQueue</c> written by <see cref="RenderLayerSet.Build"/>),
    /// not a hand-rolled substitute.
    ///
    /// GPU-context guard: if renders come back all-black (no GPU context in batch EditMode), the test goes
    /// Inconclusive (not a failure).
    /// </summary>
    [TestFixture]
    public class BackgroundSnapshotTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f); // camera clear — must NOT be what a declared background samples as

        // Central sample sub-rect (pixels) — well inside the world-cap quad's projection, away from edges.
        private const int SX0 = 216, SY0 = 216, SX1 = 296, SY1 = 296;

        // A small fill quad's footprint (world meters) and its centred/corner sample rects for the
        // mid-stack tooth — the fill quad is far smaller than the background's world-cap, so a corner
        // sample sits outside it while staying inside the camera frustum.
        private const float FillHalfExtent = 15f;
        private const int   FillCenterX0 = 236, FillCenterY0 = 236, FillCenterX1 = 276, FillCenterY1 = 276;
        private const int   CornerX0 = 20, CornerY0 = 20, CornerX1 = 60, CornerY1 = 60;

        [Test]
        public void BackgroundColor_StyleHonoured_NotCameraClear()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var lightGo = BuildLight();
            try
            {
                double[] greenMean = RenderBackgroundOnlyStyle(camera, "#00ff00", "green-background.png");
                if (greenMean == null) return; // Inconclusive — no GPU context

                Debug.Log($"[BackgroundSnapshot] green style: meanRGB=({greenMean[0]:F3},{greenMean[1]:F3},{greenMean[2]:F3})");
                Assert.Greater(greenMean[1], greenMean[0], "A green background-color must sample green-dominant, not the slate clear.");
                Assert.Greater(greenMean[1], greenMean[2], "A green background-color must sample green-dominant, not the slate clear.");

                // Falsifier: a DIFFERENT style background must sample DIFFERENTLY. Under pre-E3 code (the
                // camera-clear hack) both renders would sample the same slate clear colour regardless of
                // the style — this fails by construction against that implementation.
                double[] redMean = RenderBackgroundOnlyStyle(camera, "#ff0000", "red-background.png");
                if (redMean == null) return; // Inconclusive — no GPU context

                Debug.Log($"[BackgroundSnapshot] red style: meanRGB=({redMean[0]:F3},{redMean[1]:F3},{redMean[2]:F3})");
                Assert.Greater(redMean[0], redMean[1], "A red background-color must sample red-dominant.");
                Assert.Greater(redMean[0], redMean[2], "A red background-color must sample red-dominant.");
            }
            finally
            {
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(cameraGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        [Test]
        public void Background_MidStack_OccludesBelow_IsOccludedByAbove()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var lightGo = BuildLight();
            try
            {
                // Style A: fill declared BELOW background (fill first, drawIndex 0; background second,
                // drawIndex 1 — higher queue, drawn on top). Background must occlude the fill at centre.
                const string styleA = @"{
    ""version"": 8,
    ""layers"": [
        { ""id"": ""fill-a"", ""type"": ""fill"" },
        { ""id"": ""bg-a"",   ""type"": ""background"", ""paint"": { ""background-color"": ""#00ff00"" } }
    ]
}";
                double[] aCenter = RenderMidStackStyle(camera, styleA, fillIndex: 0, "midstack-a.png", out _);
                if (aCenter == null) return; // Inconclusive — no GPU context

                Debug.Log($"[BackgroundSnapshot] A (fill,bg) centre: meanRGB=({aCenter[0]:F3},{aCenter[1]:F3},{aCenter[2]:F3})");
                Assert.Greater(aCenter[1], aCenter[0], "Background declared ABOVE the fill must occlude it at centre (green-dominant).");
                Assert.Greater(aCenter[1], aCenter[2], "Background declared ABOVE the fill must occlude it at centre (green-dominant).");

                // Style B: background declared BELOW fill (background first, drawIndex 0; fill second,
                // drawIndex 1 — higher queue, drawn on top). The fill must win at centre; the background
                // must still show at the corner, outside the fill quad's small footprint.
                const string styleB = @"{
    ""version"": 8,
    ""layers"": [
        { ""id"": ""bg-b"",   ""type"": ""background"", ""paint"": { ""background-color"": ""#00ff00"" } },
        { ""id"": ""fill-b"", ""type"": ""fill"" }
    ]
}";
                double[] bCenter = RenderMidStackStyle(camera, styleB, fillIndex: 1, "midstack-b.png", out double[] bCorner);
                if (bCenter == null) return; // Inconclusive — no GPU context

                Debug.Log($"[BackgroundSnapshot] B (bg,fill) centre: meanRGB=({bCenter[0]:F3},{bCenter[1]:F3},{bCenter[2]:F3}), " +
                          $"corner: meanRGB=({bCorner[0]:F3},{bCorner[1]:F3},{bCorner[2]:F3})");
                Assert.IsFalse(bCenter[1] > bCenter[0] && bCenter[1] > bCenter[2],
                    "The fill declared ABOVE the background must win at centre — NOT green-dominant.");
                Assert.Greater(bCorner[1], bCorner[0], "The background must still show at the corner, outside the fill quad's footprint.");
                Assert.Greater(bCorner[1], bCorner[2], "The background must still show at the corner, outside the fill quad's footprint.");
            }
            finally
            {
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(cameraGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>Builds a background-only style, renders it, and returns the centre-region mean colour
        /// (or null on the GPU-context guard). Disposes the set (and with it the background's owned
        /// GameObject/Mesh/Material) before returning.</summary>
        private static double[] RenderBackgroundOnlyStyle(Camera camera, string colorHex, string pngName)
        {
            string json = $@"{{ ""version"": 8, ""layers"": [
                {{ ""id"": ""bg"", ""type"": ""background"", ""paint"": {{ ""background-color"": ""{colorHex}"" }} }}
            ] }}";

            using var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            snap.WritePng(pngName);

            if (GpuContextInconclusive(snap)) return null;

            return SnapshotCoverage.SampleRegionMeanColor(snap.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);
        }

        /// <summary>Builds the given two-layer (fill + background) style, hand-builds a small quad for the
        /// fill (it has no tile geometry in this headless test) using the SET's own fill material — its
        /// <c>renderQueue</c> is already written by <see cref="RenderLayerSet.Build"/> — renders, and
        /// returns the centre-region mean colour (<paramref name="corner"/> gets the corner-region mean).
        /// Null (and <paramref name="corner"/> null) on the GPU-context guard.</summary>
        private static double[] RenderMidStackStyle(
            Camera camera, string styleJson, int fillIndex, string pngName, out double[] corner)
        {
            corner = null;

            using var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(styleJson), 0.0, MapMaterialSetTestUtil.Load());

            var disposables = new List<Object>();
            GameObject fillGo = null;
            try
            {
                Material fillMat = set[fillIndex].Material;
                fillMat.SetColor("_BaseColor", new Color(1f, 0f, 0f, 1f)); // give the fill quad visible pixels, distinct from green

                var mesh = BuildFillQuadMesh(FillHalfExtent);
                fillGo = new GameObject("MidStackFillQuad");
                fillGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                fillGo.AddComponent<MeshRenderer>().sharedMaterial = fillMat;
                disposables.Add(mesh);

                using var snap = new SnapshotRenderer(SnapW, SnapH);
                snap.Render(camera);
                snap.WritePng(pngName);

                if (GpuContextInconclusive(snap)) return null;

                corner = SnapshotCoverage.SampleRegionMeanColor(snap.RawPixels, SnapW, SnapH, CornerX0, CornerY0, CornerX1, CornerY1);
                return SnapshotCoverage.SampleRegionMeanColor(
                    snap.RawPixels, SnapW, SnapH, FillCenterX0, FillCenterY0, FillCenterX1, FillCenterY1);
            }
            finally
            {
                foreach (var d in disposables) if (d != null) Object.DestroyImmediate(d);
                if (fillGo != null) Object.DestroyImmediate(fillGo);
            }
        }

        /// <summary>The <c>LayerOrderSnapshotTests.BuildFillQuad</c> vertex-attribute recipe, with the
        /// triangle winding REVERSED to match the real fill material's <c>_Cull=1</c> (Front) render
        /// state — see <see cref="BackgroundRenderLayer.BuildQuadAndPresenter"/>'s doc for why. This quad
        /// is drawn with <c>set[fillIndex].Material</c> (the SAME cull state), so it needs the same fix.</summary>
        private static Mesh BuildFillQuadMesh(float half)
        {
            var mesh = new Mesh { name = "MidStackFillQuad" };
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
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>GPU-context guard (LayerOrderSnapshotTests pattern): if the render came back all-black,
        /// confirm against a blank-camera control before flagging Inconclusive (not a failure).</summary>
        private static bool GpuContextInconclusive(SnapshotRenderer snap)
        {
            if (!snap.IsAllBlack()) return false;

            var (blankGo, blankCam) = BuildCamera();
            try
            {
                using var blank = new SnapshotRenderer(SnapW, SnapH);
                blank.Render(blankCam);
                if (blank.IsAllBlack())
                {
                    Assert.Inconclusive(
                        "Background render and blank-control are all-black: no GPU context in batch " +
                        "EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                    return true;
                }
            }
            finally { Object.DestroyImmediate(blankGo); }
            return false;
        }

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("BackgroundSnapshotCamera");
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

        private static GameObject BuildLight()
        {
            var lightGo = new GameObject("BackgroundSnapshotLight");
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
            return lightGo;
        }
    }
}
