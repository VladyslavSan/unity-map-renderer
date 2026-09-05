// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// <see cref="SymbolTileCoverageFilter.ClassifyActive"/> — the per-block tile-coverage classifier that WRITES
    /// a per-record Keep / Fade / Drop decision (a tile that WAS on screen fades out instead of popping) without
    /// compacting the record list. Real <see cref="WebMercatorProjection"/> + a diagonal viewProj scaled by
    /// <see cref="WebMercator.WorldExtent"/> so a z=0 tile (spans the WHOLE Mercator square by construction)
    /// projects to ~full-viewport NDC (coverage ~1.0, always kept) and a deep-zoom tile at the same origin
    /// projects to a vanishingly small NDC quad (coverage ~1e-12, always below <see cref="MinCoverage"/>) — no
    /// exact-area arithmetic needed, just a robust big/tiny contrast.
    ///
    /// <para>The retired compaction sibling <c>FilterActive(List&lt;…&gt;,…)</c> (over the pre-migration
    /// per-symbol managed carrier) — and its independent per-symbol coverage oracle — was deleted along with that
    /// carrier; these direct
    /// <c>ClassifyActive</c> assertions (over hand-built per-block tile keys, the shape the subsystem feeds) are
    /// now the guard for the classifier.</para>
    /// </summary>
    [TestFixture]
    public class SymbolTileCoverageFilterTests
    {
        private static readonly IProjection Projection = new WebMercatorProjection();
        private static readonly double2 Viewport = new double2(1000, 1000);
        private static readonly double3 SceneOrigin = new double3(0, 0, 0);
        private static readonly float3x3 Rebase = float3x3.identity;

        // Diagonal viewProj: clip.x = local.x / WorldExtent, clip.y = local.z / WorldExtent (north → screen
        // vertical), clip.w = 1 (always in front). local.y (altitude) is always 0 for a surface projection,
        // so its column is left zero.
        private static readonly float4x4 ViewProj = new float4x4(
            new float4((float)(1.0 / WebMercator.WorldExtent), 0, 0, 0),
            new float4(0, 0, 0, 0),
            new float4(0, (float)(1.0 / WebMercator.WorldExtent), 0, 0),
            new float4(0, 0, 0, 1));

        // z=0's single tile spans the WHOLE Mercator square (±WorldExtent on both axes, by construction of
        // MaxLatitude) → ~full-viewport coverage.
        private static readonly long BigTileKey = SymbolTileKey.Pack(new TileId { Z = 0, X = 0, Y = 0 });

        // z=19 tile at the same (lon=0, lat=0) origin: side length ≈ 2·WorldExtent / 2^19 ≈ 76m → NDC span
        // ≈ 3.8e-6 → coverage ≈ 1.4e-11. Vanishingly small regardless of threshold.
        private static readonly long TinyTileKey = SymbolTileKey.Pack(new TileId { Z = 19, X = 262144, Y = 262144 });

        private const double MinCoverage = 0.05;
        private const double GraceSeconds = 0.5;

        // Fresh, empty cross-frame state for a test that doesn't care about history (a first-touch tile).
        private static (HashSet<long> abovePrev, HashSet<long> aboveThisFrame, Dictionary<long, double> departingUntil,
            HashSet<long> fadingOut, Dictionary<long, byte> tileDecisions) FreshState()
            => (new HashSet<long>(), new HashSet<long>(), new Dictionary<long, double>(), new HashSet<long>(), new Dictionary<long, byte>());

        [Test]
        public void ClassifyActive_NoProjection_OrNonPositiveThreshold_IsNoOp_AllKeep()
        {
            // One block, one tile (Tiny), two records on it — fed as the per-block tile keys + per-record block id
            // the subsystem produces (block == tile).
            var blockTileKeys = new List<long> { TinyTileKey };
            var blockId = new List<int> { 0, 0 };
            var isDeparting = new List<byte> { 0, 0 };
            // Pre-seed cross-frame state — the no-op guard must leave it untouched (nothing to reconcile).
            var abovePrev = new HashSet<long> { TinyTileKey };
            var aboveThisFrame = new HashSet<long>();
            var departingUntil = new Dictionary<long, double> { [TinyTileKey] = 10.0 };
            var fadingOut = new HashSet<long>();
            var tileDecisions = new Dictionary<long, byte>();
            var decisions = new List<byte>();
            var blockDecision = new List<byte>();

            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, projection: null, SceneOrigin, ViewProj,
                Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0,
                GraceSeconds, tileDecisions, blockDecision, decisions, out int culledViaNullProjection);
            Assert.AreEqual(0, culledViaNullProjection);
            CollectionAssert.AreEqual(
                new[] { SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep }, decisions,
                "null projection ⇒ classify nothing, every record reads Keep");
            Assert.IsTrue(abovePrev.Contains(TinyTileKey), "no-op guard leaves cross-frame state alone");
            Assert.AreEqual(10.0, departingUntil[TinyTileKey]);

            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, minCoverage: 0.0, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0,
                GraceSeconds, tileDecisions, blockDecision, decisions, out int culledViaZeroThreshold);
            Assert.AreEqual(0, culledViaZeroThreshold);
            CollectionAssert.AreEqual(
                new[] { SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep }, decisions,
                "threshold 0 ⇒ classify nothing, every record reads Keep");
        }

        [Test]
        public void ClassifyActive_TwoBlocksShareOneTileKey_CulledCountsRecordsNotBlocks()
        {
            // Production bakes ONE block per (source, tile), so two sources over the same physical tile yield two
            // DISTINCT blocks that share a tile key. Two below-coverage blocks on the same tile, three records
            // across them. The tile classifies once (scratch collapse) but the Drop count is per RECORD (3),
            // never per block (2) or per tile (1).
            var blockTileKeys = new List<long> { TinyTileKey, TinyTileKey }; // two distinct blocks, one tile key
            var blockId = new List<int> { 0, 1, 0 };                          // three records across both blocks
            var isDeparting = new List<byte> { 0, 0, 0 };

            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, tileDecisions) = FreshState();
            var blockDecision = new List<byte>();
            var decisions = new List<byte>();
            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, Projection, SceneOrigin,
                ViewProj, Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut,
                now: 0.0, GraceSeconds, tileDecisions, blockDecision, decisions, out int culled);

            CollectionAssert.AreEqual(
                new[] { SymbolTileCoverageFilter.Drop, SymbolTileCoverageFilter.Drop, SymbolTileCoverageFilter.Drop },
                decisions, "every record on the below-coverage tile drops, whichever of the two blocks it rode");
            Assert.AreEqual(3, culled, "culled counts RECORDS (3), not distinct blocks (2) or tiles (1)");
            Assert.AreEqual(1, tileDecisions.Count, "the shared tile key is classified once — both blocks collapse in the scratch");
        }

        [Test]
        public void ClassifyActive_DepartingOnlyBlock_IsNotClassified_LeavesCrossFrameStateUntouched()
        {
            // A departing-only block (a tile that LEFT cover — the reconciler emits departing records into their
            // own blocks) must NEVER be classified: a departing tile touches no cross-frame state — otherwise a
            // freshly-departed tile (still in AbovePrev) gets a fade deadline stamped and re-enters as Fade where
            // it should Drop. Same-frame decisions are blind to this (departing records short-circuit to Keep), so
            // this asserts the CROSS-FRAME state directly. One active block (Big, above → Keep) + one SEPARATE
            // departing block (Tiny, below but in AbovePrev — would Fade+stamp if wrongly classified).
            var blockTileKeys = new List<long> { BigTileKey, TinyTileKey };
            var blockId = new List<int> { 0, 1 };
            var isDeparting = new List<byte> { 0, 1 };

            var abovePrev = new HashSet<long> { TinyTileKey }; // departing tile WAS above last frame
            var aboveThisFrame = new HashSet<long>();
            var departingUntil = new Dictionary<long, double>();
            var fadingOut = new HashSet<long>();
            var tileDecisions = new Dictionary<long, byte>();
            var blockDecision = new List<byte>();
            var decisions = new List<byte>();

            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, Projection, SceneOrigin,
                ViewProj, Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut,
                now: 5.0, GraceSeconds, tileDecisions, blockDecision, decisions, out int culled);

            Assert.AreEqual(SymbolTileCoverageFilter.Keep, decisions[0], "active Big tile is above → Keep");
            Assert.AreEqual(SymbolTileCoverageFilter.Keep, decisions[1], "departing record short-circuits to Keep");
            Assert.AreEqual(0, culled, "nothing dropped");
            Assert.IsTrue(tileDecisions.ContainsKey(BigTileKey), "sanity: the ACTIVE block WAS classified");
            // The fence: the departing-only tile is never classified, so it mutates NO cross-frame state.
            Assert.IsFalse(tileDecisions.ContainsKey(TinyTileKey), "departing-only tile must not be classified at all");
            Assert.IsFalse(departingUntil.ContainsKey(TinyTileKey), "departing-only tile must not get a fade deadline stamped");
            Assert.IsFalse(fadingOut.Contains(TinyTileKey), "departing-only tile must not be marked fading");
            Assert.IsFalse(aboveThisFrame.Contains(TinyTileKey), "departing-only tile must not enter the above set");
        }
    }
}
