using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// The feature-selection seam: given a <see cref="StyleLayer"/> and a decoded <see cref="IDecodedTile"/>,
    /// returns the subset of features from the matching source-layer that pass the layer's filter.
    ///
    /// Design:
    /// <list type="bullet">
    ///   <item>Source-layer resolution is delegated to <see cref="SourceLayerResolver.ResolveTileLayer"/> —
    ///     single resolution point, not reimplemented here (S08 seam).</item>
    ///   <item>The layer's <c>filter</c> is compiled <b>once per filter</b> and memoized (see
    ///     <see cref="FilterFor"/>), then evaluated per feature.</item>
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
        public static IReadOnlyList<ITileFeature> SelectFeatures(
            StyleLayer layer, IDecodedTile tile, double zoom = 0.0)
        {
            // 1. Resolve the tile layer through the S08 seam (single resolution point).
            var tileLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            if (tileLayer == null)
                return System.Array.Empty<ITileFeature>();

            // 2. The compiled filter for this layer — memoized across calls.
            var filter = FilterFor(layer);

            // 3. Evaluate per feature. A6: the feature IS an IFeature (the neutral carrier implements it
            // directly), so it passes straight to filter.Matches — no adapter alloc.
            var result = new List<ITileFeature>();
            foreach (var feature in tileLayer.Features)
            {
                if (filter.Matches(feature, zoom))
                    result.Add(feature);
            }
            return result;
        }

        // ---- compiled-filter memo ---------------------------------------------------------------------

        /// <summary>
        /// The <see cref="CompiledFilter"/> for <paramref name="layer"/>, compiled on first use and reused
        /// thereafter — <see cref="CompiledFilter"/>'s own contract ("built once per style layer, evaluated
        /// many times per feature"), which this seam used to break by compiling per call.
        ///
        /// <para><b>Keyed on the filter JSON node, not on the <see cref="StyleLayer"/>.</b>
        /// <see cref="StyleLayer.Filter"/> is a public mutable field, so a layer-keyed memo could serve a
        /// compile of a filter the layer no longer has. Keying on the node means reassigning
        /// <c>Filter</c> simply misses the memo and recompiles. (Mutating a <see cref="JsonValue"/> tree
        /// <i>in place</i> would still stale it — nothing does; the style document is parsed once and read.)</para>
        ///
        /// <para><b>Why a <see cref="ConditionalWeakTable{TKey,TValue}"/>:</b> tile processing runs off the
        /// main thread, so the memo must be thread-safe — a plain <c>Dictionary</c> here would be a data
        /// race. It also holds keys <i>weakly</i>, so the entries for a style go away with the style
        /// document rather than pinning every filter ever parsed for the life of the process. A racing pair
        /// of callers may both compile; only one instance is published, and the loser is garbage —
        /// <see cref="CompiledFilter.Compile"/> is pure, so that is harmless.</para>
        ///
        /// <para>A null/absent filter compiles to the shared match-all sentinel, which is free — no memo
        /// entry is made for it. A malformed filter throws out of here exactly as it did before, and
        /// nothing is cached, so the next call throws too.</para>
        /// </summary>
        internal static CompiledFilter FilterFor(StyleLayer layer)
        {
            JsonValue filter = layer?.Filter;
            if (filter == null)
                return CompiledFilter.Compile(null);

            return CompiledFilters.GetValue(filter, CompileCallback);
        }

        private static readonly ConditionalWeakTable<JsonValue, CompiledFilter> CompiledFilters =
            new ConditionalWeakTable<JsonValue, CompiledFilter>();

        // Hoisted so the lookup allocates no delegate per call.
        private static readonly ConditionalWeakTable<JsonValue, CompiledFilter>.CreateValueCallback
            CompileCallback = f => CompiledFilter.Compile(f);
    }
}
