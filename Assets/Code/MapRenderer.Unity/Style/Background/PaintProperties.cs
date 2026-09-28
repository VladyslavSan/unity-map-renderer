using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Unity.Style.Background
{
    /// <summary>
    /// The parsed MapLibre <c>background</c> <b>paint</b> properties for a single background style layer.
    /// Same shape as <see cref="Fill.PaintProperties"/>: one <see cref="StyleProperty{T}"/> per key, spec
    /// defaults when absent. A data-driven expression is spec-invalid for background and is guarded at the bind
    /// site (<see cref="MapRenderer.Unity.Rendering.Materials.MaterialFactory.BindBackgroundPaintToApplier"/>).
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>background-color: solid fill color for the background layer. Default opaque black
        /// rgba(0,0,0,1).</summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>background-opacity: background alpha multiplier [0,1]. Default 1.0.</summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>The background-pattern value (sprite name), or null when absent. Parsed for spec
        /// completeness, but no renderer reads it: background-pattern is not implemented.</summary>
        public string PatternName { get; init; }

        /// <summary>One message per property that fell back to its default. Never null.</summary>
        public IReadOnlyList<string> Errors { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse and classify all background paint properties from a layer's <c>paint</c>
        /// sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint)
        {
            var reader = new PropertyReader("paint");
            StyleProperty<Color> color = reader.ReadProperty(paint, PropertyNames.BackgroundColor,
                new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced());
            StyleProperty<float> opacity = reader.ReadProperty(paint, PropertyNames.BackgroundOpacity, 1f,
                v => (float)v.AsNumber());

            // background-pattern
            JsonValue patternJson = paint?.Get(PropertyNames.BackgroundPattern);
            string patternName = patternJson?.AsString(null);

            return new PaintProperties
            {
                Color       = color,
                Opacity     = opacity,
                PatternName = patternName,
                Errors      = reader.Errors,
            };
        }
    }
}
