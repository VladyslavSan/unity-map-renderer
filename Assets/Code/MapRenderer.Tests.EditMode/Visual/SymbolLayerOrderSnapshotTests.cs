// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
// NOT registered in core-tests.csproj.
//
// E2 acceptance teeth (the render-layer model) — now real snapshot tests
// because option (c)'s persistent per-slot MeshRenderer is the PROVEN-HEADLESS path (§5): unlike the retired
// Graphics.RenderMesh submit (0 px headless — SymbolAtlasOrientationSnapshotTests' header), a scene
// MeshRenderer Unity redraws on its own renders normally under a manually-invoked Camera.Render().
//
// Setup fuses two proven harnesses: the real-glyph label pipeline from SymbolAtlasOrientationSnapshotTests
// (fixture atlas, CodepointTextShaper, real LabelPlacementSystem) and the queue/composite scene from
// LayerOrderSnapshotTests (QualitySettings/ambient/light setup, the GPU-context Inconclusive guard).
// SymbolRenderLayers are built directly via SymbolRenderLayer.Create, bypassing RenderLayerSet.Build — so
// each test writes the renderQueue itself (`TransparentQueue + drawIndex`), mirroring exactly what
// RenderLayerSet.Build does in production.
//
// Tooth 1's occluder is a wide LINE ribbon, not a fill quad: a hand-built Vector3[]/Vector2[] quad (the
// technique LayerOrderSnapshotTests.BuildFillQuad uses) carries only the generic Lit vertex streams, not
// the real production line vertex layout (StyledLineTileBuilder.LinePositionNormal/LineWidthColor) — and
// was found (this stage) to render invisible headless, silently passing LayerOrderSnapshotTests' own
// variance tooth because that test never actually asserts the TOP fill's colour, only region uniformity.
// SyntheticLineMesh builds the real production vertex layout, proven to render headless (it backs
// LayerOrderSnapshotTests.BuildWideLine). renderQueue-vs-symbol compositing doesn't care which layer KIND
// produced the geometry, so a line still proves tooth 1's claim (higher-queue geometry composites over a
// symbol layer's labels) — see BuildOccludingLineRibbon.
//
// Sample-point discovery: rather than predicting exactly which pixels a real glyph's ink lands on (an 'A'
// has a hollow counter — its centroid is not reliably inked), every ink-sensitive test first renders the
// LABEL ALONE and finds the pixel whose colour is closest to the expected ink colour (necessarily deep
// inside the glyph body, not an AA edge) — then samples a small box around THAT pixel in the composite
// render. This is robust to exact glyph shape/position and needs no a-priori pixel math.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolPlan
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class SymbolLayerOrderSnapshotTests
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
            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        private static (GlyphAtlasTexture texture, TextLayoutResult layout) BuildGlyphA()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u]);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            TextLayoutResult layout = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default);
            return (texture, layout);
        }

        private static LabelInstance MakeCenteredLabel(TextLayoutResult layout, double3 sceneOriginRender,
            float4 textColor, int materialIndex, bool allowOverlap = false)
            => new LabelInstance
            {
                AnchorRender = sceneOriginRender, // exactly at look-at → renders centred on screen
                Layout = layout,
                Paint = new LabelPaint { TextColor = textColor, Opacity = 1f },
                TextSizePx = 200f,
                SortKey = 0f,
                FeatureIndex = 0,
                // Epic A / A1 Risk R1: a realistic containing tile (BuildOverheadScene's look-at) keeps the
                // world-anchored bake float32-safe — TileKey=0 is ~2e7m away (see
                // SymbolAtlasOrientationSnapshotTests' identical note).
                TileKey = MapRenderer.Tests.Text.Placement.TestTileKeys.PackedContaining(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14),
                MaterialIndex = materialIndex,
                AllowOverlap = allowOverlap,
            };

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
        private static bool TryFindClosestPixel(byte[] rgba, int width, int height, Color32 target, out int x, out int y)
        {
            int best = int.MaxValue; x = -1; y = -1;
            for (int row = 0; row < height; row++)
            {
                int rowBase = row * width;
                for (int col = 0; col < width; col++)
                {
                    int i = (rowBase + col) * 4;
                    int dr = rgba[i] - target.r, dg = rgba[i + 1] - target.g, db = rgba[i + 2] - target.b;
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < best) { best = dist; x = col; y = row; }
                }
            }
            return x >= 0 && best <= 40 * 40 * 3; // within ~40/255 per channel of the pure ink colour
        }

        private static double[] SampleAround(byte[] rgba, int x, int y, int half = 3)
            => SnapshotCoverage.SampleRegionMeanColor(
                rgba, Size, Size, x - half, y - half, x + half + 1, y + half + 1);

        private static void AssertNotGpuContextFailure(SnapshotRenderer snap)
        {
            if (!snap.IsAllBlack()) return;
            var blankGo = new GameObject("Blank_TestCamera");
            try
            {
                var blankCam = blankGo.AddComponent<Camera>();
                blankCam.clearFlags = CameraClearFlags.SolidColor;
                blankCam.backgroundColor = BgColor;
                using var blank = new SnapshotRenderer(Size, Size);
                blank.Render(blankCam);
                if (blank.IsAllBlack())
                    Assert.Inconclusive("Scene render and blank-control are both all-black: no GPU context in " +
                                         "batch EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
            }
            finally { Object.DestroyImmediate(blankGo); }
        }

        // ── Tooth 1 (§6.1): a fill at a HIGHER queue occludes labels; a fill BELOW them does not ──────────

        [Test]
        public void FillAboveSymbolLayer_OccludesLabels_FillBelow_LabelWins()
        {
            var saved = SetupLitAmbient();
            var (camGo, cam, mapCamera, frame) = BuildOverheadScene();
            var lightGo = BuildDirectionalLight();
            var (glyphAtlas, layout) = BuildGlyphA();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" } }
                ]
            }";
            StyleDocument style = StyleParser.Parse(StyleJson);
            var symbolLayer = (Symbol.StyleLayer)style.Layers[0];
            var settings = MapMaterialSetTestUtil.Load();

            var greenLabelColor = new Color32(26, 217, 26, 255); // (0.1, 0.85, 0.1) in 0-255
            var redOccluderColor = new Color(0.85f, 0.1f, 0.1f, 1f);

            var renderLayer = SymbolRenderLayer.Create(symbolLayer, settings, 8.0, drawIndex: 1);
            Assert.IsNotNull(renderLayer.Material, "MapMaterialSet.SymbolTextWorld must be assigned (asserted by MapMaterialSetTestUtil.Load).");
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;

            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var label = MakeCenteredLabel(layout, frame.SceneOriginRender, new float4(0.1f, 0.85f, 0.1f, 1f), materialIndex: 0);
            var layers = new List<SymbolRenderLayer> { renderLayer };
            var labelSet = new List<LabelInstance> { label };

            Mesh occluderMesh = null; Material occluderMat = null; GameObject occluderGo = null;
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(labelSet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(labelSet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(1, system.LastQuadCount, "the single 'A' glyph must place (precondition, not the tooth itself).");

                // 1. Solo render (no occluder yet) — find the sample point deep inside the glyph's ink.
                snap.Render(cam);
                AssertNotGpuContextFailure(snap);
                bool found = TryFindClosestPixel(snap.RawPixels, Size, Size, greenLabelColor, out int ix, out int iy);
                Assert.IsTrue(found, "solo label render must contain a pixel close to the label's ink colour — " +
                                      "precondition for the occlusion sample point.");

                // 2. Add the occluder ABOVE the symbol layer's queue (+2 > +1) — it must occlude the label.
                (occluderMesh, occluderMat) = BuildOccludingLineRibbon(redOccluderColor, LayerDrawOrder.TransparentQueue + 2);
                occluderGo = AttachMesh(occluderMesh, occluderMat, "Occluder_Above");
                snap.Render(cam);
                double[] above = SampleAround(snap.RawPixels, ix, iy);
                Assert.Greater(above[0], above[1] + 0.15,
                    $"a layer declared ABOVE a symbol layer must occlude its labels — sampled (R,G,B)=({above[0]:F3},{above[1]:F3},{above[2]:F3}) " +
                    "should read occluder-red (R dominant), not label-green. Under the retired Overlay-4000 pin this fails by construction.");

                // 3. Control: occluder BELOW the symbol layer's queue (+0 < +1) — the label must win instead.
                occluderMat.renderQueue = LayerDrawOrder.TransparentQueue + 0;
                snap.Render(cam);
                double[] below = SampleAround(snap.RawPixels, ix, iy);
                Assert.Greater(below[1], below[0],
                    $"a layer declared BELOW a symbol layer must NOT occlude it — sampled (R,G,B)=({below[1]:F3},{below[0]:F3},{below[2]:F3}) " +
                    "should read label-green over occluder-red. (Falsifier of the falsifier: proves tooth 1's occlusion above was a real queue effect, not a fixed 'label never shows' bug.)");
            }
            finally
            {
                if (occluderGo != null) Object.DestroyImmediate(occluderGo);
                if (occluderMesh != null) Object.DestroyImmediate(occluderMesh);
                if (occluderMat != null) Object.DestroyImmediate(occluderMat);
                renderLayer.Dispose(); // presenter BEFORE system disposes its meshes (risk #3)
                system.Dispose();
                glyphAtlas.Dispose();
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(camGo);
                RestoreAmbient(saved);
            }
        }

        // ── Tooth 2 (§6.2): declared order among symbol layers — later DrawIndex wins the overlap ────────

        [Test]
        public void TwoSymbolLayers_SameAnchor_LaterDrawIndexWins_SwapFlipsWinner()
        {
            var saved = SetupLitAmbient();
            var (camGo, cam, mapCamera, frame) = BuildOverheadScene();
            var lightGo = BuildDirectionalLight();
            var (glyphAtlas, layout) = BuildGlyphA();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""a"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""la"", ""layout"": { ""text-field"": ""{NAME}"" } },
                    { ""id"": ""b"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""lb"", ""layout"": { ""text-field"": ""{NAME}"" } }
                ]
            }";
            StyleDocument style = StyleParser.Parse(StyleJson);
            var settings = MapMaterialSetTestUtil.Load();
            var layerA = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[0], settings, 8.0, drawIndex: 1);
            var layerB = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[1], settings, 8.0, drawIndex: 2);
            layerA.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;
            layerB.Material.renderQueue = LayerDrawOrder.TransparentQueue + 2;

            var redColor  = new Color32(230, 38, 26, 255);  // (0.9, 0.15, 0.1)
            var blueColor = new Color32(26, 51, 230, 255);  // (0.1, 0.2, 0.9)
            var labelA = MakeCenteredLabel(layout, frame.SceneOriginRender, new float4(0.9f, 0.15f, 0.1f, 1f), materialIndex: 0, allowOverlap: true);
            var labelB = MakeCenteredLabel(layout, frame.SceneOriginRender, new float4(0.1f, 0.2f, 0.9f, 1f), materialIndex: 1, allowOverlap: true);

            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { layerA, layerB };

            using var snap = new SnapshotRenderer(Size, Size);
            // Solo render of label A alone (layerA only) to find the ink sample point — labelA/labelB share
            // the identical glyph/anchor/size, so the same footprint applies to both. ONE TestSymbolPlan
            // instance serves every tick in this test: LabelPlacementSystem skips the native-mirror refresh
            // unless the source's IDENTITY or version changed, and TestSymbolPlan reuses one SymbolGatherPlan
            // whose WinnerSetVersion it advances per Build — two independently-constructed plans would each
            // start at version 1 and could collide on that skip guard.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                var soloSet = new List<LabelInstance> { labelA };
                var bothSet = new List<LabelInstance> { labelA, labelB };
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                var layerAOnly = new List<SymbolRenderLayer> { layerA };
                system.Tick(in frame, plan.Build(soloSet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layerAOnly);
                system.Tick(in frame, plan.Build(soloSet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layerAOnly);
                Assert.AreEqual(1, system.LastQuadCount, "label A alone must place (precondition).");
                snap.Render(cam);
                AssertNotGpuContextFailure(snap);
                bool found = TryFindClosestPixel(snap.RawPixels, Size, Size, redColor, out int ix, out int iy);
                Assert.IsTrue(found, "solo label-A render must contain a pixel close to its ink colour — precondition for the sample point.");

                // Both labels, same anchor, AllowOverlap — collision keeps both (the tooth is DRAW order, not collision).
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(bothSet, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(bothSet, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(2, system.LastQuadCount, "both overlapping labels place (AllowOverlap — the tooth is draw order, not collision).");

                snap.Render(cam);
                double[] bFirst = SampleAround(snap.RawPixels, ix, iy);
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
                system.Tick(in frame, plan.Build(bothSet, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(bothSet, slotCount: 2), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                snap.Render(cam);
                double[] aFirst = SampleAround(snap.RawPixels, ix, iy);
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
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(camGo);
                RestoreAmbient(saved);
            }
        }

        // ── Persistence (tooth §6.4's headless proxy): show across repeated renders, hide when empty ─────

        [Test]
        public void Presenter_ShowsAcrossRepeatedRenders_HidesWhenTickIsEmpty()
        {
            var saved = SetupLitAmbient();
            var (camGo, cam, mapCamera, frame) = BuildOverheadScene();
            var lightGo = BuildDirectionalLight();
            var (glyphAtlas, layout) = BuildGlyphA();

            const string StyleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" } }
                ]
            }";
            StyleDocument style = StyleParser.Parse(StyleJson);
            var settings = MapMaterialSetTestUtil.Load();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[0], settings, 8.0, drawIndex: 0);
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 0;

            var inkColor = new Color32(230, 230, 230, 255); // near-white ink, distinct from the near-black background
            var label = MakeCenteredLabel(layout, frame.SceneOriginRender, new float4(0.9f, 0.9f, 0.9f, 1f), materialIndex: 0);
            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            var labelSet = new List<LabelInstance> { label };
            var emptySet = new List<LabelInstance>();

            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // SHOW half: one Tick with a label, render TWICE with no Tick between — no manual
                // MeshFilter/MeshRenderer attach anywhere in this test (E2's whole point: the presenter IS
                // a persistent scene renderer, created/bound entirely inside Tick).
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(labelSet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                system.Tick(in frame, plan.Build(labelSet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(1, system.LastQuadCount, "the label must place (precondition).");

                snap.Render(cam);
                AssertNotGpuContextFailure(snap);
                bool foundFirst = TryFindClosestPixel(snap.RawPixels, Size, Size, inkColor, out int ix, out int iy);
                Assert.IsTrue(foundFirst, "first render after Tick must show the label's ink (persistent MeshRenderer, no attach needed).");

                snap.Render(cam); // SAME camera, NO Tick between — proves the presenter persists across repaints
                double[] second = SampleAround(snap.RawPixels, ix, iy);
                Assert.Greater(second[0] + second[1] + second[2], 0.5,
                    $"a SECOND render with no Tick between must STILL show ink at ({ix},{iy}) — sampled sum={second[0] + second[1] + second[2]:F3}. " +
                    "Under the retired immediate-mode Graphics.RenderMesh path this would be the Editor blink (0 ink, nothing re-submitted).");

                // HIDE half (risk #1's mirror-image guard): Tick with an EMPTY set → the presenter must hide,
                // not keep drawing last frame's label frozen on screen.
                system.Tick(in frame, plan.Build(emptySet), glyphAtlas, deltaTime: float.PositiveInfinity, symbolLayers: layers);
                Assert.AreEqual(0, system.LastQuadCount, "the empty Tick must place nothing (precondition).");

                snap.Render(cam);
                double[] empty = SampleAround(snap.RawPixels, ix, iy);
                Assert.Less(empty[0] + empty[1] + empty[2], 0.3,
                    $"after an EMPTY Tick the presenter must be HIDDEN — sampled sum={empty[0] + empty[1] + empty[2]:F3} at the " +
                    "label's old ink location should read background, not stale ink (the mirror image of the blink).");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                glyphAtlas.Dispose();
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(camGo);
                RestoreAmbient(saved);
            }
        }

        // ── Collision parity (tooth §6.3): the demo (no layers) and production (real SymbolRenderLayer)
        //    paths must produce IDENTICAL candidate/survivor/quad counts — E2 touches only the DRAW, never
        //    anything upstream of the emit loop. Two fresh LabelPlacementSystem instances (not one instance
        //    ticked twice) so A-5 sticky-placement incumbency from the first call can't bias the second. ──

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static LabelInstance MakeOverlappingLabel(int featureIndex, double3 sceneOriginRender, float sortKey)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            var layout = new TextLayoutResult { Quads = quads, BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1 };
            return new LabelInstance
            {
                AnchorRender = sceneOriginRender, // SAME anchor for both labels → guaranteed real collision
                Layout = layout,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                SortKey = sortKey,
                FeatureIndex = featureIndex,
                TileKey = 0L,
                // R3: PointFadeId hashes (AnchorRender, MaterialIndex, Text, IconImage) — NOT FeatureIndex/TileKey —
                // so two labels sharing an anchor with the (both-default) Text/MaterialIndex this method used to
                // leave unset would collide on FadeId. Under R3, FadeId is the display key (LabelCandidate.FadeId's
                // uniqueness contract), so a collision would make the loser show alongside the winner. Distinct Text
                // per label keeps the anchors identical (the real collision this test needs) while giving each a
                // unique identity; it does not perturb the staged geometry (Layout is supplied explicitly here).
                Text = "L" + featureIndex,
            };
        }

        [Test]
        public void CollisionCounts_AreIdentical_DemoPath_Vs_ProductionPathWithOneSymbolRenderLayer()
        {
            var camGo = new GameObject("CollisionParity_TestCamera");
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

            var labels = new List<LabelInstance>
            {
                MakeOverlappingLabel(0, frame.SceneOriginRender, sortKey: 20f),
                MakeOverlappingLabel(1, frame.SceneOriginRender, sortKey: 10f), // lower key wins the collision
            };

            // Step 5b: this compared the labels-list overload against the SymbolLabelBatch overload — both demo
            // seams, so it compared demo against demo while calling one side "prod". Both are gone. The invariant
            // it actually asserts survives and is now stated directly: supplying per-layer render layers (E2 —
            // each with its own material + persistent presenter) partitions only the DRAW, and must not perturb
            // anything upstream of the emit loop. Same plan, same labels, layers vs no layers.
            var noLayersSystem = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            var layeredSystem  = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                SymbolGatherPlan built = plan.Build(labels);
                Assert.AreEqual(labels.Count, plan.CollectedCount,
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
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
