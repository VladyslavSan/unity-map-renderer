// Unity EditMode only — SymbolGatherPlan / SymbolTileBlock / SymbolPlacementSystem.GatherIntoMirror all use
// Unity.Collections; reached via InternalsVisibleTo("MapRenderer.Tests.EditMode"). NOT in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Is = UnityEngine.TestTools.Constraints.Is; // Is.Not.AllocatingGCMemory() — T6

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// R1 (design §10.2, plan `symbol-gather-memo-plan.md`): <see cref="SymbolPlacementSystem.GatherIntoMirror"/>
    /// memoizes its heavy compaction on <see cref="SymbolGatherPlan.WinnerSetVersion"/> — a same-source,
    /// same-version frame runs only the three per-frame masks (Departing/CoverageFading/Dropped), not the full
    /// pool rebuild. These are the CONTENT teeth: byte-identity across held frames (T1), invalidation on a real
    /// version change with the winner SET (T2a) or CONTENT (T3/T4/T4b) changing, and the memo-hit path's own
    /// zero-GC guarantee (T6). <see cref="SymbolPlacementSystem.MirrorRebuildCount"/> is the discriminating signal
    /// throughout — without it every test here would pass trivially against an unmemoized implementation.
    ///
    /// <para>T2b (a REAL front swap through the production <see cref="MapRenderer.Unity.Text.SymbolSubsystem"/>,
    /// the stage's only end-to-end guard) and T5 (a restyle through the subsystem) live in
    /// <c>SymbolReconcileAsyncTests</c> — that fixture already owns the async pump harness (UseImmediateGlyphs
    /// / DriveTileBytesReady / PumpToQuiescence) both need, so building a second copy of it here would duplicate
    /// non-trivial async machinery for no benefit; this is a placement deviation from the plan's table (which
    /// listed T5 under this file) noted for the reviewer, not a change to T5's teeth.</para>
    ///
    /// Fixture style follows <c>SymbolGatherParityTests</c> / <c>SymbolGatherPlanDropMaskTests</c>: a real
    /// <see cref="SymbolTileStore"/> seeded via <see cref="SymbolTileBlockBaker"/>, a real
    /// <see cref="SymbolPlacementSystem"/> behind a throwaway camera/material (<see cref="LpsHarness"/> — a plain
    /// camera, no real look-at, since <see cref="SymbolPlacementSystem.GatherIntoMirror"/>/<c>CopyMirrorInto</c>
    /// never touch the camera; <see cref="TickHarness"/> adds a real look-at only for the tests below that drive
    /// a full <c>Tick</c>). T3 deliberately compares content across a <see cref="TickHarness"/> (zoom 12 @
    /// 10°,10°, needed for its demo-batch <c>Tick</c> call) and a plain <see cref="LpsHarness"/> oracle (zoom 5 @
    /// 0°,0°) — valid, not an oversight, because the gather/mirror comparison is camera-independent; the two
    /// harnesses' differing cameras never enter it. The content-diff oracle is the shared
    /// <see cref="SymbolBatchDiff.FirstDifference"/>. Per NIT7, the REFERENCE gather alternates between TWO
    /// persistent <see cref="SymbolGatherPlan"/> objects fed to ONE reference <see cref="SymbolPlacementSystem"/>
    /// — a different instance identity than the previous call always mismatches <c>_mirrorSource</c>, so the
    /// reference NEVER memo-hits (a trustworthy ground truth) with zero per-tick native allocation (a
    /// fresh-plan-per-tick oracle would leak <c>Allocator.Persistent</c> lists).
    /// </summary>
    [TestFixture]
    public class SymbolGatherMemoTests
    {
        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        private static (List<SymbolQuad> Quads, float2 BoundsMin, float2 BoundsMax) OneQuad(float u) => (
            new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            float2.zero, new float2(18f, 18f));

        private static SymbolTileBuffer PointSymbol(double3 anchor, string text, int feature, long tileKey, float u)
        {
            var layout = OneQuad(u);
            return TestSymbolTileBuffer.Point(anchor, layout.Quads, layout.BoundsMin, layout.BoundsMax,
                text: text, textSizePx: 20f, paddingPx: 2f, featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);
        }

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("s", t);
        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        private static void SeedTile(SymbolTileStore store, TileId tile, SymbolTileBuffer buffer)
        {
            int gen = store.BeginBuild(Key(tile));
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P), new SymbolStringTable());
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, block), "sanity: block committed");
        }

        // Fills `plan` from `store`'s current winner set at `version`, with optional per-tile Fade/Drop/Departing
        // overrides (all default Keep/not-departing). Mirrors SymbolGatherPlanDropMaskTests.BuildMaskedPlan/
        // BuildReferencePlan but generalized over which per-frame override applies to which tile, since these tests
        // need to vary EITHER the winner set (a different store) OR just the masks (same store, same records).
        private static void BuildPlan(SymbolTileStore store, SymbolGatherPlan plan, int version,
            long dropTileKey = -1, long fadeTileKey = -1, long departingTileKey = -1)
        {
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, quantizeMeters: 1.0, out _);

            var decisions = new List<byte>(blockId.Count);
            var departing = new List<byte>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
            {
                long tk = store.OrderedBlocks[blockId[i]].TileKey;
                byte decision = tk == dropTileKey ? SymbolTileCoverageFilter.Drop
                              : tk == fadeTileKey ? SymbolTileCoverageFilter.Fade
                              : SymbolTileCoverageFilter.Keep;
                decisions.Add(decision);
                departing.Add(tk == departingTileKey ? (byte)1 : isDeparting[i]);
            }
            plan.Build(blockId, localIndex, departing, decisions, store.OrderedBlocks, version);
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates (mirrors SymbolGatherParityTests'
        // LpsHarness) — GatherIntoMirror/CopyMirrorInto touch neither the camera nor the material.
        private sealed class LpsHarness : System.IDisposable
        {
            public readonly SymbolPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("GatherMemo_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();
                UnityEngine.Object.DestroyImmediate(_camGo);
                UnityEngine.Object.DestroyImmediate(_rt);
                UnityEngine.Object.DestroyImmediate(_baseMaterial);
            }
        }

        // A REAL look-at + atlas (mirrors SymbolGatherPlanDropMaskTests.Harness) — needed by any test that drives
        // a full Tick (projection/staging/collision/emit), not just GatherIntoMirror/CopyMirrorInto.
        private sealed class TickHarness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly double3 Origin;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public TickHarness()
            {
                _camGo = new GameObject("GatherMemo_TickCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(256, 256, 0);
                uCam.targetTexture = _rt;
                var lookAt = new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt, zoom: 12.0, heading: 0.0, tilt: 0.0), projection: P);
                Origin = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
                var glyphAtlas = new GlyphAtlas();
                glyphAtlas.Append(glyph);
                Atlas = new GlyphAtlasTexture();
                Atlas.Upload(glyphAtlas);
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                System = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                UnityEngine.Object.DestroyImmediate(_camGo);
                UnityEngine.Object.DestroyImmediate(_rt);
                UnityEngine.Object.DestroyImmediate(_baseMaterial);
            }
        }

        // ═══ T1: N successive same-version gathers must stay a memo HIT and byte-match a fresh gather ═══

        [Test]
        public void Memo_NTicksNoTileEvent_MirrorByteIdenticalToFreshGather()
        {
            var tile = new TileId { Z = 6, X = 10, Y = 10 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness(); // NIT7: ONE reference system, TWO alternating plan objects below
            var plan = new SymbolGatherPlan();
            var refPlanA = new SymbolGatherPlan();
            var refPlanB = new SymbolGatherPlan();
            try
            {
                const int frames = 4;
                for (int f = 0; f < frames; f++)
                {
                    BuildPlan(store, plan, version: 0); // SAME version every frame — no tile event
                    harness.Lps.GatherIntoMirror(plan);
                    Assert.AreEqual(1, harness.Lps.MirrorRebuildCount,
                        $"frame {f}: no tile event ever occurred — the mirror must stay memo-HIT after the first rebuild");

                    SymbolGatherPlan refPlan = (f % 2 == 0) ? refPlanA : refPlanB; // alternating identity ⇒ ref never memo-hits
                    BuildPlan(store, refPlan, version: f);
                    refHarness.Lps.GatherIntoMirror(refPlan);

                    var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                    var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                    Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                        $"frame {f}: a held mirror must stay byte-identical to a fresh gather over the same content");
                }
            }
            finally { plan.Dispose(); refPlanA.Dispose(); refPlanB.Dispose(); store.Clear(); }
        }

        // ═══ T2a: rebuilding the SAME plan object with DIFFERENT content at a FIXED WinnerCount and a bumped
        //          version must invalidate the memo — the coupled constraint (3.4): vary the winner SET (a
        //          different tile), never the record count, or the release-build count backstop rescues a broken
        //          version key and this row's RED-verify goes vacuously green. ═══

        [Test]
        public void Memo_VersionChange_Invalidates()
        {
            var tileA = new TileId { Z = 6, X = 20, Y = 20 };
            var tileB = new TileId { Z = 6, X = 21, Y = 20 };
            long keyA = Tk(tileA), keyB = Tk(tileB);
            var storeA = new SymbolTileStore(cacheCap: 8);
            var storeB = new SymbolTileStore(cacheCap: 8);
            SeedTile(storeA, tileA, PointSymbol(new double3(100, 0, 200), "a", 1, keyA, 0.1f));
            SeedTile(storeB, tileB, PointSymbol(new double3(300, 0, 400), "b", 2, keyB, 0.2f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            var refPlanA = new SymbolGatherPlan();
            var refPlanB = new SymbolGatherPlan();
            try
            {
                BuildPlan(storeA, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");
                Assert.AreEqual(1, plan.WinnerCount, "sanity: tile A alone is one winner");

                BuildPlan(storeA, refPlanA, version: 0);
                refHarness.Lps.GatherIntoMirror(refPlanA);
                var got1 = new SymbolBatch(); harness.Lps.CopyMirrorInto(got1);
                var want1 = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want1);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want1, got1), "frame 1 mirror must match tile A's content");

                // SAME plan object, a DIFFERENT store's content (tile A → tile B), WinnerCount fixed at 1, bumped version.
                BuildPlan(storeB, plan, version: 1);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(2, harness.Lps.MirrorRebuildCount, "a version change must trigger a real rebuild, not a memo hit");
                Assert.AreEqual(1, plan.WinnerCount, "coupled constraint: WinnerCount stays fixed at 1 across the swap");

                BuildPlan(storeB, refPlanB, version: 0);
                refHarness.Lps.GatherIntoMirror(refPlanB);
                var got2 = new SymbolBatch(); harness.Lps.CopyMirrorInto(got2);
                var want2 = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want2);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want2, got2),
                    "frame 2 mirror must reflect tile B's content, not a stale memo hit off tile A");
            }
            finally { plan.Dispose(); refPlanA.Dispose(); refPlanB.Dispose(); storeA.Clear(); storeB.Clear(); }
        }

        // ═══ T4: masks (Departing/CoverageFading) are per-frame inputs, legitimately varying at a FIXED version
        //         (1.2's exemption) — must be tracked on a HELD (memo-hit) mirror, not frozen from the first
        //         rebuild. Fixed WinnerCount throughout (else AssertMemoPlanMatchesMirror fires for the wrong
        //         reason — 3.4's coupled constraint). Split into two single-mask tests (Codex SHOULD-FIX 2, see
        //         below) so a cross-wire between the two masks can't hide behind a "both flipped together" test. ═══

        // Split into two independent single-mask flips (Codex SHOULD-FIX 2): the original single test flipped
        // Departing and CoverageFading TOGETHER, so an implementation that copied either source mask into BOTH
        // destinations (e.g. WritePerFrameMasks accidentally writing plan.Departing into both
        // _mirrorSymbolDeparting AND _mirrorSymbolCoverageFading) would still pass — both masks would read true either way.
        // Each test below flips exactly ONE mask and asserts the OTHER stayed at its unflipped value, so a
        // mask-to-mask cross-wire fails on the "unchanged" assertion even though the "changed" one still passes.

        [Test]
        public void Memo_DepartingFlip_TrackedWhilePoolsHeld_CoverageFadingUnchanged()
        {
            var tile = new TileId { Z = 6, X = 40, Y = 40 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            var refPlan = new SymbolGatherPlan();
            try
            {
                BuildPlan(store, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");

                // SAME version (no tile event) — ONLY Departing flips; CoverageFading stays Keep (untouched).
                BuildPlan(store, plan, version: 0, departingTileKey: key);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "a mask-only change at a fixed version must stay a memo HIT");

                BuildPlan(store, refPlan, version: 0, departingTileKey: key);
                refHarness.Lps.GatherIntoMirror(refPlan);

                var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                    "the per-frame masks must be tracked on a HELD mirror, not frozen from the first rebuild");
                Assert.IsTrue(got.SymbolDeparting[0], "Departing must reflect the flip even on a memo-hit frame");
                Assert.IsFalse(got.SymbolCoverageFading[0],
                    "CoverageFading must stay UNCHANGED — a mask cross-wire (e.g. Departing's source copied into both destinations) would wrongly flip this too");
            }
            finally { plan.Dispose(); refPlan.Dispose(); store.Clear(); }
        }

        [Test]
        public void Memo_CoverageFadingFlip_TrackedWhilePoolsHeld_DepartingUnchanged()
        {
            var tile = new TileId { Z = 6, X = 41, Y = 40 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            var refPlan = new SymbolGatherPlan();
            try
            {
                BuildPlan(store, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");

                // SAME version (no tile event) — ONLY CoverageFading flips; Departing stays false (untouched).
                BuildPlan(store, plan, version: 0, fadeTileKey: key);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "a mask-only change at a fixed version must stay a memo HIT");

                BuildPlan(store, refPlan, version: 0, fadeTileKey: key);
                refHarness.Lps.GatherIntoMirror(refPlan);

                var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                    "the per-frame masks must be tracked on a HELD mirror, not frozen from the first rebuild");
                Assert.IsTrue(got.SymbolCoverageFading[0], "CoverageFading must reflect the flip even on a memo-hit frame");
                Assert.IsFalse(got.SymbolDeparting[0],
                    "Departing must stay UNCHANGED — a mask cross-wire (e.g. CoverageFading's source copied into both destinations) would wrongly flip this too");
            }
            finally { plan.Dispose(); refPlan.Dispose(); store.Clear(); }
        }

        // ═══ T4b: Dropped is invisible through CopyMirrorInto (it hard-skips in GatherSymbolPoints, not the
        //          mirror-comparison surface) — assert it BEHAVIOURALLY, on a memo-HIT frame, via a real Tick +
        //          WorldMeshReadback comparison against a reference that never collected the dropped tile at all
        //          (mirrors SymbolGatherPlanDropMaskTests' pattern), with the masked side's Drop flip held at the
        //          SAME version (a memo hit) instead of a bumped one. ═══

        // Full vertex + opacity byte comparison of two world-slot meshes — mirrors
        // SymbolGatherPlanDropMaskTests.FirstMeshDifference. Returns the first difference, or null if byte-identical.
        private static string FirstMeshDifference(Mesh a, Mesh b)
        {
            WorldMeshReadback.Read(a, out WorldBillboardVertex[] va, out float[] oa);
            WorldMeshReadback.Read(b, out WorldBillboardVertex[] vb, out float[] ob);
            if (va.Length != vb.Length) return $"vertex count {va.Length} vs {vb.Length}";
            for (int i = 0; i < va.Length; i++)
                if (!va[i].Equals(vb[i])) return $"vertex[{i}] differs";
            if (oa.Length != ob.Length) return $"opacity count {oa.Length} vs {ob.Length}";
            for (int i = 0; i < oa.Length; i++)
                if (oa[i] != ob[i]) return $"opacity[{i}] {oa[i]} vs {ob[i]}";
            return null;
        }

        [Test]
        public void Memo_DropFlip_TrackedWhilePoolsHeld()
        {
            var keepTile = new TileId { Z = 12, X = 2500, Y = 1500 };
            var dropTile = new TileId { Z = 12, X = 2501, Y = 1500 };
            long keepKey = Tk(keepTile), dropKey = Tk(dropTile);
            var dropOffset = new double3(0, 0, 1600); // clears collision with KEEP — mirrors DropMaskTests' DropOffset

            using var hMasked = new TickHarness();
            using var hRef = new TickHarness();
            var storeMasked = new SymbolTileStore(cacheCap: 16);
            var storeRef = new SymbolTileStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileStore store in new[] { storeMasked, storeRef })
                {
                    SeedTile(store, keepTile, PointSymbol(hMasked.Origin, "keep", 1, keepKey, 0.1f));
                    SeedTile(store, dropTile, PointSymbol(hMasked.Origin + dropOffset, "drop", 2, dropKey, 0.2f));
                }

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                try
                {
                    // Frame 1: both tiles Keep on both sides — establishes a live fade for the drop tile too.
                    // R3: the collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) —
                    // duplicate (same plan+version, so the second Tick is a memo HIT, not a second rebuild) so
                    // this frame's ticks actually SHOW both tiles before frame 2 masks one of them off. Without
                    // this, frame 1 is a virgin system's first Tick and shows NOTHING — the drop tile's slot
                    // would never be built, so :510's "masked: the Dropped slot must be HIDDEN" would pass
                    // vacuously (never shown ⇒ trivially not visible), proving nothing about the Drop mask.
                    BuildPlan(storeMasked, planMasked, version: 0);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildPlan(storeRef, planRef, version: 0);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.AreEqual(1, hMasked.System.MirrorRebuildCount,
                        "sanity: frame 1 is a heavy rebuild — the duplicate Tick is a memo HIT (same plan+version), not a second rebuild");

                    // Frame 2: MASKED flags the drop tile Dropped at the SAME version (a memo-HIT frame — the point
                    // of this test); REFERENCE excludes the drop tile physically (a different plan/store shape,
                    // separate rebuild — its own memoization is irrelevant here).
                    BuildPlan(storeMasked, planMasked, version: 0, dropTileKey: dropKey);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    Assert.AreEqual(1, hMasked.System.MirrorRebuildCount,
                        "the Drop flip at a fixed version must be a memo HIT — this is what makes the assertions below meaningful");

                    var refBlockId = new List<int>();
                    var refLocalIndex = new List<int>();
                    var refIsDeparting = new List<byte>();
                    var refDecisions = new List<byte>();
                    var allBlockId = new List<int>();
                    var allLocalIndex = new List<int>();
                    var allIsDeparting = new List<byte>();
                    storeRef.CollectInto(allBlockId, allLocalIndex, allIsDeparting, quantizeMeters: 1.0, out _);
                    for (int i = 0; i < allBlockId.Count; i++)
                    {
                        if (storeRef.OrderedBlocks[allBlockId[i]].TileKey == dropKey) continue;
                        refBlockId.Add(allBlockId[i]); refLocalIndex.Add(allLocalIndex[i]);
                        refIsDeparting.Add(allIsDeparting[i]); refDecisions.Add(SymbolTileCoverageFilter.Keep);
                    }
                    planRef.Build(refBlockId, refLocalIndex, refIsDeparting, refDecisions, storeRef.OrderedBlocks, winnerSetVersion: 1);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);

                    Assert.AreEqual(hRef.System.LastQuadCount, hMasked.System.LastQuadCount,
                        "masked-Drop's emitted quad count (on a memo-HIT frame) must equal the reference's");
                    Assert.AreEqual(1, hRef.System.LastQuadCount, "sanity: only the KEEP point ever draws");

                    Assert.IsTrue(hMasked.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshMasked));
                    Assert.IsTrue(hRef.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshRef));
                    Assert.IsNull(FirstMeshDifference(keepMeshRef, keepMeshMasked),
                        "the surviving KEEP point's full vertex+opacity content must be byte-identical between a memo-hit masked Drop and the reference");

                    Assert.IsTrue(hMasked.System.IsWorldSlotVisible(keepKey, 0, SymbolKind.Text), "masked: KEEP must be VISIBLE");
                    Assert.IsFalse(hMasked.System.IsWorldSlotVisible(dropKey, 0, SymbolKind.Text),
                        "masked: the Dropped slot must be HIDDEN even though its mirror pools came from a memo hit");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }

        // ═══ T6: the memo-HIT path (three mask memcpys + a subtraction) allocates ZERO managed garbage — pairs
        //         with SymbolGatherParityTests.GatherIntoMirror_Warm_AllocatesNoGCMemory (the HEAVY-path guard,
        //         repaired for R1 by forcing a version bump before its measured call). ═══

        [Test]
        public void GatherIntoMirror_MemoHit_AllocatesNoGCMemory()
        {
            var tile = new TileId { Z = 6, X = 50, Y = 50 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                BuildPlan(store, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan); // heavy warm-up — first-touch native growth happens here
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: warm-up is a heavy rebuild");
                harness.Lps.GatherIntoMirror(plan); // extra warm-up memo hit (same version — no rebuild expected)
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the measured call below must be a memo hit");

                Assert.That(() => { harness.Lps.GatherIntoMirror(plan); }, Is.Not.AllocatingGCMemory(),
                    "a memo-hit GatherIntoMirror must allocate ZERO managed garbage — three NativeArray memcpys + a subtraction");
            }
            finally { plan.Dispose(); store.Clear(); }
        }
    }
}
