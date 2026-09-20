// Unity EditMode only — needs a real Camera/Mesh/Material (reads the built billboard mesh's vertex colour /
// world slot mesh). NOT registered in core-tests.csproj.
//
// R3 (deferred collision, design §10.3): the collision is now scheduled at the END of a Tick and Completed +
// re-keyed at the START of the next one, so the main thread never blocks on the single-threaded greedy. The
// functional consequence pinned here is a ONE-TICK verdict latency — see SymbolPlacementSystem.HarvestCollision's
// header comment for the mechanism and §6 of the plan for why no headless test here can distinguish "genuinely
// deferred" from "structurally deferred but eagerly completed" (that distinction is a maintainer Play-mode
// profile, not a gate step).
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolDeferredCollisionTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static SymbolPaint PaintOf(float4 color) => new SymbolPaint { TextColor = color, Opacity = 1f };

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, float sortKey, string text, int feature, float4 color)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: PaintOf(color), textSizePx: 24f, paddingPx: 2f, sortKey: sortKey, text: text,
                featureIndex: feature, tileKey: 0L);

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("DeferredCollision_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = cam.Projection;
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                System = new SymbolPlacementSystem(cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        // Reads the WORLD slot mesh's first vertex colour — the same pattern
        // SymbolPlacementDemoProductionFlipTests.cs uses to pin which symbol's own colour reached the mesh.
        private static float3 FirstVertexColor(SymbolPlacementSystem system)
        {
            Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh), "the world slot mesh must exist.");
            WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] vertices, out _);
            Assert.Greater(vertices.Length, 0, "the world mesh must have built vertices.");
            return vertices[0].ColorRGB;
        }

        private static void AssertColorMatches(SymbolPlacementSystem system, SymbolPaint paint, string because)
        {
            float3 actual = FirstVertexColor(system);
            float4 expected = SymbolPlacementSystem.LinearColor(paint);
            float error = math.csum(math.abs(actual - expected.xyz));
            Assert.Less(error, 0.02f,
                $"{because} — sampled ({actual.x:F3},{actual.y:F3},{actual.z:F3}) vs expected ({expected.x:F3},{expected.y:F3},{expected.z:F3}).");
        }

        // ── T1 / T5's primary tooth: the collision verdict a Tick's emit reads is the one HARVESTED at the top
        //    of THAT Tick — i.e. the collision SCHEDULED at the end of the PREVIOUS Tick, over the PREVIOUS
        //    Tick's candidates. A brand-new candidate set therefore takes one extra Tick to be reflected: an
        //    incumbent holds its slot for one more Tick after a newcomer that would beat it appears. ──
        [Test]
        public void Collision_VerdictAppliesOneFrameLate_IncumbentHoldsUntilTheNextTick()
        {
            using var h = new Harness();
            var red = new float4(1f, 0f, 0f, 1f);
            var blue = new float4(0f, 0f, 1f, 1f);
            // A: higher SortKey (lower priority) → the eventual loser once B appears. B: lower SortKey → winner.
            var aOnly = new SymbolTileBuffer();
            AddPoint(aOnly, h.Origin, sortKey: 10f, text: "A", feature: 0, color: red);
            var aAndB = new SymbolTileBuffer();
            AddPoint(aAndB, h.Origin, sortKey: 10f, text: "A", feature: 0, color: red);
            AddPoint(aAndB, h.Origin, sortKey: 5f, text: "B", feature: 1, color: blue);

            // Tick 1: {A} alone — nothing has been harvested yet (no prior scheduled collision) ⇒ nothing shows.
            h.System.TickSymbols(in h.Frame, aOnly, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(0, h.System.LastQuadCount, "tick 1: no pending verdict yet — nothing shows (§2.6).");

            // Tick 2: {A} again — harvests tick 1's scheduled collision over {A} ⇒ A shows.
            h.System.TickSymbols(in h.Frame, aOnly, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "tick 2: A's own collision has now been harvested.");
            AssertColorMatches(h.System, PaintOf(red), "tick 2 must show A");

            // Tick 3: {A, B} — the harvested verdict is still tick 2's, over {A} ALONE (B did not exist when
            // that collision was scheduled) ⇒ A still shows, B does not — the one-Tick verdict latency.
            h.System.TickSymbols(in h.Frame, aAndB, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "tick 3: the deferred verdict is still A-only.");
            AssertColorMatches(h.System, PaintOf(red), "tick 3 must still show A, not B");

            // Tick 4: {A, B} again — harvests tick 3's scheduled collision over {A, B}, where B wins ⇒ B shows,
            // A eases toward 0 with deltaTime = +inf (snaps instantly), so only B is emitted.
            h.System.TickSymbols(in h.Frame, aAndB, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "tick 4: B has now won the harvested verdict.");
            AssertColorMatches(h.System, PaintOf(blue), "tick 4 must show B, A has snapped to 0");
        }

        // ── T5 — the minimal statement of §2.6: the first Tick after construction schedules but shows nothing;
        //    the second shows the survivor of that scheduled collision. ──
        [Test]
        public void FirstTick_SchedulesOnly_SecondTickShowsTheSurvivors()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0, color: new float4(1f, 1f, 1f, 1f));

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(0, h.System.LastQuadCount, "the first Tick has no prior verdict to harvest.");

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "the second Tick harvests the first Tick's scheduled collision.");
        }

        // ── T3 — DoDispose must Complete() a still-pending collision before disposing the buffers it holds
        //    (_stageCandidates/_stageBoxes/_nSurvivors/_survivorCountOut/the grid lists), or the job safety system
        //    throws (a use-after-free). Tick once (schedules a collision, leaving it pending — HarvestCollision
        //    only runs at the START of the NEXT Tick, which never comes here) then Dispose. ──
        [Test]
        public void Dispose_WithPendingCollision_CompletesCleanly()
        {
            var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0, color: new float4(1f, 1f, 1f, 1f));
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.DoesNotThrow(() => h.Dispose(), "Dispose must Complete() a still-pending collision, not tear down under it.");
        }

        private static SymbolRenderLayer BuildRenderLayer(double? minZoom, double initialZoom)
        {
            string minZoomJson = minZoom.HasValue ? $@", ""minzoom"": {minZoom.Value}" : "";
            string styleJson = $@"{{
                ""version"": 8,
                ""layers"": [
                    {{ ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l""{minZoomJson},
                      ""layout"": {{ ""text-field"": ""{{NAME}}"" }} }}
                ]
            }}";
            var settings = MapMaterialSetTestUtil.Load();
            return SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(styleJson).Layers[0], settings, initialZoom, drawIndex: 0);
        }

        // ── T4 — the display-time zoom gate (`!cand.Suppressed` in the emit `show` expression) is a SAME-frame
        //    override on top of the deferred verdict: a candidate that WON a previous collision must stop
        //    showing the instant its layer leaves the live zoom range, not linger a Tick until the next
        //    harvest catches up (R3 §2.8). ──
        [Test]
        public void SuppressedCandidate_HidesInTheSameFrame_NotOneFrameLate()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0, color: new float4(1f, 1f, 1f, 1f));

            // Camera zoom is 5.0 (Harness). Unbounded layer (no minzoom) is visible throughout.
            var visibleLayer = BuildRenderLayer(minZoom: null, initialZoom: 5.0);
            // minzoom above the live camera zoom ⇒ IsVisibleAtZoom(5.0) is false ⇒ Suppressed.
            var suppressedLayer = BuildRenderLayer(minZoom: 10.0, initialZoom: 5.0);
            try
            {
                var visibleLayers = new List<SymbolRenderLayer> { visibleLayer };
                var suppressedLayers = new List<SymbolRenderLayer> { suppressedLayer };

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity, symbolLayers: visibleLayers);
                Assert.AreEqual(0, h.System.LastQuadCount, "tick 1: no prior verdict yet.");

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity, symbolLayers: visibleLayers);
                Assert.AreEqual(1, h.System.LastQuadCount, "tick 2: the label has won its harvested verdict and shows.");

                // tick 3: the SAME symbol, now under a layer whose minzoom excludes the live zoom. Even though
                // the harvested verdict (from tick 2's scheduled collision) still says "won", Suppressed must
                // hide it THIS Tick — not one Tick later.
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity, symbolLayers: suppressedLayers);
                Assert.AreEqual(0, h.System.LastQuadCount, "tick 3: suppression is a same-frame override, not one-Tick-late.");
            }
            finally
            {
                visibleLayer.Dispose();
                suppressedLayer.Dispose();
            }
        }
    }
}
