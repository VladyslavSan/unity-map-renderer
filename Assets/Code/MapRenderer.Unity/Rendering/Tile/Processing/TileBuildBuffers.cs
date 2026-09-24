using System;
using System.Collections;
using System.Collections.Generic;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Reusable buffers for one mesh build's worker path: feature selection
    /// (<see cref="TileMeshLayerProcessor.ProcessOnWorker"/>) and the fill sort/visit order
    /// (<see cref="Meshing.StyledFillTileBuilder"/>'s <c>OrderBySortKey</c> and <c>BuildRingVisitOrder</c>). One build
    /// (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>) rents it from <see cref="TileBuildBuffersPool"/>, and no
    /// concurrent build shares it. Buffers are grow-only: a renter reads only the <c>[0, count)</c> prefix it wrote.
    /// </summary>
    internal sealed class TileBuildBuffers
    {
        private float[]                _sortKeys         = Array.Empty<float>();
        private int[]                  _declaredOrder    = Array.Empty<int>();
        private SelectedTileFeature[]  _orderedFeatures  = Array.Empty<SelectedTileFeature>();
        private int[]                  _rankStart        = Array.Empty<int>();
        private int[]                  _rankCursor       = Array.Empty<int>();

        // DISTINCT array from _orderedFeatures — see SelectionBuffer's doc for why aliasing the two would
        // corrupt OrderBySortKey (it reads a layer's selection while writing the reordered result).
        private SelectedTileFeature[]  _selectedFeatures = Array.Empty<SelectedTileFeature>();

        // Allocated once and re-fielded per call, so a steady-state build touches no managed heap. The type
        // names differ from the accessors below because CS0102 forbids a member and a nested type sharing one name.
        private readonly RankSortComparer _sortKeyComparer      = new();
        private readonly RankSortView     _orderedFeaturesView  = new();
        private readonly RankSortView     _selectedFeaturesView = new();

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

        /// <summary>The WRITABLE per-layer feature-selection scratch <c>FeatureSelector.SelectFeatures</c> fills,
        /// sized to at least <paramref name="count"/> (the layer's feature count bounds the selection). Hand the
        /// result back through <see cref="SelectionView"/>, never as this raw array. It is a distinct array from
        /// <see cref="OrderedFeaturesBuffer"/>: <c>OrderBySortKey</c> reads the selection while it writes there.
        /// </summary>
        internal SelectedTileFeature[] SelectionBuffer(int count) => Ensure(ref _selectedFeatures, count);

        /// <summary>The feature-selection result, exposed as a fixed-length <paramref name="count"/> view over
        /// the (possibly longer, grow-only) buffer <see cref="SelectionBuffer"/> filled — never the raw array
        /// itself, whose <c>Length</c> would over-report once the buffer has grown past this call's count.</summary>
        internal IReadOnlyList<SelectedTileFeature> SelectionView(int count)
        {
            SelectedTileFeature[] buffer = Ensure(ref _selectedFeatures, count); // no-op: already sized by SelectionBuffer
            _selectedFeaturesView.Array = buffer;
            _selectedFeaturesView.Count = count;
            return _selectedFeaturesView;
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

        // StyledFillTileBuilder reads only `.Count` and `[i]`. GetEnumerator's iterator would allocate, but
        // nothing on the pooled path calls it.
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
