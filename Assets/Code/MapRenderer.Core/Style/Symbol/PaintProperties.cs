using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The parsed MapLibre symbol <b>paint</b> properties for a single symbol style layer — the
    /// <c>text-*</c> colour/halo knobs. Each key is read from the layer's <c>paint</c> sub-tree (via
    /// <see cref="PropertyNames"/>) and collapsed into a <see cref="StyleProperty{T}"/> (one parsed
    /// expression + typed default), exactly as <c>Line</c>/<c>Fill</c> do. Engine-free; clean-room
    /// (public Style Spec §symbol paint).
    ///
    /// <para>How each is consumed (S20/S105 F1): <see cref="Color"/>/<see cref="Opacity"/> bake into the
    /// per-vertex billboard COLOUR stream (works for constant AND data-driven); the halo trio binds by name
    /// onto the per-layer material's <c>_HaloColor</c>/<c>_HaloWidthPx</c>/<c>_HaloBlurPx</c> uniforms
    /// (constant/zoom only in the first cut).</para>
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>text-color: glyph fill colour. Default opaque black <c>rgba(0,0,0,1)</c>.</summary>
        public StyleProperty<Color> Color { get; }

        /// <summary>text-opacity: glyph alpha multiplier [0,1]. Default 1.0.</summary>
        public StyleProperty<float> Opacity { get; }

        /// <summary>text-halo-color: halo colour. Default transparent black <c>rgba(0,0,0,0)</c> (spec).</summary>
        public StyleProperty<Color> HaloColor { get; }

        /// <summary>text-halo-width: halo width in pixels. Default 0 (no halo).</summary>
        public StyleProperty<float> HaloWidth { get; }

        /// <summary>text-halo-blur: halo blur radius in pixels. Default 0.</summary>
        public StyleProperty<float> HaloBlur { get; }

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; }

        /// <summary>Convenience: parse from a style layer's <c>PaintJson</c>.</summary>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public PaintProperties(MapRenderer.Core.Style.StyleLayer layer)
            : this((layer ?? throw new System.ArgumentNullException(nameof(layer))).PaintJson) { }

        /// <summary>Parse all symbol paint properties from the layer's <c>paint</c> sub-tree (may be null → defaults).</summary>
        public PaintProperties(JsonValue paint)
        {
            bool anyPresent = false;

            // text-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.TextColor);
            if (colorJson != null) anyPresent = true;
            Color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // text-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.TextOpacity);
            if (opacityJson != null) anyPresent = true;
            Opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // text-halo-color: default rgba(0,0,0,0)
            JsonValue haloColorJson = paint?.Get(PropertyNames.TextHaloColor);
            if (haloColorJson != null) anyPresent = true;
            HaloColor = haloColorJson != null
                ? new StyleProperty<Color>(haloColorJson, new Color(0f, 0f, 0f, 0f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 0f));

            // text-halo-width: default 0
            JsonValue haloWidthJson = paint?.Get(PropertyNames.TextHaloWidth);
            if (haloWidthJson != null) anyPresent = true;
            HaloWidth = haloWidthJson != null
                ? new StyleProperty<float>(haloWidthJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            // text-halo-blur: default 0
            JsonValue haloBlurJson = paint?.Get(PropertyNames.TextHaloBlur);
            if (haloBlurJson != null) anyPresent = true;
            HaloBlur = haloBlurJson != null
                ? new StyleProperty<float>(haloBlurJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            IsInertFallback = !anyPresent;
        }
    }
}
