using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Symbol
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
        /// as authored). Default [0, 0]. A plain constant <see cref="float2"/> (px offsets are small — no
        /// need for double precision or the zoom/expression machinery; mirrors <c>LayoutProperties.TextOffset</c>).
        /// Consumed per-frame by <c>SymbolPlacementSystem</c> via <see cref="Text.Placement.SymbolTranslate"/>.</summary>
        public float2 Translate { get; init; }

        /// <summary>text-translate-anchor: the frame of reference for <see cref="Translate"/>. Default
        /// <see cref="Text.TextTranslateAnchor.Map"/>. Parsed here; the map-vs-viewport divergence (a
        /// bearing rotation) is consumed with the rotation-alignment work — until then both
        /// resolve to the same screen-space delta (identical at bearing 0).</summary>
        public TextTranslateAnchor TranslateAnchor { get; init; }

        /// <summary>icon-opacity: icon alpha multiplier [0,1]. Default 1.0. Zoom-capable.</summary>
        public StyleProperty<float> IconOpacity { get; init; }

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse all symbol paint properties from a layer's <c>paint</c> sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            bool anyPresent = false;

            // text-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.TextColor);
            if (colorJson != null) anyPresent = true;
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // text-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.TextOpacity);
            if (opacityJson != null) anyPresent = true;
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // text-halo-color: default rgba(0,0,0,0)
            JsonValue haloColorJson = paint?.Get(PropertyNames.TextHaloColor);
            if (haloColorJson != null) anyPresent = true;
            StyleProperty<Color> haloColor = haloColorJson != null
                ? new StyleProperty<Color>(haloColorJson, new Color(0f, 0f, 0f, 0f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 0f));

            // text-halo-width: default 0
            JsonValue haloWidthJson = paint?.Get(PropertyNames.TextHaloWidth);
            if (haloWidthJson != null) anyPresent = true;
            StyleProperty<float> haloWidth = haloWidthJson != null
                ? new StyleProperty<float>(haloWidthJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // text-halo-blur: default 0
            JsonValue haloBlurJson = paint?.Get(PropertyNames.TextHaloBlur);
            if (haloBlurJson != null) anyPresent = true;
            StyleProperty<float> haloBlur = haloBlurJson != null
                ? new StyleProperty<float>(haloBlurJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // text-translate: [x, y] in pixels (constant — the components are scalars, not expressions).
            // Narrow to float at the JSON boundary (px offsets are small; double is pointless here).
            JsonValue translateJson = paint?.Get(PropertyNames.TextTranslate);
            if (translateJson != null) anyPresent = true;
            float tx = 0f, ty = 0f;
            if (translateJson != null && translateJson.IsArray && translateJson.Items.Count >= 2)
            {
                tx = (float)translateJson.Items[0].AsDouble(0.0);
                ty = (float)translateJson.Items[1].AsDouble(0.0);
            }
            float2 translate = new float2(tx, ty);

            // text-translate-anchor: "viewport" → Viewport, else map (default/unrecognized).
            JsonValue translateAnchorJson = paint?.Get(PropertyNames.TextTranslateAnchor);
            if (translateAnchorJson != null) anyPresent = true;
            TextTranslateAnchor translateAnchor = translateAnchorJson?.AsString(null) == PropertyNames.TranslateAnchorViewport
                ? TextTranslateAnchor.Viewport
                : TextTranslateAnchor.Map;

            // icon-opacity: default 1.0
            JsonValue iconOpacityJson = paint?.Get(PropertyNames.IconOpacity);
            if (iconOpacityJson != null) anyPresent = true;
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
                IsInertFallback = !anyPresent,
            };
        }
    }
}
