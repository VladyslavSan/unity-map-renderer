using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A style layer (Style Spec <c>layers[]</c>) — the generic base. Line and fill layers are specialized
    /// by <see cref="MapRenderer.Core.Style.Line.StyleLayer"/> / <see cref="MapRenderer.Core.Style.Fill.StyleLayer"/>,
    /// which add their typed, eagerly-parsed paint/layout; the other types are represented by this base
    /// directly. The common fields are typed here; only <c>filter</c> and the whole-object <c>Raw</c> are
    /// retained as raw <see cref="JsonValue"/> — <c>paint</c>/<c>layout</c> are parsed at construction.
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

        /// <summary>MapLibre layer visibility at a given DISPLAY (camera) zoom: <c>minzoom &lt;= zoom &lt; maxzoom</c>,
        /// with a null bound meaning unbounded. <b>minzoom is inclusive, maxzoom is EXCLUSIVE</b> (Style Spec). Must
        /// be evaluated against the LIVE camera zoom every frame — NOT the tile build zoom — so overzoomed tiles
        /// (camera past the source's max data zoom) still turn layers on/off as MapLibre does.</summary>
        public bool IsVisibleAtZoom(double zoom)
            => (!MinZoom.HasValue || zoom >= MinZoom.Value) && (!MaxZoom.HasValue || zoom < MaxZoom.Value);

        /// <summary>Raw <c>filter</c> sub-tree (legacy or expression), or null. Parsed in S10.</summary>
        public JsonValue Filter;

        /// <summary>The full original layer JSON object, retained so unknown/forward-compat keys survive
        /// (including its <c>paint</c>/<c>layout</c> sub-trees, which are otherwise parsed and discarded).
        /// Internal: callers read the typed <c>Paint</c>/<c>Layout</c> views on the concrete subclass instead.
        /// Access from <c>MapRenderer.Tests.EditMode</c> is granted via <c>InternalsVisibleTo</c> for
        /// forward-compat assertions only.</summary>
        internal JsonValue Raw;
    }
}
