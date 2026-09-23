using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// The parsed MapLibre fill <b>paint</b> properties for a single fill style layer.
    ///
    /// Each <c>fill-*</c> key is read from the layer's <c>paint</c> sub-tree (via
    /// <see cref="PropertyNames"/>) and collapsed into a single <see cref="StyleProperty{T}"/>:
    /// one parsed <see cref="Expressions.Expression"/>, one typed default, and a
    /// <c>Value → T</c> projection; <see cref="StyleProperty{T}.Kind"/> reads the expression's kind
    /// directly, with no separate Kind/evaluator plumbing.
    ///
    /// Absent properties use the spec defaults; <see cref="IsInertFallback"/> is true when all are absent.
    /// Engine-free; clean-room (public Style Spec, no MapLibre source).
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>
        /// fill-color: polygon interior fill color. Default opaque black rgba(0,0,0,1).
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<Color> Color { get; init; }

        /// <summary>
        /// fill-opacity: fill alpha multiplier [0,1]. Default 1.0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
        /// </summary>
        public StyleProperty<float> Opacity { get; init; }

        /// <summary>True when fill-outline-color was absent (outline defaults to fill-color value).</summary>
        public bool OutlineColorIsFallback { get; init; }

        /// <summary>
        /// fill-outline-color: stroke color around the fill polygon boundary. When absent, falls
        /// back to <see cref="Color"/> (same expression, same evaluation). Default: same as fill-color.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake.
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
        /// Constant only.
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

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; init; }

        // ── Convenience accessors matching the historical PaintPropertyEvaluator API ──────────

        /// <summary>Classification of the fill-color expression.</summary>
        public ExpressionKind ColorKind => Color.Kind;

        /// <summary>Classification of the fill-opacity expression.</summary>
        public ExpressionKind OpacityKind => Opacity.Kind;

        /// <summary>Classification of the fill-outline-color expression.</summary>
        public ExpressionKind OutlineColorKind => OutlineColor.Kind;

        /// <summary>Classification of the fill-antialias expression.</summary>
        public ExpressionKind AntialiasKind => Antialias.Kind;

        /// <summary>Classification of the fill-translate expression.</summary>
        public ExpressionKind TranslateKind => Translate.Kind;

        /// <summary>Classification of the fill-translate-anchor expression.</summary>
        public ExpressionKind TranslateAnchorKind => TranslateAnchor.Kind;

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
            bool anyPresent = false;

            // fill-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.FillColor);
            if (colorJson != null) anyPresent = true;
            StyleProperty<Color> color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // fill-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.FillOpacity);
            if (opacityJson != null) anyPresent = true;
            StyleProperty<float> opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // fill-outline-color: absent → fallback to fill-color's JsonValue (same expression)
            JsonValue outlineColorJson = paint?.Get(PropertyNames.FillOutlineColor);
            bool outlineColorIsFallback = (outlineColorJson == null);
            if (!outlineColorIsFallback) anyPresent = true;
            JsonValue outlineSource = outlineColorIsFallback ? colorJson : outlineColorJson;
            StyleProperty<Color> outlineColor = outlineSource != null
                ? new StyleProperty<Color>(outlineSource, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // fill-antialias: default true (1.0). Tolerates data-driven by falling to default.
            JsonValue antialiasJson = paint?.Get(PropertyNames.FillAntialias);
            if (antialiasJson != null) anyPresent = true;
            StyleProperty<bool> antialias;
            if (antialiasJson != null)
            {
                try
                {
                    // fill-antialias is a JSON BOOLEAN. Projecting it through AsNumber() threw
                    // ExpressionEvaluationException into the catch below, so `false` silently became the
                    // default 1 — the property parsed as its own opposite.
                    var candidate = new StyleProperty<bool>(antialiasJson, antialiasDefault, v => v.AsBool());
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

            // fill-translate: [x, y] — collapse to StyleProperty<double2>
            JsonValue translateJson = paint?.Get(PropertyNames.FillTranslate);
            if (translateJson != null) anyPresent = true;
            double txVal = 0.0, tyVal = 0.0;
            if (translateJson != null && translateJson.IsArray && translateJson.Items.Count >= 2)
            {
                txVal = translateJson.Items[0].AsDouble(0.0);
                tyVal = translateJson.Items[1].AsDouble(0.0);
            }
            StyleProperty<double2> translate = new StyleProperty<double2>(new double2(txVal, tyVal));

            // fill-translate-anchor: "map"→0, "viewport"→1
            JsonValue anchorJson = paint?.Get(PropertyNames.FillTranslateAnchor);
            if (anchorJson != null) anyPresent = true;
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            StyleProperty<float> translateAnchor = new StyleProperty<float>(anchorVal);

            // fill-pattern
            JsonValue patternJson = paint?.Get(PropertyNames.FillPattern);
            if (patternJson != null) anyPresent = true;
            string patternName = patternJson?.AsString(null);

            // x-fill-pattern-metres (ENGINE EXTENSION, not spec): the pattern's tiling period in world
            // units — the distance one full repetition spans — which switches this layer to world-absolute
            // sizing. Absent — the normal case, and every stock MapLibre
            // style — leaves the spec's screen-relative behaviour. A present-but-unusable value (non-numeric,
            // zero, negative) also falls back rather than throwing, matching this parser's forward-compatible
            // posture everywhere else: an unreadable extension must never cost you the layer.
            JsonValue patternPeriodJson = paint?.Get(PropertyNames.FillPatternMetres);
            FillPatternSizing patternSizing = FillPatternSizing.ScreenRelative;
            double patternWorldPeriodMetres = 0.0;
            if (patternPeriodJson != null)
            {
                anyPresent = true;
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
                OutlineColorIsFallback   = outlineColorIsFallback,
                OutlineColor             = outlineColor,
                Antialias                = antialias,
                Translate                = translate,
                TranslateAnchor          = translateAnchor,
                PatternName              = patternName,
                PatternSizing            = patternSizing,
                PatternWorldPeriodMetres = patternWorldPeriodMetres,
                IsInertFallback          = !anyPresent,
            };
        }
    }
}
