// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3/float2/
// float4x4 — this file lives in MapRenderer.Core.Text.Placement (see SymbolTileCoverage's header for the
// inline-qualification trap this avoids).

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Moves the per-tile screen-coverage pre-cull (<see cref="SymbolTileCoverage"/>) AHEAD of the SoA batch
    /// build — culled AFTER the A-3 cross-tile dedup (<see cref="Text.SymbolTileStore.CollectInto"/>),
    /// BEFORE the native gather stages the surviving records — so a low-coverage
    /// tile's records never enter the SoA build at all (the SoA-copy cost scales with what's on
    /// screen). NOT before/inside dedup: culling pre-dedup would change dedup WINNERS (a &lt;5%-coverage
    /// child tile culled first would let its &gt;5% parent win the finest-zoom-wins tiebreak and render a
    /// symbol that is hidden today) — see the epic doc for the full trap.
    ///
    /// <para><b>Fade, not pop.</b> A tile crossing BELOW threshold is not dropped outright — it is a 3-way
    /// classification (Keep / Fade / Drop) so a tile that was actually on screen gets to ease out instead of
    /// vanishing: <b>Keep</b> (≥ threshold) always survives; <b>Fade</b> (&lt; threshold, but was above last
    /// frame or is still within its fade-out grace window) survives too, marked so the placement gather eases
    /// it to invisible instead of popping it; only <b>Drop</b> (&lt; threshold and never on screen, or its
    /// grace has expired) is actually removed — nothing was visible to pop. The grace window and its "still
    /// alive" fade easing are the caller's job (<c>SymbolSubsystem</c> / <c>SymbolPlacementSystem</c>) —
    /// this classifier only decides Keep/Fade/Drop and stamps the crossing deadline.</para>
    /// </summary>
    public static class SymbolTileCoverageFilter
    {
        // D1 (Blocker 2): public so the Unity-side SymbolGatherPlan.Build can read a ClassifyActive decision
        // (decisions[i] == Drop / == Fade) without duplicating the encoding.
        public const byte Keep = 0, Fade = 1, Drop = 2;

        /// <summary>
        /// The per-tile screen-coverage classifier: runs the per-tile Keep/Fade/Drop classification
        /// (<see cref="ClassifyTile"/>) + cross-frame state and WRITES a per-record decision into
        /// <paramref name="decisions"/> (<see cref="Keep"/>/<see cref="Fade"/>/<see cref="Drop"/>, one per record)
        /// so a downstream native gather can MASK a Dropped record instead of physically moving/removing list
        /// elements. Runs AFTER the A-3 cross-tile dedup (<see cref="Text.SymbolTileStore.CollectInto"/>),
        /// BEFORE the SoA batch build, so a low-coverage tile's records are skipped rather than staged.
        ///
        /// <para>No-op (leaves cross-frame state untouched, clears <paramref name="coverageAboveThisFrame"/>/
        /// <paramref name="coverageFadingTilesOut"/>, and reads every record <see cref="Keep"/>) when
        /// <paramref name="projection"/> is null or <paramref name="minCoverage"/> is non-positive — mirrors
        /// <see cref="SymbolTileCoverage.IsCulled"/>'s own disable convention, so a mis-wired caller degrades to
        /// "cull nothing".</para>
        ///
        /// <para><b>Per-block, not per-symbol.</b> The decision is a pure function of the record's TILE, and every
        /// record produced from one baked block shares that block's single tile key (a block is one
        /// <c>(source, tile)</c> build; the baker stamps the block key from its symbols' common tile key). So this
        /// classifies each BLOCK once — <paramref name="blockTileKeys"/> holds one key per entry in the caller's
        /// <c>OrderedBlocks</c>, the same list the gather (<c>SymbolGatherPlan</c>) indexes by
        /// <paramref name="blockId"/> — then fans the block decision out to records through
        /// <paramref name="blockId"/>, at O(blocks) tile work + an O(records) int/byte fan-out. Blocks that share
        /// a physical tile key collapse in <paramref name="tileDecisionScratch"/>, so each cross-frame side effect
        /// still fires exactly once per tile — order-independent (set inserts + per-tile deadline stamps), so
        /// block order vs record order does not change the result.</para>
        ///
        /// <para>A departing record (<paramref name="isDeparting"/> set) is left at <see cref="Keep"/>, AND a
        /// block with no active record (a tile that left cover — the reconciler emits departing records into their
        /// OWN blocks) is never classified, so a departing tile touches NO cross-frame state (deadline stamp /
        /// above-set / fading-set) — an active-only scope. The record arrays are taken aligned and non-null: the
        /// same swapped-front-result contract the gather's block resolve (<c>OrderedBlocks[blockId]</c>) relies on
        /// one call later.</para>
        /// </summary>
        /// <param name="blockTileKeys">One tile key per ordered block (parallel to the caller's
        /// <c>OrderedBlocks</c>); <paramref name="blockId"/> indexes into this.</param>
        /// <param name="blockId">Per-record block index (parallel to <paramref name="isDeparting"/>) — selects the
        /// record's tile key from <paramref name="blockTileKeys"/>.</param>
        /// <param name="isDeparting">Per-record flag (<c>0</c> active / <c>1</c> departing). A departing record is
        /// left at <see cref="Keep"/> (see above); a <c>null</c> list reads as all-active.</param>
        /// <param name="coverageAbovePrev">Tile keys that were ≥ threshold as of the LAST call — the caller
        /// ping-pongs this against <paramref name="coverageAboveThisFrame"/> (ref-swap after each call; a
        /// mutate-in-place set would grow unbounded for a tile that goes above once then vanishes).</param>
        /// <param name="coverageAboveThisFrame">Cleared here, then filled with every Keep tile key this call —
        /// becomes the caller's next <paramref name="coverageAbovePrev"/>.</param>
        /// <param name="coverageDepartingUntil">TileKey → fade-out deadline (<paramref name="now"/> +
        /// <paramref name="graceSeconds"/>), stamped ONCE on the crossing frame (not renewed while still
        /// fading) and cleared when a tile crosses back above threshold. The caller purges expired entries
        /// after this call (not this classifier's job — it only reads/stamps).</param>
        /// <param name="coverageFadingTilesOut">Cleared here, then filled with every Fade tile key this
        /// call — handed to the per-frame gather (via the plan's per-record decisions) so it can flag each
        /// record's coverage-fading state (the placement gather eases those out instead of popping).</param>
        /// <param name="now">This call's wall-clock (seconds) — stamps/checks fade deadlines.</param>
        /// <param name="graceSeconds">How long a crossing tile keeps fading before it is finally dropped.</param>
        /// <param name="tileDecisionScratch">Reused per-tile-key decision cache (cleared here) so a tile shared by
        /// many records/blocks is classified — and its side effects (deadline stamp, above/fading-set membership)
        /// applied — ONCE per call, not once per record. The caller owns its lifetime (no per-frame GC once warm).</param>
        /// <param name="blockDecision">Caller's reused per-block scratch — cleared then filled with one decision
        /// per block (alloc-free once warm).</param>
        /// <param name="decisions">Cleared then filled with one entry per record — the caller's reused scratch.</param>
        public static void ClassifyActive(
            List<long> blockTileKeys, List<int> blockId, List<byte> isDeparting, IProjection projection,
            in double3 sceneOriginRender, in float4x4 viewProj, in double2 viewportLogicalPx,
            in float3x3 rebase, double minCoverage,
            HashSet<long> coverageAbovePrev, HashSet<long> coverageAboveThisFrame,
            Dictionary<long, double> coverageDepartingUntil, HashSet<long> coverageFadingTilesOut,
            double now, double graceSeconds,
            Dictionary<long, byte> tileDecisionScratch, List<byte> blockDecision,
            List<byte> decisions, out int culledCount)
        {
            int n = blockId.Count;
            decisions.Clear();
            if (decisions.Capacity < n) decisions.Capacity = n;

            if (projection == null || minCoverage <= 0.0)
            {
                coverageAboveThisFrame.Clear();
                coverageFadingTilesOut.Clear();
                for (int i = 0; i < n; i++) decisions.Add(Keep);
                culledCount = 0;
                return;
            }

            tileDecisionScratch.Clear();
            coverageAboveThisFrame.Clear();
            coverageFadingTilesOut.Clear();

            // Phase A — classify each block that carries an ACTIVE record, once. A departing-only block (a tile
            // that left cover — the reconciler emits departing records into their OWN blocks) is NEVER classified,
            // so a departing tile touches no cross-frame state (deadline stamp / above-set / fading-set), matching
            // the old per-record scope fence (its records short-circuit to Keep in Phase B anyway). Blocks sharing
            // a physical tile key collapse in tileDecisionScratch, so each active tile's side effect fires once.
            const byte needsClassify = 0xFF; // transient marker (never a valid Keep/Fade/Drop) — overwritten below
            int blockCount = blockTileKeys.Count;
            blockDecision.Clear();
            for (int b = 0; b < blockCount; b++) blockDecision.Add(Keep); // departing-only blocks stay Keep (unread)
            for (int i = 0; i < n; i++)
                if (!(isDeparting != null && i < isDeparting.Count && isDeparting[i] != 0))
                    blockDecision[blockId[i]] = needsClassify; // this block has an active record → classify it
            for (int b = 0; b < blockCount; b++)
                if (blockDecision[b] == needsClassify)
                    blockDecision[b] = ClassifyTile(blockTileKeys[b], projection, sceneOriginRender, viewProj,
                        viewportLogicalPx, rebase, minCoverage, coverageAbovePrev, coverageAboveThisFrame,
                        coverageDepartingUntil, coverageFadingTilesOut, now, graceSeconds, tileDecisionScratch);

            // Phase B — fan the per-block decision out to records over int/byte arrays (no managed symbol deref).
            int culled = 0;
            for (int i = 0; i < n; i++)
            {
                bool departing = isDeparting != null && i < isDeparting.Count && isDeparting[i] != 0;
                byte decision = departing ? Keep : blockDecision[blockId[i]];
                if (decision == Drop) culled++;
                decisions.Add(decision);
            }
            culledCount = culled;
        }

        // Classify (once per tile per call, cached in tileDecisionScratch) whether tileKey's on-screen coverage
        // this frame keeps it, fades it, or drops it — applying the corresponding cross-frame side effect
        // (above-set membership / deadline stamp) exactly once, regardless of how many symbols share the tile:
        //   ≥ threshold             → Keep: recorded as above this frame; any live fade deadline is cleared
        //                              (a tile that crossed back above stops fading — CrossBackAbove_ClearsDeadline).
        //   < threshold, was above  → Fade: a FRESH crossing stamps the deadline (now + grace) once; an ALREADY-
        //     last frame OR still                fading tile (deadline already live) keeps its original deadline —
        //     within its fade deadline           re-stamping every frame would make the grace window meaningless.
        //   < threshold, neither    → Drop: never visible (or its grace already expired) — nothing to pop.
        private static byte ClassifyTile(long tileKey, IProjection projection, in double3 sceneOriginRender,
            in float4x4 viewProj, in double2 viewportLogicalPx, in float3x3 rebase, double minCoverage,
            HashSet<long> coverageAbovePrev, HashSet<long> coverageAboveThisFrame,
            Dictionary<long, double> coverageDepartingUntil, HashSet<long> coverageFadingTilesOut,
            double now, double graceSeconds, Dictionary<long, byte> tileDecisionScratch)
        {
            if (tileDecisionScratch.TryGetValue(tileKey, out byte cached)) return cached;

            bool belowThreshold = TileIsCulled(tileKey, projection, sceneOriginRender, viewProj, viewportLogicalPx, rebase, minCoverage);
            byte decision;
            if (!belowThreshold)
            {
                coverageAboveThisFrame.Add(tileKey);
                coverageDepartingUntil.Remove(tileKey);
                decision = Keep;
            }
            else
            {
                bool liveDeadline = coverageDepartingUntil.TryGetValue(tileKey, out double deadline) && now < deadline;
                if (liveDeadline || coverageAbovePrev.Contains(tileKey))
                {
                    if (!liveDeadline) coverageDepartingUntil[tileKey] = now + graceSeconds; // fresh crossing — stamp once
                    coverageFadingTilesOut.Add(tileKey);
                    decision = Fade;
                }
                else
                {
                    coverageDepartingUntil.Remove(tileKey); // defensive — an expired-but-not-yet-purged stamp
                    decision = Drop;
                }
            }

            tileDecisionScratch[tileKey] = decision;
            return decision;
        }

        // Whether tileKey's on-screen coverage this frame is below minCoverage: project its 4 tile-local corners
        // (ring TL,TR,BR,BL) through the SAME path AnchorRender was built with (SymbolTileKey.Unpack
        // → TileId.ToLonLat → projection.Project — no MercatorBounds/flat-earth shortcut), then
        // SymbolTileCoverage.ScreenCoverage/IsCulled. Called at most once per tile per ClassifyActive call —
        // ClassifyTile's tileDecisionScratch is the cache, so this needs none of its own.
        private static bool TileIsCulled(long tileKey, IProjection projection, in double3 sceneOriginRender,
            in float4x4 viewProj, in double2 viewportLogicalPx, in float3x3 rebase, double minCoverage)
        {
            TileId tile = SymbolTileKey.Unpack(tileKey);
            double3 topLeft     = ProjectCorner(tile, 0.0, 0.0, projection);
            double3 topRight    = ProjectCorner(tile, 1.0, 0.0, projection);
            double3 bottomRight = ProjectCorner(tile, 1.0, 1.0, projection);
            double3 bottomLeft  = ProjectCorner(tile, 0.0, 1.0, projection);

            double coverage = SymbolTileCoverage.ScreenCoverage(
                topLeft, topRight, bottomRight, bottomLeft, sceneOriginRender, viewProj, viewportLogicalPx, rebase);
            return SymbolTileCoverage.IsCulled(coverage, minCoverage);
        }

        private static double3 ProjectCorner(in TileId tile, double px, double py, IProjection projection)
        {
            double2 lonLat = tile.ToLonLat(px, py, 1.0);
            return projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
        }
    }
}
