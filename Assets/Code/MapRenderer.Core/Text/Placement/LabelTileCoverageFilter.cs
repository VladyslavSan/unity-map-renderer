// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3/float2/
// float4x4 — this file lives in MapRenderer.Core.Text.Placement (see LabelTileCoverage's header for the
// inline-qualification trap this avoids).

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Moves the per-tile screen-coverage pre-cull (<see cref="LabelTileCoverage"/>) AHEAD of the SoA batch
    /// build — culled AFTER the A-3 cross-tile dedup (<see cref="Text.SymbolTileLabelStore.CollectInto"/>),
    /// BEFORE the parity oracle's <c>Build</c> (test assembly) — so a low-coverage
    /// tile's labels never enter the SoA build at all (the ~13ms <c>SoA.Copy</c> cost scales with what's on
    /// screen). NOT before/inside dedup: culling pre-dedup would change dedup WINNERS (a &lt;5%-coverage
    /// child tile culled first would let its &gt;5% parent win the finest-zoom-wins tiebreak and render a
    /// label that is hidden today) — see the epic doc for the full trap.
    ///
    /// <para><b>Fade, not pop.</b> A tile crossing BELOW threshold is not dropped outright — it is a 3-way
    /// classification (Keep / Fade / Drop) so a tile that was actually on screen gets to ease out instead of
    /// vanishing: <b>Keep</b> (≥ threshold) always survives; <b>Fade</b> (&lt; threshold, but was above last
    /// frame or is still within its fade-out grace window) survives too, marked so the placement gather eases
    /// it to invisible instead of popping it; only <b>Drop</b> (&lt; threshold and never on screen, or its
    /// grace has expired) is actually removed — nothing was visible to pop. The grace window and its "still
    /// alive" fade easing are the caller's job (<c>SymbolLabelSubsystem</c> / <c>LabelPlacementSystem</c>) —
    /// this classifier only decides Keep/Fade/Drop and stamps the crossing deadline.</para>
    ///
    /// <para>Every dependency here is Core, so this compiles into <c>Tools/core-tests</c> for a ~0.1s
    /// RED-verifiable loop over the fiddly compaction (active-cull + departing-preserve + activeCount
    /// recount) — the reason this is a seam of its own rather than inlined into the Unity-side builder.</para>
    /// </summary>
    public static class LabelTileCoverageFilter
    {
        // D1 (Blocker 2): public so the Unity-side SymbolGatherPlan.Build can read a ClassifyActive decision
        // (decisions[i] == Drop / == Fade) without duplicating the encoding.
        public const byte Keep = 0, Fade = 1, Drop = 2;

        /// <summary>
        /// In-place, stable compaction of <paramref name="labels"/>' ACTIVE range <c>[0, activeCount)</c>:
        /// classifies every label's tile Keep / Fade / Drop (see the type doc) and drops only Drop, keeping
        /// Keep and Fade (including a <c>null</c> label — never classified, <c>Build</c>'s null-guard expects
        /// it to pass through) in original relative order, and returns the new active count. The DEPARTING
        /// range <c>[activeCount, labels.Count)</c> (a tile leaving cover, retained for a fade-out) is
        /// preserved untouched, just shifted down behind the surviving active labels.
        ///
        /// <para>No-op (returns <paramref name="activeCount"/>, <paramref name="culledCount"/> = 0, and
        /// clears <paramref name="coverageAboveThisFrame"/>/<paramref name="coverageFadingTilesOut"/>) when
        /// <paramref name="projection"/> is null or <paramref name="minCoverage"/> is non-positive — mirrors
        /// <see cref="LabelTileCoverage.IsCulled"/>'s own disable convention, so a mis-wired caller degrades
        /// to "cull nothing". Cross-frame state (<paramref name="coverageAbovePrev"/>/
        /// <paramref name="coverageDepartingUntil"/>) is left untouched by the no-op — nothing to reconcile.</para>
        /// </summary>
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
        /// call — handed to <c>SymbolLabelBatchBuilder.Build</c> so it can flag each record's
        /// <c>RecordCoverageFading</c>.</param>
        /// <param name="now">This call's wall-clock (seconds) — stamps/checks fade deadlines.</param>
        /// <param name="graceSeconds">How long a crossing tile keeps fading before it is finally dropped.</param>
        /// <param name="tileDecisionScratch">Reused per-<see cref="LabelInstance.TileKey"/> decision cache
        /// (cleared here) so a tile shared by many labels is classified — and its side effects (deadline
        /// stamp, above/fading-set membership) applied — ONCE per call, not once per label. The caller owns
        /// its lifetime (no per-frame GC once warm).</param>
        /// <param name="planBlockId">Phase-1 Stage-2 (symbol-label native gather): OPTIONAL parallel winner-plan
        /// arrays, one entry per <paramref name="labels"/> element in lockstep. When non-null, every index move
        /// this compaction makes to <paramref name="labels"/> — the active-range compaction, the departing-tail
        /// shift, AND the final trim — is applied IDENTICALLY to <paramref name="planBlockId"/> /
        /// <paramref name="planLocalIndex"/>, so a downstream native gather reads a plan still aligned 1:1 with
        /// the post-filter list. Null (the demo / coverage-test seam) skips the permutation entirely — the list
        /// is filtered exactly as before. Engine-free: plain <see cref="int"/> lists, no Unity.Collections.</param>
        /// <param name="planLocalIndex">See <paramref name="planBlockId"/> — permuted in lockstep with it.</param>
        public static int FilterActive(
            List<LabelInstance> labels, int activeCount, IProjection projection,
            in double3 sceneOriginRender, in float4x4 viewProj, in double2 viewportLogicalPx,
            in float3x3 rebase, double minCoverage,
            HashSet<long> coverageAbovePrev, HashSet<long> coverageAboveThisFrame,
            Dictionary<long, double> coverageDepartingUntil, HashSet<long> coverageFadingTilesOut,
            double now, double graceSeconds,
            Dictionary<long, byte> tileDecisionScratch, out int culledCount,
            List<int> planBlockId = null, List<int> planLocalIndex = null)
        {
            if (projection == null || minCoverage <= 0.0)
            {
                coverageAboveThisFrame.Clear();
                coverageFadingTilesOut.Clear();
                culledCount = 0;
                return activeCount;
            }

            bool permute = planBlockId != null && planLocalIndex != null;
            int active = math.min(activeCount, labels.Count);
            tileDecisionScratch.Clear();
            coverageAboveThisFrame.Clear();
            coverageFadingTilesOut.Clear();

            int write = 0, culled = 0;
            for (int i = 0; i < active; i++)
            {
                LabelInstance label = labels[i];
                // A null label was never a candidate (Build's null-guard skips it) — never classified, passes through.
                if (label != null && ClassifyTile(label.TileKey, projection, sceneOriginRender, viewProj,
                        viewportLogicalPx, rebase, minCoverage, coverageAbovePrev, coverageAboveThisFrame,
                        coverageDepartingUntil, coverageFadingTilesOut, now, graceSeconds, tileDecisionScratch) == Drop)
                {
                    culled++;
                    continue;
                }
                labels[write] = label;
                if (permute) { planBlockId[write] = planBlockId[i]; planLocalIndex[write] = planLocalIndex[i]; }
                write++;
            }

            int newActive = write;
            // Shift the untouched departing tail down behind the surviving active labels (in lockstep).
            for (int i = active; i < labels.Count; i++)
            {
                labels[write] = labels[i];
                if (permute) { planBlockId[write] = planBlockId[i]; planLocalIndex[write] = planLocalIndex[i]; }
                write++;
            }
            int trimFrom = write, oldCount = labels.Count;
            labels.RemoveRange(trimFrom, oldCount - trimFrom);
            if (permute)
            {
                planBlockId.RemoveRange(trimFrom, oldCount - trimFrom);
                planLocalIndex.RemoveRange(trimFrom, oldCount - trimFrom);
            }

            culledCount = culled;
            return newActive;
        }

        /// <summary>
        /// D1 (Blocker 2) classify-only counterpart of <see cref="FilterActive"/>: runs the IDENTICAL per-tile
        /// Keep/Fade/Drop classification (<see cref="ClassifyTile"/>) + cross-frame state, but instead of
        /// compacting <paramref name="labels"/> in place, WRITES a per-record decision into
        /// <paramref name="decisions"/> (resized to <c>labels.Count</c>; <see cref="Keep"/>/<see cref="Fade"/>/
        /// <see cref="Drop"/>) — so a downstream native gather can mask a Dropped record instead of the caller
        /// physically moving/removing list elements (retiring the lockstep block-id/local-index permute).
        ///
        /// <para>A record whose <paramref name="isDeparting"/> flag is set (or a <c>null</c> label) is NEVER
        /// classified — it is left at <see cref="Keep"/> unconditionally, with no side effect on the cross-frame
        /// state. This reconciles with <see cref="FilterActive"/>'s "iterate <c>[0, activeCount)</c> only" — a
        /// departing record is untouched by the coverage cull, a scope fence unchanged from today.</para>
        /// </summary>
        /// <param name="isDeparting">Per-record flag (parallel to <paramref name="labels"/>, one entry per label —
        /// <c>0</c> active / <c>1</c> departing; see <see cref="MapRenderer.Unity.Text.SymbolTileLabelStore"/>'s
        /// plan-aware <c>CollectInto</c> overload, the concrete producer). A <c>null</c> reads as active-eligible
        /// but a <c>null</c> label is never classified anyway (see above).</param>
        /// <param name="decisions">Cleared then filled with exactly <c>labels.Count</c> entries — the caller's
        /// reused scratch (alloc-free once warm).</param>
        public static void ClassifyActive(
            List<LabelInstance> labels, List<byte> isDeparting, IProjection projection,
            in double3 sceneOriginRender, in float4x4 viewProj, in double2 viewportLogicalPx,
            in float3x3 rebase, double minCoverage,
            HashSet<long> coverageAbovePrev, HashSet<long> coverageAboveThisFrame,
            Dictionary<long, double> coverageDepartingUntil, HashSet<long> coverageFadingTilesOut,
            double now, double graceSeconds,
            Dictionary<long, byte> tileDecisionScratch, List<byte> decisions, out int culledCount)
        {
            int n = labels.Count;
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

            int culled = 0;
            for (int i = 0; i < n; i++)
            {
                LabelInstance label = labels[i];
                bool departing = isDeparting != null && i < isDeparting.Count && isDeparting[i] != 0;
                byte decision = Keep;
                // A null label was never a candidate (never classified, passes through as Keep — mirrors
                // FilterActive's null-guard); a departing record is out of scope (active-only classification).
                if (!departing && label != null)
                {
                    decision = ClassifyTile(label.TileKey, projection, sceneOriginRender, viewProj,
                        viewportLogicalPx, rebase, minCoverage, coverageAbovePrev, coverageAboveThisFrame,
                        coverageDepartingUntil, coverageFadingTilesOut, now, graceSeconds, tileDecisionScratch);
                    if (decision == Drop) culled++;
                }
                decisions.Add(decision);
            }
            culledCount = culled;
        }

        // Classify (once per tile per call, cached in tileDecisionScratch) whether tileKey's on-screen coverage
        // this frame keeps it, fades it, or drops it — applying the corresponding cross-frame side effect
        // (above-set membership / deadline stamp) exactly once, regardless of how many labels share the tile:
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
        // (ring TL,TR,BR,BL) through the SAME path AnchorRender was built with (LabelTileKey.Unpack
        // → TileId.ToLonLat → projection.Project — no MercatorBounds/flat-earth shortcut), then
        // LabelTileCoverage.ScreenCoverage/IsCulled. Called at most once per tile per FilterActive call —
        // ClassifyTile's tileDecisionScratch is the cache, so this needs none of its own.
        private static bool TileIsCulled(long tileKey, IProjection projection, in double3 sceneOriginRender,
            in float4x4 viewProj, in double2 viewportLogicalPx, in float3x3 rebase, double minCoverage)
        {
            TileId tile = LabelTileKey.Unpack(tileKey);
            double3 topLeft     = ProjectCorner(tile, 0.0, 0.0, projection);
            double3 topRight    = ProjectCorner(tile, 1.0, 0.0, projection);
            double3 bottomRight = ProjectCorner(tile, 1.0, 1.0, projection);
            double3 bottomLeft  = ProjectCorner(tile, 0.0, 1.0, projection);

            double coverage = LabelTileCoverage.ScreenCoverage(
                topLeft, topRight, bottomRight, bottomLeft, sceneOriginRender, viewProj, viewportLogicalPx, rebase);
            return LabelTileCoverage.IsCulled(coverage, minCoverage);
        }

        private static double3 ProjectCorner(in TileId tile, double px, double py, IProjection projection)
        {
            double2 lonLat = tile.ToLonLat(px, py, 1.0);
            return projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
        }
    }
}
