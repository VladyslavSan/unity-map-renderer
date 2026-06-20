using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A single style layer (Style Spec <c>layers[]</c>). One class for all 10 types, discriminated by
    /// <see cref="LayerType"/> — later stages own the typed paint/layout per type, so building 10
    /// hollow subclasses now would be premature. The common fields are typed here; <c>paint</c>,
    /// <c>layout</c>, and <c>filter</c> are retained as raw <see cref="JsonValue"/> sub-trees for those
    /// later stages (S09 expressions, S10 filters, S13+ per-layer paint) to consume.
    /// </summary>
    public sealed class StyleLayer
    {
        /// <summary>Unique layer id (Style Spec: required string). Null only if absent (tolerated).</summary>
        public string Id;

        /// <summary>The recognized layer type; <see cref="StyleLayerType.Unknown"/> for forward-compat.</summary>
        public StyleLayerType LayerType;

        /// <summary>The raw <c>type</c> string as written in the JSON (preserved even when Unknown).</summary>
        public string RawType;

        /// <summary>
        /// The id of the source this layer draws from (Style Spec <c>source</c>). Null for
        /// <c>background</c> (and any type that has no source).
        /// </summary>
        public string Source;

        /// <summary>
        /// The MVT source-layer name to select features from within <see cref="Source"/> (Style Spec
        /// <c>source-layer</c>). This string drives feature selection from the decoded MVT (see
        /// <see cref="SourceLayerResolver"/>). Null for layers without one (background/raster).
        /// </summary>
        public string SourceLayer;

        /// <summary>Layer <c>minzoom</c>. Nullable: the spec lists no default (absent = unbounded below).</summary>
        public double? MinZoom;

        /// <summary>Layer <c>maxzoom</c>. Nullable: the spec lists no default (absent = unbounded above).</summary>
        public double? MaxZoom;

        /// <summary>Raw <c>filter</c> sub-tree (legacy or expression), or null. Parsed in S10.</summary>
        public JsonValue Filter;

        /// <summary>Raw <c>layout</c> sub-tree, or null. Typed per layer in its own stage.</summary>
        public JsonValue Layout;

        /// <summary>Raw <c>paint</c> sub-tree, or null. Typed per layer in its own stage.</summary>
        public JsonValue Paint;

        /// <summary>The full original layer JSON object (preserves any unknown/forward-compat keys).</summary>
        public JsonValue Raw;
    }
}
