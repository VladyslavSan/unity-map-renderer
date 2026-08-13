using System;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The two guards every <see cref="ITileLayer"/> implementation applies when it takes ownership of its
    /// decoded buffer — <b>set once</b>, and <b>in lockstep with the feature list</b>.
    ///
    /// <para><b>Why it is shared and not copied.</b> IR C1's fix stage made the lockstep a property of the
    /// TYPE rather than of one decoder's discipline, and recorded that the extraction should happen "before a
    /// second <see cref="ITileLayer"/> exists". It now does (<c>GeoJsonTileLayer</c>), and an invariant that
    /// three ordinal-indexed consumers depend on must have ONE statement: two copies can drift, and the
    /// weaker of the two is then the real contract.</para>
    ///
    /// <para>Both failures are loud and both happen BEFORE any state is written, so a rejected buffer is
    /// never half-adopted: a second adopt would orphan the first buffer (a native leak nothing can reach,
    /// since the tile's <c>Dispose</c> frees only what the layer currently holds), and a mismatched feature
    /// column mis-buckets every ring — silently while the ordinals stay in range, and as an
    /// index-out-of-range once they do not.</para>
    /// </summary>
    internal static class TileLayerGeometryAdoption
    {
        /// <param name="owner">How the layer names itself in a diagnostic (e.g. <c>MvtLayer 'roads'</c>).</param>
        /// <param name="alreadyAdopted">The caller's set-once flag, read before it is set.</param>
        /// <param name="geometry">The buffer being adopted. A <c>default</c> one is legal for a feature-less
        /// layer: the materializers return it, and its zero count matches an empty feature list.</param>
        /// <param name="featureCount">The layer's own feature count — the other half of the lockstep.</param>
        // `geometry` is passed BY VALUE, not `in`: TileGeometryBuffers is a non-readonly struct, and member
        // access through `in` on one of those forces a defensive copy per read (conventions, "Pass large
        // read-only structs by `in`" — the `in` ⟺ `readonly struct` gate).
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
