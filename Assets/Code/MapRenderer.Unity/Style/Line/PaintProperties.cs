using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style.Line
{
    /// <summary>
    /// The parsed MapLibre line <b>paint</b> properties for a single line style layer. Each <c>line-*</c> key
    /// becomes one <see cref="StyleProperty{T}"/>: a parsed <see cref="MapRenderer.Core.Expressions.Expression"/>, a typed
    /// default, and a <c>Value → T</c> projection. Absent properties use the Style Spec defaults and report
    /// <see cref="ExpressionKind.Constant"/>.
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
        /// <c>TranslateAnchor</c>-space coordinates. Default [0, 0]. Constant only: an expression
        /// value reads as [0, 0].
        /// </summary>
        public StyleProperty<double2> Translate { get; init; }

        /// <summary>
        /// line-translate-anchor: coordinate space for <see cref="Translate"/>. Encoded as a float:
        /// 0.0 = "map" (default), 1.0 = "viewport". Constant only.
        /// </summary>
        public StyleProperty<float> TranslateAnchor { get; init; }

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

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse and classify all line paint properties from a layer's <c>paint</c> sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            // line-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.LineColor);
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // line-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.LineOpacity);
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // line-width: default 1.0 px
            JsonValue widthJson = paint?.Get(PropertyNames.LineWidth);
            StyleProperty<float> width = widthJson != null
                ? new StyleProperty<float>(widthJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // line-blur: default 0
            JsonValue blurJson = paint?.Get(PropertyNames.LineBlur);
            StyleProperty<float> blur = blurJson != null
                ? new StyleProperty<float>(blurJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // line-gap-width: default 0 px
            JsonValue gapWidthJson = paint?.Get(PropertyNames.LineGapWidth);
            StyleProperty<float> gapWidth = gapWidthJson != null
                ? new StyleProperty<float>(gapWidthJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // line-offset: default 0 (signed pixels)
            JsonValue offsetJson = paint?.Get(PropertyNames.LineOffset);
            StyleProperty<float> offset = offsetJson != null
                ? new StyleProperty<float>(offsetJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // line-translate: [x, y] in pixels. Collapse to StyleProperty<double2>.
            JsonValue translateJson = paint?.Get(PropertyNames.LineTranslate);
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
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            StyleProperty<float> translateAnchor = new StyleProperty<float>(anchorVal);

            // line-dasharray: stay as raw Expression (variable-length, zoom-dependent); null when absent.
            JsonValue dashArrayJson = paint?.Get(PropertyNames.LineDasharray);
            Expression dashArray = dashArrayJson != null ? LineDash.ParseDashArray(dashArrayJson) : null;

            // line-pattern: solid fallback
            JsonValue patternJson = paint?.Get(PropertyNames.LinePattern);
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
                DashArray       = dashArray,
                PatternName     = patternName,
            };
        }
    }
}
