using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// S13 aggregator: parses and classifies all MapLibre fill paint properties for a single fill
    /// style layer. Reads each <c>fill-*</c> key from <see cref="StyleLayer.Paint"/> (a raw
    /// <see cref="JsonValue"/> object) and wraps it in the appropriate evaluator:
    ///   • Constant / Zoom → <see cref="PaintPropertyEvaluator"/> (per-frame uniform, no feature data).
    ///   • Feature / Composite → <see cref="DataDrivenPaintEvaluator"/> (per-feature bake, S12 path).
    ///
    /// Absent properties (null JSON) are replaced by the MapLibre Style Spec defaults and flagged
    /// <see cref="IsInertFallback"/> = true, meaning: apply the spec default but do not attempt
    /// per-feature bake (the expression is constant and carries no data-driven information).
    ///
    /// Spec defaults (MapLibre Style Spec §fill layer):
    ///   fill-color         "#000000"  (black)
    ///   fill-opacity       1
    ///   fill-antialias     true (1)
    ///   fill-translate     [0, 0]     (no offset; stored as float4(x,y,0,0))
    ///   fill-translate-anchor "map"   (0)
    ///   fill-outline-color  absent — falls back to fill-color when not set
    ///   fill-pattern        absent — no pattern
    ///
    /// Engine-free: no UnityEngine references. Runs in both dotnet core-tests and Unity EditMode.
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public sealed class FillPaint
    {
        // ── Spec-default JSON literals (used when a property is absent) ──────────
        // Use ["rgba",...] not hex strings — bare JSON strings produce Value.String, not Value.Color.
        private const string DefaultFillColorJson    = "[\"rgba\",0,0,0,1]";
        private const string DefaultFillOpacityJson  = "1";
        private const string DefaultFillAntialiasJson = "1"; // true → 1.0
        private const string DefaultFillTranslateAnchorJson = "0"; // "map" → 0

        // ── Fill color ────────────────────────────────────────────────────────────
        // Constant/Zoom → PaintPropertyEvaluator; Feature/Composite → DataDrivenPaintEvaluator.
        // Both are always set; the caller chooses which to use based on ColorKind.

        /// <summary>Classification of the fill-color expression.</summary>
        public ExpressionKind ColorKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for fill-color. Non-null only when <see cref="ColorKind"/> is
        /// Constant or Zoom. Use <see cref="DataDrivenColor"/> for Feature/Composite.
        /// </summary>
        public PaintPropertyEvaluator Color { get; }

        /// <summary>
        /// Data-driven evaluator for fill-color. Non-null for all ExpressionKinds (accepts all four).
        /// Use for Feature/Composite baking; also valid for Constant/Zoom (evaluates correctly).
        /// </summary>
        public DataDrivenPaintEvaluator DataDrivenColor { get; }

        // ── Fill opacity ──────────────────────────────────────────────────────────

        /// <summary>Classification of the fill-opacity expression.</summary>
        public ExpressionKind OpacityKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for fill-opacity. Non-null when <see cref="OpacityKind"/> is
        /// Constant or Zoom.
        /// </summary>
        public PaintPropertyEvaluator Opacity { get; }

        /// <summary>
        /// Data-driven evaluator for fill-opacity. Non-null for all ExpressionKinds.
        /// </summary>
        public DataDrivenPaintEvaluator DataDrivenOpacity { get; }

        // ── Fill outline color ────────────────────────────────────────────────────

        /// <summary>True when fill-outline-color was absent (falls back to fill-color).</summary>
        public bool OutlineColorIsFallback { get; }

        /// <summary>Classification of the fill-outline-color expression.</summary>
        public ExpressionKind OutlineColorKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for fill-outline-color. Non-null when <see cref="OutlineColorKind"/>
        /// is Constant or Zoom.
        /// </summary>
        public PaintPropertyEvaluator OutlineColor { get; }

        /// <summary>Data-driven evaluator for fill-outline-color.</summary>
        public DataDrivenPaintEvaluator DataDrivenOutlineColor { get; }

        // ── Fill antialias ────────────────────────────────────────────────────────

        /// <summary>Classification of the fill-antialias expression.</summary>
        public ExpressionKind AntialiasKind { get; }

        /// <summary>Constant/Zoom evaluator for fill-antialias (1=true, 0=false).</summary>
        public PaintPropertyEvaluator Antialias { get; }

        // ── Fill translate ────────────────────────────────────────────────────────

        /// <summary>Classification of the fill-translate-x expression.</summary>
        public ExpressionKind TranslateXKind { get; }

        /// <summary>Constant/Zoom evaluator for the x component of fill-translate.</summary>
        public PaintPropertyEvaluator TranslateX { get; }

        /// <summary>Classification of the fill-translate-y expression.</summary>
        public ExpressionKind TranslateYKind { get; }

        /// <summary>Constant/Zoom evaluator for the y component of fill-translate.</summary>
        public PaintPropertyEvaluator TranslateY { get; }

        // ── Fill translate anchor ─────────────────────────────────────────────────

        /// <summary>Classification of fill-translate-anchor expression.</summary>
        public ExpressionKind TranslateAnchorKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for fill-translate-anchor.
        /// Encoded as: 0 = "map" (world-space offset), 1 = "viewport" (screen-space offset).
        /// </summary>
        public PaintPropertyEvaluator TranslateAnchor { get; }

        // ── Fill pattern ──────────────────────────────────────────────────────────

        /// <summary>
        /// The fill-pattern value (a sprite name / string), or null when absent.
        /// Non-null only when fill-pattern is a constant string in the style JSON.
        /// Patterns are raster sprites — not addressed before a pattern stage.
        /// </summary>
        public string PatternName { get; }

        // ── Inert-fallback flag ───────────────────────────────────────────────────

        /// <summary>
        /// True when ALL paint properties were absent from the style layer's paint object (i.e.
        /// every evaluator uses the spec default). Allows the bootstrap to short-circuit to
        /// spec-default rendering without per-feature bakes or per-frame uniform pushes.
        /// </summary>
        public bool IsInertFallback { get; }

        // ── Constructor ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parse and classify all fill paint properties from a style layer.
        /// </summary>
        /// <param name="layer">The fill style layer. Must not be null.</param>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public FillPaint(StyleLayer layer)
        {
            if (layer == null)
                throw new System.ArgumentNullException(nameof(layer));

            var paint = layer.Paint; // may be null
            bool anyPresent = false;

            // ── fill-color ──────────────────────────────────────────────────────
            JsonValue colorJson = paint?.Get("fill-color");
            bool colorPresent = colorJson != null;
            if (!colorPresent) colorJson = JsonParser.Parse(DefaultFillColorJson);
            else anyPresent = true;

            DataDrivenColor = new DataDrivenPaintEvaluator(colorJson);
            ColorKind       = DataDrivenColor.Kind;
            if (!ExpressionKinds.DependsOnFeature(ColorKind))
                Color = new PaintPropertyEvaluator(colorJson);

            // ── fill-opacity ────────────────────────────────────────────────────
            JsonValue opacityJson = paint?.Get("fill-opacity");
            bool opacityPresent = opacityJson != null;
            if (!opacityPresent) opacityJson = JsonParser.Parse(DefaultFillOpacityJson);
            else anyPresent = true;

            DataDrivenOpacity = new DataDrivenPaintEvaluator(opacityJson);
            OpacityKind       = DataDrivenOpacity.Kind;
            if (!ExpressionKinds.DependsOnFeature(OpacityKind))
                Opacity = new PaintPropertyEvaluator(opacityJson);

            // ── fill-outline-color ──────────────────────────────────────────────
            JsonValue outlineColorJson = paint?.Get("fill-outline-color");
            OutlineColorIsFallback = (outlineColorJson == null);
            if (OutlineColorIsFallback) outlineColorJson = colorJson; // fall back to fill-color
            else anyPresent = true;

            DataDrivenOutlineColor = new DataDrivenPaintEvaluator(outlineColorJson);
            OutlineColorKind       = DataDrivenOutlineColor.Kind;
            if (!ExpressionKinds.DependsOnFeature(OutlineColorKind))
                OutlineColor = new PaintPropertyEvaluator(outlineColorJson);

            // ── fill-antialias ──────────────────────────────────────────────────
            JsonValue antialiasJson = paint?.Get("fill-antialias");
            bool antialiasPresent = antialiasJson != null;
            if (!antialiasPresent) antialiasJson = JsonParser.Parse(DefaultFillAntialiasJson);
            else anyPresent = true;

            // fill-antialias is always a boolean/constant — reject data-driven (Feature/Composite)
            // by only constructing PaintPropertyEvaluator (which rejects them at construction).
            // If the style JSON provides a Feature/Composite expression for fill-antialias, we fall
            // back to the spec default to remain tolerant.
            AntialiasKind = ExpressionKind.Constant;
            try
            {
                Antialias = new PaintPropertyEvaluator(antialiasJson);
                AntialiasKind = Antialias.Kind;
            }
            catch
            {
                // Fallback to spec default if parse fails or is data-driven (non-standard).
                Antialias = new PaintPropertyEvaluator(JsonParser.Parse(DefaultFillAntialiasJson));
                AntialiasKind = ExpressionKind.Constant;
            }

            // ── fill-translate ──────────────────────────────────────────────────
            // fill-translate is an array [x, y]. We extract the two components individually.
            double translateXDefault = 0.0;
            double translateYDefault = 0.0;

            JsonValue translateJson = paint?.Get("fill-translate");
            bool translatePresent = translateJson != null;
            if (translatePresent) anyPresent = true;

            double txVal = translateXDefault;
            double tyVal = translateYDefault;
            if (translatePresent && translateJson.IsArray && translateJson.Items.Count >= 2)
            {
                txVal = translateJson.Items[0].AsDouble(0.0);
                tyVal = translateJson.Items[1].AsDouble(0.0);
            }

            // Build constant JSON numbers for the components (always Constant kind).
            JsonValue txJson = JsonParser.Parse(txVal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            JsonValue tyJson = JsonParser.Parse(tyVal.ToString(System.Globalization.CultureInfo.InvariantCulture));

            TranslateX = new PaintPropertyEvaluator(txJson);
            TranslateY = new PaintPropertyEvaluator(tyJson);
            TranslateXKind = TranslateX.Kind;
            TranslateYKind = TranslateY.Kind;

            // ── fill-translate-anchor ───────────────────────────────────────────
            JsonValue anchorJson = paint?.Get("fill-translate-anchor");
            bool anchorPresent = anchorJson != null;
            if (anchorPresent) anyPresent = true;

            // "map" → 0.0, "viewport" → 1.0. Anything else → 0.0 (spec default).
            double anchorVal = 0.0;
            if (anchorPresent)
            {
                string anchorStr = anchorJson.AsString(null);
                anchorVal = (anchorStr == "viewport") ? 1.0 : 0.0;
            }

            JsonValue anchorNumJson = JsonParser.Parse(anchorVal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            TranslateAnchor = new PaintPropertyEvaluator(anchorNumJson);
            TranslateAnchorKind = TranslateAnchor.Kind;

            // ── fill-pattern ────────────────────────────────────────────────────
            JsonValue patternJson = paint?.Get("fill-pattern");
            if (patternJson != null)
            {
                anyPresent = true;
                PatternName = patternJson.AsString(null);
            }

            // ── Inert-fallback flag ─────────────────────────────────────────────
            IsInertFallback = !anyPresent;
        }
    }
}
