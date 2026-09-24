using MapRenderer.Core.Style;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The layer→source resolution seam: maps a parsed <see cref="StyleLayer"/> onto a layer of a decoded
    /// <see cref="IDecodedTile"/> via its <c>source-layer</c> string, and lets the FORMAT answer, even for an
    /// empty name (Style Spec: unused for geojson sources), through <see cref="IDecodedTile.GetLayer"/>.
    /// Non-local invariant: this file names no format carrier type, even in prose, because it is on the
    /// WriteInto path and a raw-text scan forbids them there.
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
