using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style.Fill
{
    /// <summary>
    /// The parsed MapLibre fill <b>paint</b> properties for a single fill style layer. Each <c>fill-*</c> key
    /// becomes one <see cref="StyleProperty{T}"/>: a parsed <see cref="MapRenderer.Core.Expressions.Expression"/>, a typed
    /// default, and a <c>Value → T</c> projection. Absent properties use the spec defaults, except
    /// <see cref="OutlineColor"/>, which is null when absent.
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>
        /// fill-color: polygon interior fill color. Default opaque black rgba(0,0,0,1), except on a
        /// <c>fill-pattern</c> layer, where it defaults to white: the shader multiplies the sprite by it, so an
        /// absent fill-color leaves the sprite untinted (a departure from the spec, docs/fill-parity-design.md).
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>
        /// fill-opacity: fill alpha multiplier [0,1]. Default 1.0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>
        /// fill-outline-color: stroke color around the fill polygon boundary. Parsed but inert — no shader
        /// pass reads <c>_FillOutlineColor</c> (needs the line geometry docs/fill-parity-design.md § 7
        /// describes). Null when absent, so
        /// <see cref="MapRenderer.Unity.Rendering.Materials.MaterialFactory.BindFillPaintToApplier"/>
        /// leaves the uniform unbound.
        /// </summary>
        public StyleProperty<Color> OutlineColor { get; init; }

        /// <summary>
        /// fill-antialias: whether fill edges are anti-aliased. A bool, like its JSON type: the mesh build
        /// consumes it, and no pass reads the <c>_FillAntialias</c> uniform.
        /// Constant or zoom-varying only. ABSENT, data-driven or malformed ⇒ the <c>antialiasDefault</c>
        /// passed to <see cref="Parse"/> (<c>MapViewConfig.FillAntialiasing</c>; the Style Spec's own default is true).
        /// </summary>
        public StyleProperty<bool> Antialias { get; init; }

        /// <summary>
        /// fill-translate: pixel-space [x, y] translation offset. Default [0, 0], as one <c>double2</c>.
        /// Constant or Zoom; Feature/Composite (data-driven) falls back to the default.
        /// </summary>
        public StyleProperty<double2> Translate { get; init; }

        /// <summary>
        /// fill-translate-anchor: coordinate space for <see cref="Translate"/>. Encoded as float:
        /// 0.0 = "map" (default), 1.0 = "viewport". Constant only.
        /// </summary>
        public StyleProperty<float> TranslateAnchor { get; init; }

        /// <summary>The fill-pattern value (sprite name), or null when absent.</summary>
        public string PatternName { get; init; }

        /// <summary>
        /// How <see cref="PatternName"/>'s sprite is sized as the map zooms. Defaults to
        /// <see cref="FillPatternSizing.ScreenRelative"/> — the Style Spec's meaning, and the only thing a
        /// stock MapLibre style can express. Set to <see cref="FillPatternSizing.WorldAbsolute"/> by the
        /// <c>x-fill-pattern-metres</c> engine extension.
        /// </summary>
        public FillPatternSizing PatternSizing { get; init; }

        /// <summary>The pattern's TILING PERIOD in world units (Web-Mercator metres) under
        /// <see cref="FillPatternSizing.WorldAbsolute"/> — the world distance spanned by one full repetition
        /// of the sprite. 0 otherwise. At a period of 1 the pattern UV advances by 1 per world unit.</summary>
        public double PatternWorldPeriodMetres { get; init; }

        // ── Construction ──────────────────────────────────────────────────────────────────────

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private PaintProperties() { }

        /// <summary>Parse and classify all fill paint properties from a layer's <c>paint</c> sub-tree.</summary>
        /// <param name="paint">The raw <c>paint</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <param name="antialiasDefault">Used for <see cref="Antialias"/> when the key is absent,
        /// data-driven or malformed.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static PaintProperties Parse(JsonValue paint, bool antialiasDefault = true)
        {
            // fill-color: default rgba(0,0,0,1); white on a pattern layer, where it tints the sprite. A pattern
            // layer is one whose fill-pattern is a sprite name, the same test the material flag uses.
            JsonValue patternJson = paint?.Get(PropertyNames.FillPattern);
            string patternName = patternJson?.AsString(null);
            var colorDefault = patternName != null ? new Color(1f, 1f, 1f, 1f) : new Color(0f, 0f, 0f, 1f);
            JsonValue colorJson = paint?.Get(PropertyNames.FillColor);
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, colorDefault, v => v.AsColorCoerced())
                : new StyleProperty<Color>(colorDefault);

            // fill-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.FillOpacity);
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // fill-outline-color: null when absent (see the property doc).
            JsonValue outlineColorJson = paint?.Get(PropertyNames.FillOutlineColor);
            StyleProperty<Color> outlineColor = outlineColorJson != null
                ? new StyleProperty<Color>(outlineColorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : null;

            // fill-antialias: default true (1.0). Tolerates data-driven by falling to default.
            JsonValue antialiasJson = paint?.Get(PropertyNames.FillAntialias);
            StyleProperty<bool> antialias;
            if (antialiasJson != null)
            {
                try
                {
                    // fill-antialias is a JSON boolean: an AsNumber() projection would throw into the catch
                    // below and turn `false` into the default.
                    var candidate = new StyleProperty<bool>(antialiasJson, antialiasDefault, v => v.AsBool(), interpolatable: false);
                    // fill-antialias must not be data-driven (Feature/Composite → the project default)
                    antialias = candidate.DependsOnFeature
                        ? new StyleProperty<bool>(antialiasDefault)
                        : candidate;
                }
                catch
                {
                    antialias = new StyleProperty<bool>(antialiasDefault);
                }
            }
            else
            {
                // The layer said nothing — this is the case the project default exists for, and in the
                // shipped Liberty style it is 12 of 16 fill layers.
                antialias = new StyleProperty<bool>(antialiasDefault);
            }

            // fill-translate: [x, y] px offset, parsed through the expression engine via TranslateProperty.
            JsonValue translateJson = paint?.Get(PropertyNames.FillTranslate);
            StyleProperty<double2> translate = translateJson != null
                ? TranslateProperty.Parse(translateJson)
                : new StyleProperty<double2>(new double2(0.0, 0.0));

            // fill-translate-anchor: "map"→0, "viewport"→1
            JsonValue anchorJson = paint?.Get(PropertyNames.FillTranslateAnchor);
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            StyleProperty<float> translateAnchor = new StyleProperty<float>(anchorVal);

            // x-fill-pattern-metres (engine extension): a present, positive period switches the layer to
            // world-absolute sizing; absent or unusable values keep screen-relative, so it never costs the layer.
            JsonValue patternPeriodJson = paint?.Get(PropertyNames.FillPatternMetres);
            FillPatternSizing patternSizing = FillPatternSizing.ScreenRelative;
            double patternWorldPeriodMetres = 0.0;
            if (patternPeriodJson != null)
            {
                double metres = patternPeriodJson.AsDouble(0.0);
                if (metres > 0.0)
                {
                    patternSizing            = FillPatternSizing.WorldAbsolute;
                    patternWorldPeriodMetres = metres;
                }
            }

            return new PaintProperties
            {
                Color                    = color,
                Opacity                  = opacity,
                OutlineColor             = outlineColor,
                Antialias                = antialias,
                Translate                = translate,
                TranslateAnchor          = translateAnchor,
                PatternName              = patternName,
                PatternSizing            = patternSizing,
                PatternWorldPeriodMetres = patternWorldPeriodMetres,
            };
        }
    }
}
