using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Background
{
    /// <summary>
    /// The parsed MapLibre <c>background</c> <b>paint</b> properties for a single background style layer.
    /// Same shape as <see cref="Fill.PaintProperties"/> (the Fill pattern) — one <see cref="StyleProperty{T}"/>
    /// per key, spec defaults when absent. Background has no source/filter, so there is no feature-driven
    /// variant here: a data-driven expression is spec-invalid for background and is guarded at the bind
    /// site (<see cref="MapRenderer.Unity.Rendering.Materials.MaterialFactory.BindBackgroundPaintToApplier"/>),
    /// not here.
    ///
    /// Engine-free; clean-room (public Style Spec, no MapLibre source).
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>background-color: solid fill color for the background layer. Default opaque black
        /// rgba(0,0,0,1).</summary>
        public StyleProperty<Color> Color { get; }

        /// <summary>background-opacity: background alpha multiplier [0,1]. Default 1.0.</summary>
        public StyleProperty<float> Opacity { get; }

        /// <summary>The background-pattern value (sprite name), or null when absent. Parsed but dead
        /// (spec-complete, symmetric with fill-pattern) until sprites land.</summary>
        public string PatternName { get; }

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; }

        /// <summary>Convenience: parse the paint properties from a style layer's <c>PaintJson</c>.</summary>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public PaintProperties(MapRenderer.Core.Style.StyleLayer layer)
            : this((layer ?? throw new System.ArgumentNullException(nameof(layer))).PaintJson) { }

        /// <summary>Parse and classify all background paint properties from the layer's <c>paint</c>
        /// sub-tree (may be null → all spec defaults).</summary>
        public PaintProperties(JsonValue paint)
        {
            bool anyPresent = false;

            // background-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.BackgroundColor);
            if (colorJson != null) anyPresent = true;
            Color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // background-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.BackgroundOpacity);
            if (opacityJson != null) anyPresent = true;
            Opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // background-pattern
            JsonValue patternJson = paint?.Get(PropertyNames.BackgroundPattern);
            if (patternJson != null)
            {
                anyPresent = true;
                PatternName = patternJson.AsString(null);
            }

            IsInertFallback = !anyPresent;
        }
    }
}
