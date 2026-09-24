using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.FillExtrusion
{
    /// <summary>
    /// The parsed MapLibre fill-extrusion <b>paint</b> properties: one <see cref="StyleProperty{T}"/> per
    /// <c>fill-extrusion-*</c> key, as in <see cref="Fill.PaintProperties"/>. Non-obvious why:
    /// <see cref="Translate"/> goes through the expression engine, because the spec allows a zoom-interpolated
    /// translate that a raw <c>Items[0]/[1]</c> read would collapse to <c>[0, 0]</c>. Absent properties use
    /// the spec defaults; <see cref="IsInertFallback"/> is true when all are absent.
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>
        /// fill-extrusion-height: the extruded geometry's height in metres, measured from
        /// <see cref="Base"/>. Default 0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Height { get; init; }

        /// <summary>
        /// fill-extrusion-base: the extruded geometry's base height in metres above ground. Default 0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Base { get; init; }

        /// <summary>
        /// fill-extrusion-color: the extruded geometry's base color. Default opaque black rgba(0,0,0,1).
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>
        /// fill-extrusion-opacity: opacity multiplier [0,1]. Default 1.0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>
        /// fill-extrusion-vertical-gradient: whether a vertical gradient is applied to the sides of the
        /// extruded geometry. Encoded as float: 1.0 = true (default), 0.0 = false. Constant only;
        /// data-driven or malformed values fall back to 1.0 (true). Not yet consumed by the renderer.
        /// </summary>
        public StyleProperty<float> VerticalGradient { get; init; }

        /// <summary>
        /// fill-extrusion-translate: pixel-space [x, y] translation offset. Default [0, 0]. Unlike
        /// <see cref="Fill.PaintProperties.Translate"/> / <see cref="Line.PaintProperties.Translate"/>, this
        /// one is parsed THROUGH the expression engine, so Constant AND Zoom kinds are both preserved
        /// (see the class doc). Feature/Composite (data-driven) is spec-invalid for a layer-level property —
        /// falls back to the [0, 0] default, mirroring <see cref="VerticalGradient"/>'s guard.
        /// </summary>
        public StyleProperty<double2> Translate { get; init; }

        /// <summary>
        /// fill-extrusion-translate-anchor: coordinate space for <see cref="Translate"/>. Encoded as float:
        /// 0.0 = "map" (default), 1.0 = "viewport". Constant only.
        /// </summary>
        public StyleProperty<float> TranslateAnchor { get; init; }

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; init; }

        // ── Convenience accessors matching Fill.PaintProperties' API ────────────────────────────

        /// <summary>Classification of the fill-extrusion-height expression.</summary>
        public ExpressionKind HeightKind => Height.Kind;

        /// <summary>Classification of the fill-extrusion-base expression.</summary>
        public ExpressionKind BaseKind => Base.Kind;

        /// <summary>Classification of the fill-extrusion-color expression.</summary>
        public ExpressionKind ColorKind => Color.Kind;

        /// <summary>Classification of the fill-extrusion-opacity expression.</summary>
        public ExpressionKind OpacityKind => Opacity.Kind;

        /// <summary>Classification of the fill-extrusion-vertical-gradient expression.</summary>
        public ExpressionKind VerticalGradientKind => VerticalGradient.Kind;

        /// <summary>Classification of the fill-extrusion-translate expression.</summary>
        public ExpressionKind TranslateKind => Translate.Kind;

        /// <summary>Classification of the fill-extrusion-translate-anchor expression.</summary>
        public ExpressionKind TranslateAnchorKind => TranslateAnchor.Kind;

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse and classify all fill-extrusion paint properties from a layer's <c>paint</c>
        /// sub-tree.</summary>
        /// <param name="paint">The layer's raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec
        /// defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            bool anyPresent = false;

            // fill-extrusion-height: default 0
            JsonValue heightJson = paint?.Get(PropertyNames.FillExtrusionHeight);
            if (heightJson != null) anyPresent = true;
            StyleProperty<float> height = heightJson != null
                ? new StyleProperty<float>(heightJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // fill-extrusion-base: default 0
            JsonValue baseJson = paint?.Get(PropertyNames.FillExtrusionBase);
            if (baseJson != null) anyPresent = true;
            StyleProperty<float> baseHeight = baseJson != null
                ? new StyleProperty<float>(baseJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // fill-extrusion-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.FillExtrusionColor);
            if (colorJson != null) anyPresent = true;
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // fill-extrusion-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.FillExtrusionOpacity);
            if (opacityJson != null) anyPresent = true;
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // fill-extrusion-vertical-gradient: default true (1.0). Tolerates data-driven by falling to default.
            JsonValue verticalGradientJson = paint?.Get(PropertyNames.FillExtrusionVerticalGradient);
            if (verticalGradientJson != null) anyPresent = true;
            StyleProperty<float> verticalGradient;
            if (verticalGradientJson != null)
            {
                try
                {
                    var candidate = new StyleProperty<float>(
                        verticalGradientJson, 1f, v => v.AsBool() ? 1f : 0f);
                    // fill-extrusion-vertical-gradient must not be data-driven (Feature/Composite → default true)
                    verticalGradient = candidate.DependsOnFeature
                        ? new StyleProperty<float>(1f)
                        : candidate;
                }
                catch
                {
                    verticalGradient = new StyleProperty<float>(1f);
                }
            }
            else
            {
                verticalGradient = new StyleProperty<float>(1f);
            }

            // fill-extrusion-translate: [x, y] px offset, parsed through the expression engine (see the class
            // doc). WrapBareArrayLiterals admits the constant bare [x, y] form, which the strict parser rejects.
            JsonValue translateJson = paint?.Get(PropertyNames.FillExtrusionTranslate);
            if (translateJson != null) anyPresent = true;
            StyleProperty<double2> translate;
            if (translateJson != null)
            {
                try
                {
                    JsonValue wrapped = ExpressionParser.WrapBareArrayLiterals(translateJson);
                    var candidate = new StyleProperty<double2>(wrapped, new double2(0.0, 0.0),
                        v =>
                        {
                            var a = v.AsArray();
                            return new double2(a[0].AsNumber(), a[1].AsNumber());
                        });
                    // fill-extrusion-translate is a layer-level property — a data-driven value is spec-invalid
                    // (Feature/Composite → default [0,0]), mirroring the VerticalGradient/Antialias guard.
                    translate = candidate.DependsOnFeature
                        ? new StyleProperty<double2>(new double2(0.0, 0.0))
                        : candidate;
                }
                catch
                {
                    // A malformed translate (a parse failure, or a short array like [5] that throws at eager eval)
                    // falls to the spec default rather than taking down the whole layer.
                    translate = new StyleProperty<double2>(new double2(0.0, 0.0));
                }
            }
            else
            {
                translate = new StyleProperty<double2>(new double2(0.0, 0.0));
            }

            // fill-extrusion-translate-anchor: "map"→0, "viewport"→1
            JsonValue anchorJson = paint?.Get(PropertyNames.FillExtrusionTranslateAnchor);
            if (anchorJson != null) anyPresent = true;
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            StyleProperty<float> translateAnchor = new StyleProperty<float>(anchorVal);

            return new PaintProperties
            {
                Height           = height,
                Base             = baseHeight,
                Color            = color,
                Opacity          = opacity,
                VerticalGradient = verticalGradient,
                Translate        = translate,
                TranslateAnchor  = translateAnchor,
                IsInertFallback  = !anyPresent,
            };
        }
    }
}
