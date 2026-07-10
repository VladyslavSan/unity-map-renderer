using MapRenderer.Core.Mvt;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The layer→source resolution seam: maps a parsed <see cref="StyleLayer"/> onto features in a
    /// decoded <see cref="MvtTile"/> via the layer's <c>source-layer</c> string. This is the plumbing
    /// that connects the style model to the data pipeline; per-layer rendering stages (S13 fill, S14
    /// line, …) extend behavior off this seam rather than re-implementing the lookup.
    ///
    /// Engine-free and tolerant: a layer with no <c>source-layer</c> (background, raster) resolves to
    /// null without throwing.
    /// </summary>
    public static class SourceLayerResolver
    {
        /// <summary>
        /// Resolves the MVT layer a style layer selects features from. Returns null (no throw) when the
        /// style layer declares no <c>source-layer</c>, or when no MVT layer of that name exists in the
        /// tile.
        /// </summary>
        public static MvtLayer ResolveMvtLayer(StyleLayer layer, MvtTile tile)
        {
            if (layer == null || tile == null) return null;
            if (string.IsNullOrEmpty(layer.SourceLayer)) return null;
            return tile.GetLayer(layer.SourceLayer);
        }

        /// <summary>
        /// Resolves the style source definition a layer draws from (by the layer's <c>source</c> id).
        /// Returns null when the layer has no source (background) or the id is not defined.
        /// </summary>
        public static SourceDefinition ResolveSource(StyleLayer layer, StyleDocument document)
        {
            if (layer == null || document == null) return null;
            if (string.IsNullOrEmpty(layer.Source)) return null;
            return document.GetSource(layer.Source);
        }

        /// <summary>
        /// True when this style layer renders vector features from a vector source for which a matching
        /// MVT source-layer exists in the tile (i.e. it has a resolvable feature set). Background and
        /// non-vector layers return false.
        /// </summary>
        public static bool HasResolvableFeatures(StyleLayer layer, StyleDocument document, MvtTile tile)
        {
            var source = ResolveSource(layer, document);
            if (source == null || source.Type != SourceType.Vector) return false;
            return ResolveMvtLayer(layer, tile) != null;
        }
    }
}
