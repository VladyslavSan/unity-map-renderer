// Unity EditMode only — needs UnityEngine.TestTools.Constraints (GC.Alloc profiler recorder) + a real
// Camera/Mesh/Material. NOT registered in core-tests.csproj.
//
// Only UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() is
// trustworthy for this measurement (GC.GetTotalMemory / GetAllocatedBytesForCurrentThread both lie on
// this Unity Mono runtime). First-frame warmup (NativeList growth, first Mesh/Material creation) may
// allocate; the tooth is the STEADY (post-warmup) path.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T4: over a STABLE synthetic symbol set, a steady-state <see cref="SymbolPlacementSystem.Tick"/>
    /// (project → billboard-build → submit) allocates ZERO managed garbage.
    /// </summary>
    [TestFixture]
    public class SymbolPlacementAllocTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

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

        // AnchorRender is render-space PRE-RTC (the same space projection.Project(geo) emits) -- NOT a
        // small local offset. It must be built relative to the frame's SceneOriginRender (a large absolute
        // Mercator coordinate), or TryProjectAnchor's rebase lands it far outside the viewport and every
        // symbol is silently culled (steady-state Tick would then measure the trivial "nothing to place"
        // no-op branch, not the real project->build->submit path).
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
            var camGo = new GameObject("SymbolAlloc_TestCamera");
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

            var atlasTexture = BuildTinyAtlasTexture();
            var buffer = BuildSymbols(20, frame.SceneOriginRender);
            var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));

            // Step 5a: the plan is built ONCE, outside every measured region — TestSymbolPlan.Build
            // allocates managed scratch, and the thing under measurement is Tick, not plan construction.
            // This is the faithful translation of what the pre-native-gather batch path did: the SoA build
            // also ran once and the mirror refresh then skipped on the unchanged version, so repeated
            // ticks measured the same memo-hit steady state they measure here.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);


            try
            {
                // Warm-up ticks: first-frame NativeList growth + Mesh/Material creation is allowed to allocate.
                for (int i = 0; i < 3; i++)
                {
                    system.Tick(in frame, builtPlan, atlasTexture);
                }

                Assert.That(() => system.Tick(in frame, builtPlan, atlasTexture),
                    Is.Not.AllocatingGCMemory(),
                    "a steady-state Tick (same label count/shape as the warm-up) must allocate ZERO managed garbage");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Epic A / A1 hardening round (B): the tooth above never supplies worldTextBase, so
        //    WorldSymbolRenderer.EndFrame's WorldBillboardMeshBuilder.Build + material-bind +
        //    EnsureChild/present branch never runs (every world slot resolves a null material and
        //    takes the "hide" branch) — the built+presented path A1 actually ships was unmeasured. This adds
        //    that arm: a world text base (so TEXT slots build+present for real) PLUS one Icon-kind symbol with
        //    NO world icon material configured (worldIconBase omitted) — its world slot is emitted-but-
        //    unrenderable every Tick, the exact steady-state shape the round's fix-A idle-reclaim bug hits
        //    (a slot that stays emitted but never resolves a material must NOT churn its
        //    Mesh/GameObject/NativeLists every IdleReclaimFrames — see WorldSymbolRenderer.EndFrame's
        //    emittedThisFrame gate). Runs enough steady-state Ticks to cross the K=60 reclaim boundary so a
        //    regression there shows up as periodic alloc, not just a single-Tick false negative. ──
        [Test]
        public void Tick_SteadyState_WorldBuildAndPresent_AcrossReclaimBoundary_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("SymbolAllocWorld_TestCamera");
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

            var atlasTexture = BuildTinyAtlasTexture();
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 5); // matches camera zoom (Risk R1 + coverage-cull realism)
            // allowOverlap: true — the 20 symbols sit within a few px of each other (i*10, i*5 offsets), so
            // WITHOUT it collision would cull all but the sort-key winner (this tooth wants a stable, fully-
            // placed scene every Tick, not a collision fixed-point).
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
                                      // Tick but never resolves a material (the fix-A scenario).
                paint: SymbolPaint.Default,
                textSizePx: TextQuadLayout.OneEm,
                allowOverlap: true,
                sortKey: 0f,
                featureIndex: textCount,
                tileKey: tileKey,
                // R3: this symbol's AnchorRender coincides exactly with textSymbols[0]'s (both sit at
                // frame.SceneOriginRender + zero offset), and PointFadeId hashes (AnchorRender,
                // MaterialIndex, Text, IconImage) — NOT FeatureIndex/TileKey — so with both Text and
                // IconImage left at their default null, this candidate shared a FadeId with textSymbols[0].
                // Under R3, FadeId is the display key (SymbolCandidate.FadeId's uniqueness contract), so a
                // co-live collision fires AssertFadeIdsUnique's Debug.LogAssertion every steady-state Tick
                // — a real per-Tick managed allocation this GC-zero tooth exists to catch. Distinct
                // IconImage keeps this candidate's identity unique (harmless here — IconImage is an
                // identity fold only; this test supplies quads directly, no sprite atlas lookup).
                iconImage: "steady-state-icon");

            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));

            // Step 5a: the plan is built ONCE, outside every measured region — TestSymbolPlan.Build
            // allocates managed scratch, and the thing under measurement is Tick, not plan construction.
            // This is the faithful translation of what the pre-native-gather batch path did: the SoA build
            // also ran once and the mirror refresh then skipped on the unchanged version, so repeated
            // ticks measured the same memo-hit steady state they measure here.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);


            try
            {
                for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
                Assert.AreEqual(buffer.Symbols.Count, system.LastQuadCount, "every label (incl. the unrenderable icon) must place — the icon is emitted regardless of a missing world material (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Text), "the world TEXT slot must be built+presented after warm-up (the branch this tooth measures).");
                Assert.IsFalse(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Icon), "the world ICON slot has no material — it must stay hidden (not crash, not churn).");

                // 65 > IdleReclaimFrames (60) — the icon slot, emitted every Tick but never presentable,
                // would churn a Mesh/GameObject/3x NativeList every 60th Tick under the pre-fix bug.
                Assert.That(() =>
                    {
                        for (int i = 0; i < 65; i++) system.Tick(in frame, builtPlan, atlasTexture);
                    },
                    Is.Not.AllocatingGCMemory(),
                    "65 steady-state Ticks (crossing the K=60 idle-reclaim boundary) with a real world-built+presented " +
                    "TEXT slot AND an emitted-but-unrenderable ICON slot must allocate ZERO managed garbage.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Stage AC (curved-world) T-ALLOC: extends the tooth above to a CURVED-emitting frame — the
        //    curved arm of WorldSymbolRenderer.Emit must reuse the slot's NativeLists exactly as point does,
        //    not allocate per glyph. Curved now shares the SAME world sink as point/icon (StageCurved's
        //    emit.IsWorld=true), so this is the analogous steady-state warm-up + Not.AllocatingGCMemory check. ──
        [Test]
        public void Tick_SteadyState_CurvedWorldEmit_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("SymbolAllocCurved_TestCamera");
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

            var atlasTexture = BuildTinyAtlasTexture();
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

            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));

            // Step 5a: the plan is built ONCE, outside every measured region — TestSymbolPlan.Build
            // allocates managed scratch, and the thing under measurement is Tick, not plan construction.
            // This is the faithful translation of what the pre-native-gather batch path did: the SoA build
            // also ran once and the mirror refresh then skipped on the unchanged version, so repeated
            // ticks measured the same memo-hit steady state they measure here.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);


            try
            {
                for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "the curved label's single glyph must place (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Text), "the world TEXT slot must be built+presented (curved routes there since Stage AC).");

                Assert.That(() =>
                    {
                        for (int i = 0; i < 5; i++) system.Tick(in frame, builtPlan, atlasTexture);
                    },
                    Is.Not.AllocatingGCMemory(),
                    "steady-state Ticks over a curved-emitting scene must allocate ZERO managed garbage — the " +
                    "curved arm of WorldSymbolRenderer.Emit reuses the slot's NativeLists exactly like point.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── §10 D8 P11: a centred icon+text pair's steady-state Tick allocates ZERO. The pair collapses two
        //    records into ONE SymbolCandidate/emit-range pair, which is fewer sort/grid/fade operations than
        //    before, not more — SymbolPairing is a stateless O(1) helper and AppendPointHalf reuses the existing
        //    emit NativeArray, so nothing here should allocate any differently than the lone-icon tooth above. ──
        [Test]
        public void Tick_SteadyState_CentredPair_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("SymbolAllocPair_TestCamera");
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

            var atlasTexture = BuildTinyAtlasTexture();
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

            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            try
            {
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
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── C8 (stage C): the same steady-state zero-alloc claim for a pair that is ACTUALLY placing without
        //    one half — the state that exercises every new branch (the collision write-back, the dropped-halves
        //    map write in HarvestCollision, the stage job's probe, the emit skip). The map is a persistent
        //    NativeHashMap cleared and refilled in place, so none of that may reach the managed heap. ──
        [Test]
        public void Tick_SteadyState_OptionalPairPlacingWithoutItsText_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("SymbolAllocOptionalPair_TestCamera");
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

            var atlasTexture = BuildTinyAtlasTexture();
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

            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")),
                worldIconBase: new Material(Shader.Find("Map/Symbol/IconWorld")));

            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(buffer);

            try
            {
                for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
                Assert.AreEqual(2, system.LastCandidateCount, "precondition: the blocker + the pair.");
                Assert.AreEqual(2, system.LastQuadCount,
                    "precondition: the blocker's quad and the pair's ICON quad — the text half must be dropped, " +
                    "or this measures the ordinary all-or-nothing path rather than stage C's.");

                Assert.That(() =>
                    {
                        for (int i = 0; i < 65; i++) system.Tick(in frame, builtPlan, atlasTexture);
                    },
                    Is.Not.AllocatingGCMemory(),
                    "65 steady-state Ticks over a text-optional pair placing WITHOUT its text must allocate ZERO " +
                    "managed garbage.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Stage 2 (symbols-async-reconcile) T-ALLOC: a WARM per-frame cross-tile dedup allocates ZERO. The
        //    interning happens ONCE at CompleteBuild, so the per-frame CollectInto does no string work; and the
        //    integer DedupKey (a `readonly struct : IEquatable<DedupKey>`) does NOT box in the reused _dedup.
        //    RED-verify: make DedupKey a `class` (or drop IEquatable, forcing boxed comparisons) → per-symbol
        //    heap alloc every frame → RED. Store-only tooth (no SymbolPlacementSystem needed). ──
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
}
