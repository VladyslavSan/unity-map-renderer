using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style.Symbol
{
    /// <summary>
    /// The parsed MapLibre symbol <b>paint</b> properties: the <c>text-*</c> colour/halo knobs plus
    /// <c>icon-opacity</c>, each a <see cref="StyleProperty{T}"/> as in <c>Line</c>/<c>Fill</c>; <c>icon-color</c>
    /// and <c>icon-halo-*</c> are not parsed. The text properties evaluate per feature into the billboard vertex
    /// streams, except a constant <see cref="Color"/> or <see cref="HaloColor"/>: its RGB binds to the per-layer
    /// <c>_TextColor</c>/<c>_HaloColor</c> uniform so a restyle can ease it, and the stream carries white.
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>text-color: glyph fill colour. Default opaque black <c>rgba(0,0,0,1)</c>.</summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>text-opacity: glyph alpha multiplier [0,1]. Default 1.0.</summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>text-halo-color: halo colour. Default transparent black <c>rgba(0,0,0,0)</c> (spec).</summary>
        public StyleProperty<Color> HaloColor { get; init; }

        /// <summary>text-halo-width: halo width in pixels. Default 0 (no halo).</summary>
        public StyleProperty<float> HaloWidth { get; init; }

        /// <summary>text-halo-blur: halo blur radius in pixels. Default 0.</summary>
        public StyleProperty<float> HaloBlur { get; init; }

        /// <summary>text-translate: [x, y] pixel offset applied to the symbol's placed screen anchor (y-down,
        /// as authored). Default [0, 0]. Constant or Zoom, parsed through the expression engine like
        /// <c>Line</c>/<c>Fill</c>'s own translate. Read per-frame, per-slot by <c>SymbolPlacementSystem</c>
        /// (never baked onto a symbol) and applied via
        /// <see cref="MapRenderer.Core.Text.Placement.SymbolTranslate"/>.</summary>
        public StyleProperty<double2> Translate { get; init; }

        /// <summary>text-translate-anchor: the frame of reference for <see cref="Translate"/>. Default
        /// <see cref="MapRenderer.Core.Text.TextTranslateAnchor.Map"/>. Constant only. Under <c>Map</c> the offset rotates
        /// with the map bearing; under <c>Viewport</c> it stays screen-aligned
        /// (<see cref="MapRenderer.Core.Text.Placement.SymbolTranslate"/>).</summary>
        public TextTranslateAnchor TranslateAnchor { get; init; }

        /// <summary>icon-opacity: icon alpha multiplier [0,1]. Default 1.0. Zoom-capable.</summary>
        public StyleProperty<float> IconOpacity { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse all symbol paint properties from a layer's <c>paint</c> sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            // text-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.TextColor);
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // text-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.TextOpacity);
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // text-halo-color: default rgba(0,0,0,0)
            JsonValue haloColorJson = paint?.Get(PropertyNames.TextHaloColor);
            StyleProperty<Color> haloColor = haloColorJson != null
                ? new StyleProperty<Color>(haloColorJson, new Color(0f, 0f, 0f, 0f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 0f));

            // text-halo-width: default 0
            JsonValue haloWidthJson = paint?.Get(PropertyNames.TextHaloWidth);
            StyleProperty<float> haloWidth = haloWidthJson != null
                ? new StyleProperty<float>(haloWidthJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // text-halo-blur: default 0
            JsonValue haloBlurJson = paint?.Get(PropertyNames.TextHaloBlur);
            StyleProperty<float> haloBlur = haloBlurJson != null
                ? new StyleProperty<float>(haloBlurJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // text-translate: [x, y] px offset, parsed through the expression engine via TranslateProperty
            // (Constant AND Zoom both survive — mirrors Line/Fill's own translate).
            JsonValue translateJson = paint?.Get(PropertyNames.TextTranslate);
            StyleProperty<double2> translate = translateJson != null
                ? TranslateProperty.Parse(translateJson)
                : new StyleProperty<double2>(new double2(0.0, 0.0));

            // text-translate-anchor: "viewport" → Viewport, else map (default/unrecognized).
            JsonValue translateAnchorJson = paint?.Get(PropertyNames.TextTranslateAnchor);
            TextTranslateAnchor translateAnchor = translateAnchorJson?.AsString(null) == PropertyNames.TranslateAnchorViewport
                ? TextTranslateAnchor.Viewport
                : TextTranslateAnchor.Map;

            // icon-opacity: default 1.0
            JsonValue iconOpacityJson = paint?.Get(PropertyNames.IconOpacity);
            StyleProperty<float> iconOpacity = iconOpacityJson != null
                ? new StyleProperty<float>(iconOpacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            return new PaintProperties
            {
                Color           = color,
                Opacity         = opacity,
                HaloColor       = haloColor,
                HaloWidth       = haloWidth,
                HaloBlur        = haloBlur,
                Translate       = translate,
                TranslateAnchor = translateAnchor,
                IconOpacity     = iconOpacity,
            };
        }
    }
}
