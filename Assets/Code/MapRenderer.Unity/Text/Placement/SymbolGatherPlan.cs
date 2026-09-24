// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Collections, so it keeps TOP-LEVEL usings and unqualified types.

using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// The per-frame winner plan that <see cref="Text.SymbolSubsystem.CurrentBatch"/> produces for the native gather.
    /// One entry per collected winner, in render order: a <see cref="BlockId"/>/<see cref="LocalIndex"/> into a
    /// pre-baked <see cref="SymbolTileBlock"/>, plus three per-frame masks kept out of the immutable block.
    /// Non-obvious why: a Drop is a mask, not a compaction, so no parallel array is permuted in lockstep.
    /// Non-local invariant: <see cref="Blocks"/> holds borrowed refs; the store owns block disposal.
    /// </summary>
    internal sealed class SymbolGatherPlan : VerifiedDisposable
    {
        internal NativeList<int>  BlockId;         // index into Blocks[]
        internal NativeList<int>  LocalIndex;      // raw symbol index within that block (null-slot-safe)
        internal NativeList<byte> Departing;       // per-symbol: the store's IsDeparting flag
        internal NativeList<byte> CoverageFading;  // per-symbol: SymbolTileCoverageFilter classified this winner Fade
        // Per-symbol Drop decision (SymbolTileCoverageFilter.ClassifyActive). The winner stays resident and
        // counted; GatherSymbolPoints skips it as its first, unconditional check.
        internal NativeList<byte> Dropped;
        internal int WinnerCount;

        // How many winners this Build stamped Dropped, so SymbolPlacementSystem derives _mirrorNonDroppedCount
        // by subtraction instead of a per-frame walk.
        internal int DroppedCount;

        // Front-set version: the memo key of SymbolPlacementSystem.GatherIntoMirror. Non-obvious why: the store's
        // CollectGeneration moves at the tile event, but the front moves 1-4 frames later at the swap, so a
        // CollectGeneration key serves a stale mirror across the swap.
        internal int WinnerSetVersion;

        // This Build's snapshot of the store's ordered blocks, so a later collect cannot dangle this frame's
        // gather. It grows and never shrinks.
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

        /// <summary>Refills this plan in place from the collected winner arrays, without compaction. The arrays
        /// hold every winner (Keep, Fade, Drop, departing), and <see cref="WinnerCount"/> counts them all.
        /// <paramref name="decisions"/> holds <c>SymbolTileCoverageFilter.ClassifyActive</c>'s per-symbol decision;
        /// <paramref name="orderedBlocks"/> is the store's block list that <paramref name="blockId"/> indexes.</summary>
        /// <param name="winnerSetVersion">The caller's front-set version. Non-local invariant: at a fixed version the
        /// winner identity and blocks are unchanged; only the three per-symbol masks may change per frame.</param>
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
