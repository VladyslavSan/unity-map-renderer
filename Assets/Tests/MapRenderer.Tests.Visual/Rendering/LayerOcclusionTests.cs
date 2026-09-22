// Layer-occlusion and layer-compositing GPU/visual acceptance tests (UMR-176 pack: render-layers topic).
//
// Both members pin one layer occluding/being occluded by another in the composited draw
// stack (background vs. surrounding layers; a symbol layer vs. a fill layer by draw index).
// SymbolLayerOrderSnapshotTests lands here (render-layers), not in Text/Placement — all
// four of its tests pin draw order / render-layer occlusion, not text placement geometry
// (docs/commit-conventions.md: render-layers is IRenderLayer/draw-order/material indexing;
// the Symbol/Text/Icon naming rule governs what a TYPE is named, not which topic a TEST is in).
// Also split from RenderLayerTests.cs by the same CameraProperties collision as
// BackendCameraTests.cs above: both members here use the bare Core.Geo.CameraProperties
// constructor.
//
// Contents:
//   BackgroundSnapshotTests        — Epic A / A2 — tooth §F.4: a style's background layer renders at its declared draw slot (the style's colour, not the camera clear) and composites mid-stack (occludes a layer declared below it, is occluded by one declared above it).
//   SymbolLayerOrderSnapshotTests  — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using System.IO;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolPlan
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // BackgroundSnapshotTests — Epic A / A2 — tooth §F.4
    // ───────────────────────────────────────────────────────────────────────────────────

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
    /// </summary>
    [TestFixture]
    public class BackgroundSnapshotTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = UnityEngine.Rendering.AmbientMode.Flat,
            AmbientLight = new Color(0.9f, 0.9f, 0.9f, 1f),
        };

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
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var lightGo = Track(BuildLight());
            {
                double[] greenMean = RenderBackgroundOnlyStyle(camera, "#00ff00", "green-background.png");

                Debug.Log($"[BackgroundSnapshot] green style: meanRGB=({greenMean[0]:F3},{greenMean[1]:F3},{greenMean[2]:F3})");
                Assert.Greater(greenMean[1], greenMean[0], "A green background-color must sample green-dominant, not the slate clear.");
                Assert.Greater(greenMean[1], greenMean[2], "A green background-color must sample green-dominant, not the slate clear.");

                // Falsifier: a DIFFERENT style background must sample DIFFERENTLY. Under the camera-clear
                // hack both renders would sample the same slate clear colour regardless of the style — this
                // fails by construction against that implementation.
                double[] redMean = RenderBackgroundOnlyStyle(camera, "#ff0000", "red-background.png");

                Debug.Log($"[BackgroundSnapshot] red style: meanRGB=({redMean[0]:F3},{redMean[1]:F3},{redMean[2]:F3})");
                Assert.Greater(redMean[0], redMean[1], "A red background-color must sample red-dominant.");
                Assert.Greater(redMean[0], redMean[2], "A red background-color must sample red-dominant.");
            }
        }

        [Test]
        public void Background_MidStack_OccludesBelow_IsOccludedByAbove()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var lightGo = Track(BuildLight());
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

                Debug.Log($"[BackgroundSnapshot] B (bg,fill) centre: meanRGB=({bCenter[0]:F3},{bCenter[1]:F3},{bCenter[2]:F3}), " +
                          $"corner: meanRGB=({bCorner[0]:F3},{bCorner[1]:F3},{bCorner[2]:F3})");
                Assert.IsFalse(bCenter[1] > bCenter[0] && bCenter[1] > bCenter[2],
                    "The fill declared ABOVE the background must win at centre — NOT green-dominant.");
                Assert.Greater(bCorner[1], bCorner[0], "The background must still show at the corner, outside the fill quad's footprint.");
                Assert.Greater(bCorner[1], bCorner[2], "The background must still show at the corner, outside the fill quad's footprint.");
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

            using var bag = new ObjectDisposalBag();
            var (go, view) = NewView();
            bag.Track(go);
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


                return SnapshotCoverage.SampleRegionMeanColor(snap.Pixels, SX0, SY0, SX1, SY1);
            }
            finally { view.Teardown(); }
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

            using var bag = new ObjectDisposalBag();
            var (go, view) = NewView();
            bag.Track(go);
            try
            {
                view.LoadTestStyle(null, LookAt, StyleParser.Parse(styleJson));
                PumpUntilSettled(view);

                Bounds b = LoadedBounds(view);
                Assert.Greater(b.size.magnitude, 0f, "background must produce non-degenerate render-space bounds");
                float halfFrame = FrameCamera(camera, b);

                Material fillMat = view.Layers[fillIndex].Material;
                fillMat.SetColor("_BaseColor", new Color(1f, 0f, 0f, 1f)); // give the fill quad visible pixels, distinct from green

                Mesh fillMesh = bag.Track(BuildFillQuadMesh(halfFrame * FillFraction));
                var fillGo = bag.Track(new GameObject("MidStackFillQuad"));
                fillGo.transform.position = b.center;
                fillGo.AddComponent<MeshFilter>().sharedMesh = fillMesh;
                fillGo.AddComponent<MeshRenderer>().sharedMaterial = fillMat;

                using var snap = new SnapshotRenderer(SnapW, SnapH);
                snap.Render(camera);
                snap.WritePng(pngName);


                corner = SnapshotCoverage.SampleRegionMeanColor(snap.Pixels, CornerX0, CornerY0, CornerX1, CornerY1);
                return SnapshotCoverage.SampleRegionMeanColor(
                    snap.Pixels, FillCenterX0, FillCenterY0, FillCenterX1, FillCenterY1);
            }
            finally
            {
                view.Teardown();
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

    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
    // NOT registered in core-tests.csproj.
    //
    // E2 acceptance teeth (the render-layer model) — now real snapshot tests
    // because option (c)'s persistent per-slot MeshRenderer is the PROVEN-HEADLESS path (§5): unlike the retired
    // Graphics.RenderMesh submit (0 px headless — SymbolAtlasOrientationSnapshotTests' header), a scene
    // MeshRenderer Unity redraws on its own renders normally under a manually-invoked Camera.Render().
    //
    // Setup fuses two proven harnesses: the real-glyph symbol pipeline from SymbolAtlasOrientationSnapshotTests
    // (fixture atlas, CodepointTextShaper, real SymbolPlacementSystem) and the queue/composite scene from
    // LayerOrderSnapshotTests (the QualitySettings/ambient/light recipe).
    // SymbolRenderLayers are built directly via SymbolRenderLayer.Create, bypassing RenderLayerSet.Build — so
    // each test writes the renderQueue itself (`TransparentQueue + drawIndex`, one sub-slot's worth — these
    // tests order two TEXT materials against each other / against an occluding line, never a layer's own
    // icon-vs-text pair, so the G7/D7 sub-slot band is inert here and the hand-written values stay valid),
    // mirroring exactly what RenderLayerSet.Build does in production for a single sub-slot. EVERY Create call
    // below additionally writes its own WorldIconMaterial queue at QueueFor(drawIndex, Base), which can sit
    // ABOVE the text queue the test hand-writes — inert in all of them for the reason above; the first such
    // site carries the worked example.
    //
    // Tooth 1's occluder is a wide LINE ribbon, not a fill quad, so it exercises the REAL production line
    // vertex layout (StyledLineTileBuilder.LinePositionNormal/LineWidthColor) rather than the generic Lit
    // vertex streams a hand-built Vector3[]/Vector2[] quad carries. (A hand-built quad DOES render headless
    // once wound Unity-front — see LayerOrderSnapshotTests.BuildFillQuad and its §7.11 winding note — and that
    // file's variance tooth now carries a non-vacuous top-fill guard; the line here is a vertex-layout choice,
    // not a workaround for an invisible quad.)
    // SyntheticLineMesh builds the real production vertex layout, proven to render headless (it backs
    // LayerOrderSnapshotTests.BuildWideLine). renderQueue-vs-symbol compositing doesn't care which layer KIND
    // produced the geometry, so a line still proves tooth 1's claim (higher-queue geometry composites over a
    // symbol layer's symbols) — see BuildOccludingLineRibbon.
    //
    // Sample-point discovery: rather than predicting exactly which pixels a real glyph's ink lands on (an 'A'
    // has a hollow counter — its centroid is not reliably inked), every ink-sensitive test first renders the
    // LABEL ALONE and finds the pixel whose colour is closest to the expected ink colour (necessarily deep
    // inside the glyph body, not an AA edge) — then samples a small box around THAT pixel in the composite
    // render. This is robust to exact glyph shape/position and needs no a-priori pixel math.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolLayerOrderSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolLayerOrderSnapshotTests : BaseTestFixture
    {
        private const int Size = 512;
        private static readonly Color BgColor = new Color(0.05f, 0.05f, 0.08f, 1f); // near-black — distinct from every ink/fill colour below

        private const float OccluderHalfExtent = 200_000f; // huge — covers the whole frustum regardless of camera altitude/FOV (Y=0 plane, camera looks straight down at world origin — MapCamera at tilt 0)

        // ── shared harness: real fixture glyph 'A' (SymbolAtlasOrientationSnapshotTests) ──────────────────

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;
            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        private static (GlyphAtlasTexture texture, List<SymbolQuad> quads, TextLayoutBounds bounds) BuildGlyphA()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);
            return (texture, quads, bounds);
        }

        private static void AddCenteredSymbol(SymbolTileBuffer buffer, List<SymbolQuad> quads, TextLayoutBounds bounds,
            double3 sceneOriginRender, float4 textColor, int materialIndex, bool allowOverlap = false)
            => TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, quads, bounds.Min, bounds.Max, // exactly at look-at → renders centred on screen
                paint: new SymbolPaint { TextColor = textColor, Opacity = 1f },
                textSizePx: 200f,
                sortKey: 0f,
                featureIndex: 0,
                // Epic A / A1 Risk R1: a realistic containing tile (BuildOverheadScene's look-at) keeps the
                // world-anchored bake float32-safe — TileKey=0 is ~2e7m away (see
                // SymbolAtlasOrientationSnapshotTests' identical note).
                tileKey: TestTileKeys.PackedContaining(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14),
                materialIndex: materialIndex,
                allowOverlap: allowOverlap);

        // ── shared harness: overhead camera + queue/fill-quad scene (LayerOrderSnapshotTests) ────────────

        private static (GameObject camGo, Camera cam, MapCamera mapCamera, SceneFrame frame) BuildOverheadScene()
        {
            var camGo = new GameObject("SymbolLayerOrder_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = BgColor;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };
            return (camGo, uCam, mapCamera, frame);
        }

        // Ambient + quality setup so a real Map/Fill material (lit) reads back strongly — copied verbatim
        // from LayerOrderSnapshotTests.CoplanarLayers_CompositeCleanly_LowVariance.
        private static (int prevQuality, UnityEngine.Rendering.AmbientMode prevMode, Color prevLight) SetupLitAmbient()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevMode = RenderSettings.ambientMode;
            var prevLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            return (prevQuality, prevMode, prevLight);
        }

        private static void RestoreAmbient((int prevQuality, UnityEngine.Rendering.AmbientMode prevMode, Color prevLight) saved)
        {
            QualitySettings.SetQualityLevel(saved.prevQuality, false);
            RenderSettings.ambientMode = saved.prevMode;
            RenderSettings.ambientLight = saved.prevLight;
        }

        private static GameObject BuildDirectionalLight()
        {
            var lightGo = new GameObject("SymbolLayerOrder_DirLight");
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            return lightGo;
        }

        // A HUGE line ribbon through the world origin at Y=0 (the ground plane a MapCamera at tilt 0 looks
        // straight down at, world origin == the look-at, S52 camera-relative rendering), standing in for a
        // fill occluder — see the header comment for why a fill quad doesn't work here. Built via
        // SyntheticLineMesh (the real production line vertex layout, proven to render headless by
        // LayerOrderSnapshotTests.BuildWideLine), sized to cover the whole frustum regardless of camera
        // altitude/FOV so no camera-specific geometry math is needed.
        private static (Mesh mesh, Material mat) BuildOccludingLineRibbon(Color color, int renderQueue)
        {
            var mesh = SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-OccluderHalfExtent, 0), new double2(OccluderHalfExtent, 0) },
                JoinType.Miter, CapType.Butt);

            var mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Width", OccluderHalfExtent * 2f); // full width in meters — wide enough to cover the frustum
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetFloat("_Opacity", 1f);
            mat.renderQueue = renderQueue;
            return (mesh, mat);
        }

        private static GameObject AttachMesh(Mesh mesh, Material mat, string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // Finds the pixel whose colour is closest to `target` — necessarily deep inside a solid-ink glyph
        // region (not an AA edge), so a small box around it is a robust, position-independent sample point.
        // Returns false (and asserts a minimum closeness) so a solo-render precondition failure surfaces
        // clearly rather than silently sampling a background pixel later.
        private static bool TryFindClosestPixel(Frame frame, Color32 target, out int x, out int y)
        {
            int best = int.MaxValue; x = -1; y = -1;
            for (int row = 0; row < frame.Height; row++)
            {
                for (int col = 0; col < frame.Width; col++)
                {
                    Color32 px = frame[col, row];
                    int dr = px.r - target.r, dg = px.g - target.g, db = px.b - target.b;
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < best) { best = dist; x = col; y = row; }
                }
            }
            return x >= 0 && best <= 40 * 40 * 3; // within ~40/255 per channel of the pure ink colour
        }

        private static double[] SampleAround(Frame frame, int x, int y, int half = 3)
            => SnapshotCoverage.SampleRegionMeanColor(
                frame, x - half, y - half, x + half + 1, y + half + 1);


        // ── Tooth 1 (§6.1): a fill at a HIGHER queue occludes symbols; a fill BELOW them does not ──────────

        [Test]
        public void FillAboveSymbolLayer_OccludesSymbols_FillBelow_SymbolWins()
        {
            var saved = SetupLitAmbient();
            var (camGo, cam, mapCamera, frame) = BuildOverheadScene();
            Track(camGo);
            var lightGo = Track(BuildDirectionalLight());
            var (glyphAtlas, quads, bounds) = BuildGlyphA();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-color"": ""#ffffff"" } }
                ]
            }";
            // text-color: white — a CONSTANT text-color now binds _TextColor (style-transitions epic), so
            // leaving it at the spec default (black) would multiply the hand-injected vertex ink below to
            // black. White is the uniform's identity, so this test's direct SymbolPaint injection (bypassing
            // SymbolFeatureExtractor.EvaluatePaint) reaches the pixel untouched, as it always has.
            StyleDocument style = StyleParser.Parse(StyleJson);
            var symbolLayer = (Symbol.StyleLayer)style.Layers[0];
            var settings = MapMaterialSetTestUtil.Load();

            var greenSymbolColor = new Color32(26, 217, 26, 255); // (0.1, 0.85, 0.1) in 0-255
            var redOccluderColor = new Color(0.85f, 0.1f, 0.1f, 1f);

            // G7/D7 note: Create(drawIndex: 1) now also writes WorldIconMaterial.renderQueue itself, at
            // QueueFor(1, Base) = 3002 — ABOVE the TransparentQueue + 1 = 3001 this test hand-writes to
            // Material (the text) next. That is inert here: this test never stages or renders an icon quad,
            // only the text glyph, so the icon material's queue is set but never composited against
            // anything. Left as-is per the Phase 4 fence — rewriting this scene's hand-written queues to
            // QueueFor(...) is the riskiest available edit for zero contract gain.
            var renderLayer = SymbolRenderLayer.Create(symbolLayer, settings, 8.0, drawIndex: 1);
            Assert.IsNotNull(renderLayer.Material, "MapMaterialSet.SymbolTextWorld must be assigned (asserted by MapMaterialSetTestUtil.Load).");
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;

            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var buffer = new SymbolTileBuffer();
            AddCenteredSymbol(buffer, quads, bounds, frame.SceneOriginRender, new float4(0.1f, 0.85f, 0.1f, 1f), materialIndex: 0);
            var layers = new List<SymbolRenderLayer> { renderLayer };

            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(buffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(buffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(1, system.LastQuadCount, "the single 'A' glyph must place (precondition, not the tooth itself).");

                // 1. Solo render (no occluder yet) — find the sample point deep inside the glyph's ink.
                snap.Render(cam);
                bool found = TryFindClosestPixel(snap.Pixels, greenSymbolColor, out int ix, out int iy);
                Assert.IsTrue(found, "solo label render must contain a pixel close to the label's ink colour — " +
                                      "precondition for the occlusion sample point.");

                // 2. Add the occluder ABOVE the symbol layer's queue (+2 > +1) — it must occlude the symbol.
                (Mesh occluderMesh, Material occluderMat) = BuildOccludingLineRibbon(redOccluderColor, LayerDrawOrder.TransparentQueue + 2);
                Track(occluderMesh);
                Track(occluderMat);
                GameObject occluderGo = Track(AttachMesh(occluderMesh, occluderMat, "Occluder_Above"));
                snap.Render(cam);
                double[] above = SampleAround(snap.Pixels, ix, iy);
                Assert.Greater(above[0], above[1] + 0.15,
                    $"a layer declared ABOVE a symbol layer must occlude its labels — sampled (R,G,B)=({above[0]:F3},{above[1]:F3},{above[2]:F3}) " +
                    "should read occluder-red (R dominant), not label-green. Under the retired Overlay-4000 pin this fails by construction.");

                // 3. Control: occluder BELOW the symbol layer's queue (+0 < +1) — the symbol must win instead.
                occluderMat.renderQueue = LayerDrawOrder.TransparentQueue + 0;
                snap.Render(cam);
                double[] below = SampleAround(snap.Pixels, ix, iy);
                Assert.Greater(below[1], below[0],
                    $"a layer declared BELOW a symbol layer must NOT occlude it — sampled (R,G,B)=({below[1]:F3},{below[0]:F3},{below[2]:F3}) " +
                    "should read label-green over occluder-red. (Falsifier of the falsifier: proves tooth 1's occlusion above was a real queue effect, not a fixed 'label never shows' bug.)");
            }
            finally
            {
                renderLayer.Dispose(); // presenter BEFORE system disposes its meshes (risk #3)
                system.Dispose();
                glyphAtlas.Dispose();
                RestoreAmbient(saved);
            }
        }

        // ── Tooth 2 (§6.2): declared order among symbol layers — later DrawIndex wins the overlap ────────

        [Test]
        public void TwoSymbolLayers_SameAnchor_LaterDrawIndexWins_SwapFlipsWinner()
        {
            var saved = SetupLitAmbient();
            var (camGo, cam, mapCamera, frame) = BuildOverheadScene();
            Track(camGo);
            var lightGo = Track(BuildDirectionalLight());
            var (glyphAtlas, quads, bounds) = BuildGlyphA();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""a"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""la"", ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-color"": ""#ffffff"" } },
                    { ""id"": ""b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""lb"", ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-color"": ""#ffffff"" } }
                ]
            }";
            // text-color: white on both layers — see the identical note on this file's first test.
            StyleDocument style = StyleParser.Parse(StyleJson);
            var settings = MapMaterialSetTestUtil.Load();
            var layerA = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[0], settings, 8.0, drawIndex: 1);
            var layerB = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[1], settings, 8.0, drawIndex: 2);
            layerA.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;
            layerB.Material.renderQueue = LayerDrawOrder.TransparentQueue + 2;

            var redColor  = new Color32(230, 38, 26, 255);  // (0.9, 0.15, 0.1)
            var blueColor = new Color32(26, 51, 230, 255);  // (0.1, 0.2, 0.9)

            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { layerA, layerB };

            using var snap = new SnapshotRenderer(Size, Size);
            // Solo render of symbol A alone (layerA only) to find the ink sample point — labelA/labelB share
            // the identical glyph/anchor/size, so the same footprint applies to both. ONE TestSymbolPlan
            // instance serves every tick in this test: SymbolPlacementSystem skips the native-mirror refresh
            // unless the source's IDENTITY or version changed, and TestSymbolPlan reuses one SymbolGatherPlan
            // whose WinnerSetVersion it advances per Build — two independently-constructed plans would each
            // start at version 1 and could collide on that skip guard.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                var soloBuffer = new SymbolTileBuffer();
                AddCenteredSymbol(soloBuffer, quads, bounds, frame.SceneOriginRender, new float4(0.9f, 0.15f, 0.1f, 1f), materialIndex: 0, allowOverlap: true);

                var bothBuffer = new SymbolTileBuffer();
                AddCenteredSymbol(bothBuffer, quads, bounds, frame.SceneOriginRender, new float4(0.9f, 0.15f, 0.1f, 1f), materialIndex: 0, allowOverlap: true);
                AddCenteredSymbol(bothBuffer, quads, bounds, frame.SceneOriginRender, new float4(0.1f, 0.2f, 0.9f, 1f), materialIndex: 1, allowOverlap: true);

                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                var layerAOnly = new List<SymbolRenderLayer> { layerA };
                system.Tick(in frame, plan.Build(soloBuffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layerAOnly);
                system.Tick(in frame, plan.Build(soloBuffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layerAOnly);
                Assert.AreEqual(1, system.LastQuadCount, "label A alone must place (precondition).");
                snap.Render(cam);
                bool found = TryFindClosestPixel(snap.Pixels, redColor, out int ix, out int iy);
                Assert.IsTrue(found, "solo label-A render must contain a pixel close to its ink colour — precondition for the sample point.");

                // Both symbols, same anchor, AllowOverlap — collision keeps both (the tooth is DRAW order, not collision).
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(bothBuffer, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(bothBuffer, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(2, system.LastQuadCount, "both overlapping labels place (AllowOverlap — the tooth is draw order, not collision).");

                snap.Render(cam);
                double[] bFirst = SampleAround(snap.Pixels, ix, iy);
                Assert.Greater(bFirst[2], bFirst[0],
                    $"layer B (DrawIndex 2, later/higher queue) must win the overlap — sampled (R,B)=({bFirst[0]:F3},{bFirst[2]:F3}) should read layer B's blue.");

                // Swap declared order (mutate renderQueue in place — same materials, same presenters).
                layerA.Material.renderQueue = LayerDrawOrder.TransparentQueue + 2;
                layerB.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;
                // Epic A / A1 (D5): points now draw through the WORLD path, whose queue lives on a SEPARATE
                // material (WorldTextMaterial) synced from Material.renderQueue at Tick/present time (the
                // PresentIcon precedent) — unlike the OLD path (presenter bound directly to Material, so a
                // live queue mutation took effect on the very next Render with no re-Tick). A re-Tick here
                // matches real usage (production always Ticks before every Render); rebuilding through the
                // SAME TestSymbolPlan keeps the mirror-refresh guard satisfied (see its note above).
                system.Tick(in frame, plan.Build(bothBuffer, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(bothBuffer, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                snap.Render(cam);
                double[] aFirst = SampleAround(snap.Pixels, ix, iy);
                Assert.Greater(aFirst[0], aFirst[2],
                    $"after swapping declared order, layer A must now win — sampled (R,B)=({aFirst[0]:F3},{aFirst[2]:F3}) should read layer A's red. " +
                    "(Falsifier: an equal-queue/undefined order would not flip deterministically with the swap.)");
            }
            finally
            {
                layerA.Dispose(); // presenters BEFORE system disposes their meshes (risk #3)
                layerB.Dispose();
                system.Dispose();
                glyphAtlas.Dispose();
                RestoreAmbient(saved);
            }
        }

        // ── Persistence (tooth §6.4's headless proxy): show across repeated renders, hide when empty ─────

        [Test]
        public void Presenter_ShowsAcrossRepeatedRenders_HidesWhenTickIsEmpty()
        {
            var saved = SetupLitAmbient();
            var (camGo, cam, mapCamera, frame) = BuildOverheadScene();
            Track(camGo);
            var lightGo = Track(BuildDirectionalLight());
            var (glyphAtlas, quads, bounds) = BuildGlyphA();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-color"": ""#ffffff"" } }
                ]
            }";
            // text-color: white — see the identical note on this file's first test.
            StyleDocument style = StyleParser.Parse(StyleJson);
            var settings = MapMaterialSetTestUtil.Load();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[0], settings, 8.0, drawIndex: 0);
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 0;

            var inkColor = new Color32(230, 230, 230, 255); // near-white ink, distinct from the near-black background
            var buffer = new SymbolTileBuffer();
            AddCenteredSymbol(buffer, quads, bounds, frame.SceneOriginRender, new float4(0.9f, 0.9f, 0.9f, 1f), materialIndex: 0);
            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            var emptyBuffer = new SymbolTileBuffer();

            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // SHOW half: one Tick with a symbol, render TWICE with no Tick between — no manual
                // MeshFilter/MeshRenderer attach anywhere in this test (E2's whole point: the presenter IS
                // a persistent scene renderer, created/bound entirely inside Tick).
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(buffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(buffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(1, system.LastQuadCount, "the label must place (precondition).");

                snap.Render(cam);
                bool foundFirst = TryFindClosestPixel(snap.Pixels, inkColor, out int ix, out int iy);
                Assert.IsTrue(foundFirst, "first render after Tick must show the label's ink (persistent MeshRenderer, no attach needed).");

                snap.Render(cam); // SAME camera, NO Tick between — proves the presenter persists across repaints
                double[] second = SampleAround(snap.Pixels, ix, iy);
                Assert.Greater(second[0] + second[1] + second[2], 0.5,
                    $"a SECOND render with no Tick between must STILL show ink at ({ix},{iy}) — sampled sum={second[0] + second[1] + second[2]:F3}. " +
                    "Under the retired immediate-mode Graphics.RenderMesh path this would be the Editor blink (0 ink, nothing re-submitted).");

                // HIDE half (risk #1's mirror-image guard): Tick with an EMPTY set → the presenter must hide,
                // not keep drawing last frame's symbol frozen on screen.
                system.Tick(in frame, plan.Build(emptyBuffer), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(0, system.LastQuadCount, "the empty Tick must place nothing (precondition).");

                snap.Render(cam);
                double[] empty = SampleAround(snap.Pixels, ix, iy);
                Assert.Less(empty[0] + empty[1] + empty[2], 0.3,
                    $"after an EMPTY Tick the presenter must be HIDDEN — sampled sum={empty[0] + empty[1] + empty[2]:F3} at the " +
                    "label's old ink location should read background, not stale ink (the mirror image of the blink).");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                glyphAtlas.Dispose();
                RestoreAmbient(saved);
            }
        }

        // ── Collision parity (tooth §6.3): the demo (no layers) and production (real SymbolRenderLayer)
        //    paths must produce IDENTICAL candidate/survivor/quad counts — E2 touches only the DRAW, never
        //    anything upstream of the emit loop. Two fresh SymbolPlacementSystem instances (not one instance
        //    ticked twice) so A-5 sticky-placement incumbency from the first call can't bias the second. ──

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static void AddOverlappingSymbol(SymbolTileBuffer buffer, int featureIndex, double3 sceneOriginRender, float sortKey)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, quads, float2.zero, new float2(18f, 18f), // SAME anchor for both symbols → guaranteed real collision
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                sortKey: sortKey,
                featureIndex: featureIndex,
                tileKey: 0L,
                // R3: PointFadeId hashes (AnchorRender, MaterialIndex, Text, IconImage) — NOT FeatureIndex/TileKey —
                // so two symbols sharing an anchor with the (both-default) Text/MaterialIndex this method used to
                // leave unset would collide on FadeId. Under R3, FadeId is the display key (SymbolCandidate.FadeId's
                // uniqueness contract), so a collision would make the loser show alongside the winner. Distinct Text
                // per symbol keeps the anchors identical (the real collision this test needs) while giving each a
                // unique identity; it does not perturb the staged geometry (its quads are supplied explicitly here).
                text: "L" + featureIndex);
        }

        [Test]
        public void CollisionCounts_AreIdentical_DemoPath_Vs_ProductionPathWithOneSymbolRenderLayer()
        {
            var camGo = Track(new GameObject("CollisionParity_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            var atlasTexture = BuildTinyAtlasTexture();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" } }
                ]
            }";
            var settings = MapMaterialSetTestUtil.Load();
            var renderLayer = SymbolRenderLayer.Create(
                (Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 0;

            var buffer = new SymbolTileBuffer();
            AddOverlappingSymbol(buffer, 0, frame.SceneOriginRender, sortKey: 20f);
            AddOverlappingSymbol(buffer, 1, frame.SceneOriginRender, sortKey: 10f); // lower key wins the collision

            // Step 5b: this compared the symbols-list overload against the SymbolBatch overload — both demo
            // seams, so it compared demo against demo while calling one side "prod". Both are gone. The invariant
            // it actually asserts survives and is now stated directly: supplying per-layer render layers (E2 —
            // each with its own material + persistent presenter) partitions only the DRAW, and must not perturb
            // anything upstream of the emit loop. Same plan, same symbols, layers vs no layers.
            var noLayersSystem = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layeredSystem  = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                SymbolGatherPlan built = plan.Build(buffer);
                Assert.AreEqual(buffer.Symbols.Count, plan.CollectedCount,
                    "precondition: both labels reach the placement path — they share an anchor, so a dedup " +
                    "merge here would silently turn the comparison into 1-vs-2 and read as a real divergence.");

                // R3: duplicate both systems' ticks — the collision verdict is harvested one Tick late (§2.6).
                // Without this, a fresh system's single Tick harvests nothing (LastSurvivorCount == 0 on both
                // sides), and the equality assertions below would pass VACUOUSLY (0 == 0) without ever exercising
                // a real collision — hence the Assert.Greater lines strengthening them against that.
                noLayersSystem.Tick(in frame, built, atlasTexture);
                noLayersSystem.Tick(in frame, built, atlasTexture);

                var symbolLayers = new List<SymbolRenderLayer> { renderLayer };
                layeredSystem.Tick(in frame, built, atlasTexture, deltaTime: float.PositiveInfinity, symbolLayers: symbolLayers);
                layeredSystem.Tick(in frame, built, atlasTexture, deltaTime: float.PositiveInfinity, symbolLayers: symbolLayers);

                Assert.Greater(noLayersSystem.LastCandidateCount, 0, "sanity: candidates were actually staged.");
                Assert.AreEqual(noLayersSystem.LastCandidateCount, layeredSystem.LastCandidateCount,
                    "collision candidate count must be identical — E2 must not touch anything upstream of the emit loop.");
                Assert.Greater(noLayersSystem.LastSurvivorCount, 0,
                    "sanity: a real collision actually ran and produced a survivor (otherwise the equality below could pass vacuously on 0 == 0).");
                Assert.AreEqual(noLayersSystem.LastSurvivorCount, layeredSystem.LastSurvivorCount,
                    "collision survivor count must be identical.");
                Assert.AreEqual(noLayersSystem.LastQuadCount, layeredSystem.LastQuadCount,
                    "emitted quad count must be identical.");
            }
            finally
            {
                renderLayer.Dispose();
                noLayersSystem.Dispose();
                layeredSystem.Dispose();
                atlasTexture.Dispose();
            }
        }
    }
}
