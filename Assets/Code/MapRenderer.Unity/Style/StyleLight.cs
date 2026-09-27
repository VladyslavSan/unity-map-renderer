using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;

namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// <c>light.position</c>: placement of the light source relative to lit geometry, as a radial
    /// distance plus two angles. Style Spec order is <c>[radial, azimuthal, polar]</c>; azimuthal and
    /// polar are given in degrees in JSON.
    /// </summary>
    public readonly struct LightPosition
    {
        /// <summary>Radial distance from the center of the reference sphere. Unitless.</summary>
        public double Radial { get; init; }

        /// <summary>Azimuth of the light position, clockwise from north.</summary>
        public Angle Azimuthal { get; init; }

        /// <summary>Polar angle: angle from the zenith (90° = level with the ground).</summary>
        public Angle Polar { get; init; }
    }

    /// <summary>
    /// The parsed MapLibre root <c>light</c> block: <c>position</c>, <c>color</c>, <c>intensity</c>.
    /// Same shape as a layer's paint properties — one <see cref="StyleProperty{T}"/> per key, spec
    /// defaults when absent. <c>light.anchor</c> is ignored: this renderer always lights as
    /// <c>anchor: "map"</c>, a deviation from the spec's own <c>"viewport"</c> default.
    /// </summary>
    public sealed class StyleLight
    {
        private const string PositionKey  = "position";
        private const string ColorKey     = "color";
        private const string IntensityKey = "intensity";

        private static readonly LightPosition DefaultPosition = new LightPosition
        {
            Radial    = 1.15,
            Azimuthal = Angle.FromDegrees(210.0),
            Polar     = Angle.FromDegrees(30.0),
        };

        private static readonly Color DefaultColor = new Color(1.0, 1.0, 1.0, 1.0);
        private const float DefaultIntensity = 0.5f;

        /// <summary>light-position. Default [1.15, 210°, 30°]. Constant only (array-typed, like
        /// <see cref="Fill.PaintProperties.Translate"/>) — a non-array or short value falls back to the
        /// default, same as a malformed <see cref="Fill.PaintProperties.Translate"/>.</summary>
        public StyleProperty<LightPosition> Position { get; init; }

        /// <summary>light-color. Default white. A malformed OR data-driven value throws at parse
        /// (see <see cref="Parse"/>).</summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>light-intensity: brightness multiplier [0,1]. Default 0.5. A malformed OR data-driven
        /// value throws at parse (see <see cref="Parse"/>).</summary>
        public StyleProperty<float> Intensity { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private StyleLight() { }

        /// <summary>Parse the root <c>light</c> block. <see cref="Position"/> falls back to its spec
        /// default when malformed; <see cref="Color"/> and <see cref="Intensity"/> throw
        /// <see cref="MapRenderer.Core.Expressions.ExpressionEvaluationException"/> on a malformed OR
        /// data-driven value. Every property falls back to its spec default when <paramref name="light"/>
        /// is null, non-object, or the key is absent.</summary>
        public static StyleLight Parse(JsonValue light)
        {
            LightPosition position = DefaultPosition;
            JsonValue positionJson = light?.Get(PositionKey);
            if (positionJson != null && positionJson.IsArray && positionJson.Items.Count >= 3)
            {
                var items = positionJson.Items;
                position = new LightPosition
                {
                    Radial    = items[0].AsDouble(DefaultPosition.Radial),
                    Azimuthal = Angle.FromDegrees(items[1].AsDouble(DefaultPosition.Azimuthal.Degrees)),
                    Polar     = Angle.FromDegrees(items[2].AsDouble(DefaultPosition.Polar.Degrees)),
                };
            }

            JsonValue colorJson = light?.Get(ColorKey);
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, DefaultColor, v => v.AsColorCoerced())
                : new StyleProperty<Color>(DefaultColor);
            RejectDataDriven(color, ColorKey);

            JsonValue intensityJson = light?.Get(IntensityKey);
            StyleProperty<float> intensity = intensityJson != null
                ? new StyleProperty<float>(intensityJson, DefaultIntensity, v => (float)v.AsNumber())
                : new StyleProperty<float>(DefaultIntensity);
            RejectDataDriven(intensity, IntensityKey);

            return new StyleLight
            {
                Position  = new StyleProperty<LightPosition>(position),
                Color     = color,
                Intensity = intensity,
            };
        }

        /// <summary>Fails the parse when <paramref name="property"/> is data-driven.
        /// <see cref="MapRenderer.Unity.Rendering.Map.SunLight"/> calls <see cref="StyleProperty{T}.Evaluate(double)"/>,
        /// which throws for Feature/Composite anyway — this surfaces the same failure at parse time.</summary>
        private static void RejectDataDriven<T>(StyleProperty<T> property, string key)
        {
            if (property.DependsOnFeature)
                throw new ExpressionEvaluationException($"light.{key} may not be data-driven.");
        }
    }
}
