using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// The parsed MapLibre line <b>paint</b> properties for a single line style layer.
    ///
    /// Each <c>line-*</c> paint key is read from the layer's <c>paint</c> sub-tree (via
    /// <see cref="PropertyNames"/>) and collapsed into a single <see cref="StyleProperty{T}"/>:
    /// one parsed <see cref="Expressions.Expression"/>, one typed default, and a
    /// <c>Value → T</c> projection. The old triple
    /// (<c>XKind</c> + <c>PaintPropertyEvaluator X</c> + <c>DataDrivenPaintEvaluator DataDrivenX</c>)
    /// is gone; <see cref="StyleProperty{T}.Kind"/> reads the expression's kind directly.
    ///
    /// Absent properties use the MapLibre Style Spec defaults and report
    /// <see cref="ExpressionKind.Constant"/>; <see cref="IsInertFallback"/> is true when ALL paint
    /// properties are absent. Engine-free; clean-room (public Style Spec, no MapLibre source).
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>
        /// line-color: stroke fill color. Default opaque black rgba(0,0,0,1).
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>
        /// line-opacity: stroke alpha multiplier [0,1]. Default 1.0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>
        /// line-width: stroke half-width in pixels. Default 1.0 px.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Width { get; init; }

        /// <summary>
        /// line-blur: Gaussian blur radius in pixels applied to the stroke edges. Default 0.0.
        /// Constant/Zoom only (data-driven is uncommon; rejected at <see cref="StyleProperty{T}.Evaluate(double)"/>
        /// if supplied — callers can widen via the bake path if needed in future).
        /// </summary>
        public StyleProperty<float> Blur { get; init; }

        /// <summary>
        /// line-gap-width: half-width of a gap opened in the centre of the stroke, creating a hollow
        /// double-line effect. 0 = solid. Units: pixels. Default 0.
        /// Constant/Zoom only.
        /// </summary>
        public StyleProperty<float> GapWidth { get; init; }

        /// <summary>
        /// line-offset: signed pixel shift perpendicular to the line direction.
        /// Positive values shift left of the travel direction; negative shift right.
        /// Default 0. Constant/Zoom only.
        /// </summary>
        public StyleProperty<float> Offset { get; init; }

        /// <summary>
        /// line-translate: pixel-space [x, y] translation offset applied in
        /// <c>TranslateAnchor</c>-space coordinates. Default [0, 0]. Constant/Zoom only.
        /// Collapsed from the old TranslateX/Y/XKind/YKind quad into one <c>double2</c>.
        /// </summary>
        public StyleProperty<double2> Translate { get; init; }

        /// <summary>
        /// line-translate-anchor: coordinate space for <see cref="Translate"/>. Encoded as a float:
        /// 0.0 = "map" (default), 1.0 = "viewport". Constant only.
        /// </summary>
        public StyleProperty<float> TranslateAnchor { get; init; }

        /// <summary>True when a line-dasharray property was present (mirrors <see cref="DashArray"/> != null).</summary>
        public bool HasDashArray { get; init; }

        /// <summary>
        /// line-dasharray: dash/gap lengths in line-width units, alternating on/off starting with on.
        /// Stays as a raw <see cref="Expression"/> (variable-length per spec AND zoom-dependent — a
        /// step/interpolate yields a different array per zoom, so it cannot be a static scalar T).
        /// Capped at <see cref="LineDash.MaxEntries"/> = 4 entries at evaluation time; extras truncated.
        /// Evaluated per-zoom by <see cref="LineDash.TryEvaluatePattern"/>. Null when absent/malformed.
        /// </summary>
        public Expression DashArray { get; init; }

        /// <summary>Classification of <see cref="DashArray"/> (Constant when absent).</summary>
        public ExpressionKind DashArrayKind => DashArray?.Kind ?? ExpressionKind.Constant;

        /// <summary>The line-pattern value (sprite name), or null when absent. Falls back to solid.</summary>
        public string PatternName { get; init; }

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; init; }

        // ── Convenience accessors matching the old PaintPropertyEvaluator API ──────────────────
        // These enable consumers that branch on feature-dependence to use the same property for both
        // the uniform path (Evaluate(zoom)) and the bake path (TryEvaluate(zoom, feature, out T)).

        /// <summary>Classification of the line-color expression.</summary>
        public ExpressionKind ColorKind => Color.Kind;

        /// <summary>Classification of the line-opacity expression.</summary>
        public ExpressionKind OpacityKind => Opacity.Kind;

        /// <summary>Classification of the line-width expression.</summary>
        public ExpressionKind WidthKind => Width.Kind;

        /// <summary>Classification of the line-blur expression.</summary>
        public ExpressionKind BlurKind => Blur.Kind;

        /// <summary>Classification of the line-gap-width expression.</summary>
        public ExpressionKind GapWidthKind => GapWidth.Kind;

        /// <summary>Classification of the line-offset expression.</summary>
        public ExpressionKind OffsetKind => Offset.Kind;

        /// <summary>Classification of the line-translate expression.</summary>
        public ExpressionKind TranslateKind => Translate.Kind;

        /// <summary>Classification of the line-translate-anchor expression.</summary>
        public ExpressionKind TranslateAnchorKind => TranslateAnchor.Kind;

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse and classify all line paint properties from a layer's <c>paint</c> sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            bool anyPresent = false;

            // line-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.LineColor);
            if (colorJson != null) anyPresent = true;
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // line-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.LineOpacity);
            if (opacityJson != null) anyPresent = true;
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // line-width: default 1.0 px
            JsonValue widthJson = paint?.Get(PropertyNames.LineWidth);
            if (widthJson != null) anyPresent = true;
            StyleProperty<float> width = widthJson != null
                ? new StyleProperty<float>(widthJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // line-blur: default 0
            JsonValue blurJson = paint?.Get(PropertyNames.LineBlur);
            if (blurJson != null) anyPresent = true;
            StyleProperty<float> blur = blurJson != null
                ? new StyleProperty<float>(blurJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // line-gap-width: default 0 px
            JsonValue gapWidthJson = paint?.Get(PropertyNames.LineGapWidth);
            if (gapWidthJson != null) anyPresent = true;
            StyleProperty<float> gapWidth = gapWidthJson != null
                ? new StyleProperty<float>(gapWidthJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // line-offset: default 0 (signed pixels)
            JsonValue offsetJson = paint?.Get(PropertyNames.LineOffset);
            if (offsetJson != null) anyPresent = true;
            StyleProperty<float> offset = offsetJson != null
                ? new StyleProperty<float>(offsetJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // line-translate: [x, y] in pixels. Collapse to StyleProperty<double2>.
            JsonValue translateJson = paint?.Get(PropertyNames.LineTranslate);
            if (translateJson != null) anyPresent = true;
            double txVal = 0.0, tyVal = 0.0;
            if (translateJson != null && translateJson.IsArray && translateJson.Items.Count >= 2)
            {
                txVal = translateJson.Items[0].AsDouble(0.0);
                tyVal = translateJson.Items[1].AsDouble(0.0);
            }
            // Translate is always constant (the array components are scalars, not expressions).
            StyleProperty<double2> translate = new StyleProperty<double2>(new double2(txVal, tyVal));

            // line-translate-anchor: "map"→0, "viewport"→1
            JsonValue anchorJson = paint?.Get(PropertyNames.LineTranslateAnchor);
            if (anchorJson != null) anyPresent = true;
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            StyleProperty<float> translateAnchor = new StyleProperty<float>(anchorVal);

            // line-dasharray: stay as raw Expression (variable-length, zoom-dependent)
            JsonValue dashArrayJson = paint?.Get(PropertyNames.LineDasharray);
            bool hasDashArray = dashArrayJson != null;
            Expression dashArray = null;
            if (dashArrayJson != null)
            {
                anyPresent = true;
                dashArray  = LineDash.ParseDashArray(dashArrayJson);
            }

            // line-pattern: solid fallback
            JsonValue patternJson = paint?.Get(PropertyNames.LinePattern);
            if (patternJson != null) anyPresent = true;
            string patternName = patternJson?.AsString(null);

            return new PaintProperties
            {
                Color           = color,
                Opacity         = opacity,
                Width           = width,
                Blur            = blur,
                GapWidth        = gapWidth,
                Offset          = offset,
                Translate       = translate,
                TranslateAnchor = translateAnchor,
                HasDashArray    = hasDashArray,
                DashArray       = dashArray,
                PatternName     = patternName,
                IsInertFallback = !anyPresent,
            };
        }
    }
}
