using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A style layer (Style Spec <c>layers[]</c>) — the generic base. Line and fill layers are specialized
    /// by <see cref="MapRenderer.Core.Style.Line.StyleLayer"/> / <see cref="MapRenderer.Core.Style.Fill.StyleLayer"/>,
    /// which add their typed parsed paint/layout; the other types are represented by this base directly.
    /// The common fields are typed here; <c>paint</c>, <c>layout</c>, and <c>filter</c> are retained as raw
    /// <see cref="JsonValue"/> sub-trees (named <c>*Json</c> to leave the bare <c>Paint</c>/<c>Layout</c>
    /// names free for the typed subclass views).
    /// </summary>
    public class StyleLayer
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

        /// <summary>Raw <c>layout</c> sub-tree, or null. Typed views: the per-type subclass's <c>Layout</c>.
        /// Internal: external callers access paint/layout via the typed <c>Paint</c>/<c>Layout</c> properties
        /// on the concrete subclass. Access from <c>MapRenderer.Tests.EditMode</c> is granted via
        /// <c>InternalsVisibleTo</c> for forward-compat assertions only.</summary>
        internal JsonValue LayoutJson;

        /// <summary>Raw <c>paint</c> sub-tree, or null. Typed views: the per-type subclass's <c>Paint</c>.
        /// Internal: see <see cref="LayoutJson"/>.</summary>
        internal JsonValue PaintJson;

        /// <summary>The full original layer JSON object (preserves any unknown/forward-compat keys).
        /// Internal: see <see cref="LayoutJson"/>.</summary>
        internal JsonValue Raw;
    }
}
