// Unity-side (holds SymbolTileBlock refs — Core stays engine-free). No longer engine-free itself: the
// reader cutover (symbols-async-reconcile stage 4.2) retypes TileSlice.Block/OrderedBlocks from
// System.IDisposable to the concrete Unity.Collections-backed SymbolTileBlock, so this file left the
// core-tests.csproj fast loop (still compiled + run by the Unity EditMode runner).

using System.Collections.Generic;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Stage 4b (symbols-async-reconcile): the IMMUTABLE main-thread snapshot the off-main
    /// <see cref="SymbolReconciler"/> reads. Captured on a SCHEDULE frame by
    /// <see cref="SymbolTileStore.CaptureSnapshot"/> (main thread), it holds each collected tile's baked
    /// native block + whether the tile is departing. The worker reads these <b>off-thread</b>; the store's pin
    /// guard (<see cref="SymbolTileStore.Pin"/>/<see cref="SymbolTileStore.ReleasePins"/>) keeps every
    /// referenced <see cref="TileSlice.Block"/> alive until the snapshot leaves service, so the off-thread reads
    /// are never a use-after-free (design §3.2).
    ///
    /// <para><b>Reused, alloc-light.</b> <see cref="Slices"/> is reused across captures — <see cref="Clear"/>
    /// resets the count and nulls the reused slices' refs (so a disposed block is never held past a capture),
    /// <see cref="Add"/> hands back a pooled <see cref="TileSlice"/>. Off the per-frame path (capture happens
    /// only on a tile-event frame), but kept low-alloc anyway per the design's GC caveat (§4).</para>
    /// </summary>
    internal sealed class SymbolSnapshot
    {
        /// <summary>The captured tiles — the first <see cref="Count"/> entries are live; ACTIVE tiles first
        /// (in <c>_active</c> order), then DEPARTING (in <c>_departing</c> order), IDENTICAL to the store's
        /// <c>CollectInto</c> scan order so the reconcile is byte-identical.</summary>
        public readonly List<TileSlice> Slices = new List<TileSlice>();

        /// <summary>Live slice count (<see cref="Slices"/> beyond this are pooled/idle).</summary>
        public int Count { get; private set; }

        /// <summary>#3a: reset to empty, nulling each live slice's refs (never hold a block past a capture) and
        /// keeping the pooled <see cref="TileSlice"/> objects for reuse.</summary>
        public void Clear()
        {
            for (int i = 0; i < Count; i++) Slices[i].Reset();
            Count = 0;
        }

        /// <summary>Append one tile (growing the pool if needed) and return it for the caller to fill.</summary>
        public TileSlice Add()
        {
            if (Count == Slices.Count) Slices.Add(new TileSlice());
            return Slices[Count++];
        }
    }

    /// <summary>One tile's immutable off-thread-readable symbol data — a pooled, reused carrier
    /// (<see cref="SymbolSnapshot.Add"/> fills it, <see cref="Reset"/> clears it). Fields mirror
    /// <c>SymbolTileStore.Entry</c>'s collect-relevant members plus the departing flag.</summary>
    internal sealed class TileSlice
    {
        /// <summary>The tile's baked native block (borrowed ref; the store owns disposal, pinned across the
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
            Block = null;
            IsDeparting = false;
        }
    }

    /// <summary>
    /// Stage 4b (symbols-async-reconcile): the off-main reconcile OUTPUT — the deduped winner set the main
    /// thread consumes (coverage-classify → <c>SymbolGatherPlan.Build</c>) once picked up. All lists are
    /// reused; the double-buffer swaps this whole object front/back so a completed worker fills the BACK
    /// result while the main thread reads the stable FRONT (design §2). Byte-identical (order + membership +
    /// the plan arrays) to the store's inline <c>CollectInto</c>.
    ///
    /// <para><b>Reader cutover (4.2):</b> winner identity is now purely <c>(BlockId[i], LocalIndex[i])</c> —
    /// there is no managed symbol list to hand back (<see cref="SymbolGatherPlan.Build"/> reads only the
    /// count). <c>Output</c> is gone: <see cref="MapRenderer.Unity.Text.Placement.SymbolGatherPlan.Build"/>
    /// is the sole production reader and it only ever needed <c>BlockId.Count</c>.</para>
    /// </summary>
    internal sealed class SymbolReconcileResult
    {
        /// <summary>Per-symbol block id — indexes <see cref="OrderedBlocks"/>.</summary>
        public readonly List<int> BlockId = new List<int>();
        /// <summary>Per-symbol raw symbol index within its tile's block (null-slot-safe).</summary>
        public readonly List<int> LocalIndex = new List<int>();
        /// <summary>Per-symbol departing flag (0 active, 1 departing) — Blocker 2's positional-count materialization.</summary>
        public readonly List<byte> IsDeparting = new List<byte>();
        /// <summary>One block per scanned tile, in blockId order (active tiles first, then departing).</summary>
        public readonly List<SymbolTileBlock> OrderedBlocks = new List<SymbolTileBlock>();
        /// <summary>Count of ACTIVE winners written first; every symbol at index ≥ this is departing.</summary>
        public int ActiveCount;

        /// <summary>#3b: clear ALL FOUR lists AND reset <see cref="ActiveCount"/> — the reconciler calls this
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
