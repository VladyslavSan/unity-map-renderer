// Engine-free (like SymbolTileLabelStore, co-located here): references only System + MapRenderer.Core types
// (LabelInstance), NO `using UnityEngine`. Compiled by BOTH the Unity runner and Tools/core-tests (its
// <Compile Include> lives in Tools/core-tests/core-tests.csproj) so the off-main reconcile carriers get the
// fast headless tests. Do NOT add a UnityEngine reference. Native blocks are held as System.IDisposable — the
// same engine-free constraint as SymbolTileLabelStore.Entry.Block (see that type's header).

using System;
using System.Collections.Generic;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Stage 4b (labels-async-reconcile): the IMMUTABLE main-thread snapshot the off-main
    /// <see cref="SymbolLabelReconciler"/> reads. Captured on a SCHEDULE frame by
    /// <see cref="SymbolTileLabelStore.CaptureSnapshot"/> (main thread), it holds each collected tile's label
    /// list + its parallel interned text/icon ids + its baked native block (as <see cref="IDisposable"/>) +
    /// whether the tile is departing. The worker reads these <b>off-thread</b>; the store's pin guard
    /// (<see cref="SymbolTileLabelStore.Pin"/>/<see cref="SymbolTileLabelStore.ReleasePins"/>) keeps every
    /// referenced <see cref="TileSlice.Block"/> alive until the snapshot leaves service, so the off-thread reads
    /// are never a use-after-free (design §3.2).
    ///
    /// <para><b>Reused, alloc-light.</b> <see cref="Slices"/> is reused across captures — <see cref="Clear"/>
    /// resets the count and nulls the reused slices' refs (so a disposed block is never held past a capture),
    /// <see cref="Add"/> hands back a pooled <see cref="TileSlice"/>. Off the per-frame path (capture happens
    /// only on a tile-event frame), but kept low-alloc anyway per the design's GC caveat (§4).</para>
    /// </summary>
    internal sealed class SymbolLabelSnapshot
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

    /// <summary>One tile's immutable off-thread-readable label data — a pooled, reused carrier
    /// (<see cref="SymbolLabelSnapshot.Add"/> fills it, <see cref="Reset"/> clears it). Fields mirror
    /// <c>SymbolTileLabelStore.Entry</c>'s collect-relevant members plus the departing flag.</summary>
    internal sealed class TileSlice
    {
        /// <summary>The tile's committed labels (set-once, immutable after build — the worker only reads them).</summary>
        public List<LabelInstance> Labels;
        /// <summary>Interned text ids, index-aligned with <see cref="Labels"/> (the dedup key input).</summary>
        public int[] TextIds;
        /// <summary>Interned icon-image ids, index-aligned with <see cref="Labels"/>.</summary>
        public int[] IconImageIds;
        /// <summary>The tile's baked native block (borrowed ref; the store owns disposal, pinned across the
        /// snapshot's life). Null for a tile committed without a block (e.g. label-only test commits).</summary>
        public IDisposable Block;
        /// <summary>True for a departing (left-cover, fading-out) tile — emitted after the active split.</summary>
        public bool IsDeparting;

        public void Reset()
        {
            Labels = null;
            TextIds = null;
            IconImageIds = null;
            Block = null;
            IsDeparting = false;
        }
    }

    /// <summary>
    /// Stage 4b (labels-async-reconcile): the off-main reconcile OUTPUT — the deduped winner set the main
    /// thread consumes (coverage-classify → <c>SymbolGatherPlan.Build</c>) once picked up. All lists are
    /// reused; the double-buffer swaps this whole object front/back so a completed worker fills the BACK
    /// result while the main thread reads the stable FRONT (design §2). Byte-identical (order + membership +
    /// the plan arrays) to the store's inline <c>CollectInto</c>.
    /// </summary>
    internal sealed class SymbolLabelReconcileResult
    {
        /// <summary>Emitted winners, in final render order (active winners first, then departing).</summary>
        public readonly List<LabelInstance> Output = new List<LabelInstance>();
        /// <summary>Per-record block id — indexes <see cref="OrderedBlocks"/>.</summary>
        public readonly List<int> BlockId = new List<int>();
        /// <summary>Per-record raw label index within its tile's block (null-slot-safe).</summary>
        public readonly List<int> LocalIndex = new List<int>();
        /// <summary>Per-record departing flag (0 active, 1 departing) — Blocker 2's positional-count materialization.</summary>
        public readonly List<byte> IsDeparting = new List<byte>();
        /// <summary>One block per scanned tile, in blockId order (active tiles first, then departing).</summary>
        public readonly List<IDisposable> OrderedBlocks = new List<IDisposable>();
        /// <summary>Count of ACTIVE winners written first; every record at index ≥ this is departing.</summary>
        public int ActiveCount;

        /// <summary>#3b: clear ALL five lists AND reset <see cref="ActiveCount"/> — the reconciler calls this
        /// FIRST every run so a reused result never leaks a prior run's records (the SHRINK correctness core).</summary>
        public void Clear()
        {
            Output.Clear();
            BlockId.Clear();
            LocalIndex.Clear();
            IsDeparting.Clear();
            OrderedBlocks.Clear();
            ActiveCount = 0;
        }
    }
}
