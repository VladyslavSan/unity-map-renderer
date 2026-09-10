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
    /// <c>Value → T</c> projection. The old triple
    /// (<c>XKind</c> + <c>PaintPropertyEvaluator X</c> + <c>DataDrivenPaintEvaluator DataDrivenX</c>)
    /// is gone; <see cref="StyleProperty{T}.Kind"/> reads the expression's kind directly.
    ///
    /// Absent properties use the spec defaults; <see cref="IsInertFallback"/> is true when all are absent.
    /// Engine-free; clean-room (public Style Spec, no MapLibre source).
    /// </summary>
    public sealed class PaintProperties
    {
        /// <summary>
        /// fill-color: polygon interior fill color. Default opaque black rgba(0,0,0,1).
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake (S12).
        /// </summary>
        public StyleProperty<Color> Color { get; }

        /// <summary>
        /// fill-opacity: fill alpha multiplier [0,1]. Default 1.0.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake (S12).
        /// </summary>
        public StyleProperty<float> Opacity { get; }

        /// <summary>True when fill-outline-color was absent (outline defaults to fill-color value).</summary>
        public bool OutlineColorIsFallback { get; }

        /// <summary>
        /// fill-outline-color: stroke color around the fill polygon boundary. When absent, falls
        /// back to <see cref="Color"/> (same expression, same evaluation). Default: same as fill-color.
        /// Constant/Zoom → material uniform; Feature/Composite → per-vertex bake (S12).
        /// </summary>
        public StyleProperty<Color> OutlineColor { get; }

        /// <summary>
        /// fill-antialias: whether fill edges are anti-aliased. A JSON boolean, so a bool — it used to be
        /// float-encoded only to feed the <c>_FillAntialias</c> uniform, which no pass reads.
        /// Constant or zoom-varying only. ABSENT, data-driven or malformed ⇒ the project default the ctor
        /// was given (<c>MapViewConfig.FillAntialiasing</c>; the Style Spec's own default is true).
        /// </summary>
        public StyleProperty<bool> Antialias { get; }



        /// <summary>
        /// fill-translate: pixel-space [x, y] translation offset. Default [0, 0].
        /// Collapsed from the old TranslateX/Y/XKind/YKind quad into one <c>double2</c>.
        /// Constant only.
        /// </summary>
        public StyleProperty<double2> Translate { get; }

        /// <summary>
        /// fill-translate-anchor: coordinate space for <see cref="Translate"/>. Encoded as float:
        /// 0.0 = "map" (default), 1.0 = "viewport". Constant only.
        /// </summary>
        public StyleProperty<float> TranslateAnchor { get; }

        /// <summary>The fill-pattern value (sprite name), or null when absent.</summary>
        public string PatternName { get; }

        /// <summary>
        /// How <see cref="PatternName"/>'s sprite is sized as the map zooms. Defaults to
        /// <see cref="FillPatternSizing.ScreenRelative"/> — the Style Spec's meaning, and the only thing a
        /// stock MapLibre style can express. Set to <see cref="FillPatternSizing.WorldAbsolute"/> by the
        /// <c>x-fill-pattern-metres</c> engine extension.
        /// </summary>
        public FillPatternSizing PatternSizing { get; }

        /// <summary>The pattern's TILING PERIOD in world units (Web-Mercator metres) under
        /// <see cref="FillPatternSizing.WorldAbsolute"/> — the world distance spanned by one full repetition
        /// of the sprite. 0 otherwise. At a period of 1 the pattern UV advances by 1 per world unit.</summary>
        public double PatternWorldPeriodMetres { get; }

        /// <summary>True when ALL paint properties were absent (every property uses the spec default).</summary>
        public bool IsInertFallback { get; }

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

        // ── Constructors ──────────────────────────────────────────────────────────────────────

        /// <summary>Convenience: parse the paint properties from a style layer's <c>PaintJson</c>.</summary>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public PaintProperties(MapRenderer.Core.Style.StyleLayer layer)
            : this((layer ?? throw new System.ArgumentNullException(nameof(layer))).PaintJson) { }

        /// <summary>Parse and classify all fill paint properties from the layer's <c>paint</c> sub-tree
        /// (may be null → all spec defaults).</summary>
        public PaintProperties(JsonValue paint, bool antialiasDefault = true)
        {
            bool anyPresent = false;

            // fill-color: default rgba(0,0,0,1)
            JsonValue colorJson = paint?.Get(PropertyNames.FillColor);
            if (colorJson != null) anyPresent = true;
            Color = colorJson != null
                ? new StyleProperty<Color>(colorJson, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced())
                : new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // fill-opacity: default 1.0
            JsonValue opacityJson = paint?.Get(PropertyNames.FillOpacity);
            if (opacityJson != null) anyPresent = true;
            Opacity = opacityJson != null
                ? new StyleProperty<float>(opacityJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            // fill-outline-color: absent → fallback to fill-color's JsonValue (same expression)
            JsonValue outlineColorJson = paint?.Get(PropertyNames.FillOutlineColor);
            OutlineColorIsFallback = (outlineColorJson == null);
            if (!OutlineColorIsFallback) anyPresent = true;
            JsonValue outlineSource = OutlineColorIsFallback ? colorJson : outlineColorJson;
            if (outlineSource != null)
                OutlineColor = new StyleProperty<Color>(outlineSource, new Color(0f, 0f, 0f, 1f), v => v.AsColorCoerced());
            else
                OutlineColor = new StyleProperty<Color>(new Color(0f, 0f, 0f, 1f));

            // fill-antialias: default true (1.0). Tolerates data-driven by falling to default.
            JsonValue antialiasJson = paint?.Get(PropertyNames.FillAntialias);
            if (antialiasJson != null) anyPresent = true;
            if (antialiasJson != null)
            {
                try
                {
                    // fill-antialias is a JSON BOOLEAN. Projecting it through AsNumber() threw
                    // ExpressionEvaluationException into the catch below, so `false` silently became the
                    // default 1 — the property parsed as its own opposite.
                    var candidate = new StyleProperty<bool>(antialiasJson, antialiasDefault, v => v.AsBool());
                    // fill-antialias must not be data-driven (Feature/Composite → default 1.0)
                    // fill-antialias must not be data-driven (Feature/Composite → the project default)
                    Antialias = candidate.DependsOnFeature
                        ? new StyleProperty<bool>(antialiasDefault)
                        : candidate;
                }
                catch
                {
                    Antialias = new StyleProperty<bool>(antialiasDefault);
                }
            }
            else
            {
                // The layer said nothing — this is the case the project default exists for, and in the
                // shipped Liberty style it is 12 of 16 fill layers.
                Antialias = new StyleProperty<bool>(antialiasDefault);
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
            Translate = new StyleProperty<double2>(new double2(txVal, tyVal));

            // fill-translate-anchor: "map"→0, "viewport"→1
            JsonValue anchorJson = paint?.Get(PropertyNames.FillTranslateAnchor);
            if (anchorJson != null) anyPresent = true;
            float anchorVal = (anchorJson != null && anchorJson.AsString(null) == "viewport") ? 1.0f : 0.0f;
            TranslateAnchor = new StyleProperty<float>(anchorVal);

            // fill-pattern
            JsonValue patternJson = paint?.Get(PropertyNames.FillPattern);
            if (patternJson != null)
            {
                anyPresent = true;
                PatternName = patternJson.AsString(null);
            }

            // x-fill-pattern-metres (ENGINE EXTENSION, not spec): the pattern's tiling period in world
            // units — the distance one full repetition spans — which switches this layer to world-absolute
            // sizing. Absent — the normal case, and every stock MapLibre
            // style — leaves the spec's screen-relative behaviour. A present-but-unusable value (non-numeric,
            // zero, negative) also falls back rather than throwing, matching this parser's forward-compatible
            // posture everywhere else: an unreadable extension must never cost you the layer.
            JsonValue patternPeriodJson = paint?.Get(PropertyNames.FillPatternMetres);
            if (patternPeriodJson != null)
            {
                anyPresent = true;
                double metres = patternPeriodJson.AsDouble(0.0);
                if (metres > 0.0)
                {
                    PatternSizing          = FillPatternSizing.WorldAbsolute;
                    PatternWorldPeriodMetres = metres;
                }
            }

            IsInertFallback = !anyPresent;
        }
    }
}
