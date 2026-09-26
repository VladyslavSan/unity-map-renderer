using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The parsed MapLibre root <c>sky</c> block: <c>sky-color</c>, <c>horizon-color</c>,
    /// <c>fog-color</c>. The blend-control keys (<c>sky-horizon-blend</c>, <c>horizon-fog-blend</c>,
    /// <c>fog-ground-blend</c>, <c>atmosphere-blend</c>) are not modeled.
    /// </summary>
    public sealed class StyleSky
    {
        private const string SkyColorKey     = "sky-color";
        private const string HorizonColorKey = "horizon-color";
        private const string FogColorKey     = "fog-color";

        // Spec defaults (https://maplibre.org/maplibre-style-spec/sky/).
        private static readonly Color DefaultSkyColor     = new Color(0x88 / 255.0, 0xC6 / 255.0, 0xFC / 255.0, 1.0);
        private static readonly Color DefaultHorizonColor = new Color(1.0, 1.0, 1.0, 1.0);
        private static readonly Color DefaultFogColor     = new Color(1.0, 1.0, 1.0, 1.0);

        /// <summary>sky-color: base color of the sky dome. Default #88C6FC.</summary>
        public StyleProperty<Color> SkyColor { get; init; }

        /// <summary>horizon-color: base color at the horizon. Default white.</summary>
        public StyleProperty<Color> HorizonColor { get; init; }

        /// <summary>fog-color: base fog color. Default white.</summary>
        public StyleProperty<Color> FogColor { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private StyleSky() { }

        /// <summary>Parse the root <c>sky</c> block. Each color falls back to its spec default when
        /// <paramref name="sky"/> is null, non-object, or the key is absent. A malformed color value
        /// throws <see cref="Expressions.ExpressionEvaluationException"/>, like any paint property.</summary>
        public static StyleSky Parse(JsonValue sky)
        {
            return new StyleSky
            {
                SkyColor     = ParseColor(sky, SkyColorKey, DefaultSkyColor),
                HorizonColor = ParseColor(sky, HorizonColorKey, DefaultHorizonColor),
                FogColor     = ParseColor(sky, FogColorKey, DefaultFogColor),
            };
        }

        private static StyleProperty<Color> ParseColor(JsonValue sky, string key, Color defaultValue)
        {
            JsonValue json = sky?.Get(key);
            if (json == null)
                return new StyleProperty<Color>(defaultValue);
            return new StyleProperty<Color>(json, defaultValue, v => v.AsColorCoerced());
        }
    }
}
