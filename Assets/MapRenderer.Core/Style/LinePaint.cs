using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// S14 aggregator: parses and classifies all MapLibre line paint and layout properties for
    /// a single line style layer. Reads each <c>line-*</c> key from <see cref="StyleLayer.Paint"/>
    /// (and <see cref="StyleLayer.Layout"/> for join/cap/miter/round) and wraps it in the
    /// appropriate evaluator:
    ///   • Constant / Zoom → <see cref="PaintPropertyEvaluator"/> (per-frame uniform, no feature data).
    ///   • Feature / Composite → <see cref="DataDrivenPaintEvaluator"/> (per-feature bake, S12 path).
    ///
    /// Absent properties are replaced by the MapLibre Style Spec defaults and flagged
    /// <see cref="IsInertFallback"/> = true when ALL properties are absent.
    ///
    /// Spec defaults (MapLibre Style Spec §line layer):
    ///   line-color         "#000000"  (black)
    ///   line-opacity       1
    ///   line-width         1
    ///   line-blur          0
    ///   line-gap-width     0
    ///   line-translate     [0, 0]     (no offset)
    ///   line-translate-anchor "map"   (0 = world-space)
    ///   line-join          "miter"
    ///   line-cap           "butt"
    ///   line-miter-limit   2
    ///   line-round-limit   1.05
    ///   line-dasharray     absent (null → solid line, _DashCount=0 identity)
    ///   line-offset        0          (no perpendicular shift)
    ///   line-pattern       absent (null)
    ///
    /// Engine-free: no UnityEngine references. Runs in both dotnet core-tests and Unity EditMode.
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public sealed class LinePaint
    {
        // ── Spec-default JSON literals ─────────────────────────────────────────────
        private const string DefaultLineColorJson     = "[\"rgba\",0,0,0,1]";
        private const string DefaultLineOpacityJson   = "1";
        private const string DefaultLineWidthJson     = "1";
        private const string DefaultLineBlurJson      = "0";
        private const string DefaultLineGapWidthJson  = "0";
        private const string DefaultLineOffsetJson    = "0";

        // ── line-color ────────────────────────────────────────────────────────────

        /// <summary>Classification of the line-color expression.</summary>
        public ExpressionKind ColorKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for line-color. Non-null when <see cref="ColorKind"/> is
        /// Constant or Zoom.
        /// </summary>
        public PaintPropertyEvaluator Color { get; }

        /// <summary>
        /// Data-driven evaluator for line-color. Non-null for all ExpressionKinds.
        /// Use for Feature/Composite baking; also valid for Constant/Zoom.
        /// </summary>
        public DataDrivenPaintEvaluator DataDrivenColor { get; }

        // ── line-opacity ──────────────────────────────────────────────────────────

        /// <summary>Classification of the line-opacity expression.</summary>
        public ExpressionKind OpacityKind { get; }

        /// <summary>Constant/Zoom evaluator for line-opacity.</summary>
        public PaintPropertyEvaluator Opacity { get; }

        /// <summary>Data-driven evaluator for line-opacity.</summary>
        public DataDrivenPaintEvaluator DataDrivenOpacity { get; }

        // ── line-width ────────────────────────────────────────────────────────────

        /// <summary>Classification of the line-width expression.</summary>
        public ExpressionKind WidthKind { get; }

        /// <summary>Constant/Zoom evaluator for line-width (in pixels).</summary>
        public PaintPropertyEvaluator Width { get; }

        /// <summary>Data-driven evaluator for line-width.</summary>
        public DataDrivenPaintEvaluator DataDrivenWidth { get; }

        // ── line-blur ─────────────────────────────────────────────────────────────

        /// <summary>Classification of the line-blur expression.</summary>
        public ExpressionKind BlurKind { get; }

        /// <summary>Constant/Zoom evaluator for line-blur.</summary>
        public PaintPropertyEvaluator Blur { get; }

        // ── line-gap-width ────────────────────────────────────────────────────────

        /// <summary>Classification of the line-gap-width expression.</summary>
        public ExpressionKind GapWidthKind { get; }

        /// <summary>Constant/Zoom evaluator for line-gap-width (in pixels).</summary>
        public PaintPropertyEvaluator GapWidth { get; }

        // ── line-translate ────────────────────────────────────────────────────────

        /// <summary>Classification of the line-translate-x expression.</summary>
        public ExpressionKind TranslateXKind { get; }

        /// <summary>Constant/Zoom evaluator for the x component of line-translate.</summary>
        public PaintPropertyEvaluator TranslateX { get; }

        /// <summary>Classification of the line-translate-y expression.</summary>
        public ExpressionKind TranslateYKind { get; }

        /// <summary>Constant/Zoom evaluator for the y component of line-translate.</summary>
        public PaintPropertyEvaluator TranslateY { get; }

        // ── line-translate-anchor ─────────────────────────────────────────────────

        /// <summary>Classification of the line-translate-anchor expression.</summary>
        public ExpressionKind TranslateAnchorKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for line-translate-anchor.
        /// Encoded as: 0 = "map" (world-space offset), 1 = "viewport" (screen-space offset).
        /// </summary>
        public PaintPropertyEvaluator TranslateAnchor { get; }

        // ── Layout: join / cap / limits (baked at tessellate time, D4) ───────────

        /// <summary>
        /// line-join value parsed from layout. "miter", "round", "bevel".
        /// Used to select JoinType when tessellating. Default "miter".
        /// </summary>
        public string LineJoin { get; }

        /// <summary>
        /// line-cap value parsed from layout. "butt", "round", "square".
        /// Used to select CapType when tessellating. Default "butt".
        /// </summary>
        public string LineCap { get; }

        /// <summary>
        /// line-miter-limit from layout. Default 2.
        /// Passed to LineTessellator to control miter join clipping.
        /// </summary>
        public double MiterLimit { get; }

        /// <summary>
        /// line-round-limit from layout. Default 1.05.
        /// Passed to LineTessellator to control round join threshold.
        /// </summary>
        public double RoundLimit { get; }

        // ── line-offset (S44) ────────────────────────────────────────────────────

        /// <summary>Classification of the line-offset expression.</summary>
        public ExpressionKind OffsetKind { get; }

        /// <summary>
        /// Constant/Zoom evaluator for line-offset (signed pixels; positive = left of travel).
        /// Non-null for Constant/Zoom kind. Spec default: 0 (no shift).
        /// The world-space displacement mirrors the same px→m path as <see cref="Width"/>.
        /// </summary>
        public PaintPropertyEvaluator Offset { get; }

        // ── line-dasharray (S43) ──────────────────────────────────────────────────

        /// <summary>
        /// True when a <c>line-dasharray</c> property was present in the style layer's paint.
        /// When false, <see cref="DashArrayJson"/> is null and the line renders solid.
        /// </summary>
        public bool HasDashArray { get; }

        /// <summary>
        /// The raw JSON value for <c>line-dasharray</c>. May be a constant array or a zoom-step
        /// expression. Null when absent. Use <see cref="LineDash.TryEvaluateDashArray"/> to
        /// evaluate at a given zoom.
        /// <!-- S43: constant + zoom-step forms supported; feature-dependent dasharray is out of scope. -->
        /// </summary>
        public JsonValue DashArrayJson { get; }

        // ── line-pattern (hook only — S17) ────────────────────────────────────────

        /// <summary>
        /// The line-pattern value (sprite name / string), or null when absent.
        /// When non-null, the renderer falls back to solid line-color until S17.
        /// <!-- S14_LINE_PATTERN_HOOK: parse+plumb only, no sprite sampling. Fallback to solid _MapColor. -->
        /// </summary>
        public string PatternName { get; }

        // ── Inert-fallback flag ───────────────────────────────────────────────────

        /// <summary>
        /// True when ALL paint properties were absent from the style layer (every evaluator uses
        /// the spec default). Allows short-circuit rendering without per-feature bakes.
        /// </summary>
        public bool IsInertFallback { get; }

        // ── Constructor ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parse and classify all line paint/layout properties from a style layer.
        /// </summary>
        /// <param name="layer">The line style layer. Must not be null.</param>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public LinePaint(StyleLayer layer)
        {
            if (layer == null)
                throw new System.ArgumentNullException(nameof(layer));

            var paint  = layer.Paint;   // may be null
            var layout = layer.Layout;  // may be null
            bool anyPresent = false;

            // ── line-color ──────────────────────────────────────────────────────
            JsonValue colorJson = paint?.Get("line-color");
            bool colorPresent = colorJson != null;
            if (!colorPresent) colorJson = JsonParser.Parse(DefaultLineColorJson);
            else anyPresent = true;

            DataDrivenColor = new DataDrivenPaintEvaluator(colorJson);
            ColorKind       = DataDrivenColor.Kind;
            if (!ExpressionKinds.DependsOnFeature(ColorKind))
                Color = new PaintPropertyEvaluator(colorJson);

            // ── line-opacity ────────────────────────────────────────────────────
            JsonValue opacityJson = paint?.Get("line-opacity");
            bool opacityPresent = opacityJson != null;
            if (!opacityPresent) opacityJson = JsonParser.Parse(DefaultLineOpacityJson);
            else anyPresent = true;

            DataDrivenOpacity = new DataDrivenPaintEvaluator(opacityJson);
            OpacityKind       = DataDrivenOpacity.Kind;
            if (!ExpressionKinds.DependsOnFeature(OpacityKind))
                Opacity = new PaintPropertyEvaluator(opacityJson);

            // ── line-width ──────────────────────────────────────────────────────
            JsonValue widthJson = paint?.Get("line-width");
            bool widthPresent = widthJson != null;
            if (!widthPresent) widthJson = JsonParser.Parse(DefaultLineWidthJson);
            else anyPresent = true;

            DataDrivenWidth = new DataDrivenPaintEvaluator(widthJson);
            WidthKind       = DataDrivenWidth.Kind;
            if (!ExpressionKinds.DependsOnFeature(WidthKind))
                Width = new PaintPropertyEvaluator(widthJson);

            // ── line-blur ───────────────────────────────────────────────────────
            JsonValue blurJson = paint?.Get("line-blur");
            bool blurPresent = blurJson != null;
            if (!blurPresent) blurJson = JsonParser.Parse(DefaultLineBlurJson);
            else anyPresent = true;

            // blur is not typically data-driven; treat as constant/zoom only.
            Blur = new PaintPropertyEvaluator(blurJson);
            BlurKind = Blur.Kind;

            // ── line-gap-width ──────────────────────────────────────────────────
            JsonValue gapWidthJson = paint?.Get("line-gap-width");
            bool gapWidthPresent = gapWidthJson != null;
            if (!gapWidthPresent) gapWidthJson = JsonParser.Parse(DefaultLineGapWidthJson);
            else anyPresent = true;

            GapWidth = new PaintPropertyEvaluator(gapWidthJson);
            GapWidthKind = GapWidth.Kind;

            // ── line-offset (S44) ───────────────────────────────────────────────
            // Signed pixel shift perpendicular to the centerline. Constant + zoom-interpolate.
            // Positive = left of travel direction. Default 0 (no shift).
            // Feature-dependent offset is out of scope; treat as constant/zoom only (mirrors gap-width).
            JsonValue offsetJson = paint?.Get("line-offset");
            bool offsetPresent = offsetJson != null;
            if (!offsetPresent) offsetJson = JsonParser.Parse(DefaultLineOffsetJson);
            else anyPresent = true;

            Offset = new PaintPropertyEvaluator(offsetJson);
            OffsetKind = Offset.Kind;

            // ── line-translate ──────────────────────────────────────────────────
            // line-translate is an array [x, y]. Extract the two components individually.
            JsonValue translateJson = paint?.Get("line-translate");
            bool translatePresent = translateJson != null;
            if (translatePresent) anyPresent = true;

            double txVal = 0.0;
            double tyVal = 0.0;
            if (translatePresent && translateJson.IsArray && translateJson.Items.Count >= 2)
            {
                txVal = translateJson.Items[0].AsDouble(0.0);
                tyVal = translateJson.Items[1].AsDouble(0.0);
            }

            JsonValue txJson = JsonParser.Parse(txVal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            JsonValue tyJson = JsonParser.Parse(tyVal.ToString(System.Globalization.CultureInfo.InvariantCulture));

            TranslateX = new PaintPropertyEvaluator(txJson);
            TranslateY = new PaintPropertyEvaluator(tyJson);
            TranslateXKind = TranslateX.Kind;
            TranslateYKind = TranslateY.Kind;

            // ── line-translate-anchor ───────────────────────────────────────────
            JsonValue anchorJson = paint?.Get("line-translate-anchor");
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

            // ── Layout: line-join, line-cap, line-miter-limit, line-round-limit ─
            // These are baked at tessellate time (D4). Read from the layout sub-tree.
            LineJoin  = layout?.Get("line-join")?.AsString("miter")  ?? "miter";
            LineCap   = layout?.Get("line-cap")?.AsString("butt")    ?? "butt";
            MiterLimit = layout?.Get("line-miter-limit")?.AsDouble(2.0) ?? 2.0;
            RoundLimit = layout?.Get("line-round-limit")?.AsDouble(1.05) ?? 1.05;

            // ── line-dasharray (S43) ───────────────────────────────────────────
            // Constant array or zoom-step array expression. Evaluated per-frame in BindLinePaintToApplier
            // via LineDash.TryEvaluateDashArray. Feature-dependent dasharray is out of scope for S43.
            JsonValue dashArrayJson = paint?.Get("line-dasharray");
            if (dashArrayJson != null)
            {
                anyPresent   = true;
                HasDashArray = true;
                DashArrayJson = dashArrayJson;
            }

            // ── line-pattern (hook only) ────────────────────────────────────────
            // S14_LINE_PATTERN_HOOK: parse+plumb only; fallback to solid _MapColor until S17.
            JsonValue patternJson = paint?.Get("line-pattern");
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
