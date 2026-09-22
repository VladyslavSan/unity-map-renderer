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

        /// <summary><c>layout: {"visibility": ...}</c> ONLY — <see langword="false"/> means
        /// <c>"none"</c> was declared. Does NOT consider zoom; see <see cref="IsVisibleAtZoom"/> for the
        /// combined answer of whether the layer actually draws. Defaults <see langword="true"/> so a
        /// layer built without going through <see cref="StyleParser"/> (a test fixture, a decoder probe)
        /// is visible by the same spec default an absent <c>layout</c> parses to.</summary>
        public bool Visible = true;

        /// <summary>MapLibre layer visibility at a given DISPLAY (camera) zoom: <see cref="Visible"/>, and
        /// <c>minzoom &lt;= zoom &lt; maxzoom</c>, with a null bound meaning unbounded. <b>minzoom is inclusive,
        /// maxzoom is EXCLUSIVE</b> (Style Spec). Must be evaluated against the LIVE camera zoom every frame — NOT
        /// the tile build zoom — so overzoomed tiles (camera past the source's max data zoom) still turn layers
        /// on/off as MapLibre does.</summary>
        public bool IsVisibleAtZoom(double zoom)
            => Visible && (!MinZoom.HasValue || zoom >= MinZoom.Value) && (!MaxZoom.HasValue || zoom < MaxZoom.Value);

        /// <summary>Raw <c>filter</c> sub-tree (legacy or expression), or null.</summary>
        public JsonValue Filter;

        /// <summary>The full original layer JSON object, retained so unknown/forward-compat keys survive
        /// (including its <c>paint</c>/<c>layout</c> sub-trees, which are otherwise parsed and discarded).
        /// Callers read the typed <c>Paint</c>/<c>Layout</c> views on the concrete subclass instead, except
        /// the restyle survivor gate (<c>SurvivingLayerGate</c>, in <c>MapRenderer.Unity</c>), which must
        /// compare the whole raw object — an unknown or forward-compat key must be inside that comparison,
        /// and the typed views drop it.</summary>
        public JsonValue Raw;
    }
}
