using MapRenderer.Core.Style;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The layer→source resolution seam: maps a parsed <see cref="StyleLayer"/> onto features in a
    /// decoded <see cref="IDecodedTile"/> via the layer's <c>source-layer</c> string. This is the plumbing
    /// that connects the style model to the data pipeline; the per-layer rendering paths extend behaviour
    /// off this seam rather than re-implementing the lookup.
    ///
    /// Engine-free and <b>source-type-agnostic</b>: it dispatches the style layer's <c>source-layer</c>
    /// string — empty or not — to the decoded tile and lets the FORMAT answer.
    ///
    /// <para><b>What an empty <c>source-layer</c> means is the tile's claim, not this resolver's.</b>
    /// Short-circuiting to null here is right for MVT, where a background or raster layer names no
    /// source-layer and must select nothing, and wrong for GeoJSON, where the Style Spec says
    /// <c>source-layer</c> is <i>required for vector sources</i> and <i>unused for geojson sources</i>, so a
    /// conformant geojson style layer omits it. Each decoded tile answers for its own format through
    /// <see cref="IDecodedTile.GetLayer"/>: the MVT one returns null for an empty name, the GeoJSON one
    /// returns its sole layer.</para>
    ///
    /// <para>No format carrier type is NAMED here, not even in prose: this file is on the WriteInto path,
    /// which must reference none of them, and that scan reads the raw text.</para>
    /// </summary>
    public static class SourceLayerResolver
    {
        /// <summary>
        /// Resolves the tile layer a style layer selects features from. Returns null (no throw) when the tile
        /// has no layer answering to the style layer's <c>source-layer</c> — including, for formats that key
        /// on the name, when there is no <c>source-layer</c> at all.
        /// </summary>
        public static ITileLayer ResolveTileLayer(StyleLayer layer, IDecodedTile tile)
        {
            if (layer == null || tile == null) return null;
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
        /// True when this style layer renders vector features from a FEATURE source (vector or geojson — the
        /// two source types that decode to <see cref="ITileLayer"/>) for which the tile has a matching layer.
        /// Background and non-feature layers return false.
        /// </summary>
        public static bool HasResolvableFeatures(StyleLayer layer, StyleDocument document, IDecodedTile tile)
        {
            var source = ResolveSource(layer, document);
            if (source == null || source.Type is not (SourceType.Vector or SourceType.GeoJson)) return false;
            return ResolveTileLayer(layer, tile) != null;
        }
    }
}
