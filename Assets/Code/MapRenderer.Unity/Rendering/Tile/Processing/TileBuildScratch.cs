using System;
using System.Collections;
using System.Collections.Generic;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Per-build reusable scratch buffers for the fill mesh-build worker path (<see cref="Meshing.StyledFillTileBuilder"/>'s
    /// <c>OrderBySortKey</c> and <c>BuildRingVisitOrder</c>) — rented from <see cref="TileBuildScratchPool"/> for
    /// the whole duration of ONE build (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>/
    /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>) and returned when it completes. A build
    /// reuses this one instance across every layer/feature it processes — sequential within a build — and never
    /// shares it with another build running concurrently; see the pool for the rent/return contract that makes
    /// that true.
    ///
    /// <para>Buffers are GROW-ONLY: each <c>EnsureXxx</c>/accessor grows its backing array to at least the
    /// requested count and hands back the same instance once it has grown far enough, so a tile whose feature/
    /// ring counts stay at or below a prior build's peak allocates nothing. A renter must only read the
    /// <c>[0, count)</c> prefix IT wrote this call — the pool hands back whatever a PRIOR renter left in the
    /// tail beyond that.</para>
    ///
    /// <para><b>Public</b> (not internal): <see cref="Meshing.StyledFillTileBuilder.WriteMeshData"/> is public
    /// API, so its trailing <c>scratch</c> parameter's type must be at least as accessible — CS0051.</para>
    /// </summary>
    public sealed class TileBuildScratch
    {
        private float[]                _sortKeys        = Array.Empty<float>();
        private int[]                  _declaredOrder   = Array.Empty<int>();
        private SelectedTileFeature[]  _orderedFeatures = Array.Empty<SelectedTileFeature>();
        private int[]                  _rankStart       = Array.Empty<int>();
        private int[]                  _rankCursor      = Array.Empty<int>();

        // Reused CLASS instances (not per-call closures/wrappers): the whole point of pooling is that a
        // steady-state build touches no managed heap, so the comparer and the view below are allocated once
        // (first Rent to grow past zero capacity) and re-fielded on every call, never re-`new`'d. Named
        // distinctly from the accessor methods below (RankSortComparer/RankSortView vs SortKeyComparer/
        // OrderedFeaturesView) — CS0102 forbids a member and a nested type sharing one name.
        private readonly RankSortComparer _sortKeyComparer     = new();
        private readonly RankSortView     _orderedFeaturesView = new();

        /// <summary>The per-feature sort-key scratch for <c>OrderBySortKey</c>, sized to at least <paramref name="count"/>.</summary>
        internal float[] SortKeys(int count) => Ensure(ref _sortKeys, count);

        /// <summary>The declared-index scratch <c>OrderBySortKey</c> sorts in place, sized to at least <paramref name="count"/>.</summary>
        internal int[] DeclaredOrder(int count) => Ensure(ref _declaredOrder, count);

        /// <summary>A reusable <see cref="IComparer{T}"/> over <paramref name="keys"/> — a stored field on this
        /// instance, not a per-call closure, so the pooled <c>OrderBySortKey</c> path allocates no display
        /// class/delegate the way <c>Array.Sort</c>'s lambda overload does.</summary>
        internal IComparer<int> SortKeyComparer(float[] keys)
        {
            _sortKeyComparer.Keys = keys;
            return _sortKeyComparer;
        }

        /// <summary>The WRITABLE reordered-features scratch <c>OrderBySortKey</c> fills, sized to at least
        /// <paramref name="count"/>. Pair with <see cref="OrderedFeaturesView"/> to hand the result back —
        /// never return this raw array to a caller (see that method).</summary>
        internal SelectedTileFeature[] OrderedFeaturesBuffer(int count) => Ensure(ref _orderedFeatures, count);

        /// <summary>The reordered-features result of <c>OrderBySortKey</c>, exposed as a fixed-length
        /// <paramref name="count"/> view over the (possibly longer, grow-only) buffer <see cref="OrderedFeaturesBuffer"/>
        /// filled — never the raw array itself, whose <c>Length</c> would over-report once the buffer has grown
        /// past this call's count.</summary>
        internal IReadOnlyList<SelectedTileFeature> OrderedFeaturesView(int count)
        {
            SelectedTileFeature[] buffer = Ensure(ref _orderedFeatures, count); // no-op: already sized by OrderedFeaturesBuffer
            _orderedFeaturesView.Array = buffer;
            _orderedFeaturesView.Count = count;
            return _orderedFeaturesView;
        }

        /// <summary>The counting-sort bucket-start array for <c>BuildRingVisitOrder</c>, sized <paramref name="count"/>
        /// (= rankCount + 1) and zero-cleared over that prefix — required, because the caller accumulates into
        /// it from zero; a stale value left by a prior renter would corrupt the count.</summary>
        internal int[] RankStart(int count)
        {
            int[] buffer = Ensure(ref _rankStart, count);
            Array.Clear(buffer, 0, count);
            return buffer;
        }

        /// <summary>The cursor array <c>BuildRingVisitOrder</c> copies <see cref="RankStart"/>'s prefix sum
        /// into before walking it — the pooled replacement for <c>(int[])rankStart.Clone()</c>.</summary>
        internal int[] RankCursor(int count) => Ensure(ref _rankCursor, count);

        private static T[] Ensure<T>(ref T[] buffer, int count)
        {
            if (buffer.Length < count) buffer = new T[count];
            return buffer;
        }

        private sealed class RankSortComparer : IComparer<int>
        {
            internal float[] Keys;

            public int Compare(int left, int right)
            {
                int byKey = Keys[left].CompareTo(Keys[right]);
                return byKey != 0 ? byKey : left.CompareTo(right); // stable: declared order breaks ties
            }
        }

        // Indexer-only in the production hot path (StyledFillTileBuilder reads `.Count` and `[i]`, never
        // enumerates) — GetEnumerator's compiler-generated iterator would allocate if ever called, but nothing
        // on the pooled path calls it, so it never fires there.
        private sealed class RankSortView : IReadOnlyList<SelectedTileFeature>
        {
            internal SelectedTileFeature[] Array;
            public int Count { get; internal set; }
            public SelectedTileFeature this[int index] => Array[index];

            public IEnumerator<SelectedTileFeature> GetEnumerator()
            {
                for (int i = 0; i < Count; i++) yield return Array[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
