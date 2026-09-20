using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Epic A / A2 — tooth §F.4: a style's <c>background</c> layer renders at its declared draw slot (the
    /// style's colour, not the camera clear) and composites mid-stack (occludes a layer declared below it,
    /// is occluded by one declared above it). MIGRATED from E3's bare-<c>RenderLayerSet</c> harness (plan §E
    /// step 10, §G risk 1): A2 moves background's geometry from a self-owned world-cap
    /// <see cref="MeshRenderer"/> (visible on a bare-set render) to per-covered-tile meshes owned by the
    /// backend (visible only through <see cref="MapRenderer.Unity.Rendering.Tile.TileManager"/>'s
    /// cover→build→consume loop) — so this harness now drives a real <see cref="MapView"/> over a small
    /// deterministic cover (<see cref="MapRenderer.Tests.MapViewTestExtensions.LoadTestStyle"/>) and frames
    /// the render camera on ONE specific loaded tile's own container position (read from the GameObject
    /// backend's live Transform hierarchy — a frustum-selected cover is not guaranteed to be a solid square
    /// block, so framing on the whole cover's bounding-box centre can land in an uncovered gap), instead of a bare
    /// <see cref="MapRenderer.Unity.Rendering.Style.RenderLayerSet"/> render. The ASSERTIONS are unchanged
    /// (green/red-dominant centre sample; mid-stack occlude-below / occluded-by-above) — only the geometry
    /// SOURCE moved (plan §G risk 1: "Mercator background visually preserved", not "byte-identical pixels
    /// through an unchanged harness").
    ///
    /// GPU-context guard: if renders come back all-black (no GPU context in batch EditMode), the test goes
    /// Inconclusive (not a failure).
    /// </summary>
    [TestFixture]
    public class BackgroundSnapshotTests
    {
        private const int   SnapW = 512;
        private const int   SnapH = 512;
        private const float CamY  = 200f;

        // Central sample sub-rect (pixels), well inside the framed footprint, away from its edges.
        private const int SX0 = 216, SY0 = 216, SX1 = 296, SY1 = 296;

        // The mid-stack tooth's small hand-built fill quad's footprint (a FRACTION of the framed footprint,
        // §B below) and its centred/corner sample rects — the fill quad sits centred in the frame, so a
        // corner sample lands outside it while staying inside the camera frustum (and inside the covered
        // background's real per-tile extent — see FrameFraction/FillFraction below).
        private const int FillCenterX0 = 236, FillCenterY0 = 236, FillCenterX1 = 276, FillCenterY1 = 276;
        private const int CornerX0 = 20, CornerY0 = 20, CornerX1 = 60, CornerY1 = 60;

        // Deterministic single-source cover: an INTERIOR look-at (never a Mercator tile-grid corner — lon=0/
        // lat=0 sits exactly on a 4-tile seam at every integer zoom ≥1, which would put the sample regions on
        // a sub-pixel gap between adjacent per-tile background quads) at a fixed zoom, mirroring
        // PreparedCacheTests' TrackedTile pattern.
        private const int Zoom = 4;
        private static readonly CameraProperties LookAt =
            new CameraProperties(new GeoCoordinate3D { Longitude = 10, Latitude = 10, Altitude = 0 }, Zoom, 0, 0);

        // The camera frustum stays well inside the covered background's real per-tile footprint (never
        // samples past its true edge into the camera clear) — see the design note on RenderCameraOrthoSize.
        private const float FrameFraction = 0.2f;
        // The hand-built fill quad's half-extent, as a fraction of the frustum half-size — small enough that
        // the corner sample (§ above) sits clearly outside it, large enough that the centre sample sits
        // clearly inside it.
        private const float FillFraction = 0.15f;

        private static (GameObject go, MapView view) NewView()
        {
            var go   = new GameObject("BackgroundSnapshotMapView");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = Zoom;
            view.Config.TileSelection.MaxZoom = Zoom;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            // GameObject backend: a frustum-selected cover is not guaranteed to be a solid square block (it
            // can be sparse/diamond-shaped near the horizon), so framing on the WHOLE cover's bounding-box
            // CENTER (the naive ComputeSceneBounds idiom) can land in an uncovered gap. Framing on ONE
            // specific loaded tile's own container position (below) is robust regardless of cover shape —
            // and requires reading the live Transform hierarchy (GameObjectTileRendererTests' pattern).
            view.Config.Backend = RenderBackend.GameObject;
            return (go, view);
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
            }
        }

        /// <summary>The world-space bounds of ONE specific loaded (background) tile — its container's own
        /// SW-corner position (read from the live GameObject backend hierarchy) expanded to the tile's own
        /// physical size. Framing on a SINGLE real tile (rather than the whole cover's bounding box) is
        /// robust regardless of the frustum-selected cover's shape (§NewView).</summary>
        private static Bounds LoadedBounds(MapView view)
        {
            float tileSize = (float)(WebMercator.WorldExtent * 2.0 / math.pow(2.0, Zoom));

            var keys = new List<LoadedTileKey>();
            view.TileManager.CollectLoadedTileKeys(keys);
            Assert.Greater(keys.Count, 0, "at least one background tile must be loaded.");

            var gor = view.GameObjectRenderer();
            Assert.IsNotNull(gor, "the GameObject backend must be selected for this deterministic-framing harness.");
            Transform container = gor.Container(keys[0].Tile);
            Assert.IsNotNull(container, $"a container must exist for the loaded tile {keys[0].Tile}.");

            // Container position is the tile's SW-corner render origin (ComputeSceneBounds' own convention) —
            // the bounds CENTER is half a tile further in +X/+Z.
            Vector3 sw = container.position;
            Vector3 center = new Vector3(sw.x + tileSize * 0.5f, 0f, sw.z + tileSize * 0.5f);
            return new Bounds(center, new Vector3(tileSize, 1f, tileSize));
        }

        [Test]
        public void BackgroundColor_StyleHonoured_NotCameraClear()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
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

                // Falsifier: a DIFFERENT style background must sample DIFFERENTLY. Under the camera-clear
                // hack both renders would sample the same slate clear colour regardless of the style — this
                // fails by construction against that implementation.
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
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
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

        /// <summary>Drives a background-only style through a real <see cref="MapView"/> cover, renders, and
        /// returns the centre-region mean colour (or null on the GPU-context guard). Tears the view down
        /// before returning.</summary>
        private static double[] RenderBackgroundOnlyStyle(Camera camera, string colorHex, string pngName)
        {
            string json = $@"{{ ""version"": 8, ""layers"": [
                {{ ""id"": ""bg"", ""type"": ""background"", ""paint"": {{ ""background-color"": ""{colorHex}"" }} }}
            ] }}";

            var (go, view) = NewView();
            try
            {
                view.LoadTestStyle(null, LookAt, StyleParser.Parse(json)); // background is source-less — the injected source is never consulted
                PumpUntilSettled(view);

                Bounds b = LoadedBounds(view);
                Assert.Greater(b.size.magnitude, 0f, "background must produce non-degenerate render-space bounds");
                FrameCamera(camera, b);

                using var snap = new SnapshotRenderer(SnapW, SnapH);
                snap.Render(camera);
                snap.WritePng(pngName);

                if (GpuContextInconclusive(snap)) return null;

                return SnapshotCoverage.SampleRegionMeanColor(snap.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary>Drives the given two-layer (fill + background) style through a real <see cref="MapView"/>
        /// cover (background is per-tile, produced by the real pipeline; the fill layer declares no
        /// <c>source</c> — same as the pre-A2 harness — so it never fetches and is hand-quaded here, using
        /// the SET's own fill material, exactly as before), renders, and returns the centre-region mean
        /// colour (<paramref name="corner"/> gets the corner-region mean). Null (and <paramref name="corner"/>
        /// null) on the GPU-context guard.</summary>
        private static double[] RenderMidStackStyle(
            Camera camera, string styleJson, int fillIndex, string pngName, out double[] corner)
        {
            corner = null;

            var (go, view) = NewView();
            GameObject fillGo = null;
            Mesh fillMesh = null;
            try
            {
                view.LoadTestStyle(null, LookAt, StyleParser.Parse(styleJson));
                PumpUntilSettled(view);

                Bounds b = LoadedBounds(view);
                Assert.Greater(b.size.magnitude, 0f, "background must produce non-degenerate render-space bounds");
                float halfFrame = FrameCamera(camera, b);

                Material fillMat = view.Layers[fillIndex].Material;
                fillMat.SetColor("_BaseColor", new Color(1f, 0f, 0f, 1f)); // give the fill quad visible pixels, distinct from green

                fillMesh = BuildFillQuadMesh(halfFrame * FillFraction);
                fillGo = new GameObject("MidStackFillQuad");
                fillGo.transform.position = b.center;
                fillGo.AddComponent<MeshFilter>().sharedMesh = fillMesh;
                fillGo.AddComponent<MeshRenderer>().sharedMaterial = fillMat;

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
                if (fillMesh != null) Object.DestroyImmediate(fillMesh);
                if (fillGo != null) Object.DestroyImmediate(fillGo);
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Frames <paramref name="camera"/> (top-down orthographic) on <paramref name="bounds"/>'
        /// centre, with a frustum comfortably (<see cref="FrameFraction"/>) inside the covered background's
        /// real per-tile footprint — so no sample ever crosses the true tile-cover edge into the camera
        /// clear. Returns the resulting orthographic HALF-size (world units) for the caller's own
        /// footprint-relative placement (e.g. the mid-stack fill quad).</summary>
        private static float FrameCamera(Camera camera, in Bounds bounds)
        {
            float half = Mathf.Min(bounds.size.x, bounds.size.z) * 0.5f * FrameFraction;
            camera.transform.position = new Vector3(bounds.center.x, bounds.center.y + CamY, bounds.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographicSize   = half;
            return half;
        }

        /// <summary>The <c>LayerOrderSnapshotTests.BuildFillQuad</c> vertex-attribute recipe. This hand-built
        /// quad does NOT flow through <c>StyledFillTileBuilder</c>'s boundary winding reversal, so it is wound to be
        /// Unity-front under the shipped <c>MapFill.mat _Cull:2</c> (stock Cull Back): viewed from above (+Y
        /// normal) the front-facing order is <c>{0,2,1,0,3,2}</c>.</summary>
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
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
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
            camera.orthographic       = true;
            camera.farClipPlane       = 1e9f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = new Color(0.10f, 0.11f, 0.15f, 1f); // camera clear — must NOT be what a declared background samples as
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
