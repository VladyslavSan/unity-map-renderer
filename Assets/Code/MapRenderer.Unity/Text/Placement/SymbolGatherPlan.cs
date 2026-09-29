// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Collections, so it keeps TOP-LEVEL usings and unqualified types.

using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>A cross-zoom fade handover found by the reconciler: the departing copy's fade identity
    /// <see cref="From"/> passes its opacity state to the matched active winner's identity <see cref="To"/>.</summary>
    internal struct FadeAlias
    {
        public long From;
        public long To;
    }

    /// <summary>
    /// The per-frame winner plan that <see cref="Text.SymbolSubsystem.CurrentBatch"/> produces for the native gather.
    /// One entry per collected winner, in render order: a <see cref="BlockId"/>/<see cref="LocalIndex"/> into a
    /// pre-baked <see cref="SymbolTileBlock"/>, plus a per-frame departing mask kept out of the immutable block.
    /// Non-local invariant: <see cref="Blocks"/> holds borrowed refs; the store owns block disposal.
    /// </summary>
    internal sealed class SymbolGatherPlan : VerifiedDisposable
    {
        internal NativeList<int>  BlockId;         // index into Blocks[]
        internal NativeList<int>  LocalIndex;      // raw symbol index within that block (null-slot-safe)
        internal NativeList<byte> Departing;       // per-symbol: the store's IsDeparting flag
        internal int WinnerCount;

        // The reconciler's cross-zoom fade handovers for this winner set (departing FadeId → active FadeId).
        internal NativeList<FadeAlias> FadeAliases;

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
            FadeAliases    = new NativeList<FadeAlias>(Allocator.Persistent);
        }

        /// <summary>Refills this plan in place from the collected winner arrays, without compaction. The arrays
        /// hold every winner (active and departing), and <see cref="WinnerCount"/> counts them all.
        /// <paramref name="orderedBlocks"/> is the store's block list that <paramref name="blockId"/> indexes.</summary>
        /// <param name="fadeAliases">The reconciler's fade handovers for this winner set; <c>null</c> reads as none.</param>
        /// <param name="winnerSetVersion">The caller's front-set version. Non-local invariant: at a fixed version the
        /// winner identity and blocks are unchanged; only the departing mask may change per frame.</param>
        internal void Build(List<int> blockId, List<int> localIndex,
            List<byte> isDeparting, IReadOnlyList<SymbolTileBlock> orderedBlocks,
            IReadOnlyList<FadeAlias> fadeAliases, int winnerSetVersion)
        {
            FadeAliases.Clear();
            if (fadeAliases != null)
                for (int a = 0; a < fadeAliases.Count; a++) FadeAliases.Add(fadeAliases[a]);

            int n = blockId.Count;
            WinnerCount = n;
            WinnerSetVersion = winnerSetVersion;
            BlockId.ResizeUninitialized(n);
            LocalIndex.ResizeUninitialized(n);
            Departing.ResizeUninitialized(n);

            // Snapshot the ordered blocks into Blocks[] (this frame's own copy — a later collect mutating the
            // store's list can't dangle it).
            BlockCount = orderedBlocks.Count;
            if (Blocks.Length < BlockCount) System.Array.Resize(ref Blocks, BlockCount);
            for (int b = 0; b < BlockCount; b++) Blocks[b] = orderedBlocks[b];

            for (int i = 0; i < n; i++)
            {
                BlockId[i] = blockId[i];
                LocalIndex[i] = localIndex[i];
                Departing[i] = isDeparting[i];
            }
        }

        protected override void DoDispose()
        {
            BlockId.Dispose();
            LocalIndex.Dispose();
            Departing.Dispose();
            FadeAliases.Dispose();
        }
    }
}
