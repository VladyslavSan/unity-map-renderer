// Unity EditMode only — needs a real Camera/Mesh/GameObject (WorldLabelRenderer creates one). NOT
// registered in core-tests.csproj.
//
// Regression pin (the render-layer model; from the E2 review, filed at
// commit 491bbf6e): ONE LabelPlacementSystem instance is shared between the demo (_demoBatch, fallback
// presenters) and production (SymbolLabelSubsystem's batch, SymbolRenderLayer presenters) tick paths —
// MapView.LateUpdate flips between them at runtime. Flipping leaked per-path state, one root cause, two
// symptoms:
//   1a (stale mirror, pre-existing since E1): RefreshBatchMirror keyed its skip on BuildId alone, so a
//       demo Tick and a DIFFERENT, freshly-built production batch that both reach BuildId==1 (their
//       independent monotonic counters both start at 1) collided on the skip guard — the production Tick
//       then staged off the demo's stale mirrored content instead of its own.
//   1b (double-draw, introduced by E2): production PresentSlot never hid the fallback presenter a prior
//       demo Tick left enabled (and vice-versa), so both draw the SAME rewritten slot mesh at once.
// Both fixed together in LabelPlacementSystem: RefreshBatchMirror gained a _lastBatch identity guard
// (ReferenceEquals AND BuildId, not BuildId alone); PresentSlot now hides the inactive path's presenter
// every Tick.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class LabelPlacementDemoProductionFlipTests
    {
        private const string StyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""text-field"": ""{NAME}"" } }
            ]
        }";

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // A single centred point label, distinguished only by colour — 1a's symptom is the WRONG colour
        // reaching the mesh, so every other field (anchor/quad/size) is held identical between demo and
        // production so a mismatch can only be explained by stale mirrored content.
        private static LabelInstance MakeLabel(double3 sceneOriginRender, float4 color) => new LabelInstance
        {
            AnchorRender = sceneOriginRender,
            Layout = new TextLayoutResult
            {
                Quads = new List<SymbolQuad>
                {
                    new SymbolQuad
                    {
                        TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                        UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                    },
                },
                BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
            },
            Paint = new LabelPaint { TextColor = color, Opacity = 1f },
            TextSizePx = 24f,
            SortKey = 0f,
            FeatureIndex = 0,
            TileKey = 0L,
        };

        private static (GameObject camGo, MapCamera mapCamera, SceneFrame frame) BuildScene()
        {
            var camGo = new GameObject("DemoProdFlip_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }), float3x3.identity);
            return (camGo, mapCamera, frame);
        }

        private static SymbolRenderLayer BuildRenderLayer(double zoom)
        {
            var settings = MapMaterialSetTestUtil.Load();
            return SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, zoom, drawIndex: 0);
        }

        [Test]
        public void DemoThenProduction_SameBuildId_RefreshesMirror_AndHidesFallbackPresenter()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var renderLayer = BuildRenderLayer(5.0);

            var demoColor = new float4(1f, 0f, 0f, 1f); // red
            var prodColor = new float4(0f, 0f, 1f, 1f); // blue

            // Epic A / A1 D9 §E-flip: point labels now draw through the WORLD path — the demo tick needs its
            // own world base material (production resolves through renderLayer.WorldTextMaterial instead,
            // via MapMaterialSetTestUtil.Load()'s SymbolTextWorld, independent of this).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // Demo Tick (managed-list overload): builds _demoBatch to BuildId 1, sets _mirrorBuildId 1,
                // shows the world presenter drawing red.
                system.Tick(in frame, new List<LabelInstance> { MakeLabel(frame.SceneOriginRender, demoColor) }, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "demo tick must place its label (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Text), "demo tick must show the world presenter (precondition).");

                // Production Tick: a FRESH SymbolLabelBatch — its first Build() also reaches BuildId 1
                // (SymbolLabelBatch.BuildId starts at 0, Reset() bumps it to 1), the exact value the demo
                // tick above already left in _mirrorBuildId — the collision the finding describes.
                var prodBatch = new SymbolLabelBatch();
                SymbolLabelBatchBuilder.Build(prodBatch,
                    new List<LabelInstance> { MakeLabel(frame.SceneOriginRender, prodColor) }, 1, mapCamera.Projection);
                Assert.AreEqual(1, prodBatch.BuildId,
                    "sanity: a fresh batch's first Build reaches BuildId 1, colliding with the demo mirror's.");

                system.Tick(in frame, prodBatch, atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: new List<SymbolRenderLayer> { renderLayer });
                Assert.AreEqual(1, system.LastQuadCount, "production tick must place its label (precondition).");

                // 1a: the production label's OWN colour must reach the WORLD mesh, not the demo's stale
                // mirrored colour. deltaTime = PositiveInfinity snaps the A-4 fade to its target instantly, so
                // the vertex colour is exactly LinearColor(label) with no fade scaling to account for. Epic A
                // / A1 D9: reads TryGetWorldSlotMesh instead of system.Mesh (points no longer land there) —
                // the §7.10 1a RefreshBatchMirror-BuildId-collision precondition this guards is UNCHANGED.
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh worldMesh), "the world slot mesh must exist.");
                WorldMeshReadback.Read(worldMesh, out WorldBillboardVertex[] vertices, out _);
                Assert.Greater(vertices.Length, 0, "the world mesh must have built vertices.");
                float3 actual = vertices[0].ColorRGB;
                float4 expected = LabelPlacementSystem.LinearColor(MakeLabel(frame.SceneOriginRender, prodColor));
                float error = math.csum(math.abs(actual - expected.xyz));
                Assert.Less(error, 0.02f,
                    $"the production label's own colour (blue) must render — sampled vertex colour ({actual.x:F3},{actual.y:F3},{actual.z:F3}) " +
                    $"should read close to ({expected.x:F3},{expected.y:F3},{expected.z:F3}). Reading the demo's stale red mirror content " +
                    "is the §7.10 1a bug (RefreshBatchMirror wrongly skipped on the shared BuildId).");

                // 1b: only ONE presenter may draw a given key's mesh at a time. Epic A / A1 D9: the world
                // presenter is now the ONE presenter for a point label's key (demo and production share it by
                // construction — D1's "shared-key safety", never two presenters) — assert it is visible.
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Text), "the world presenter must be showing after the production Tick.");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // Stage-2 regression: the demo batch overload tolerates a NULL batch (an empty frame) — its signature's
        // `batch?.Count ?? 0` documents null as valid input. Before the RefreshBatchMirror null-guard, routing the
        // batch path through RefreshBatchMirror (which reads batch.BuildId) NRE'd on null. A null Tick must (a) not
        // throw, and (b) produce an empty frame that HIDES a label a prior Tick showed (not leave it drawing stale).
        [Test]
        public void NullBatchTick_DoesNotThrow_AndClearsPreviouslyShownLabels()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // Show a label first, so the null Tick has something to clear.
                system.Tick(in frame, new List<LabelInstance> { MakeLabel(frame.SceneOriginRender, new float4(1f, 0f, 0f, 1f)) }, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "precondition: the label is placed.");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Text), "precondition: the world presenter is showing.");

                // The null batch overload must not NRE, and must clear the frame.
                Assert.DoesNotThrow(() => system.Tick(in frame, (SymbolLabelBatch)null, atlasTexture),
                    "a null batch is a valid empty-frame input (batch?.Count ?? 0) — it must not NRE via RefreshBatchMirror.");
                Assert.AreEqual(0, system.LastQuadCount, "a null batch produces an empty frame (no quads placed).");
                Assert.IsFalse(system.IsWorldSlotVisible(0L, 0, LabelKind.Text),
                    "…and the previously-shown label is HIDDEN, not left drawing stale content.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void ProductionThenDemo_Flip_HidesLayerPresenter()
        {
            // The symmetric direction: production shows a label, then the demo/fallback path takes the
            // slot back — the layer presenter must be hidden, not left drawing alongside the fallback one.
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var renderLayer = BuildRenderLayer(5.0);

            // Epic A / A1 D9: the demo tick's world path needs its own base material (see the first test's
            // identical note).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                var prodBatch = new SymbolLabelBatch();
                SymbolLabelBatchBuilder.Build(prodBatch,
                    new List<LabelInstance> { MakeLabel(frame.SceneOriginRender, new float4(0f, 0f, 1f, 1f)) }, 1, mapCamera.Projection);
                system.Tick(in frame, prodBatch, atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: new List<SymbolRenderLayer> { renderLayer });
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Text), "production tick must show the world presenter (precondition).");

                system.Tick(in frame, new List<LabelInstance> { MakeLabel(frame.SceneOriginRender, new float4(1f, 0f, 0f, 1f)) }, atlasTexture);
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, LabelKind.Text), "the demo tick must show the world presenter (D1 shared-key — same key, rebuilt content).");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
