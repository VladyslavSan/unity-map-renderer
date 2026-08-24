// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Collections — TOP-LEVEL usings + unqualified types. Unity-side (holds SymbolTileBlock refs
// + native lists — Core stays engine-free).

using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Symbol-symbol perf Phase 1 / Stage 2 (design §5 B): the per-frame WINNER PLAN a native gather consumes —
    /// what <see cref="Text.SymbolSubsystem.CurrentBatch"/> produces instead of a fully-built
    /// <see cref="SymbolBatch"/>. One entry per collected winner, in final render order: a
    /// <see cref="BlockId"/>/<see cref="LocalIndex"/> pointing at the winner's pre-baked
    /// <see cref="SymbolTileBlock"/> symbol, plus the three per-frame overrides
    /// (<see cref="Departing"/>/<see cref="CoverageFading"/>/<see cref="Dropped"/>) that are fenced OUT of the
    /// immutable block and applied here at plan-fill time. <see cref="Blocks"/> resolves <see cref="BlockId"/>
    /// to the actual block.
    ///
    /// <para><b>D1 — masking, not filtering.</b> <see cref="Dropped"/> (D1) is the tile-coverage cull's Drop
    /// decision, materialized as a per-symbol MASK rather than a physical compaction: EVERY collected winner
    /// stays resident in the plan (and the native mirror <see cref="SymbolPlacementSystem"/> builds from it) —
    /// <see cref="WinnerCount"/> counts them all — and a Dropped symbol is hard-skipped downstream
    /// (<see cref="SymbolPlacementSystem.GatherSymbolPoints"/>). This retires the old lockstep block-id/local-index
    /// permute (<c>SymbolTileCoverageFilter.FilterActive</c>'s compaction moved elements + kept the plan arrays in
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
        internal NativeList<int>  LocalIndex;      // raw symbol index within that block (null-slot-safe)
        internal NativeList<byte> Departing;       // per-symbol: the store's IsDeparting flag (Blocker 2)
        internal NativeList<byte> CoverageFading;  // per-symbol: SymbolTileCoverageFilter classified this winner Fade
        // D1: per-symbol Drop decision (SymbolTileCoverageFilter.ClassifyActive) — the winner STAYS RESIDENT
        // (WinnerCount counts it) instead of being compacted out; GatherIntoMirror stamps it onto the native
        // mirror (_mirrorSymbolDropped) and GatherSymbolPoints hard-skips it as its first, unconditional check.
        internal NativeList<byte> Dropped;
        internal int WinnerCount;

        // R1: how many of the WinnerCount symbols this Build stamped Dropped — counted here, in the loop that
        // already branches on the decision, so SymbolPlacementSystem derives _mirrorNonDroppedCount by subtraction
        // instead of re-walking every symbol each frame.
        internal int DroppedCount;

        // R1: the FRONT-SET version this plan was built from — the memo key SymbolPlacementSystem.GatherIntoMirror
        // holds its pools on. Bumped by SymbolSubsystem on every front-content change (reconcile swap /
        // SetStyle / Dispose), NOT on a store mutation: the store's CollectGeneration moves at the tile event, the
        // FRONT moves 1-4 frames later at the swap, so a CollectGeneration key would serve a stale mirror across
        // the swap.
        internal int WinnerSetVersion;

        // Resolved once per Build from the store's ordered-blocks list — the plan's own snapshot, so a later
        // collect mutating the store's list can't dangle this frame's gather. Grows geometrically, never shrinks.
        internal SymbolTileBlock[] Blocks = System.Array.Empty<SymbolTileBlock>();
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
        /// happens upstream any more, so <paramref name="blockId"/>/<paramref name="localIndex"/> hold EVERY
        /// winner (Keep + Fade + Drop + departing); <see cref="WinnerCount"/> counts all of them (Drops stay
        /// resident, masked downstream). Winner identity is <c>(BlockId, LocalIndex)</c> — the reader cutover
        /// (4.2) dropped the parallel managed symbol list <c>CollectInto</c> used to fill alongside them (nothing
        /// downstream of this method ever dereferenced it). <paramref name="isDeparting"/> is the store's
        /// per-symbol <c>IsDeparting</c> flag (Blocker 2 — replaces the old <c>i &gt;= activeCount</c> derivation);
        /// <paramref name="decisions"/> is <c>SymbolTileCoverageFilter.ClassifyActive</c>'s per-symbol Keep/Fade/Drop
        /// decision; <paramref name="orderedBlocks"/> is the store's block list <paramref name="blockId"/> indexes.</summary>
        /// <param name="winnerSetVersion">R1: the caller's front-set version, stamped onto <see cref="WinnerSetVersion"/>
        /// — every caller must state one (no default). At a fixed <see cref="WinnerSetVersion"/>, <see cref="BlockId"/>
        /// / <see cref="LocalIndex"/> / <see cref="Blocks"/> / <see cref="WinnerCount"/> must be unchanged. The three
        /// per-symbol masks (<see cref="Departing"/> / <see cref="CoverageFading"/> / <see cref="Dropped"/>) are
        /// explicitly EXEMPT — they are the per-frame inputs <c>SymbolPlacementSystem.WritePerFrameMasks</c> rewrites
        /// on every tick, held mirror or not. (In production <see cref="Departing"/> happens to be set-derived too —
        /// it comes from <c>_frontResult.IsDeparting</c> — but the memo does not rely on that.)</param>
        internal void Build(List<int> blockId, List<int> localIndex,
            List<byte> isDeparting, List<byte> decisions, IReadOnlyList<SymbolTileBlock> orderedBlocks,
            int winnerSetVersion)
        {
            int n = blockId.Count;
            WinnerCount = n;
            WinnerSetVersion = winnerSetVersion;
            BlockId.ResizeUninitialized(n);
            LocalIndex.ResizeUninitialized(n);
            Departing.ResizeUninitialized(n);
            CoverageFading.ResizeUninitialized(n);
            Dropped.ResizeUninitialized(n);

            // Snapshot the ordered blocks into Blocks[] (this frame's own copy — a later collect mutating the
            // store's list can't dangle it).
            BlockCount = orderedBlocks.Count;
            if (Blocks.Length < BlockCount) System.Array.Resize(ref Blocks, BlockCount);
            for (int b = 0; b < BlockCount; b++) Blocks[b] = orderedBlocks[b];

            int droppedCount = 0;
            for (int i = 0; i < n; i++)
            {
                BlockId[i] = blockId[i];
                LocalIndex[i] = localIndex[i];
                Departing[i] = isDeparting[i];
                byte decision = decisions[i];
                CoverageFading[i] = (byte)(decision == SymbolTileCoverageFilter.Fade ? 1 : 0);
                bool dropped = decision == SymbolTileCoverageFilter.Drop;
                Dropped[i] = (byte)(dropped ? 1 : 0);
                if (dropped) droppedCount++;
            }
            DroppedCount = droppedCount;
        }

        protected override void DoDispose()
        {
            BlockId.Dispose();
            LocalIndex.Dispose();
            Departing.Dispose();
            CoverageFading.Dispose();
            Dropped.Dispose();
        }
    }
}
