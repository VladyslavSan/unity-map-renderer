using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// The feature-selection seam: given a <see cref="StyleLayer"/> and a decoded <see cref="MvtTile"/>,
    /// returns the subset of features from the matching MVT source-layer that pass the layer's filter.
    ///
    /// Design:
    /// <list type="bullet">
    ///   <item>Source-layer resolution is delegated to <see cref="SourceLayerResolver.ResolveMvtLayer"/> —
    ///     single resolution point, not reimplemented here (S08 seam).</item>
    ///   <item>The layer's <c>filter</c> is compiled once per call via <see cref="CompiledFilter.Compile"/>
    ///     and then evaluated per feature. Callers that call <see cref="SelectFeatures"/> in a hot loop
    ///     should cache the <see cref="CompiledFilter"/> themselves.</item>
    ///   <item>Null/absent source-layer → empty result (mirrors <see cref="SourceLayerResolver"/> null-tolerance).</item>
    /// </list>
    /// </summary>
    public static class FeatureSelector
    {
        /// <summary>
        /// Selects features from <paramref name="tile"/> that belong to the source-layer declared by
        /// <paramref name="layer"/> and pass <paramref name="layer"/>'s filter at <paramref name="zoom"/>.
        ///
        /// Returns an empty list when the source-layer is absent/unresolvable. Never throws for missing
        /// layers; may throw <see cref="ExpressionParseException"/> if the layer's filter is malformed.
        /// </summary>
        public static IReadOnlyList<MvtFeature> SelectFeatures(
            StyleLayer layer, MvtTile tile, double zoom = 0.0)
        {
            // 1. Resolve the MVT layer through the S08 seam (single resolution point).
            var mvtLayer = SourceLayerResolver.ResolveMvtLayer(layer, tile);
            if (mvtLayer == null)
                return System.Array.Empty<MvtFeature>();

            // 2. Compile the filter once for this call.
            var filter = CompiledFilter.Compile(layer?.Filter);

            // 3. Evaluate per feature.
            var result = new List<MvtFeature>();
            foreach (var feature in mvtLayer.Features)
            {
                var adapted = new MvtFeatureAdapter(feature);
                if (filter.Matches(adapted, zoom))
                    result.Add(feature);
            }
            return result;
        }
    }
}
