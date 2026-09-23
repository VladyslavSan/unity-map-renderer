// Unity-side (holds SymbolTileBlock refs — Core stays engine-free). Not engine-free itself:
// TileSlice.Block/OrderedBlocks are the concrete Unity.Collections-backed SymbolTileBlock, so this file is
// outside the core-tests.csproj fast loop (still compiled + run by the Unity EditMode runner).

using System;
using System.Collections.Generic;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// The IMMUTABLE main-thread snapshot the off-main <see cref="SymbolReconciler"/> reads. Captured on a
    /// SCHEDULE frame by <see cref="SymbolTileStore.CaptureSnapshot"/> (main thread), it holds each
    /// collected tile's baked native block + whether the tile is departing. Non-local invariant: each
    /// slice's own <see cref="SharedDisposable{T}"/> reference keeps every referenced
    /// <see cref="TileSlice.Block"/> alive until the snapshot leaves service, so the worker's off-thread
    /// reads are never a use-after-free (`docs/labels-async-reconcile-design.md`). Reused, alloc-light:
    /// <see cref="Slices"/> is reused across captures — <see cref="Clear"/> releases and resets the count,
    /// <see cref="Add"/> fills a pooled <see cref="TileSlice"/> in place.
    /// </summary>
    internal sealed class SymbolSnapshot
    {
        /// <summary>The captured tiles — the first <see cref="Count"/> entries are live; ACTIVE tiles first
        /// (in <c>_active</c> order), then DEPARTING (in <c>_departing</c> order), IDENTICAL to the store's
        /// <c>CollectInto</c> scan order so the reconcile is byte-identical.</summary>
        public readonly List<TileSlice> Slices = new List<TileSlice>();

        /// <summary>Live slice count (<see cref="Slices"/> beyond this are pooled/idle).</summary>
        public int Count { get; private set; }

        /// <summary>Reset to empty: DROP each live slice's block reference (the sole release site — the last one
        /// out disposes the block), then reset the slices, keeping the pooled <see cref="TileSlice"/> objects.
        /// Idempotent: a second call sees <see cref="Count"/> 0 and releases nothing.</summary>
        public void Clear()
        {
            for (int i = 0; i < Count; i++) { Slices[i].Pin.Release(); Slices[i].Reset(); }
            Count = 0;
        }

        /// <summary>Append one tile, TAKING a reference on its block for this snapshot's service life
        /// (<see cref="Clear"/> drops it). The sole acquire site — see <see cref="TileSlice.Pin"/>.</summary>
        public void Add(SharedDisposable<IDisposable> pin, bool isDeparting)
        {
            if (Count == Slices.Count) Slices.Add(new TileSlice());
            TileSlice slice = Slices[Count++];
            pin.Acquire();
            slice.Pin = pin;
            slice.Block = (SymbolTileBlock)pin.Value;
            slice.IsDeparting = isDeparting;
        }
    }

    /// <summary>One tile's immutable off-thread-readable symbol data — a pooled, reused carrier
    /// (<see cref="SymbolSnapshot.Add"/> fills it, <see cref="Reset"/> clears it). Fields mirror
    /// <c>SymbolTileStore.Entry</c>'s collect-relevant members plus the departing flag.</summary>
    internal sealed class TileSlice
    {
        /// <summary>The reference that KEEPS <see cref="Block"/> alive for this snapshot's whole service life.
        /// Taken by <see cref="SymbolSnapshot.Add"/>, dropped by <see cref="SymbolSnapshot.Clear"/> — the only
        /// two sites that may touch it. <see cref="Block"/> is the borrowed value this reference guarantees.</summary>
        public SharedDisposable<IDisposable> Pin;
        /// <summary>The tile's baked native block (borrowed ref; kept alive by <see cref="Pin"/> across the
        /// snapshot's life). The reconciler reads every per-symbol column straight off this — raw-order
        /// <see cref="SymbolTileBlock.Kinds"/>/
        /// <see cref="SymbolTileBlock.PairRoles"/>/<see cref="SymbolTileBlock.RepAnchor"/>/
        /// <see cref="SymbolTileBlock.MaterialIndexes"/>/<see cref="SymbolTileBlock.TextIds"/>/
        /// <see cref="SymbolTileBlock.IconImageIds"/> — instead of a parallel managed symbol list.</summary>
        public SymbolTileBlock Block;
        /// <summary>True for a departing (left-cover, fading-out) tile — emitted after the active split.</summary>
        public bool IsDeparting;

        public void Reset()
        {
            Pin = null;
            Block = null;
            IsDeparting = false;
        }
    }

    /// <summary>
    /// The off-main reconcile OUTPUT — the deduped winner set the main thread consumes (coverage-classify
    /// → <c>SymbolGatherPlan.Build</c>) once picked up. All lists are reused; the double-buffer swaps this
    /// whole object front/back so a completed worker fills the BACK result while the main thread reads the
    /// stable FRONT (`docs/labels-async-reconcile-design.md`). Byte-identical (order + membership + the
    /// plan arrays) to the store's inline <c>CollectInto</c>. Winner identity is purely
    /// <c>(BlockId[i], LocalIndex[i])</c> — there is no managed symbol list to hand back.
    /// </summary>
    internal sealed class SymbolReconcileResult
    {
        /// <summary>Per-symbol block id — indexes <see cref="OrderedBlocks"/>.</summary>
        public readonly List<int> BlockId = new List<int>();
        /// <summary>Per-symbol raw symbol index within its tile's block (null-slot-safe).</summary>
        public readonly List<int> LocalIndex = new List<int>();
        /// <summary>Per-symbol departing flag (0 active, 1 departing), materialized positionally.</summary>
        public readonly List<byte> IsDeparting = new List<byte>();
        /// <summary>One block per scanned tile, in blockId order (active tiles first, then departing).</summary>
        public readonly List<SymbolTileBlock> OrderedBlocks = new List<SymbolTileBlock>();
        /// <summary>Count of ACTIVE winners written first; every symbol at index ≥ this is departing.</summary>
        public int ActiveCount;

        /// <summary>Clears ALL FOUR lists AND resets <see cref="ActiveCount"/> — the reconciler calls this
        /// FIRST every run so a reused result never leaks a prior run's symbols (the SHRINK correctness core).</summary>
        public void Clear()
        {
            BlockId.Clear();
            LocalIndex.Clear();
            IsDeparting.Clear();
            OrderedBlocks.Clear();
            ActiveCount = 0;
        }
    }
}
