// Text/Placement/SymbolPlacementAllocTests.cs — symbol pair wiring, steady-state placement allocation, projection job parity, tile-block baking, the up-carrier chain, the zoom gate, and world-billboard vertex/up-population teeth.
//
// Pair wiring and steady-state allocation first, then projection/screen-projection parity, then tile-block baking, the up-carrier chain, the zoom gate, then the two world-billboard fixtures.
//
// Contents:
//   SymbolPairWiringTests             — Road-shields (docs/road-shields-design.md) — the Tick()-level wiring tooth for a centred icon+text pair: a Owner(icon)+Rider(text) pair must stage as ONE SymbolCandidate (LastCandidateCount) yet still place BOTH halves' quads (LastQuadCount)…
//   SymbolPlacementAllocTests         — over a STABLE synthetic symbol set, a steady-state Tick (project → billboard-build → submit) allocates ZERO managed garbage.
//   SymbolProjectionJobTests          — the parallel symbol-projection pass.
//   SymbolScreenProjectionUnityTests  — Core-vs-real-camera pin: a synthetic world anchor's TryProjectAnchor pixel must MATCH the real WorldToScreenPoint pixel for the SAME (origin-relative) local position, and a behind-camera anchor must be culled by both.
//   SymbolTileBlockBakerTests         — Bake against a REAL SymbolTileBlock — the native-lifetime half of the acceptance teeth (the dispose-SITE teeth — commit-overwrite / FIFO-evict / true-release / Clear — live in…
//   SymbolUpCarrierChainTests         — the Up CARRIER CHAIN from Block A, driven end to end through the REAL BuildAsync (which itself calls the real Extract), asserted against a closed form written out here — never obtained from ProjectPoint or any production sampler.
//   SymbolZoomGateGatherTests         — The pre-projection ZOOM gate: a symbol whose style layer is out of the live camera zoom's [minzoom, maxzoom) is hard-skipped in GatherSymbolPoints — never projected/staged/collided — instead of being projected, staged, collided and only THEN marked…
//   WorldBillboardVertexLayoutTests   — the vertex-layout/struct sync tooth.
//   WorldSurfaceUpPopulationTests     — The byte-identical invariant means the RENDER cannot tell you whether Up is populated correctly — a zeroed, a hardcoded +Y, and a correct per-projection Up would all render IDENTICALLY (the vertex attribute is written and unread…

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
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Symbols;
using System;
using System.IO;
using System.Threading.Tasks;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Tests; // TestGlyphSource
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using CameraProperties = MapRenderer.Core.Geo.CameraProperties;


namespace MapRenderer.Tests.Text.Placement
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolPairWiringTests — an icon+text pair stages as one candidate, places both halves' quads
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolPairWiringTests
    {
        private const string StyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""shield"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""icon-image"": ""shield"" } }
            ]
        }";

        private static MapMaterialSet BuildSettings()
        {
            var settings = ScriptableObject.CreateInstance<MapMaterialSet>();
            settings.SymbolTextWorld = new Material(Shader.Find("Map/Symbol/TextWorld"));
            settings.SymbolIconWorld = new Material(Shader.Find("Map/Symbol/IconWorld"));
            return settings;
        }

        private static Texture2D BuildSpriteTexture()
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false);
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(200, 50, 50, 255);
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            return tex;
        }

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // A centred pair: Owner (icon) immediately followed by its Rider (text), the adjacency the baker keeps.
        // textTranslatePx pushes the text's box clear of the icon's, so a blocker can hit one half alone.
        private static void AddPairSymbols(SymbolTileBuffer buffer, double3 sceneOriginRender,
            bool textOptional = false, float textTranslatePx = 0f)
        {
            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            // Owner (icon) then Rider (text), in that order — the adjacency contract relies on.
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon,
                iconImage: "shield", // a REAL identity — a null one would dedup-collide with any other
                                      // null-identity symbol sharing this anchor (e.g. a hand-built blocker).
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: 0L,
                pairRole: SymbolPairRole.Owner,
                pairId: 0);

            var textQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 6f), BottomRight = new float2(6f, -6f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender, textQuads, new float2(-6f, -6f), new float2(6f, 6f),
                text: "42", // a REAL identity — see the icon's IconImage comment above
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                sortKey: 0f,
                featureIndex: 1,
                tileKey: 0L,
                pairRole: SymbolPairRole.Rider,
                pairId: 0,
                pairOptional: textOptional,
                translatePx: new float2(textTranslatePx, 0f),
                translateAnchor: TextTranslateAnchor.Viewport);
        }

        private static (GameObject camGo, MapCamera mapCamera, SceneFrame frame) BuildScene()
        {
            var camGo = new GameObject("PairWiring_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            return (camGo, mapCamera, frame);
        }

        [Test]
        public void CentredPair_Tick_DrawsBothTextAndIconMeshes_OneCandidateBothHalves()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                var pairBuffer = new SymbolTileBuffer();
                AddPairSymbols(pairBuffer, frame.SceneOriginRender);

                // Duplicate — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(pairBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(pairBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                // TWO symbols (icon+text), ONE candidate: fewer sort/grid/fade operations than two candidates.
                Assert.AreEqual(1, system.LastCandidateCount, "a centred pair must stage as exactly ONE candidate");
                Assert.AreEqual(2, system.LastQuadCount, "both halves' quads must still be placed (icon quad + text quad)");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Icon, out Mesh worldIconMesh),
                    "the world icon slot mesh must exist");
                Assert.Greater(worldIconMesh.vertexCount, 0, "the icon mesh must have built non-zero vertices");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon), "the icon presenter must be showing");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh worldTextMesh),
                    "the world text slot mesh must exist");
                Assert.Greater(worldTextMesh.vertexCount, 0,
                    "the text mesh must have built non-zero vertices — the bare-number bug is a MISSING icon, not a missing text");
                Assert.IsTrue(system.IsWorldSlotVisible(0L, 0, SymbolKind.Text),
                    "the text presenter must be showing — BOTH halves survive together");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(settings.SymbolTextWorld);
                Object.DestroyImmediate(settings.SymbolIconWorld);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── At the Tick level: text-optional lets the ICON survive its text's collision loss ─────────────────
        //    The two runs differ ONLY by the property; the blocker, translate and tile split stay constant.
        [Test]
        public void TextOptional_Tick_TextLosesCollision_IconMeshStillBuilds_TextMeshDoesNot(
            [Values(false, true)] bool textOptional)
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                const float TextOffsetPx = 200f;
                var mixedBuffer = new SymbolTileBuffer();
                AddPairSymbols(mixedBuffer, frame.SceneOriginRender, textOptional, TextOffsetPx);

                // A higher-priority blocker on the TEXT half's translated box only. Its own tile key puts its text
                // quads in a different world slot, so a non-empty text mesh stays attributable.
                long blockerTileKey = SymbolTileKey.Pack(new TileId { Z = 1, X = 1, Y = 0 });
                var blockerQuads = new List<SymbolQuad>
                {
                    new SymbolQuad
                    {
                        TopLeft = new float2(-40f, 40f), BottomRight = new float2(40f, -40f),
                        UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                    },
                };
                TestSymbolTileBuffer.AddPoint(mixedBuffer, frame.SceneOriginRender, blockerQuads, new float2(-40f, -40f), new float2(40f, 40f),
                    text: "blocker",
                    paint: SymbolPaint.Default,
                    textSizePx: 24f,
                    sortKey: -1f,
                    featureIndex: 99,
                    tileKey: blockerTileKey,
                    translatePx: new float2(TextOffsetPx, 0f),
                    translateAnchor: TextTranslateAnchor.Viewport);

                // Two Ticks: the first schedules the collision, the second harvests its verdict — and,
                // for the optional case, seeds the per-half drop mask the emit loop reads.
                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(2, system.LastCandidateCount, "precondition: the blocker + the pair (one candidate each)");

                bool iconShows = system.IsWorldSlotVisible(0L, 0, SymbolKind.Icon);
                bool textShows = system.IsWorldSlotVisible(0L, 0, SymbolKind.Text);
                if (textOptional)
                {
                    Assert.AreEqual(2, system.LastQuadCount,
                        "text-optional: the blocker's quad AND the pair's ICON quad place — the text half alone drops");
                    Assert.IsTrue(iconShows, "the icon must survive its text half losing collision");
                    Assert.IsFalse(textShows, "the pair's text half lost collision, so its mesh must stay empty");
                }
                else
                {
                    Assert.AreEqual(1, system.LastQuadCount,
                        "without text-optional only the blocker places — the pair drops all-or-nothing");
                    Assert.IsFalse(iconShows, "un-optional: the icon drops WITH its text");
                    Assert.IsFalse(textShows, "un-optional: the text drops too");
                }
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(settings.SymbolTextWorld);
                Object.DestroyImmediate(settings.SymbolIconWorld);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── At the Tick level: a real collision loss drops BOTH halves, not just the icon ────────────────────
        [Test]
        public void CentredPair_Tick_BlockedByHigherPrioritySymbol_BothHalvesDropTogether_NoBareNumber()
        {
            var (camGo, mapCamera, frame) = BuildScene();
            var atlasTexture = BuildTinyAtlasTexture();
            var spriteTexture = BuildSpriteTexture();
            var settings = BuildSettings();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(StyleJson).Layers[0], settings, 5.0, drawIndex: 0);

            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var layers = new List<SymbolRenderLayer> { renderLayer };
            using var plan = new TestSymbolPlan(mapCamera.Projection);

            try
            {
                // A higher-priority (lower SortKey) blocker at the SAME anchor, big enough to overlap the icon's box.
                var blockerQuads = new List<SymbolQuad>
                {
                    new SymbolQuad
                    {
                        TopLeft = new float2(-40f, 40f), BottomRight = new float2(40f, -40f),
                        UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                    },
                };
                var mixedBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddPoint(mixedBuffer, frame.SceneOriginRender, blockerQuads, new float2(-40f, -40f), new float2(40f, 40f),
                    paint: SymbolPaint.Default,
                    textSizePx: 24f,
                    sortKey: -1f,
                    featureIndex: 99,
                    tileKey: 0L);
                AddPairSymbols(mixedBuffer, frame.SceneOriginRender);

                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);
                system.Tick(in frame, plan.Build(mixedBuffer), atlasTexture, deltaTime: float.PositiveInfinity,
                    symbolLayers: layers, spriteTexture: spriteTexture);

                Assert.AreEqual(2, system.LastCandidateCount, "the blocker + the pair (one candidate each)");
                // The blocker shares the pair text's (tileKey, slot, Text) mesh, so slot visibility cannot tell
                // them apart; LastQuadCount can: only the blocker's ONE quad may place.
                Assert.AreEqual(1, system.LastQuadCount,
                    "only the blocker's quad places — the pair (icon+text) drops TOGETHER, not just the icon (no bare number)");
            }
            finally
            {
                renderLayer.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(spriteTexture);
                Object.DestroyImmediate(settings.SymbolTextWorld);
                Object.DestroyImmediate(settings.SymbolIconWorld);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(camGo);
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolPlacementAllocTests — a steady-state Tick over a stable symbol set allocates zero garbage
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Over a STABLE synthetic symbol set, a steady-state <see cref="SymbolPlacementSystem.Tick"/>
    /// (project → billboard-build → submit) allocates ZERO managed garbage.
    /// </summary>
    [TestFixture]
    public class SymbolPlacementAllocTests : BaseTestFixture
    {
        // Catches a baked block that is never disposed: only Dispose decrements DebugLiveAllocCount, not a
        // finalizer, so the delta does not depend on GC timing.
        private long _liveBlocks;

        protected override void OnSetUp()
        {
            base.OnSetUp();
            _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        }

        protected override void OnTearDown()
        {
            try
            {
                Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
                    "this test baked a block it never disposed — release the snapshot and Clear() the store");
            }
            finally { base.OnTearDown(); }
        }

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65,
                Width = 10,
                Height = 10,
                Left = 0,
                Top = 8,
                Advance = 12,
                Bitmap = new byte[16 * 16], // CellSize = (10+2*3, 10+2*3) = (16,16)
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);

            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // AnchorRender is render-space PRE-RTC, so it is built from SceneOriginRender. A small local offset
        // is culled, and the Tick would then measure the empty "nothing to place" branch.
        private static SymbolTileBuffer BuildSymbols(int count, double3 sceneOriginRender, long tileKey = 0L, bool allowOverlap = false)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f),
                    BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f),
                    UvBottomRight = new float2(0.4f, 0.4f),
                    LineIndex = 0,
                },
            };

            var buffer = new SymbolTileBuffer();
            for (int i = 0; i < count; i++)
            {
                TestSymbolTileBuffer.AddPoint(buffer, sceneOriginRender + new double3(i * 10.0, 0.0, i * 5.0),
                    quads, float2.zero, new float2(18f, 18f),
                    paint: SymbolPaint.Default,
                    textSizePx: 24f,
                    sortKey: 0f,
                    featureIndex: i,
                    tileKey: tileKey,
                    allowOverlap: allowOverlap);
            }
            return buffer;
        }

        [Test]
        public void Tick_SteadyState_AllocatesNoGCMemory()
        {
            var camGo = Track(new GameObject("SymbolAlloc_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));

            // Build the frame from the projection, matching MapView.BuildSceneFrame's math (identity
            // rebase for planar Mercator).
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 }),
                Rebase = float3x3.identity,
            };

            using var atlasTexture = BuildTinyAtlasTexture();
            var buffer = BuildSymbols(20, frame.SceneOriginRender);
            using var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));

            // The plan is built ONCE, outside every measured region: TestSymbolPlan.Build allocates managed
            // scratch, and the thing under measurement is Tick, not plan construction.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            // Warm-up ticks: first-frame NativeList growth + Mesh/Material creation is allowed to allocate.
            for (int i = 0; i < 3; i++)
            {
                system.Tick(in frame, builtPlan, atlasTexture);
            }

            Assert.That(() => system.Tick(in frame, builtPlan, atlasTexture),
                Is.Not.AllocatingGCMemory(),
                "a steady-state Tick (same label count/shape as the warm-up) must allocate ZERO managed garbage");
        }

        // ── The built+presented world path: TEXT slots build for real, and an Icon with no world material is
        //    emitted but unrenderable every Tick. Non-obvious why: such a slot must not churn its Mesh,
        //    GameObject and NativeLists every IdleReclaimFrames (WorldSymbolRenderer.EndFrame's
        //    emittedThisFrame gate), so the measured run crosses the K=60 reclaim boundary. ──
        [Test]
        public void Tick_SteadyState_WorldBuildAndPresent_AcrossReclaimBoundary_AllocatesNoGCMemory()
        {
            var camGo = Track(new GameObject("SymbolAllocWorld_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            using var atlasTexture = BuildTinyAtlasTexture();
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 5); // matches camera zoom (coverage-cull realism)
            // allowOverlap: the 20 symbols sit a few px apart, and without it collision would cull all but one;
            // this tooth wants a stable, fully placed scene every Tick.
            SymbolTileBuffer buffer = BuildSymbols(20, frame.SceneOriginRender, tileKey, allowOverlap: true);
            int textCount = buffer.Symbols.Count;

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon, // no worldIconBase supplied below — this slot is emitted every
                                      // Tick but never resolves a material (the scenario under test).
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm,
                allowOverlap: true,
                sortKey: 0f,
                featureIndex: textCount,
                tileKey: tileKey,
                // Non-obvious why: this anchor equals the first text symbol's, and PointFadeId hashes
                // (AnchorRender, MaterialIndex, Text, IconImage), so a null IconImage would share its FadeId.
                // AssertFadeIdsUnique would then log (and allocate) every Tick. No sprite lookup reads it here.
                iconImage: "steady-state-icon");

            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));

            // The plan is built ONCE, outside every measured region: TestSymbolPlan.Build allocates managed
            // scratch, and the thing under measurement is Tick, not plan construction.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
            Assert.AreEqual(buffer.Symbols.Count, system.LastQuadCount, "every label (incl. the unrenderable icon) must place — the icon is emitted regardless of a missing world material (precondition).");
            Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Text), "the world TEXT slot must be built+presented after warm-up (the branch this tooth measures).");
            Assert.IsFalse(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Icon), "the world ICON slot has no material — it must stay hidden (not crash, not churn).");

            // 65 > IdleReclaimFrames (60): a system that rebuilt the never-presentable icon slot would churn
            // a Mesh/GameObject/3x NativeList every 60th Tick.
            Assert.That(() =>
                {
                    for (int i = 0; i < 65; i++) system.Tick(in frame, builtPlan, atlasTexture);
                },
                Is.Not.AllocatingGCMemory(),
                "65 steady-state Ticks (crossing the K=60 idle-reclaim boundary) with a real world-built+presented " +
                "TEXT slot AND an emitted-but-unrenderable ICON slot must allocate ZERO managed garbage.");
        }

        // ── A CURVED-emitting frame: curved shares the world sink with point/icon, and the curved arm of
        //    WorldSymbolRenderer.Emit must reuse the slot's NativeLists as point does, not allocate per glyph. ──
        [Test]
        public void Tick_SteadyState_CurvedWorldEmit_AllocatesNoGCMemory()
        {
            var camGo = Track(new GameObject("SymbolAllocCurved_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            using var atlasTexture = BuildTinyAtlasTexture();
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 5);

            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 5.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 15.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph>
            {
                new CurvedGlyph
                {
                    ArcCenter = 0f,
                    Cell = new SymbolQuad
                    {
                        TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                        UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                    },
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { new LineAnchor(0, 0.5f) }, path,
                placement: SymbolPlacement.LineCenter,
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                maxAngleDeg: 45f,
                keepUpright: true,
                featureIndex: 0,
                tileKey: tileKey);

            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));

            // The plan is built ONCE, outside every measured region: TestSymbolPlan.Build allocates managed
            // scratch, and the thing under measurement is Tick, not plan construction.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
            Assert.AreEqual(1, system.LastQuadCount, "the curved label's single glyph must place (precondition).");
            Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Text), "the world TEXT slot must be built+presented (curved text routes there).");

            Assert.That(() =>
                {
                    for (int i = 0; i < 5; i++) system.Tick(in frame, builtPlan, atlasTexture);
                },
                Is.Not.AllocatingGCMemory(),
                "steady-state Ticks over a curved-emitting scene must allocate ZERO managed garbage — the " +
                "curved arm of WorldSymbolRenderer.Emit reuses the slot's NativeLists exactly like point.");
        }

        // ── A centred icon+text pair's steady-state Tick allocates ZERO: the pair stages as ONE candidate, and
        //    AppendPointHalf reuses the existing emit NativeArray. ──
        [Test]
        public void Tick_SteadyState_CentredPair_AllocatesNoGCMemory()
        {
            var camGo = Track(new GameObject("SymbolAllocPair_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            using var atlasTexture = BuildTinyAtlasTexture();
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 5);

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            // Owner immediately followed by its Rider — the adjacency contract TestSymbolPlan/the baker preserve.
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon,
                iconImage: "shield",
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey,
                pairRole: SymbolPairRole.Owner,
                pairId: 0);

            var textQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, textQuads, float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                text: "42",
                sortKey: 0f,
                featureIndex: 1,
                tileKey: tileKey,
                pairRole: SymbolPairRole.Rider,
                pairId: 0);

            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
            Assert.AreEqual(1, system.LastCandidateCount, "precondition: the pair must stage as ONE candidate.");
            Assert.AreEqual(2, system.LastQuadCount, "precondition: both halves' quads must place.");
            Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Icon), "precondition: the icon presenter must show.");
            Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Text), "precondition: the text presenter must show.");

            Assert.That(() =>
                {
                    for (int i = 0; i < 65; i++) system.Tick(in frame, builtPlan, atlasTexture);
                },
                Is.Not.AllocatingGCMemory(),
                "65 steady-state Ticks over a centred icon+text pair (crossing the K=60 idle-reclaim boundary) " +
                "must allocate ZERO managed garbage.");
        }

        // ── Zero alloc for a pair that places WITHOUT one half, which runs every optional-pair branch
        //    (collision write-back, dropped-halves map, stage-job probe, emit skip). ──
        [Test]
        public void Tick_SteadyState_OptionalPairPlacingWithoutItsText_AllocatesNoGCMemory()
        {
            var camGo = Track(new GameObject("SymbolAllocOptionalPair_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            using var atlasTexture = BuildTinyAtlasTexture();
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 5);
            const float TextOffsetPx = 200f;

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon,
                iconImage: "shield",
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey,
                pairRole: SymbolPairRole.Owner,
                pairId: 0);

            var textQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, textQuads, float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                text: "42",
                sortKey: 0f,
                featureIndex: 1,
                tileKey: tileKey,
                pairRole: SymbolPairRole.Rider,
                pairId: 0,
                pairOptional: true, // text-optional
                translatePx: new float2(TextOffsetPx, 0f),
                translateAnchor: TextTranslateAnchor.Viewport);

            // A higher-priority blocker over the text half's box only, so every steady Tick re-decides
            // "place the icon, drop the text" and the dropped-halves map is written and read every frame.
            var blockerQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-40f, 40f), BottomRight = new float2(40f, -40f),
                    UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, blockerQuads, new float2(-40f, -40f), new float2(40f, 40f),
                paint: SymbolPaint.Default,
                textSizePx: 24f,
                text: "blocker",
                sortKey: -1f,
                featureIndex: 99,
                tileKey: tileKey,
                translatePx: new float2(TextOffsetPx, 0f),
                translateAnchor: TextTranslateAnchor.Viewport);

            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
            Assert.AreEqual(2, system.LastCandidateCount, "precondition: the blocker + the pair.");
            Assert.AreEqual(2, system.LastQuadCount,
                "precondition: the blocker's quad and the pair's ICON quad — the text half must be dropped, " +
                "or this measures the ordinary all-or-nothing path rather than the optional-half one.");

            Assert.That(() =>
                {
                    for (int i = 0; i < 65; i++) system.Tick(in frame, builtPlan, atlasTexture);
                },
                Is.Not.AllocatingGCMemory(),
                "65 steady-state Ticks over a text-optional pair placing WITHOUT its text must allocate ZERO " +
                "managed garbage.");
        }

        // ── A WARM cross-tile dedup allocates ZERO: interning runs once at CompleteBuild, and the IEquatable
        //    struct DedupKey does not box. RED: make DedupKey a class or drop IEquatable. ──
        [Test]
        public void WarmDedup_ZeroAlloc()
        {
            var store = new SymbolTileStore(cacheCap: 16);
            var tile = new TileId { Z = 12, X = 3, Y = 4 };
            var key = new SymbolTileStore.Key("src", tile);

            var buffer = new SymbolTileBuffer();
            for (int i = 0; i < 20; i++)
            {
                TestSymbolTileBuffer.AddPoint(buffer,
                    anchorRender: new double3(i * 100.0, 0.0, i * 100.0), // distinct cells → distinct dedup keys
                    quads: null, boundsMin: float2.zero, boundsMax: float2.zero,
                    text: "L" + i, materialIndex: 0, tileKey: 0L);
            }
            // Reader cutover: CaptureSnapshot only collects a tile with a baked block — bake+commit a real one
            // (rather than the symbol-only commit this test used pre-cutover) so CollectInto still sees all 20.
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, double3.zero);
            store.CompleteBuild(key, store.BeginBuild(key), block); // interns once here (off the per-frame path)

            var blockId = new List<int>(64); var localIndex = new List<int>(64); var isDeparting = new List<byte>(64);
            const double q = 50.0;
            store.CollectInto(blockId, localIndex, isDeparting, q, out _); // warm: grow _dedup + list capacity once (allowed to allocate)

            Assert.That(() => store.CollectInto(blockId, localIndex, isDeparting, q, out _),
                Is.Not.AllocatingGCMemory(),
                "a warm per-frame dedup must allocate ZERO — interning happened once at CompleteBuild and the " +
                "integer DedupKey does not box in the reused _dedup dictionary.");
            store.Clear(); // CollectInto already released its own pins — nothing pinned, just the committed block
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolProjectionJobTests — the parallel symbol-projection pass
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Burst <see cref="SymbolProjectionJob"/> projects every visible symbol's anchors and line paths up
    /// front, and staging reads the results. Its per-point output must equal the inline
    /// <see cref="SymbolScreenProjection.TryProjectPoint"/>, and over an INTERLEAVED scene the symbol→flat-point
    /// index mapping must stay stable when some symbols are not gathered.
    /// </summary>
    [TestFixture]
    public class SymbolProjectionJobTests
    {
        // ── Tooth 1: SymbolProjectionJob output == inline TryProjectPoint, per index. ──
        [Test]
        public void SymbolProjectionJob_MatchesInlineProjection_PerPoint()
        {
            const int n = 64;
            var rng = new System.Random(12345);
            double3 origin = new double3(1_000_000.0, 0.0, 2_000_000.0);
            double2 viewport = new double2(1280.0, 720.0);
            float4x4 viewProj = math.mul(
                float4x4.PerspectiveFov(math.radians(60f), (float)(viewport.x / viewport.y), 0.1f, 5000f),
                float4x4.Translate(new float3(0f, 0f, -800f)));

            var points = new NativeArray<double3>(n, Allocator.TempJob);
            var outScreen = new NativeArray<float2>(n, Allocator.TempJob);
            var outDepth = new NativeArray<float>(n, Allocator.TempJob);
            var outValid = new NativeArray<byte>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                    points[i] = origin + new double3(
                        (rng.NextDouble() - 0.5) * 4000.0, (rng.NextDouble() - 0.5) * 4000.0,
                        (rng.NextDouble() - 0.5) * 4000.0);

                new SymbolProjectionJob
                {
                    Points = points, SceneOriginRender = origin, Rebase = float3x3.identity, ViewProj = viewProj, ViewportLogicalPx = viewport,
                    OutScreen = outScreen, OutDepth = outDepth, OutValid = outValid,
                }.Schedule(n, 8).Complete();

                bool anyValid = false, anyInvalid = false;
                for (int i = 0; i < n; i++)
                {
                    bool ok = SymbolScreenProjection.TryProjectPoint(points[i], origin, viewProj, viewport,
                        float3x3.identity, out float2 s, out float d);
                    Assert.AreEqual(ok, outValid[i] != 0, $"valid flag mismatch at {i}");
                    if (ok)
                    {
                        Assert.AreEqual(s.x, outScreen[i].x, 1e-4f, $"screen.x mismatch at {i}");
                        Assert.AreEqual(s.y, outScreen[i].y, 1e-4f, $"screen.y mismatch at {i}");
                        Assert.AreEqual(d, outDepth[i], 1e-4f, $"depth mismatch at {i}");
                    }
                    anyValid |= ok; anyInvalid |= !ok;
                }
                Assert.IsTrue(anyValid, "test matrix should project some points in front of the camera");
                Assert.IsTrue(anyInvalid, "…and some behind it, to exercise both branches");
            }
            finally
            {
                points.Dispose(); outScreen.Dispose(); outDepth.Dispose(); outValid.Dispose();
            }
        }

        // ── Same parity, but with a NON-identity Rebase (a real globe rebase) — proves the Burst job
        //    carries the rotation identically to the inline (managed) seam, not just the identity fast-path. ──
        [Test]
        public void SymbolProjectionJob_MatchesInlineProjection_PerPoint_NonIdentityRebase()
        {
            const int n = 64;
            var rng = new System.Random(54321);
            var proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 45.0, Longitude = 30.0 };
            double3 origin = proj.Project(lookAt);
            float3x3 rebase = math.transpose(proj.TangentBasisAt(lookAt));
            double2 viewport = new double2(1280.0, 720.0);
            float4x4 viewProj = math.mul(
                float4x4.PerspectiveFov(math.radians(60f), (float)(viewport.x / viewport.y), 0.1f, 5000f),
                float4x4.Translate(new float3(0f, 0f, -800f)));

            var points = new NativeArray<double3>(n, Allocator.TempJob);
            var outScreen = new NativeArray<float2>(n, Allocator.TempJob);
            var outDepth = new NativeArray<float>(n, Allocator.TempJob);
            var outValid = new NativeArray<byte>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                    points[i] = origin + new double3(
                        (rng.NextDouble() - 0.5) * 4000.0, (rng.NextDouble() - 0.5) * 4000.0,
                        (rng.NextDouble() - 0.5) * 4000.0);

                new SymbolProjectionJob
                {
                    Points = points, SceneOriginRender = origin, Rebase = rebase, ViewProj = viewProj, ViewportLogicalPx = viewport,
                    OutScreen = outScreen, OutDepth = outDepth, OutValid = outValid,
                }.Schedule(n, 8).Complete();

                bool anyValid = false, anyInvalid = false;
                for (int i = 0; i < n; i++)
                {
                    bool ok = SymbolScreenProjection.TryProjectPoint(points[i], origin, viewProj, viewport,
                        rebase, out float2 s, out float d);
                    Assert.AreEqual(ok, outValid[i] != 0, $"valid flag mismatch at {i}");
                    if (ok)
                    {
                        // Wider than the identity case: Burst may fuse or reorder math.mul(rebase, …), a ULP-scale
                        // difference at these pixel sizes. A dropped rebase misses by millions of px.
                        Assert.AreEqual(s.x, outScreen[i].x, 1e-2f, $"screen.x mismatch at {i}");
                        Assert.AreEqual(s.y, outScreen[i].y, 1e-2f, $"screen.y mismatch at {i}");
                        Assert.AreEqual(d, outDepth[i], 1e-4f, $"depth mismatch at {i}");
                    }
                    anyValid |= ok; anyInvalid |= !ok;
                }
                Assert.IsTrue(anyValid, "test matrix should project some points in front of the camera");
                Assert.IsTrue(anyInvalid, "…and some behind it, to exercise both branches");
            }
            finally
            {
                points.Dispose(); outScreen.Dispose(); outDepth.Dispose(); outValid.Dispose();
            }
        }

        // ── The index-mapping tooth: point, far-culled point, curved line, point. Points advance the flat cursor
        //    by 1, curves by N and culled symbols by 0; a drifted mapping moves the on-screen symbols off-screen.
        //    Non-obvious why: the second Tick must be byte-identical, as distinct sort keys and the +inf deltaTime make
        //    incumbency and fade inert, so only a partial fill of the uninitialized buffers can change it. ──
        [Test]
        public void JobFill_OverInterleavedScene_PlacesGathered_ExcludesUngathered_AndIsStable()
        {
            using var h = new Harness();
            SymbolTileBuffer Scene()
            {
                var buffer = new SymbolTileBuffer();
                h.AddPoint(buffer, h.Origin, 0f, "A", 0);                             // on-screen point
                h.AddPoint(buffer, h.Origin + new double3(1e8, 0, 1e8), 1f, "F", 1);  // far → distance culled
                h.AddCurvedAcrossView(buffer, 2);                                     // line symbol (N path points)
                h.AddPoint(buffer, h.Origin + new double3(50_000, 0, 0), 3f, "B", 3); // another on-screen point
                return buffer;
            }

            // The verdict is harvested one Tick late, so tick twice; separate Scene() calls give the same FadeIds,
            // so the assertions read a settled state.
            h.System.TickSymbols(in h.Frame, Scene(), h.Atlas, h.Camera.Projection);
            h.System.TickSymbols(in h.Frame, Scene(), h.Atlas, h.Camera.Projection);

            // The points and the curved line share ONE world text slot (TileKey 0, Slot 0, Text), so the
            // world mesh carries the whole index-mapping check.
            Assert.IsTrue(h.System.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh worldMesh0), "the world text slot must exist (2 on-screen points).");
            WorldMeshReadback.Read(worldMesh0, out WorldBillboardVertex[] firstWorldV, out float[] firstWorldOpacity);
            Assert.Greater(firstWorldV.Length, 0, "the two on-screen points must have emitted world vertices (mapping intact).");

            // The far point is culled and the two on-screen points stage; a broken mapping projects them
            // off-screen, so candidates drop below 2 or quads to 0.
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the far point must be B-3 distance-culled");
            Assert.GreaterOrEqual(h.System.LastCandidateCount, 2, "the two on-screen points must stage (mapping intact)");
            Assert.Greater(h.System.LastQuadCount, 0, "the on-screen labels must place (else the scene is vacuous / mapping broken)");

            // Stability: an identical second Tick must reproduce the world mesh bit-for-bit (deterministic
            // fill over the UninitializedMemory buffers + a stable index mapping).
            h.System.TickSymbols(in h.Frame, Scene(), h.Atlas, h.Camera.Projection);

            Assert.IsTrue(h.System.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh worldMesh1), "the world text slot must still exist.");
            WorldMeshReadback.Read(worldMesh1, out WorldBillboardVertex[] secondWorldV, out float[] secondWorldOpacity);
            Assert.AreEqual(firstWorldV, secondWorldV, "WORLD vertex data (AnchorLocal/ColorRGB/Uv/Page/Offset/AlignFlags) drifted across identical Ticks — the two point labels' index mapping is unstable.");
            Assert.AreEqual(firstWorldOpacity, secondWorldOpacity, "WORLD opacity stream drifted across identical Ticks.");
        }

        // ── Harness: a MapCamera + tiny atlas + symbol builders (point + a curved line spanning the view). ──
        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly MapCamera Camera;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("SymbolProjectionJob_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                Camera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                // Point symbols draw through the world path — needs its own world base
                // material for the stability tooth to observe real world-mesh content.
                System = new SymbolPlacementSystem(Camera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void AddPoint(SymbolTileBuffer buffer, double3 anchor, float sortKey, string text, int feature)
                => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                    paint: SymbolPaint.Default, textSizePx: 24f, paddingPx: 2f, sortKey: sortKey, text: text,
                    featureIndex: feature, tileKey: 0L);

            // A straight line across the view, 3 glyphs centred. The ±2° endpoints stay well inside the camera far
            // distance, so only the FAR point "F" is distance-culled.
            public void AddCurvedAcrossView(SymbolTileBuffer buffer, int feature)
            {
                double3 a = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 18.0 });
                double3 b = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 22.0 });
                var path = new double3[] { a, b };
                TestSymbolTileBuffer.AddCurved(buffer,
                    glyphs: new List<CurvedGlyph> { Glyph(0f), Glyph(24f), Glyph(48f) },
                    anchors: new[] { new LineAnchor(0, 0.5f) },
                    path: path,
                    placement: SymbolPlacement.LineCenter,
                    paint: SymbolPaint.Default, textSizePx: 24f, paddingPx: 2f, sortKey: 5f,
                    featureIndex: feature, tileKey: 0L, maxAngleDeg: 45f, keepUpright: true);
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        private static CurvedGlyph Glyph(float arcCenter) => new CurvedGlyph
        {
            ArcCenter = arcCenter,
            Cell = new SymbolQuad
            {
                TopLeft = new float2(-5f, 8f), BottomRight = new float2(5f, -2f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

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
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolScreenProjectionUnityTests — a synthetic anchor's pixel must match the real camera's
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core-vs-real-camera pin: a synthetic world anchor's <see cref="SymbolScreenProjection.TryProjectAnchor"/>
    /// pixel must MATCH the real <see cref="Camera.WorldToScreenPoint"/> pixel for the SAME
    /// (origin-relative) local position, and a behind-camera anchor must be culled by both.
    /// </summary>
    [TestFixture]
    public class SymbolScreenProjectionUnityTests : BaseTestFixture
    {
        private const int ViewportWidth = 800;
        private const int ViewportHeight = 600;

        private static (MapCamera mapCamera, GameObject camGo) BuildCamera(double tiltDeg = 0.0)
        {
            var camGo = new GameObject("SymbolProjection_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(ViewportWidth, ViewportHeight, 0);

            var lookAt = new GeoCoordinate3D { Latitude = 52.52, Longitude = 13.405, Altitude = 0.0 };
            var props = new CameraProperties(lookAt, zoom: 10.0, heading: 0.0, tilt: tiltDeg);
            var mapCamera = new MapCamera(uCam, props); // ctor calls SyncToCamera

            return (mapCamera, camGo);
        }

        [Test]
        public void TryProjectAnchor_MatchesRealCameraWorldToScreenPoint()
        {
            (MapCamera mapCamera, GameObject camGo) = BuildCamera();
            Track(camGo);
            var lookAtSurface = new GeoCoordinate { Latitude = 52.52, Longitude = 13.405 };
            double3 sceneOriginRender = mapCamera.Projection.Project(lookAtSurface);

            // An anchor offset from the look-at -- projects away from dead-center, a non-degenerate check.
            var anchorGeo = new GeoCoordinate { Latitude = 52.521, Longitude = 13.406 };
            double3 renderPos = mapCamera.Projection.Project(anchorGeo);

            double3 local = renderPos - sceneOriginRender;
            var localUnity = new Vector3((float)local.x, (float)local.y, (float)local.z);
            Vector3 expectedScreen = mapCamera.Camera.WorldToScreenPoint(localUnity);

            float4x4 viewProj = math.mul(
                SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
            double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOriginRender, in viewProj, in viewportLogicalPx,
                float3x3.identity, out float2 screenPx, out float depth);

            Assert.IsTrue(ok, "an anchor near the look-at, in front of an overhead camera, must not be culled");
            Assert.AreEqual(expectedScreen.x, screenPx.x, 0.5f,
                $"SymbolScreenProjection.x ({screenPx.x:F2}) must match Camera.WorldToScreenPoint.x ({expectedScreen.x:F2})");
            Assert.AreEqual(expectedScreen.y, screenPx.y, 0.5f,
                $"SymbolScreenProjection.y ({screenPx.y:F2}) must match Camera.WorldToScreenPoint.y ({expectedScreen.y:F2})");
        }

        [Test]
        public void TryProjectAnchor_BehindTiltedCamera_CulledLikeRealCamera()
        {
            // Tilt the camera so it has a real "behind" direction (at tilt=0 it looks straight down and
            // nothing at ground level is ever behind it).
            (MapCamera mapCamera, GameObject camGo) = BuildCamera(tiltDeg: 60.0);
            Track(camGo);
            var lookAtSurface = new GeoCoordinate { Latitude = 52.52, Longitude = 13.405 };
            double3 sceneOriginRender = mapCamera.Projection.Project(lookAtSurface);

            // The look-at sits at the render origin, so a point at 1.5x the camera's own position lies beyond
            // the camera, behind it, for ANY tilt or heading.
            Vector3 cameraPosUnity = mapCamera.Camera.transform.position;
            var cameraPos = new double3(cameraPosUnity.x, cameraPosUnity.y, cameraPosUnity.z);
            double3 local = cameraPos * 1.5;
            double3 renderPos = sceneOriginRender + local;
            var localUnity = new Vector3((float)local.x, (float)local.y, (float)local.z);

            Vector4 clip = mapCamera.Camera.projectionMatrix * mapCamera.Camera.worldToCameraMatrix * new Vector4(localUnity.x, localUnity.y, localUnity.z, 1f);
            Assert.LessOrEqual(clip.w, 0f, "test precondition: 1.5x the camera's own render-relative position must be behind the real camera (clip.w <= 0)");

            float4x4 viewProj = math.mul(
                SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
            double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOriginRender, in viewProj, in viewportLogicalPx, float3x3.identity, out _, out _);

            Assert.IsFalse(ok, "an anchor behind the real (tilted) camera must be culled");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTileBlockBakerTests — bake against a real SymbolTileBlock, the native-lifetime half
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SymbolTileBlockBaker.Bake"/> against a REAL <see cref="SymbolTileBlock"/>: the native
    /// lifetime. The dispose-SITE teeth (commit-overwrite, FIFO-evict, true-release, Clear) live in
    /// <c>SymbolTileStoreTests</c> against a fake <see cref="IDisposable"/> counter.
    /// </summary>
    [TestFixture]
    public class SymbolTileBlockBakerTests
    {
        private static readonly SymbolQuad Quad = new SymbolQuad
        {
            TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
            UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(0.5f, 0.5f), LineIndex = 0,
        };

        private static void AddPointSymbol(SymbolTileBuffer buffer, int featureIndex, long tileKey) =>
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(100.0, 0.0, 200.0), quads: new List<SymbolQuad> { Quad },
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: "Point", textSizePx: 16f, paddingPx: 2f, sortKey: 0f,
                featureIndex: featureIndex, tileKey: tileKey, paint: SymbolPaint.Default);

        private static void AddCurvedSymbol(SymbolTileBuffer buffer, int featureIndex, long tileKey) =>
            TestSymbolTileBuffer.AddCurved(buffer,
                glyphs: new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 4f, Cell = Quad } },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                path: new[] { new double3(0, 0, 0), new double3(10, 0, 0), new double3(20, 0, 0) },
                anchorRender: new double3(10.0, 0.0, 20.0),
                text: "Curved", textSizePx: 16f, paddingPx: 2f, sortKey: 1f,
                maxAngleDeg: 45f, keepUpright: true,
                featureIndex: featureIndex, tileKey: tileKey, paint: SymbolPaint.Default);

        // ── The real native lifetime: bake a mixed point/curved list, then dispose. ──
        [Test]
        public void Bake_MixedSymbols_ProducesExpectedShape_ThenDisposeFreesEveryArray()
        {
            const long tileKey = 5L;
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, 0, tileKey);
            AddCurvedSymbol(buffer, 2, tileKey);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, tileOriginRender: double3.zero);
            try
            {
                Assert.AreEqual(2, block.Kinds.Length, "raw list length");
                Assert.AreEqual(1, block.Points.Length);
                Assert.AreEqual(1, block.Curveds.Length);
                Assert.AreEqual(tileKey, block.TileKey, "every label shares one physical tile");

                // localIndex == raw index for every slot.
                Assert.AreEqual(SymbolPlacementKind.Point, block.Kinds[0]);
                Assert.AreEqual(SymbolPlacementKind.Curved, block.Kinds[1]);

                // Staging upper bounds — the two symbols' contributions
                // (1 point box/quad/candidate + 1 curved placement's worth: (1 anchor + 1 fallback) * 1 glyph).
                Assert.AreEqual(1 + 2, block.MaxBoxes, "point: 1 box; curved: (anchor+fallback)=2 placements * 1 glyph");
                Assert.AreEqual(1 + 2, block.MaxQuads);
                Assert.AreEqual(1 + 2, block.MaxCandidates);

                // The point symbol's baked quad survives the bake byte-identically.
                int pointDetail = block.Detail[0];
                int quadStart = block.PointQuadStart[pointDetail];
                Assert.AreEqual(1, block.PointQuadCount[pointDetail]);
                Assert.AreEqual(Quad.UvTopLeft.x, block.Quads[quadStart].UvTopLeft.x, 1e-6f);

                // The curved symbol's anchor-fade-ids: one per anchor (1) + the trailing centred fallback.
                int curvedDetail = block.Detail[1];
                Assert.AreEqual(1, block.CurvedAnchorCount[curvedDetail]);
                Assert.AreEqual(2, block.AnchorFadeIds.Length, "1 anchor + 1 fallback");
            }
            finally
            {
                block.Dispose();
            }

            // Real native lifetime: every array must report !IsCreated after Dispose (idempotent — a second
            // Dispose() call, e.g. a future double-release, must not throw either).
            Assert.IsFalse(block.Kinds.IsCreated);
            Assert.IsFalse(block.Detail.IsCreated);
            Assert.IsFalse(block.WorldStart.IsCreated);
            Assert.IsFalse(block.WorldCount.IsCreated);
            Assert.IsFalse(block.RepAnchor.IsCreated);
            Assert.IsFalse(block.MaterialIndexes.IsCreated);
            Assert.IsFalse(block.PairRoles.IsCreated);
            Assert.IsFalse(block.TextIds.IsCreated);
            Assert.IsFalse(block.IconImageIds.IsCreated);
            Assert.IsFalse(block.Points.IsCreated);
            Assert.IsFalse(block.PointQuadStart.IsCreated);
            Assert.IsFalse(block.PointQuadCount.IsCreated);
            Assert.IsFalse(block.Curveds.IsCreated);
            Assert.IsFalse(block.CurvedGlyphStart.IsCreated);
            Assert.IsFalse(block.CurvedGlyphCount.IsCreated);
            Assert.IsFalse(block.CurvedAnchorStart.IsCreated);
            Assert.IsFalse(block.CurvedAnchorCount.IsCreated);
            Assert.IsFalse(block.CurvedAnchorFadeStart.IsCreated);
            Assert.IsFalse(block.Quads.IsCreated);
            Assert.IsFalse(block.Glyphs.IsCreated);
            Assert.IsFalse(block.Anchors.IsCreated);
            Assert.IsFalse(block.WorldPoints.IsCreated);
            Assert.IsFalse(block.AnchorFadeIds.IsCreated);
            // A second Dispose() must be a no-op: not throw AND not move the live-alloc counter (a double
            // decrement would under-count and mask a real leak elsewhere — VerifiedDisposable runs DoDispose once).
            long afterFirstDispose = SymbolTileBlock.DebugLiveAllocCount;
            Assert.DoesNotThrow(() => block.Dispose(), "Dispose must be idempotent");
            Assert.AreEqual(afterFirstDispose, SymbolTileBlock.DebugLiveAllocCount,
                "a second Dispose must not decrement the live-alloc counter again");
        }

        // ── Native-representation migration: MaterialIndexes is a per-symbol raw-order column mirroring
        //    symbol.MaterialIndex — baked so the off-main reconciler reads it off the block. ──
        [Test]
        public void Bake_MaterialIndexColumn_MirrorsRawSlots()
        {
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(1, 0, 2), quads: new List<SymbolQuad> { Quad },
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: "A", textSizePx: 16f, paddingPx: 2f,
                featureIndex: 0, tileKey: 9L, materialIndex: 3, paint: SymbolPaint.Default);
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(3, 0, 4), quads: new List<SymbolQuad> { Quad },
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: "B", textSizePx: 16f, paddingPx: 2f,
                featureIndex: 2, tileKey: 9L, materialIndex: 5, paint: SymbolPaint.Default);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 8, tileOriginRender: double3.zero);
            try
            {
                Assert.AreEqual(2, block.MaterialIndexes.Length, "raw list length");
                Assert.AreEqual(3, block.MaterialIndexes[0], "MaterialIndexes[0] mirrors label a (raw, un-clamped)");
                Assert.AreEqual(5, block.MaterialIndexes[1], "MaterialIndexes[1] mirrors label b");
            }
            finally { block.Dispose(); }
        }

        // ── PairRoles is a per-raw-slot column of the RESOLVED pair role, so the reconciler need not
        //    re-resolve. An adjacent owner+rider → Owner/Rider; a plain point and a curved record → None. ──
        [Test]
        public void Bake_PairRolesColumn_MirrorsResolvedPairing()
        {
            var buffer = new SymbolTileBuffer();
            // owner+rider must match on PairId/TileKey/MaterialIndex (SymbolPairing's ShapedSymbol overload rule).
            void AddPairHalf(string text, SymbolPairRole role) =>
                TestSymbolTileBuffer.AddPoint(buffer,
                    anchorRender: new double3(1, 0, 2), quads: new List<SymbolQuad> { Quad },
                    boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                    text: text, textSizePx: 16f, paddingPx: 2f,
                    featureIndex: 0, tileKey: 9L, materialIndex: 2, pairId: 1, pairRole: role, paint: SymbolPaint.Default);
            AddPairHalf("O", SymbolPairRole.Owner);
            AddPairHalf("R", SymbolPairRole.Rider);
            AddPairHalf("P", SymbolPairRole.None);
            AddCurvedSymbol(buffer, 4, 9L); // curved is never a pair half

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 8, tileOriginRender: double3.zero);
            try
            {
                Assert.AreEqual(SymbolPairRole.Owner, block.PairRoles[0], "resolved owner");
                Assert.AreEqual(SymbolPairRole.Rider, block.PairRoles[1], "resolved rider (adjacent, matching PairId/TileKey/MaterialIndex)");
                Assert.AreEqual(SymbolPairRole.None, block.PairRoles[2], "a plain point is unpaired");
                Assert.AreEqual(SymbolPairRole.None, block.PairRoles[3], "a curved record is never a pair half (road-shields-design.md § \"Symbol pairing\")");
            }
            finally { block.Dispose(); }
        }

        // ── TextIds/IconImageIds hold the ids interned at symbol construction, and Bake copies them. Distinct
        //    strings take ids in call order (Text before IconImage); a shared string resolves to the SAME id. ──
        [Test]
        public void Bake_TextIdColumns_MirrorInternedIds_SharedAndNullResolveCorrectly()
        {
            var buffer = new SymbolTileBuffer();
            var stringTable = new SymbolStringTable();
            void AddPointWith(string text, string icon, int feature) =>
                TestSymbolTileBuffer.AddPoint(buffer,
                    anchorRender: new double3(feature, 0, feature), quads: new List<SymbolQuad> { Quad },
                    boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                    text: text, iconImage: icon, textSizePx: 16f, paddingPx: 2f,
                    featureIndex: feature, tileKey: 9L, paint: SymbolPaint.Default, stringTable: stringTable);
            // a: Text "Alpha"/icon "star"; b: Text "Beta"/icon "star" (shares a's icon).
            AddPointWith("Alpha", "star", 0);
            AddPointWith("Beta", "star", 2);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 8, tileOriginRender: double3.zero);
            try
            {
                Assert.AreEqual(2, block.TextIds.Length, "raw list length");
                // Bake order: i=0 Intern("Alpha")=1,Intern("star")=2; i=1 Intern("Beta")=3,Intern("star")=2.
                Assert.AreEqual(1, block.TextIds[0], "first distinct text → id 1");
                Assert.AreEqual(3, block.TextIds[1], "second distinct text → id 3 (icon 'star' took id 2)");
                Assert.AreEqual(2, block.IconImageIds[0], "first distinct icon → id 2");
                Assert.AreEqual(2, block.IconImageIds[1], "shared icon 'star' → SAME id as label a");
                // Idempotent-mirror: re-interning the same field via the SAME table returns the id the bake stored.
                Assert.AreEqual(stringTable.Intern("Alpha"), block.TextIds[0], "column mirrors the interned text id");
                Assert.AreEqual(stringTable.Intern("star"), block.IconImageIds[1], "column mirrors the interned icon id");
            }
            finally { block.Dispose(); }
        }

        // ── Positive control: an un-Disposed block IS visible in DebugLiveAllocCount, proving
        //    the counter has teeth before the throwing-bake test below leans on it. ──
        [Test]
        public void Bake_PositiveControl_UndisposedBlock_CounterNonZeroDelta()
        {
            long before = SymbolTileBlock.DebugLiveAllocCount;
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, 0, 5L);
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, tileOriginRender: double3.zero);

            Assert.Greater(SymbolTileBlock.DebugLiveAllocCount, before,
                "a freshly-baked, not-yet-disposed block must show as a live allocation");

            block.Dispose();
            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount, "disposing returns the counter to baseline");
        }

        // ── A bake that throws after every array is allocated must leak nothing: Bake's catch disposes the
        //    partial block. A QuadCount larger than the quad pool makes Fill's pool read throw mid-fill. ──
        [Test]
        public void Bake_ThrowingSymbol_DisposesPartialBlock_NoLeak()
        {
            long before = SymbolTileBlock.DebugLiveAllocCount;

            var buffer = new SymbolTileBuffer();
            buffer.Quads.Add(Quad); // the pool holds exactly ONE quad
            buffer.AddSymbol(new ShapedSymbol
            {
                AnchorRender = new double3(100.0, 0.0, 200.0), Placement = SymbolPlacement.Point,
                Paint = SymbolPaint.Default, TextId = 1, // an arbitrary non-zero id — irrelevant to the throw
                TextSizePx = 16f, PaddingPx = 2f, SortKey = 0f, FeatureIndex = 0, TileKey = 5L,
                QuadStart = 0, QuadCount = 2, // claims TWO — Fill's second read overruns the pool
            });

            Assert.Throws<ArgumentOutOfRangeException>(
                () => SymbolTileBlockBaker.Bake(buffer, slotCount: 1, tileOriginRender: double3.zero));

            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount,
                "a throwing bake must dispose its partially-allocated block — no leaked live block, no leaked NativeArray");
        }

        // No zero-alloc tooth for Bake: it creates one SymbolTileBlock (a class) per tile commit. The reused
        // buffer's zero-alloc tooth is SymbolTileBufferAllocTests, which runs only in core-tests.
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolUpCarrierChainTests — the Up carrier chain driven end to end through the real BuildAsync
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The `Up` CARRIER CHAIN, driven end to end through the REAL
    /// <see cref="StyledSymbolTileBuilder.BuildAsync"/> and <see cref="SymbolFeatureExtractor.Extract"/>, against a
    /// closed form written out here. <c>WorldSurfaceUpPopulationTests</c> starts from a hand-built
    /// <see cref="ShapedSymbol"/>, so it cannot see a dropped <c>UpRender</c>/<c>PathUpRender</c> assignment.
    /// It uses <see cref="SphericalProjection"/> because Mercator's constant (0,1,0) `Up` hides a dropped one.
    /// </summary>
    [TestFixture]
    public class SymbolUpCarrierChainTests
    {

        /// <summary>A synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this guard exists to catch.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private static readonly TileId TileId0 = new TileId { Z = 10, X = 500, Y = 500 };
        private const uint Extent = 4096;
        private static readonly SphericalProjection Projection = new SphericalProjection();
        private const string FontName = "LatinFont";

        // Closed form, hand-written — see WorldSurfaceUpPopulationTests for the same discipline. Render
        // axes are (X,Z,Y) per SphericalProjection.cs's documented ECEF axis-swap, so Up.y carries sinφ.
        private static float3 ClosedFormUp(double latDeg, double lonDeg)
        {
            double phi = latDeg * math.PI_DBL / 180.0;
            double lambda = lonDeg * math.PI_DBL / 180.0;
            return new float3(
                (float)(math.cos(phi) * math.cos(lambda)), (float)math.sin(phi), (float)(math.cos(phi) * math.sin(lambda)));
        }

        private static float3 ExpectedUpAtTilePoint(double2 tilePoint)
        {
            double2 lonLat = TileId0.ToLonLat(tilePoint.x, tilePoint.y, Extent);
            return ClosedFormUp(lonLat.y, lonLat.x);
        }

        private static void AssertUp(float3 expected, double3 actual, string message)
        {
            Assert.AreEqual(expected.x, (float)actual.x, 1e-5f, $"{message} (x)");
            Assert.AreEqual(expected.y, (float)actual.y, 1e-5f, $"{message} (y)");
            Assert.AreEqual(expected.z, (float)actual.z, 1e-5f, $"{message} (z)");
            Assert.AreNotEqual(double3.zero, actual, $"{message}: must not be the dropped-assignment zero");
        }

        private static GlyphManager BuildGlyphManager()
        {
            string[] starts = { System.AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
            byte[] latin = null;
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
                    if (File.Exists(p)) { latin = File.ReadAllBytes(p); break; }
                    dir = dir.Parent;
                }
                if (latin != null) break;
            }
            Assert.IsNotNull(latin, "fixture glyph range must be found walking up from cwd/AppContext");
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latin };
            return new GlyphManager(TestGlyphSource.FromRanges(ranges));
        }

        private static SpriteAtlasView OneSpriteAtlas(string name)
            => new SpriteAtlasView
            {
                Index = SpriteIndex.Parse("{\"" + name + "\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}"),
                Size = new int2(32, 32),
            };

        // ── fixtures: a single Point feature, or a single 2-vertex LineString feature ───────────────────

        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        private static IDecodedTile OnePointTile(double2 point)
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Point, hasId: false, geometry: new[] { 1u | (1u << 3), ZigZagEncode((long)point.x), ZigZagEncode((long)point.y) });
            return TestDecodedTiles.Of("points", TileId0, new List<IFeature> { feature }, Extent);
        }

        private static readonly double2 LineFrom = new double2(500, 500);
        private static readonly double2 LineTo = new double2(3500, 3500);

        private static IDecodedTile OneLineTile()
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: new uint[]
                {
                    1u | (1u << 3), ZigZagEncode((long)LineFrom.x), ZigZagEncode((long)LineFrom.y),
                    2u | (1u << 3), ZigZagEncode((long)(LineTo.x - LineFrom.x)), ZigZagEncode((long)(LineTo.y - LineFrom.y)),
                });
            return TestDecodedTiles.Of("roads", TileId0, new List<IFeature> { feature }, Extent);
        }

        // ── (1) point TEXT, unpaired — SymbolFeatureExtractor.EmitText + StyledSymbolTileBuilder :257 ──

        [Test]
        public async Task PointText_ThroughRealExtractionAndBuild_UpRenderMatchesClosedForm()
        {
            var point = new double2(2000, 2000);
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "labels", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"text-font\":[\"" + FontName + "\"]}"),
            };

            using GlyphManager manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var symbols = new SymbolTileBuffer();
            await builder.BuildAsync(OnePointTile(point), TileId0, new[] { layer }, 0.0, Projection, symbols);

            Assert.AreEqual(1, symbols.Symbols.Count, "one point-text label");
            Assert.AreEqual(SymbolKind.Text, symbols.Symbols[0].Kind);
            AssertUp(ExpectedUpAtTilePoint(point), symbols.Symbols[0].UpRender, "point-text UpRender");
        }

        // ── (2) point ICON+TEXT PAIR — both EmitIcon(Owner)/EmitText(Rider) + builder :179/:257 ──

        [Test]
        public async Task PointIconTextPair_ThroughRealExtractionAndBuild_BothHalvesUpRenderMatchClosedForm()
        {
            var point = new double2(2000, 2000);
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "labels", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"text-font\":[\"" + FontName + "\"],\"icon-image\":\"marker\"}"),
            };

            using GlyphManager manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var symbols = new SymbolTileBuffer();
            await builder.BuildAsync(OnePointTile(point), TileId0, new[] { layer }, 0.0, Projection, symbols,
                spriteAtlas: OneSpriteAtlas("marker"));

            Assert.AreEqual(2, symbols.Symbols.Count, "a feature resolving both icon and text emits a paired instance (icon then text)");
            ShapedSymbol icon = symbols.Symbols[0], text = symbols.Symbols[1];
            Assert.AreEqual(SymbolKind.Icon, icon.Kind); Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole);
            Assert.AreEqual(SymbolKind.Text, text.Kind); Assert.AreEqual(SymbolPairRole.Rider, text.PairRole);

            float3 expected = ExpectedUpAtTilePoint(point);
            AssertUp(expected, icon.UpRender, "paired icon (owner) UpRender");
            AssertUp(expected, text.UpRender, "paired text (rider) UpRender");
        }

        // ── (3) curved TEXT — SymbolFeatureExtractor's curved-text PathUpRender (:321) + builder :287 ──

        [Test]
        public async Task CurvedText_ThroughRealExtractionAndBuild_PathUpRenderIsIndexParallelAndMatchesClosedFormAtBothEnds()
        {
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "lines", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "roads",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"text-font\":[\"" + FontName + "\"],\"symbol-placement\":\"line-center\"}"),
            };

            using GlyphManager manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var symbols = new SymbolTileBuffer();
            await builder.BuildAsync(OneLineTile(), TileId0, new[] { layer }, 0.0, Projection, symbols);

            Assert.AreEqual(1, symbols.Symbols.Count, "one curved text label");
            ShapedSymbol curved = symbols.Symbols[0];
            Assert.AreEqual(SymbolPlacement.LineCenter, curved.Placement);
            Assert.Greater(curved.PathCount, 0, "a curved label must carry a non-empty path");
            // AppendPath pads a short up-array with zero, so PathUp stays index-parallel to Path; AssertUp's
            // non-zero check catches a dropped extractor assignment.

            // Subdivision keeps the original endpoints, so elements 0 and last are safe to check whether or not
            // the globe subdivided this span.
            AssertUp(ExpectedUpAtTilePoint(LineFrom), symbols.PathUp[curved.PathStart], "curved text PathUpRender[0] (start)");
            AssertUp(ExpectedUpAtTilePoint(LineTo), symbols.PathUp[curved.PathStart + curved.PathCount - 1], "curved text PathUpRender[last] (end)");
        }

        // ── (4) along-line ICON — SymbolFeatureExtractor.EmitAlongLineIcon's PathUpRender (:605) + builder :216 ──

        [Test]
        public async Task AlongLineIcon_ThroughRealExtractionAndBuild_PathUpRenderIsIndexParallelAndMatchesClosedFormAtBothEnds()
        {
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "lines", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "roads",
                Paint = TestStyle.SymbolPaint(),
                // icon-rotation-alignment unset -> resolves auto -> map for line placement -> the P-B
                // one-glyph curved-icon emit shape (mirrors IconSkirtCarrierChainTests.AlongLineIconLayer).
                Layout = TestStyle.SymbolLayout("{\"icon-image\":\"arrow\",\"symbol-placement\":\"line\"}"),
            };

            // An icon-only layer must never touch the glyph/shaper machinery (mirrors
            // IconSkirtCarrierChainTests.IconOnlyGlyphManager).
            using var manager = new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));
            var builder = new StyledSymbolTileBuilder(manager);
            var symbols = new SymbolTileBuffer();
            await builder.BuildAsync(OneLineTile(), TileId0, new[] { layer }, 0.0, Projection, symbols,
                spriteAtlas: OneSpriteAtlas("arrow"));

            Assert.AreEqual(1, symbols.Symbols.Count, "one along-line icon label");
            ShapedSymbol icon = symbols.Symbols[0];
            Assert.AreEqual(SymbolKind.Icon, icon.Kind);
            Assert.AreEqual(1, icon.GlyphCount, "an along-line icon is a ONE-glyph curved label");
            Assert.Greater(icon.PathCount, 0, "an along-line icon must carry a non-empty path");
            // PathUp is always index-parallel to Path (see the curved-text test's identical note).

            AssertUp(ExpectedUpAtTilePoint(LineFrom), symbols.PathUp[icon.PathStart], "along-line icon PathUpRender[0] (start)");
            AssertUp(ExpectedUpAtTilePoint(LineTo), symbols.PathUp[icon.PathStart + icon.PathCount - 1], "along-line icon PathUpRender[last] (end)");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolZoomGateGatherTests — an out-of-zoom symbol is hard-skipped before it is ever projected
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolZoomGateGatherTests
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

        // MaterialIndex 0 ⇒ slot 0 ⇒ symbolLayers[0]. The anchor sits AT the look-at so no distance/horizon cull
        // preempts the zoom trigger — the zoom gate is the only thing that can skip it.
        private static SymbolTileBuffer Point(double3 anchor, string text, int feature)
        {
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: new SymbolPaint { TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f },
                textSizePx: 24f, paddingPx: 2f, sortKey: 0f, text: text, featureIndex: feature, tileKey: 0L,
                materialIndex: 0);
            return buffer;
        }

        // A single symbol layer (slot 0). minzoom null ⇒ visible at any zoom (the in-zoom control); a minzoom
        // above the Harness's live zoom (5.0) ⇒ out of zoom ⇒ the gate fires.
        private static SymbolRenderLayer BuildRenderLayer(double? minZoom)
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
            return SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(styleJson).Layers[0], settings, initialZoom: 5.0, drawIndex: 0);
        }

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
                _go = new GameObject("ZoomGate_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                // Live camera zoom 5.0 — a layer's minzoom > 5 is out of zoom, minzoom null/≤5 is in zoom.
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

        /// <summary>THE headline tooth (RED against the pre-gate code): a FRESH symbol whose layer is out of the
        /// live zoom is hard-skipped in gather — never a collision candidate — and attributed to the zoom bucket.
        /// Before the gate, it would have been staged as a Suppressed candidate (<c>LastCandidateCount == 1</c>,
        /// <c>LastZoomCulledCount == 0</c>): the work this change removes.</summary>
        [Test]
        public void FreshOutOfZoomSymbol_HardSkippedInGather_AbsentFromCollision()
        {
            using var h = new Harness();
            var outOfZoom = BuildRenderLayer(minZoom: 10.0); // 10 > live 5.0 ⇒ out of zoom
            try
            {
                var buffer = Point(h.Origin, "A", 0);
                var layers = new List<SymbolRenderLayer> { outOfZoom };

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: layers); // fresh — no live fade

                Assert.AreEqual(0, h.System.LastCandidateCount, "an out-of-zoom label never enters the collision pass");
                Assert.AreEqual(1, h.System.LastZoomCulledCount, "…it is attributed to the pre-projection zoom cull");
                Assert.AreEqual(0, h.System.LastDistanceCulledCount, "…and not mis-attributed to the B-3 distance cull");
                Assert.AreEqual(0, h.System.LastQuadCount, "…and draws nothing (output unchanged — it drew nothing before either)");
            }
            finally { outOfZoom.Dispose(); }
        }

        /// <summary>Control: the identical symbol under an IN-zoom layer is NOT zoom-culled — it stages as a
        /// candidate and the zoom bucket stays empty. Pins that the gate fires on the zoom predicate, not on
        /// merely having a layer list (which would silently cull everything).</summary>
        [Test]
        public void InZoomSymbol_NotZoomCulled_StagesAsCandidate()
        {
            using var h = new Harness();
            var inZoom = BuildRenderLayer(minZoom: null); // unbounded ⇒ visible at zoom 5.0
            try
            {
                var buffer = Point(h.Origin, "A", 0);
                var layers = new List<SymbolRenderLayer> { inZoom };

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: layers);

                Assert.AreEqual(1, h.System.LastCandidateCount, "an in-zoom label stages as a collision candidate");
                Assert.AreEqual(0, h.System.LastZoomCulledCount, "…and nothing is zoom-culled");
            }
            finally { inZoom.Dispose(); }
        }

        /// <summary>With NO layer list (the demo / single-material path) the zoom gate is inert — every symbol
        /// stages, nothing is zoom-culled. Pins <c>IsOutOfLiveZoom</c>'s <c>slotCount == 0</c> early return, the
        /// mirror of <c>ApplySuppression</c>'s own "no layer list → no zoom ranges" guard.</summary>
        [Test]
        public void NoLayerList_ZoomGateInert_EverythingStages()
        {
            using var h = new Harness();
            var buffer = Point(h.Origin, "A", 0);

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection); // symbolLayers: null

            Assert.AreEqual(1, h.System.LastCandidateCount, "no layer list ⇒ no zoom gate ⇒ the label stages");
            Assert.AreEqual(0, h.System.LastZoomCulledCount, "…and nothing is zoom-culled");
        }

        /// <summary>The fade-out-in-place exemption (guards the fall-through): a symbol shown while IN zoom, whose
        /// layer then leaves the zoom range, must NOT be hard-skipped while its fade is still alive — it stays a
        /// staged candidate so <c>ApplySuppression</c> hides it the SAME frame (no pop), and it is NOT counted in
        /// the zoom bucket (only fade-DEAD records are). Goes RED if the gate ever hard-skips out-of-zoom records
        /// unconditionally instead of routing live-fade ones through <c>MarkFadeOutIfAlive</c>.</summary>
        [Test]
        public void PreviouslyShownSymbol_GoneOutOfZoom_StaysStagedFading_NotHardSkipped()
        {
            using var h = new Harness();
            var inZoom = BuildRenderLayer(minZoom: null);
            var outOfZoom = BuildRenderLayer(minZoom: 10.0);
            try
            {
                var buffer = Point(h.Origin, "A", 0);
                var inLayers = new List<SymbolRenderLayer> { inZoom };
                var outLayers = new List<SymbolRenderLayer> { outOfZoom };

                // Two in-zoom ticks: it stages, wins its harvested verdict, and snaps to full opacity (a LIVE fade).
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: inLayers);
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: inLayers);
                Assert.AreEqual(1, h.System.LastQuadCount, "precondition: the label is placed and drawing while in zoom");

                // Now its layer leaves the live zoom. The record has a live fade ⇒ it must stay staged (fall
                // through), be Suppressed, and hide THIS frame — not be hard-culled (which would pop it).
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, symbolLayers: outLayers);

                Assert.AreEqual(1, h.System.LastCandidateCount, "a still-fading out-of-zoom label stays staged (not hard-skipped)");
                Assert.AreEqual(0, h.System.LastZoomCulledCount, "…so it is NOT counted in the zoom-cull bucket (only fade-dead records are)");
                Assert.AreEqual(0, h.System.LastQuadCount, "…and ApplySuppression hides it the same frame (no lingering draw)");
            }
            finally { inZoom.Dispose(); outOfZoom.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldBillboardVertexLayoutTests — the vertex-layout/struct sync tooth
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldBillboardVertexLayoutTests : BaseTestFixture
    {
        private static Mesh BuildFourVertexMesh()
        {
            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            vertices[0] = new WorldBillboardVertex();
            vertices[1] = new WorldBillboardVertex();
            vertices[2] = new WorldBillboardVertex();
            vertices[3] = new WorldBillboardVertex();
            var opacity = new NativeArray<float>(4, Allocator.Temp);
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f;
            var indices = new NativeArray<int>(6, Allocator.Temp);
            indices[0] = 0; indices[1] = 1; indices[2] = 2; indices[3] = 0; indices[4] = 2; indices[5] = 3;

            var mesh = new Mesh();
            WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh);
            vertices.Dispose();
            opacity.Dispose();
            indices.Dispose();
            return mesh;
        }

        [Test]
        public void Build_VertexBufferStride_MatchesTheStructSize()
        {
            Mesh mesh = Track(BuildFourVertexMesh());
            // 80 B: 18 pre-halo floats + SdfWidenPx's 2 floats (72 B -> 80 B). A field added without a
            // matching descriptor (or vice versa) mismatches the struct size against Unity's own accounting.
            Assert.AreEqual(UnsafeUtility.SizeOf<WorldBillboardVertex>(), mesh.GetVertexBufferStride(0));
            Assert.AreEqual(80, mesh.GetVertexBufferStride(0), "stream 0 grew 72 B -> 80 B for SdfWidenPx");
        }

        [Test]
        public void Build_DeclaresTexCoord6_Float32x3_OnStream0()
        {
            Mesh mesh = Track(BuildFourVertexMesh());
            Assert.IsTrue(mesh.HasVertexAttribute(VertexAttribute.TexCoord6), "Up must be declared as TEXCOORD6");
            Assert.AreEqual(3, mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord6), "Up is a float3");
            Assert.AreEqual(VertexAttributeFormat.Float32, mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord6));
            Assert.AreEqual(0, mesh.GetVertexAttributeStream(VertexAttribute.TexCoord6), "Up rides stream 0, with AnchorLocal/.../Tangent — not the per-frame Opacity stream");
        }

        [Test]
        public void Build_DeclaresTexCoord7_Float32x2_OnStream0()
        {
            Mesh mesh = Track(BuildFourVertexMesh());
            Assert.IsTrue(mesh.HasVertexAttribute(VertexAttribute.TexCoord7), "SdfWidenPx must be declared as TEXCOORD7");
            Assert.AreEqual(2, mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord7), "SdfWidenPx is a float2 — edge widening and AA widening");
            Assert.AreEqual(VertexAttributeFormat.Float32, mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord7));
            Assert.AreEqual(0, mesh.GetVertexAttributeStream(VertexAttribute.TexCoord7), "SdfWidenPx rides stream 0 — it is fixed per run, unlike the per-frame Opacity stream");
        }

        [Test]
        public void Build_VertexAttributeArray_StaysStrictlyAscendingByAttributeAcrossBothStreams()
        {
            Mesh mesh = Track(BuildFourVertexMesh());
            VertexAttributeDescriptor[] descriptors = mesh.GetVertexAttributes();

            for (int i = 1; i < descriptors.Length; i++)
                Assert.Less((int)descriptors[i - 1].attribute, (int)descriptors[i].attribute,
                    $"descriptor {i - 1} ({descriptors[i - 1].attribute}) must be strictly before descriptor {i} " +
                    "({descriptors[i].attribute}) — Unity's silent non-standard-order re-adjustment (zero ink) " +
                    "triggers the instant this is violated, REGARDLESS of which stream each attribute is on.");

            // The array's LAST element must be TexCoord7 (SdfWidenPx) — the frozen "append, never
            // reshuffle" rule, now one append further on than the Up carrier.
            Assert.AreEqual(VertexAttribute.TexCoord7, descriptors[descriptors.Length - 1].attribute,
                "TexCoord7 (SdfWidenPx) must be the new truly-last descriptor");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldSurfaceUpPopulationTests — the byte-identical invariant means render can't tell Up is wrong
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldSurfaceUpPopulationTests : BaseTestFixture
    {
        // A non-equator, non-prime-meridian anchor, so the sign constants are read where the code is not inert:
        // all three closed-form components (cosφcosλ, sinφ, cosφsinλ) are distinct and nonzero here.
        private static readonly GeoCoordinate Anchor = new GeoCoordinate { Latitude = 47.0, Longitude = 8.0 };

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16],
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static SymbolTileBuffer MakePointSymbol(double3 anchorRender, double3 upRender)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, float2.zero, new float2(18f, 18f),
                up: upRender, paint: SymbolPaint.Default, textSizePx: 24f, sortKey: 0f, featureIndex: 0, tileKey: 0L);
            return buffer;
        }

        // Runs one real Tick of `buffer` under `projection` and returns the world mesh's stream-0 vertices.
        private WorldBillboardVertex[] RunPointTick(IProjection projection, SymbolTileBuffer buffer)
        {
            var camGo = Track(new GameObject("SurfaceUpPopulation_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude, Altitude = 0.0 },
                zoom: 6.0, heading: 0.0, tilt: 0.0), projection: projection);
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(Anchor),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            // Collision verdicts apply one Tick late — duplicate before reading placement.
            system.TickSymbols(in frame, buffer, atlasTexture, projection);
            system.TickSymbols(in frame, buffer, atlasTexture, projection);
            Assert.AreEqual(1, system.LastQuadCount, "precondition: the label must place (not cull).");
            Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh), "the world slot mesh must exist.");
            WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] v, out _);
            return v;
        }

        // ── (a) Spherical, point symbol ──────────────────────────────────────────────────────────────────

        [Test]
        public void SphericalPointSymbol_Up_MatchesClosedFormGeodeticNormal_OnEveryCorner()
        {
            var projection = new SphericalProjection();
            double lambda = Anchor.Longitude * math.PI_DBL / 180.0;
            double phi    = Anchor.Latitude  * math.PI_DBL / 180.0;
            double sinPhi = math.sin(phi), cosPhi = math.cos(phi);
            double sinLam = math.sin(lambda), cosLam = math.cos(lambda);
            // Closed form written out here, not derived by calling ProjectPoint: render axes are (X,Z,Y) —
            // SphericalProjection.cs's own documented axis-swap — so Up.y carries sinφ.
            var expectedUp = new float3((float)(cosPhi * cosLam), (float)sinPhi, (float)(cosPhi * sinLam));

            double3 anchorRender = projection.Project(Anchor);
            double3 upRender = projection.ProjectPoint(Anchor).Up; // the value the extractor threads through the carrier chain

            WorldBillboardVertex[] v = RunPointTick(projection, MakePointSymbol(anchorRender, upRender));
            Assert.AreEqual(4, v.Length, "one point quad, 4 corners");

            foreach (WorldBillboardVertex vv in v)
            {
                Assert.AreEqual(expectedUp.x, vv.Up.x, 1e-5f, "Up.x (cosφ·cosλ)");
                Assert.AreEqual(expectedUp.y, vv.Up.y, 1e-5f, "Up.y (sinφ) — the axis a swapped narrow would miss");
                Assert.AreEqual(expectedUp.z, vv.Up.z, 1e-5f, "Up.z (cosφ·sinλ)");
                Assert.AreEqual(1.0, math.length(vv.Up), 1e-4, "Up is unit-length");
                Assert.AreNotEqual(new float3(0f, 1f, 0f), vv.Up, "must not be the Mercator-hardcoded +Y");
            }
            // All four corners of the same quad share the anchor's up (kill: a per-corner constant/garbage).
            for (int i = 1; i < v.Length; i++)
                Assert.AreEqual(v[0].Up, v[i].Up, "every corner of a point quad carries the SAME Up");
        }

        // ── (b) Web Mercator, same fixture ──────────────────────────────────────────────────────────────

        [Test]
        public void WebMercatorPointSymbol_Up_IsExactlyPlusY()
        {
            var projection = new WebMercatorProjection();
            double3 anchorRender = projection.Project(Anchor);
            double3 upRender = projection.ProjectPoint(Anchor).Up;

            WorldBillboardVertex[] v = RunPointTick(projection, MakePointSymbol(anchorRender, upRender));
            Assert.AreEqual(4, v.Length);

            foreach (WorldBillboardVertex vv in v)
                Assert.AreEqual(new float3(0f, 1f, 0f), vv.Up, "Web Mercator's Up is the CONSTANT +Y — no per-vertex math to narrow wrong");
        }

        // ── (c) Spherical, curved/along-line symbol ──────────────────────────────────────────────────────

        [Test]
        public void SphericalCurvedSymbol_Up_MatchesClosedFormAtEachGlyphsOwnSampledPosition()
        {
            var projection = new SphericalProjection();
            // A 2° span centred on the look-at, short enough that geodesic and linear lat/lon interpolation
            // agree well inside the 1e-4 tolerance.
            var geoA = new GeoCoordinate { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude - 1.0 };
            var geoB = new GeoCoordinate { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude + 1.0 };
            double3 a = projection.Project(geoA), b = projection.Project(geoB);
            double3 upA = projection.ProjectPoint(geoA).Up, upB = projection.ProjectPoint(geoB).Up;
            var path = new[] { a, b };
            var pathUps = new[] { upA, upB };

            var glyphs = new List<CurvedGlyph>
            {
                MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f),
            };
            LineAnchor centerAnchor = AnchorAtMidpoint(path);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { centerAnchor }, path, pathUps,
                placement: SymbolPlacement.LineCenter, paint: SymbolPaint.Default, textSizePx: 24f,
                maxAngleDeg: 45f, keepUpright: true, featureIndex: 0, tileKey: 0L);

            var camGo = Track(new GameObject("SurfaceUpPopulationCurved_TestCamera"));
            WorldBillboardVertex[] v;
            {
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude, Altitude = 0.0 },
                    zoom: 6.0, heading: 0.0, tilt: 0.0), projection: projection);
                var frame = new SceneFrame { SceneOriginRender = mapCamera.Projection.Project(Anchor), Rebase = float3x3.identity };
                using var atlasTexture = BuildTinyAtlasTexture();
                using var system = new SymbolPlacementSystem(mapCamera,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                system.TickSymbols(in frame, buffer, atlasTexture, projection);
                system.TickSymbols(in frame, buffer, atlasTexture, projection);
                Assert.AreEqual(3, system.LastQuadCount, "precondition: 3 glyphs, all placed");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh), "the world text slot must exist.");
                WorldMeshReadback.Read(mesh, out v, out _);
            }

            Assert.AreEqual(12, v.Length, "3 glyphs x 4 verts");

            // TileKey 0 unpacks to TileId{0,0,0}: the tile origin AnchorLocal was baked against, so worldPt
            // below is the glyph's real sampled world position.
            double3 tileOriginRender = TileRenderOrigin.Project(new TileId { Z = 0, X = 0, Y = 0 }, projection);

            double3 abDelta = b - a;
            double abLenSq = math.dot(abDelta, abDelta);
            var sampledT = new double[3];
            for (int g = 0; g < 3; g++)
            {
                WorldBillboardVertex vv = v[g * 4]; // one AnchorLocal/Up per glyph, shared by its 4 corners
                double3 worldPt = new double3(vv.AnchorLocal.x, vv.AnchorLocal.y, vv.AnchorLocal.z) + tileOriginRender;
                double t = math.dot(worldPt - a, abDelta) / abLenSq;
                sampledT[g] = t;

                var geoAtT = new GeoCoordinate { Latitude = Anchor.Latitude, Longitude = math.lerp(geoA.Longitude, geoB.Longitude, t) };
                double lambda = geoAtT.Longitude * math.PI_DBL / 180.0;
                double phi    = geoAtT.Latitude  * math.PI_DBL / 180.0;
                var expectedUp = new float3(
                    (float)(math.cos(phi) * math.cos(lambda)), (float)math.sin(phi), (float)(math.cos(phi) * math.sin(lambda)));

                Assert.AreEqual(expectedUp.x, vv.Up.x, 1e-4f, $"glyph {g}: Up.x at its own sampled position");
                Assert.AreEqual(expectedUp.y, vv.Up.y, 1e-4f, $"glyph {g}: Up.y at its own sampled position");
                Assert.AreEqual(expectedUp.z, vv.Up.z, 1e-4f, $"glyph {g}: Up.z at its own sampled position");

                // The residual is bounded by the subdivision policy (~2°) for a genuine curved surface —
                // a swapped axis (Up<->Tangent) would give O(1), not a small residual.
                float dotUpTangent = math.dot(vv.Up, vv.Tangent);
                Assert.Less(math.abs(dotUpTangent), 0.02f, $"glyph {g}: |dot(Up, Tangent)| must stay small");

                for (int c = 1; c < 4; c++)
                    Assert.AreEqual(vv.Up, v[g * 4 + c].Up, $"glyph {g}: every corner shares the glyph's own Up");
            }

            // The glyphs sample distinct, MONOTONIC fractions. Either direction is correct: keep-upright reverses
            // the walk when the on-screen tangent points left, which this geometry can trigger.
            bool ascending = sampledT[0] < sampledT[1];
            if (ascending)
            {
                Assert.Less(sampledT[0], sampledT[1] - 1e-3, "glyph 0 must sample strictly before glyph 1 (ascending walk)");
                Assert.Less(sampledT[1], sampledT[2] - 1e-3, "glyph 1 must sample strictly before glyph 2 (ascending walk)");
            }
            else
            {
                Assert.Greater(sampledT[0], sampledT[1] + 1e-3, "glyph 0 must sample strictly after glyph 1 (KeepUpright-reversed walk)");
                Assert.Greater(sampledT[1], sampledT[2] + 1e-3, "glyph 1 must sample strictly after glyph 2 (KeepUpright-reversed walk)");
            }
        }

        private static CurvedGlyph MakeCurvedGlyph(float arcCenter) => new CurvedGlyph
        {
            ArcCenter = arcCenter,
            Cell = new SymbolQuad
            {
                TopLeft = new float2(-5f, 8f), BottomRight = new float2(5f, -2f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        // The 2-point case of SymbolPlacementStructureTests' private AnchorAt(path, 0.5), duplicated because
        // a six-line test formula does not justify sharing across fixtures.
        private static LineAnchor AnchorAtMidpoint(double3[] path)
        {
            double total = math.length(path[1] - path[0]);
            return total > 0.0 ? new LineAnchor(0, 0.5f) : new LineAnchor(0, 0f);
        }
    }
}
