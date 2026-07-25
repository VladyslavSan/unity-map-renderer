// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Collections — TOP-LEVEL usings + unqualified types. Unity-side (holds SymbolTileLabelBlock refs
// + native lists — Core stays engine-free).

using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 2 (design §5 B): the per-frame WINNER PLAN a native gather consumes —
    /// what <see cref="Text.SymbolLabelSubsystem.CurrentBatch"/> produces instead of a fully-built
    /// <see cref="SymbolLabelBatch"/>. One entry per collected winner, in final render order: a
    /// <see cref="BlockId"/>/<see cref="LocalIndex"/> pointing at the winner's pre-baked
    /// <see cref="SymbolTileLabelBlock"/> record, plus the three per-frame overrides
    /// (<see cref="Departing"/>/<see cref="CoverageFading"/>/<see cref="Dropped"/>) that are fenced OUT of the
    /// immutable block and applied here at plan-fill time. <see cref="Blocks"/> resolves <see cref="BlockId"/>
    /// to the actual block.
    ///
    /// <para><b>D1 — masking, not filtering.</b> <see cref="Dropped"/> (D1) is the tile-coverage cull's Drop
    /// decision, materialized as a per-record MASK rather than a physical compaction: EVERY collected winner
    /// stays resident in the plan (and the native mirror <see cref="LabelPlacementSystem"/> builds from it) —
    /// <see cref="WinnerCount"/> counts them all — and a Dropped record is hard-skipped downstream
    /// (<see cref="LabelPlacementSystem.GatherSymbolPoints"/>). This retires the old lockstep block-id/local-index
    /// permute (<c>LabelTileCoverageFilter.FilterActive</c>'s compaction moved elements + kept the plan arrays in
    /// sync by permuting them in lockstep); a masking classify (<c>ClassifyActive</c>) moves nothing.</para>
    ///
    /// <para><b>Reuse / lifetime.</b> Subsystem-owned, reused every frame (<see cref="Build"/> clears + refills
    /// the native lists in place — alloc-free once warm). The lists are <see cref="Allocator.Persistent"/>,
    /// disposed once via <see cref="VerifiedDisposable"/>. <see cref="Blocks"/> holds BORROWED refs (the store
    /// owns block disposal) — this plan never disposes a block.</para>
    /// </summary>
    internal sealed class SymbolGatherPlan : VerifiedDisposable
    {
        internal NativeList<int>  BlockId;         // index into Blocks[]
        internal NativeList<int>  LocalIndex;      // raw record index within that block (null-slot-safe)
        internal NativeList<byte> Departing;       // per-record: the store's IsDeparting flag (Blocker 2)
        internal NativeList<byte> CoverageFading;  // per-record: LabelTileCoverageFilter classified this winner Fade
        // D1: per-record Drop decision (LabelTileCoverageFilter.ClassifyActive) — the winner STAYS RESIDENT
        // (WinnerCount counts it) instead of being compacted out; GatherIntoMirror stamps it onto the native
        // mirror (_mRecordDropped) and GatherSymbolPoints hard-skips it as its first, unconditional check.
        internal NativeList<byte> Dropped;
        internal int WinnerCount;

        // R1: how many of the WinnerCount records this Build stamped Dropped — counted here, in the loop that
        // already branches on the decision, so LabelPlacementSystem derives _mNonDroppedCount by subtraction
        // instead of re-walking every record each frame.
        internal int DroppedCount;

        // R1: the FRONT-SET version this plan was built from — the memo key LabelPlacementSystem.GatherIntoMirror
        // holds its pools on. Bumped by SymbolLabelSubsystem on every front-content change (reconcile swap /
        // SetStyle / Dispose), NOT on a store mutation: the store's CollectGeneration moves at the tile event, the
        // FRONT moves 1-4 frames later at the swap, so a CollectGeneration key would serve a stale mirror across
        // the swap.
        internal int WinnerSetVersion;

        // Resolved once per Build from the store's ordered-blocks list — the plan's own snapshot, so a later
        // collect mutating the store's list can't dangle this frame's gather. Grows geometrically, never shrinks.
        internal SymbolTileLabelBlock[] Blocks = System.Array.Empty<SymbolTileLabelBlock>();
        internal int BlockCount;

        internal SymbolGatherPlan()
        {
            BlockId        = new NativeList<int>(Allocator.Persistent);
            LocalIndex     = new NativeList<int>(Allocator.Persistent);
            Departing      = new NativeList<byte>(Allocator.Persistent);
            CoverageFading = new NativeList<byte>(Allocator.Persistent);
            Dropped        = new NativeList<byte>(Allocator.Persistent);
        }

        /// <summary>Refill this plan in place from the collected winner arrays — D1: NO compaction/filtering
        /// happens upstream any more, so <paramref name="collected"/> holds EVERY winner (Keep + Fade + Drop +
        /// departing); <see cref="WinnerCount"/> counts all of them (Drops stay resident, masked downstream).
        /// <paramref name="blockId"/> / <paramref name="localIndex"/> are the parallel lists <c>CollectInto</c>
        /// filled (aligned 1:1 with <paramref name="collected"/>, never permuted — nothing moves under D1's
        /// masking classify); <paramref name="isDeparting"/> is the store's per-record <c>IsDeparting</c> flag
        /// (Blocker 2 — replaces the old <c>i &gt;= activeCount</c> derivation); <paramref name="decisions"/> is
        /// <c>LabelTileCoverageFilter.ClassifyActive</c>'s per-record Keep/Fade/Drop decision; <paramref name="orderedBlocks"/>
        /// is the store's block list <paramref name="blockId"/> indexes.</summary>
        /// <param name="winnerSetVersion">R1: the caller's front-set version, stamped onto <see cref="WinnerSetVersion"/>
        /// — every caller must state one (no default). At a fixed <see cref="WinnerSetVersion"/>, <see cref="BlockId"/>
        /// / <see cref="LocalIndex"/> / <see cref="Blocks"/> / <see cref="WinnerCount"/> must be unchanged. The three
        /// per-record masks (<see cref="Departing"/> / <see cref="CoverageFading"/> / <see cref="Dropped"/>) are
        /// explicitly EXEMPT — they are the per-frame inputs <c>LabelPlacementSystem.WritePerFrameMasks</c> rewrites
        /// on every tick, held mirror or not. (In production <see cref="Departing"/> happens to be set-derived too —
        /// it comes from <c>_frontResult.IsDeparting</c> — but the memo does not rely on that.)</param>
        internal void Build(List<int> blockId, List<int> localIndex, List<LabelInstance> collected,
            List<byte> isDeparting, List<byte> decisions, IReadOnlyList<System.IDisposable> orderedBlocks,
            int winnerSetVersion)
        {
            int n = collected.Count;
            WinnerCount = n;
            WinnerSetVersion = winnerSetVersion;
            BlockId.ResizeUninitialized(n);
            LocalIndex.ResizeUninitialized(n);
            Departing.ResizeUninitialized(n);
            CoverageFading.ResizeUninitialized(n);
            Dropped.ResizeUninitialized(n);

            // Snapshot the ordered blocks into Blocks[] (cast from the engine-free store's IDisposable view).
            BlockCount = orderedBlocks.Count;
            if (Blocks.Length < BlockCount) System.Array.Resize(ref Blocks, BlockCount);
            for (int b = 0; b < BlockCount; b++) Blocks[b] = (SymbolTileLabelBlock)orderedBlocks[b];

            int droppedCount = 0;
            for (int i = 0; i < n; i++)
            {
                BlockId[i] = blockId[i];
                LocalIndex[i] = localIndex[i];
                Departing[i] = isDeparting[i];
                byte decision = decisions[i];
                CoverageFading[i] = (byte)(decision == LabelTileCoverageFilter.Fade ? 1 : 0);
                bool dropped = decision == LabelTileCoverageFilter.Drop;
                Dropped[i] = (byte)(dropped ? 1 : 0);
                if (dropped) droppedCount++;
            }
            DroppedCount = droppedCount;
        }

        protected override void DoDispose()
        {
            if (BlockId.IsCreated) BlockId.Dispose();
            if (LocalIndex.IsCreated) LocalIndex.Dispose();
            if (Departing.IsCreated) Departing.Dispose();
            if (CoverageFading.IsCreated) CoverageFading.Dispose();
            if (Dropped.IsCreated) Dropped.Dispose();
        }
    }
}
