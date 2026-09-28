using System.Collections.Generic;
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
    /// streams, except a constant <see cref="Color"/> and a non-feature <see cref="HaloColor"/>: their RGB binds to
    /// the per-layer <c>_TextColor</c>/<c>_HaloColor</c> uniform so a restyle can ease it, and the stream carries white.
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

        /// <summary>One message per property that fell back to its default. Never null.</summary>
        public IReadOnlyList<string> Errors { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse all symbol paint properties from a layer's <c>paint</c> sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            var reader = new PropertyReader("paint");
            StyleProperty<Color> color = reader.ReadProperty(paint, PropertyNames.TextColor,
                new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced());
            StyleProperty<float> opacity = reader.ReadProperty(paint, PropertyNames.TextOpacity, 1f,
                v => (float)v.AsNumber());
            StyleProperty<Color> haloColor = reader.ReadProperty(paint, PropertyNames.TextHaloColor,
                new Color(0f, 0f, 0f, 0f), v => v.AsColorCoerced());
            StyleProperty<float> haloWidth = reader.ReadProperty(paint, PropertyNames.TextHaloWidth, 0f,
                v => (float)v.AsNumber());
            StyleProperty<float> haloBlur = reader.ReadProperty(paint, PropertyNames.TextHaloBlur, 0f,
                v => (float)v.AsNumber());
            StyleProperty<double2> translate = reader.Read(paint, PropertyNames.TextTranslate,
                new StyleProperty<double2>(new double2(0.0, 0.0)), TranslateProperty.Parse);

            // text-translate-anchor: "viewport" → Viewport, else map (default/unrecognized).
            JsonValue translateAnchorJson = paint?.Get(PropertyNames.TextTranslateAnchor);
            TextTranslateAnchor translateAnchor = translateAnchorJson?.AsString(null) == PropertyNames.TranslateAnchorViewport
                ? TextTranslateAnchor.Viewport
                : TextTranslateAnchor.Map;

            StyleProperty<float> iconOpacity = reader.ReadProperty(paint, PropertyNames.IconOpacity, 1f,
                v => (float)v.AsNumber());

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
                Errors          = reader.Errors,
            };
        }
    }
}
