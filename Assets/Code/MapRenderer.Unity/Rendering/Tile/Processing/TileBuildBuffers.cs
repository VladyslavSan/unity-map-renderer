using System;
using System.Collections.Generic;
using MapRenderer.Unity.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Reusable buffers for one mesh build's worker path: feature selection
    /// (<see cref="TileMeshLayerProcessor.ProcessOnWorker"/>), fill's sort order, and the ring-visit order
    /// fill and fill-extrusion both build through <see cref="Meshing.StyledFillTileBuilder.BuildRingVisitOrder"/>.
    /// One build (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>) rents it from <see cref="TileBuildBuffersPool"/>,
    /// and no concurrent build shares it. Each accessor returns a cleared list, except <c>RankStart</c>, which is zero-filled.
    /// </summary>
    internal sealed class TileBuildBuffers
    {
        private readonly List<float>                _sortKeys         = new List<float>();
        private readonly List<int>                  _declaredOrder    = new List<int>();
        private readonly List<SelectedTileFeature>  _orderedFeatures  = new List<SelectedTileFeature>();
        private readonly List<int>                  _rankStart        = new List<int>();
        private readonly List<int>                  _rankCursor       = new List<int>();

        // DISTINCT list from _orderedFeatures — see Selection's doc for why aliasing the two would
        // corrupt OrderBySortKey (it reads a layer's selection while writing the reordered result).
        private readonly List<SelectedTileFeature>  _selectedFeatures = new List<SelectedTileFeature>();

        internal TileBuildBuffers() => SortKeyComparison = CompareBySortKey;

        /// <summary>The per-feature sort-key list for <c>OrderBySortKey</c>, cleared.</summary>
        internal List<float> SortKeys()
        {
            _sortKeys.Clear();
            return _sortKeys;
        }

        /// <summary>The declared-index list <c>OrderBySortKey</c> sorts, cleared.</summary>
        internal List<int> DeclaredOrder()
        {
            _declaredOrder.Clear();
            return _declaredOrder;
        }

        /// <summary>Orders declared indices by <see cref="SortKeys"/>, ties by declared index, so the sort is stable. A stored
        /// delegate for <c>List&lt;int&gt;.Sort</c>, not a per-call closure. A delegate, because this runtime's sort allocates
        /// when it is handed an <see cref="IComparer{T}"/>.</summary>
        internal Comparison<int> SortKeyComparison { get; }

        private int CompareBySortKey(int left, int right)
        {
            int byKey = _sortKeys[left].CompareTo(_sortKeys[right]);
            return byKey != 0 ? byKey : left.CompareTo(right);
        }

        /// <summary>The reordered-features list <c>OrderBySortKey</c> fills and returns to its caller, cleared.</summary>
        internal List<SelectedTileFeature> OrderedFeatures()
        {
            _orderedFeatures.Clear();
            return _orderedFeatures;
        }

        /// <summary>The per-layer feature-selection list <c>FeatureSelector.SelectFeatures</c> fills, cleared and sized
        /// for <paramref name="featureCount"/> (the layer's feature count bounds the selection). It is a distinct list
        /// from <see cref="OrderedFeatures"/>: <c>OrderBySortKey</c> reads the selection while it writes there.</summary>
        internal List<SelectedTileFeature> Selection(int featureCount)
        {
            _selectedFeatures.Clear();
            if (_selectedFeatures.Capacity < featureCount) _selectedFeatures.Capacity = featureCount; // no EnsureCapacity on this profile
            return _selectedFeatures;
        }

        /// <summary>The counting-sort bucket-start list
        /// <see cref="Meshing.StyledFillTileBuilder.BuildRingVisitOrder"/> shares between fill and
        /// fill-extrusion, zero-filled to <paramref name="count"/> (= rankCount + 1) — required, because the caller
        /// accumulates into it from zero; a stale value left by a prior renter would corrupt the count.</summary>
        internal List<int> RankStart(int count)
        {
            _rankStart.Clear();
            for (int i = 0; i < count; i++) _rankStart.Add(0);
            return _rankStart;
        }

        /// <summary>The cursor list <see cref="Meshing.StyledFillTileBuilder.BuildRingVisitOrder"/> copies
        /// <see cref="RankStart"/>'s prefix sum into before walking it, cleared.</summary>
        internal List<int> RankCursor()
        {
            _rankCursor.Clear();
            return _rankCursor;
        }
    }
}
