using System;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The two guards every <see cref="ITileLayer"/> implementation applies when it takes ownership of its
    /// decoded buffer — <b>set once</b>, and <b>in lockstep with the feature list</b>. Non-obvious why: several
    /// layer types exist, and two copies of one invariant drift, so the guards live here once. Both throw
    /// before any state is written: a second adopt would leak the first buffer, and a mismatched feature
    /// column mis-buckets every ring.
    /// </summary>
    internal static class LayerGeometryAdoption
    {
        /// <param name="owner">How the layer names itself in a diagnostic (e.g. <c>MvtLayer 'roads'</c>).</param>
        /// <param name="alreadyAdopted">The caller's set-once flag, read before it is set.</param>
        /// <param name="geometry">The buffer being adopted. A <c>default</c> one is legal for a feature-less
        /// layer: the materializers return it, and its zero count matches an empty feature list.</param>
        /// <param name="featureCount">The layer's own feature count — the other half of the lockstep.</param>
        // `geometry` is BY VALUE, not `in`: TileGeometryBuffers is a non-readonly struct, so `in` forces a
        // defensive copy per read (conventions, "Pass large read-only structs by `in`").
        internal static void Validate(
            string owner, bool alreadyAdopted, TileGeometryBuffers geometry, int featureCount)
        {
            if (alreadyAdopted)
                throw new InvalidOperationException(
                    $"{owner} already owns its geometry. A layer's buffer is minted exactly once, " +
                    "inside the decode; adopting a second would orphan the first (the decoded tile's " +
                    "Dispose frees only what the layer currently holds).");

            if (geometry.FeatureCount != featureCount)
                throw new ArgumentException(
                    $"{owner}: the buffer's feature column has {geometry.FeatureCount} entries but " +
                    $"the layer has {featureCount} features. RingFeatureIdx indexes that column while " +
                    "consumers index their per-feature arrays by SelectedTileFeature.Ordinal, which comes " +
                    "from Features — a mismatch mis-buckets every ring, and once an ordinal exceeds the " +
                    "column length it is an index-out-of-range instead.",
                    "geometry");
        }
    }
}
