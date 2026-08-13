// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). No UnityEngine — the store + CameraPoseMath are pure Core/engine-free.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stage 3 headline tooth: the cross-tile dedup winner set is now a pure function of the tile set —
    /// INVARIANT across display zoom. The store keys on the fixed <see cref="CrossTileLabelKey.CanonicalGridMeters"/>,
    /// so the <c>quantizeMeters</c> argument only GATES dedup on/off; its magnitude no longer sets the grid.
    /// Sweeping the gate across a wide zoom range (its old per-frame <see cref="CameraPoseMath.MetersPerPixel"/>
    /// values) must leave the ordered winner list — and the plan arrays — element-for-element identical.
    ///
    /// <para>The single-gate-value case passes with or without the fix, so it would be a degenerate tooth; the
    /// SWEEP is what bites. RED-verified by reverting the store's four <c>DedupKey.For</c> grid inputs back to
    /// <c>quantizeMeters</c>: the winner set then varies across the sweep (a coarse gate merges the split pair,
    /// a fine gate splits it) → the cross-gate assertion fails.</para>
    /// </summary>
    [TestFixture]
    public class SymbolDedupZoomInvarianceTests
    {
        private static LabelInstance Point(double3 anchor, string text, int feature, TileId tile)
            => new LabelInstance
            {
                Placement = SymbolPlacement.Point,
                AnchorRender = anchor,
                MaterialIndex = 0,
                Text = text,
                FeatureIndex = feature,
                TileKey = LabelTileKey.Pack(tile),
            };

        private readonly struct Snapshot
        {
            public readonly List<LabelInstance> Output;
            public readonly List<int> BlockId;
            public readonly List<int> LocalIndex;
            public readonly List<byte> IsDeparting;
            public readonly int ActiveCount;
            public Snapshot(List<LabelInstance> output, List<int> blockId, List<int> localIndex,
                List<byte> isDeparting, int activeCount)
            {
                Output = output; BlockId = blockId; LocalIndex = localIndex;
                IsDeparting = isDeparting; ActiveCount = activeCount;
            }
        }

        private static Snapshot Collect(SymbolTileLabelStore store, double gate)
        {
            var output = new List<LabelInstance>();
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(output, blockId, localIndex, isDeparting, gate, out int activeCount);
            return new Snapshot(output, blockId, localIndex, isDeparting, activeCount);
        }

        // ── The winner set + order + plan arrays are IDENTICAL for every gate value across a wide zoom span. ──
        [Test]
        public void WinnerSet_InvariantAcrossDisplayZoom()
        {
            // Fixture: two same-text co-located labels across two same-z tiles (must MERGE — one 4 m cell), plus
            // two same-text labels 8 m apart (must SPLIT — distinct 4 m cells). Under the fixed grid the winner
            // set is {merged Co, splitA, splitB} = 3 for every gate. Under a per-zoom grid a coarse gate would
            // collapse the 8 m pair (and, at the coarsest, the Co pair with them), so the count would vary.
            double3 coAnchor = new double3(0, 0, 0);          // a 4 m cell centre
            double3 splitA = new double3(1000, 0, 0);         // cell 250
            double3 splitB = new double3(1008, 0, 0);         // cell 252 — 8 m from splitA, distinct under 4 m

            var t1 = new TileId { Z = 10, X = 500, Y = 400 };
            var t2 = new TileId { Z = 10, X = 501, Y = 400 }; // same band as t1 (finest-z tie → lowest TileKey)

            var co1 = Point(coAnchor, "Co", 1, t1);
            var sA = Point(splitA, "Split", 2, t1);
            var sB = Point(splitB, "Split", 3, t1);
            var co2 = Point(coAnchor, "Co", 4, t2); // the co-located twin in the OTHER tile

            var store = new SymbolTileLabelStore(cacheCap: 8);
            var k1 = new SymbolTileLabelStore.Key("src", t1);
            var k2 = new SymbolTileLabelStore.Key("src", t2);
            store.CompleteBuild(k1, store.BeginBuild(k1), new List<LabelInstance> { co1, sA, sB });
            store.CompleteBuild(k2, store.BeginBuild(k2), new List<LabelInstance> { co2 });

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
            Assert.AreEqual(3, baseline.Output.Count, "sanity: co-located pair merges, 8 m pair splits → 3 winners");
            Assert.AreEqual(3, baseline.ActiveCount, "…all active");
            Assert.IsTrue(baseline.Output.Contains(sA) && baseline.Output.Contains(sB), "the 8 m pair both survive");
            Assert.IsTrue(baseline.Output.Contains(co1) ^ baseline.Output.Contains(co2), "exactly one Co copy wins the merge");

            for (int g = 1; g < gates.Length; g++)
            {
                Snapshot s = Collect(store, gates[g]);
                Assert.AreEqual(baseline.Output.Count, s.Output.Count,
                    $"winner count differs at gate {gates[g]} — dedup is NOT zoom-invariant");
                Assert.AreEqual(baseline.ActiveCount, s.ActiveCount, $"active split differs at gate {gates[g]}");
                for (int i = 0; i < baseline.Output.Count; i++)
                {
                    Assert.AreSame(baseline.Output[i], s.Output[i], $"winner/order differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.BlockId[i], s.BlockId[i], $"blockId differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.LocalIndex[i], s.LocalIndex[i], $"localIndex differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.IsDeparting[i], s.IsDeparting[i], $"isDeparting differs at index {i}, gate {gates[g]}");
                }
            }
        }
    }
}
