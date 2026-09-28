using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style.FillExtrusion
{
    /// <summary>
    /// The parsed MapLibre fill-extrusion <b>paint</b> properties: one <see cref="StyleProperty{T}"/> per
    /// <c>fill-extrusion-*</c> key, as in <see cref="Fill.PaintProperties"/>. Non-obvious why:
    /// <see cref="Translate"/> goes through the expression engine, because the spec allows a zoom-interpolated
    /// translate that a raw <c>Items[0]/[1]</c> read would collapse to <c>[0, 0]</c>. Absent properties use
    /// the spec defaults.
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
        /// fill-extrusion-opacity: opacity multiplier [0,1]. Default 1.0. Not data-driven per the spec —
        /// Constant/Zoom rides the material uniform; a Feature/Composite value has no effect (pinned to 1).
        /// </summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>
        /// fill-extrusion-translate: pixel-space [x, y] translation offset. Default [0, 0]. Unlike
        /// <see cref="Fill.PaintProperties.Translate"/> / <see cref="Line.PaintProperties.Translate"/>, this
        /// one is parsed THROUGH the expression engine, so Constant AND Zoom kinds are both preserved
        /// (see the class doc). Feature/Composite (data-driven) is spec-invalid for a layer-level property —
        /// falls back to the [0, 0] default.
        /// </summary>
        public StyleProperty<double2> Translate { get; init; }

        /// <summary>
        /// fill-extrusion-translate-anchor: coordinate space for <see cref="Translate"/>. Encoded as float:
        /// 0.0 = "map" (default), 1.0 = "viewport". Constant only.
        /// </summary>
        public StyleProperty<float> TranslateAnchor { get; init; }

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
            // fill-extrusion-height: default 0
            JsonValue heightJson = paint?.Get(PropertyNames.FillExtrusionHeight);
            StyleProperty<float> height = heightJson != null
                ? new StyleProperty<float>(heightJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // fill-extrusion-base: default 0
            JsonValue baseJson = paint?.Get(PropertyNames.FillExtrusionBase);
            StyleProperty<float> baseHeight = baseJson != null
                ? new StyleProperty<float>(baseJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // fill-extrusion-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.FillExtrusionColor);
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // fill-extrusion-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.FillExtrusionOpacity);
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // fill-extrusion-translate: [x, y] px offset, parsed through the expression engine (see the
            // class doc) via the shared TranslateProperty helper.
            JsonValue translateJson = paint?.Get(PropertyNames.FillExtrusionTranslate);
            StyleProperty<double2> translate = translateJson != null
                ? TranslateProperty.Parse(translateJson)
                : new StyleProperty<double2>(new double2(0.0, 0.0));

            // fill-extrusion-translate-anchor: "map"→0, "viewport"→1
            JsonValue anchorJson = paint?.Get(PropertyNames.FillExtrusionTranslateAnchor);
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            StyleProperty<float> translateAnchor = new StyleProperty<float>(anchorVal);

            return new PaintProperties
            {
                Height           = height,
                Base             = baseHeight,
                Color            = color,
                Opacity          = opacity,
                Translate        = translate,
                TranslateAnchor  = translateAnchor,
            };
        }
    }
}
