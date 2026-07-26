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
using MapRenderer.Core.View.Camera;
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
    /// S20 T4: over a STABLE synthetic label set, a steady-state <see cref="LabelPlacementSystem.Tick"/>
    /// (project → billboard-build → submit) allocates ZERO managed garbage.
    /// </summary>
    [TestFixture]
    public class LabelPlacementAllocTests
    {
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
            atlas.Append(glyph);

            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // AnchorRender is render-space PRE-RTC (the same space projection.Project(geo) emits) -- NOT a
        // small local offset. It must be built relative to the frame's SceneOriginRender (a large absolute
        // Mercator coordinate), or TryProjectAnchor's rebase lands it far outside the viewport and every
        // label is silently culled (steady-state Tick would then measure the trivial "nothing to place"
        // no-op branch, not the real project->build->submit path).
        private static List<LabelInstance> BuildLabels(int count, double3 sceneOriginRender, long tileKey = 0L, bool allowOverlap = false)
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
            var layout = new TextLayoutResult { Quads = quads, BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1 };

            var labels = new List<LabelInstance>(count);
            for (int i = 0; i < count; i++)
            {
                labels.Add(new LabelInstance
                {
                    AnchorRender = sceneOriginRender + new double3(i * 10.0, 0.0, i * 5.0),
                    Layout = layout,
                    Paint = LabelPaint.Default,
                    TextSizePx = 24f,
                    SortKey = 0f,
                    FeatureIndex = i,
                    TileKey = tileKey,
                    AllowOverlap = allowOverlap,
                });
            }
            return labels;
        }

        [Test]
        public void Tick_SteadyState_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("LabelAlloc_TestCamera");
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
            var labels = BuildLabels(20, frame.SceneOriginRender);
            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));

            // Step 5a: the plan is built ONCE, outside every measured region — TestSymbolPlan.Build
            // allocates managed scratch, and the thing under measurement is Tick, not plan construction.
            // This is the faithful translation of what the batch path did: SymbolLabelBatchBuilder.Build
            // also ran once and the mirror refresh then skipped on the unchanged version, so repeated
            // ticks measured the same memo-hit steady state they measure here.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(labels);


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
        //    WorldLabelRenderer.EndFrame's WorldBillboardMeshBuilder.Build + material-bind +
        //    EnsureChild/present branch never runs (every world slot resolves a null material and
        //    takes the "hide" branch) — the built+presented path A1 actually ships was unmeasured. This adds
        //    that arm: a world text base (so TEXT slots build+present for real) PLUS one Icon-kind label with
        //    NO world icon material configured (worldIconBase omitted) — its world slot is emitted-but-
        //    unrenderable every Tick, the exact steady-state shape the round's fix-A idle-reclaim bug hits
        //    (a slot that stays emitted but never resolves a material must NOT churn its
        //    Mesh/GameObject/NativeLists every IdleReclaimFrames — see WorldLabelRenderer.EndFrame's
        //    emittedThisFrame gate). Runs enough steady-state Ticks to cross the K=60 reclaim boundary so a
        //    regression there shows up as periodic alloc, not just a single-Tick false negative. ──
        [Test]
        public void Tick_SteadyState_WorldBuildAndPresent_AcrossReclaimBoundary_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("LabelAllocWorld_TestCamera");
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
            // allowOverlap: true — the 20 labels sit within a few px of each other (i*10, i*5 offsets), so
            // WITHOUT it collision would cull all but the sort-key winner (this tooth wants a stable, fully-
            // placed scene every Tick, not a collision fixed-point).
            List<LabelInstance> textLabels = BuildLabels(20, frame.SceneOriginRender, tileKey, allowOverlap: true);

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(1f, 1f), LineIndex = 0,
                },
            };
            var iconLayout = new TextLayoutResult { Quads = iconQuads, BoundsMin = new float2(-8f, -8f), BoundsMax = new float2(8f, 8f), LineCount = 1 };
            var labels = new List<LabelInstance>(textLabels)
            {
                new LabelInstance
                {
                    AnchorRender = frame.SceneOriginRender,
                    Layout = iconLayout,
                    Kind = LabelKind.Icon, // no worldIconBase supplied below — this slot is emitted every
                                           // Tick but never resolves a material (the fix-A scenario).
                    Paint = LabelPaint.Default,
                    TextSizePx = TextQuadLayout.OneEm,
                    AllowOverlap = true,
                    SortKey = 0f,
                    FeatureIndex = textLabels.Count,
                    TileKey = tileKey,
                    // R3: this label's AnchorRender coincides exactly with textLabels[0]'s (both sit at
                    // frame.SceneOriginRender + zero offset), and PointFadeId hashes (AnchorRender,
                    // MaterialIndex, Text, IconImage) — NOT FeatureIndex/TileKey — so with both Text and
                    // IconImage left at their default null, this candidate shared a FadeId with textLabels[0].
                    // Under R3, FadeId is the display key (LabelCandidate.FadeId's uniqueness contract), so a
                    // co-live collision fires AssertFadeIdsUnique's Debug.LogAssertion every steady-state Tick
                    // — a real per-Tick managed allocation this GC-zero tooth exists to catch. Distinct
                    // IconImage keeps this candidate's identity unique (harmless here — IconImage is an
                    // identity fold only; this test supplies quads directly, no sprite atlas lookup).
                    IconImage = "steady-state-icon",
                },
            };

            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));

            // Step 5a: the plan is built ONCE, outside every measured region — TestSymbolPlan.Build
            // allocates managed scratch, and the thing under measurement is Tick, not plan construction.
            // This is the faithful translation of what the batch path did: SymbolLabelBatchBuilder.Build
            // also ran once and the mirror refresh then skipped on the unchanged version, so repeated
            // ticks measured the same memo-hit steady state they measure here.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(labels);


            try
            {
                for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
                Assert.AreEqual(labels.Count, system.LastQuadCount, "every label (incl. the unrenderable icon) must place — the icon is emitted regardless of a missing world material (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, LabelKind.Text), "the world TEXT slot must be built+presented after warm-up (the branch this tooth measures).");
                Assert.IsFalse(system.IsWorldSlotVisible(tileKey, 0, LabelKind.Icon), "the world ICON slot has no material — it must stay hidden (not crash, not churn).");

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
        //    curved arm of WorldLabelRenderer.Emit must reuse the slot's NativeLists exactly as point does,
        //    not allocate per glyph. Curved now shares the SAME world sink as point/icon (StageCurved's
        //    emit.IsWorld=true), so this is the analogous steady-state warm-up + Not.AllocatingGCMemory check. ──
        [Test]
        public void Tick_SteadyState_CurvedWorldEmit_AllocatesNoGCMemory()
        {
            var camGo = new GameObject("LabelAllocCurved_TestCamera");
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
            var curvedLabel = new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                PathRender = path,
                LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                MaxAngleDeg = 45f,
                KeepUpright = true,
                FeatureIndex = 0,
                TileKey = tileKey,
            };
            var labels = new List<LabelInstance> { curvedLabel };

            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));

            // Step 5a: the plan is built ONCE, outside every measured region — TestSymbolPlan.Build
            // allocates managed scratch, and the thing under measurement is Tick, not plan construction.
            // This is the faithful translation of what the batch path did: SymbolLabelBatchBuilder.Build
            // also ran once and the mirror refresh then skipped on the unchanged version, so repeated
            // ticks measured the same memo-hit steady state they measure here.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            SymbolGatherPlan builtPlan = plan.Build(labels);


            try
            {
                for (int i = 0; i < 3; i++) system.Tick(in frame, builtPlan, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "the curved label's single glyph must place (precondition).");
                Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, LabelKind.Text), "the world TEXT slot must be built+presented (curved routes there since Stage AC).");

                Assert.That(() =>
                    {
                        for (int i = 0; i < 5; i++) system.Tick(in frame, builtPlan, atlasTexture);
                    },
                    Is.Not.AllocatingGCMemory(),
                    "steady-state Ticks over a curved-emitting scene must allocate ZERO managed garbage — the " +
                    "curved arm of WorldLabelRenderer.Emit reuses the slot's NativeLists exactly like point.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Stage 2 (labels-async-reconcile) T-ALLOC: a WARM per-frame cross-tile dedup allocates ZERO. The
        //    interning happens ONCE at CompleteBuild, so the per-frame CollectInto does no string work; and the
        //    integer DedupKey (a `readonly struct : IEquatable<DedupKey>`) does NOT box in the reused _dedup.
        //    RED-verify: make DedupKey a `class` (or drop IEquatable, forcing boxed comparisons) → per-label
        //    heap alloc every frame → RED. Store-only tooth (no LabelPlacementSystem needed). ──
        [Test]
        public void WarmDedup_ZeroAlloc()
        {
            var store = new SymbolTileLabelStore(cacheCap: 16);
            var tile = new TileId { Z = 12, X = 3, Y = 4 };
            var key = new SymbolTileLabelStore.Key("src", tile);

            var labels = new List<LabelInstance>(24);
            for (int i = 0; i < 20; i++)
            {
                labels.Add(new LabelInstance
                {
                    Placement = SymbolPlacement.Point,
                    AnchorRender = new double3(i * 100.0, 0.0, i * 100.0), // distinct cells → distinct dedup keys
                    MaterialIndex = 0,
                    Text = "L" + i,
                    TileKey = 0L,
                });
            }
            store.CompleteBuild(key, store.BeginBuild(key), labels); // interns once here (off the per-frame path)

            var output = new List<LabelInstance>(64);
            const double q = 50.0;
            store.CollectInto(output, q, out _); // warm: grow _dedup + output capacity once (allowed to allocate)

            Assert.That(() => store.CollectInto(output, q, out _),
                Is.Not.AllocatingGCMemory(),
                "a warm per-frame dedup must allocate ZERO — interning happened once at CompleteBuild and the " +
                "integer DedupKey does not box in the reused _dedup dictionary.");
        }
    }
}
