// Unity EditMode only — the reader cutover (symbols-async-reconcile stage 4.2) makes CaptureSnapshot/CollectInto
// read the Unity.Collections-backed SymbolTileBlock directly, so this file left the core-tests.csproj fast
// loop (still compiled + run by the Unity EditMode runner).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolTileBuffer

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stage 3 headline tooth: the cross-tile dedup winner set is now a pure function of the tile set —
    /// INVARIANT across display zoom. The store keys on the fixed <see cref="CrossTileSymbolKey.CanonicalGridMeters"/>,
    /// so the <c>quantizeMeters</c> argument only GATES dedup on/off; its magnitude no longer sets the grid.
    /// Sweeping the gate across a wide zoom range (its old per-frame <see cref="CameraPoseMath.MetersPerPixel"/>
    /// values) must leave the ordered winner list — and the plan arrays — element-for-element identical.
    ///
    /// <para>The single-gate-value case passes with or without the fix, so it would be a degenerate tooth; the
    /// SWEEP is what bites. RED-verified by reverting the store's four <c>DedupKey.For</c> grid inputs back to
    /// <c>quantizeMeters</c>: the winner set then varies across the sweep (a coarse gate merges the split pair,
    /// a fine gate splits it) → the cross-gate assertion fails.</para>
    ///
    /// <para><b>Reader cutover (4.2).</b> Winner identity is <c>(BlockId, LocalIndex)</c> — there is no more
    /// managed symbol list to compare by reference. Comparing the SAME two arrays (blockId/localIndex/isDeparting)
    /// across two DIFFERENT gate values is still a genuine, non-vacuous invariance check: it is not a restatement
    /// of anything a single run computed, it is proof that TWO INDEPENDENT runs (different gate) produced the
    /// SAME winner identities.</para>
    /// </summary>
    [TestFixture]
    public class SymbolDedupZoomInvarianceTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, string text, int feature, TileId tile) =>
            TestSymbolTileBuffer.AddPoint(buffer, anchor, quads: null, boundsMin: float2.zero, boundsMax: float2.zero,
                text: text, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));

        private readonly struct Snapshot
        {
            public readonly List<int> BlockId;
            public readonly List<int> LocalIndex;
            public readonly List<byte> IsDeparting;
            public readonly int ActiveCount;
            public Snapshot(List<int> blockId, List<int> localIndex, List<byte> isDeparting, int activeCount)
            {
                BlockId = blockId; LocalIndex = localIndex;
                IsDeparting = isDeparting; ActiveCount = activeCount;
            }
        }

        private static Snapshot Collect(SymbolTileStore store, double gate)
        {
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, gate, out int activeCount);
            return new Snapshot(blockId, localIndex, isDeparting, activeCount);
        }

        // ── The winner set + order + plan arrays are IDENTICAL for every gate value across a wide zoom span. ──
        [Test]
        public void WinnerSet_InvariantAcrossDisplayZoom()
        {
            // Fixture: two same-text co-located symbols across two same-z tiles (must MERGE — one 4 m cell), plus
            // two same-text symbols 8 m apart (must SPLIT — distinct 4 m cells). Under the fixed grid the winner
            // set is {merged Co, splitA, splitB} = 3 for every gate. Under a per-zoom grid a coarse gate would
            // collapse the 8 m pair (and, at the coarsest, the Co pair with them), so the count would vary.
            double3 coAnchor = new double3(0, 0, 0);          // a 4 m cell centre
            double3 splitA = new double3(1000, 0, 0);         // cell 250
            double3 splitB = new double3(1008, 0, 0);         // cell 252 — 8 m from splitA, distinct under 4 m

            var t1 = new TileId { Z = 10, X = 500, Y = 400 };
            var t2 = new TileId { Z = 10, X = 501, Y = 400 }; // same band as t1 (finest-z tie → lowest TileKey)

            var store = new SymbolTileStore(cacheCap: 8);
            var k1 = new SymbolTileStore.Key("src", t1);
            var k2 = new SymbolTileStore.Key("src", t2);
            var t1Buffer = new SymbolTileBuffer();
            AddPoint(t1Buffer, coAnchor, "Co", 1, t1);
            AddPoint(t1Buffer, splitA, "Split", 2, t1);
            AddPoint(t1Buffer, splitB, "Split", 3, t1);
            var t2Buffer = new SymbolTileBuffer();
            AddPoint(t2Buffer, coAnchor, "Co", 4, t2); // the co-located twin in the OTHER tile
            SymbolTileBlock block1 = SymbolTileBlockBaker.Bake(
                t1Buffer, slotCount: 1, double3.zero);
            SymbolTileBlock block2 = SymbolTileBlockBaker.Bake(
                t2Buffer, slotCount: 1, double3.zero);
            store.CompleteBuild(k1, store.BeginBuild(k1), block1);
            store.CompleteBuild(k2, store.BeginBuild(k2), block2);

            // The sweep: gate 1.0 plus the old per-frame MetersPerPixel at z5 / z8 / z14 — a >2^9 grid span.
            double[] gates =
            {
                1.0,
                CameraPoseMath.MetersPerPixel(5.0),
                CameraPoseMath.MetersPerPixel(8.0),
                CameraPoseMath.MetersPerPixel(14.0),
            };

            Snapshot baseline = Collect(store, gates[0]);

            // Precondition: the fixture actually exercises BOTH a merge and a split (not a degenerate all-merge/
            // all-distinct set) — 3 winners = {one Co, splitA, splitB}, all active, none departing.
            Assert.AreEqual(3, baseline.BlockId.Count, "sanity: co-located pair merges, 8 m pair splits → 3 winners");
            Assert.AreEqual(3, baseline.ActiveCount, "…all active");
            // Exactly one of the two Co copies wins: (block1, localIndex 0) XOR (block2, localIndex 0).
            bool baselineHasCo1 = false, baselineHasCo2 = false;
            for (int i = 0; i < baseline.BlockId.Count; i++)
            {
                if (baseline.BlockId[i] == 0 && baseline.LocalIndex[i] == 0) baselineHasCo1 = true;
                if (baseline.BlockId[i] == 1 && baseline.LocalIndex[i] == 0) baselineHasCo2 = true;
            }
            Assert.IsTrue(baselineHasCo1 ^ baselineHasCo2, "exactly one Co copy wins the merge");
            // splitA (block0, local1) and splitB (block0, local2) both present — not merged.
            bool hasSplitA = false, hasSplitB = false;
            for (int i = 0; i < baseline.BlockId.Count; i++)
            {
                if (baseline.BlockId[i] == 0 && baseline.LocalIndex[i] == 1) hasSplitA = true;
                if (baseline.BlockId[i] == 0 && baseline.LocalIndex[i] == 2) hasSplitB = true;
            }
            Assert.IsTrue(hasSplitA && hasSplitB, "the 8 m pair both survive");

            for (int g = 1; g < gates.Length; g++)
            {
                Snapshot s = Collect(store, gates[g]);
                Assert.AreEqual(baseline.BlockId.Count, s.BlockId.Count,
                    $"winner count differs at gate {gates[g]} — dedup is NOT zoom-invariant");
                Assert.AreEqual(baseline.ActiveCount, s.ActiveCount, $"active split differs at gate {gates[g]}");
                for (int i = 0; i < baseline.BlockId.Count; i++)
                {
                    Assert.AreEqual(baseline.BlockId[i], s.BlockId[i], $"blockId differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.LocalIndex[i], s.LocalIndex[i], $"localIndex differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.IsDeparting[i], s.IsDeparting[i], $"isDeparting differs at index {i}, gate {gates[g]}");
                }
            }
            store.Clear();
        }
    }
}
