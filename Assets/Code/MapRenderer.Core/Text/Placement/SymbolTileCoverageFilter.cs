// TOP-LEVEL `using Unity.Mathematics;` + unqualified types (see SymbolScreenProjection's header for the
// inline-qualification trap this avoids).

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Runs the per-tile screen-coverage pre-cull (<see cref="SymbolTileCoverage"/>) after the cross-tile dedup
    /// (<see cref="Text.SymbolTileStore.CollectInto"/>) and before the SoA batch build, so a low-coverage tile's
    /// records never enter it. Non-obvious why: culling before dedup would change dedup winners. A tile below
    /// threshold is Fade (eased out) while it was above last frame or is inside its grace window, else Drop;
    /// the caller owns the grace window and easing, and this only classifies and stamps the deadline.
    /// </summary>
    public static class SymbolTileCoverageFilter
    {
        // Public so the Unity-side SymbolGatherPlan.Build can read a ClassifyActive decision
        // (decisions[i] == Drop / == Fade) without duplicating the encoding.
        public const byte Keep = 0, Fade = 1, Drop = 2;

        /// <summary>
        /// Writes one Keep/Fade/Drop decision per record into <paramref name="decisions"/>, so the native gather
        /// masks Dropped records instead of removing them. Each BLOCK is classified once (a block is one tile) and
        /// fanned out through <paramref name="blockId"/>; blocks sharing a tile collapse in
        /// <paramref name="tileDecisions"/>, so each cross-frame side effect fires once per tile. A departing record
        /// stays Keep and a departing-only block is never classified. With a null <paramref name="projection"/> or
        /// non-positive <paramref name="minCoverage"/> every record is Keep and cross-frame state is untouched.
        /// Non-local invariant: the record arrays are aligned and non-null, as the gather relies on next.
        /// </summary>
        /// <param name="blockTileKeys">One tile key per ordered block (parallel to the caller's
        /// <c>OrderedBlocks</c>); <paramref name="blockId"/> indexes into this.</param>
        /// <param name="blockId">Per-record block index (parallel to <paramref name="isDeparting"/>) — selects the
        /// record's tile key from <paramref name="blockTileKeys"/>.</param>
        /// <param name="isDeparting">Per-record flag (<c>0</c> active / <c>1</c> departing). A departing record is
        /// left at <see cref="Keep"/> (see above); a <c>null</c> list reads as all-active.</param>
        /// <param name="coverageAbovePrev">Tile keys ≥ threshold at the LAST call; the caller swaps it with
        /// <paramref name="coverageAboveThisFrame"/>, since one in-place set would grow unbounded.</param>
        /// <param name="coverageAboveThisFrame">Cleared here, then filled with every Keep tile key this call —
        /// becomes the caller's next <paramref name="coverageAbovePrev"/>.</param>
        /// <param name="coverageDepartingUntil">TileKey → fade-out deadline (<paramref name="now"/> + grace), stamped
        /// once on the crossing frame, cleared when back above threshold; the caller purges expired entries.</param>
        /// <param name="coverageFadingTilesOut">Cleared here, then filled with every Fade tile key this call,
        /// so the placement gather eases those records out instead of popping them.</param>
        /// <param name="now">This call's wall-clock (seconds) — stamps/checks fade deadlines.</param>
        /// <param name="graceSeconds">How long a crossing tile keeps fading before it is finally dropped.</param>
        /// <param name="tileDecisions">Caller-owned per-tile decision cache (cleared here), so a shared tile is
        /// classified and its side effects applied once per call.</param>
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
            Dictionary<long, byte> tileDecisions, List<byte> blockDecision,
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

            tileDecisions.Clear();
            coverageAboveThisFrame.Clear();
            coverageFadingTilesOut.Clear();

            // Phase A: classify each block with an ACTIVE record once. A departing-only block is never classified,
            // so a departing tile touches no cross-frame state; its records become Keep in Phase B.
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
                        coverageDepartingUntil, coverageFadingTilesOut, now, graceSeconds, tileDecisions);

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

        // Keep (≥ threshold): recorded above; any fade deadline is cleared. Fade (< threshold, above last frame
        // or inside its deadline): a fresh crossing stamps the deadline once, never re-stamped. Else Drop.
        private static byte ClassifyTile(long tileKey, IProjection projection, in double3 sceneOriginRender,
            in float4x4 viewProj, in double2 viewportLogicalPx, in float3x3 rebase, double minCoverage,
            HashSet<long> coverageAbovePrev, HashSet<long> coverageAboveThisFrame,
            Dictionary<long, double> coverageDepartingUntil, HashSet<long> coverageFadingTilesOut,
            double now, double graceSeconds, Dictionary<long, byte> tileDecisions)
        {
            if (tileDecisions.TryGetValue(tileKey, out byte cached)) return cached;

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

            tileDecisions[tileKey] = decision;
            return decision;
        }

        // Whether the tile's coverage is below minCoverage: its corners (TL,TR,BR,BL) project through the same
        // path AnchorRender was built with (TileId.ToLonLat → projection.Project), with no flat-earth shortcut.
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
