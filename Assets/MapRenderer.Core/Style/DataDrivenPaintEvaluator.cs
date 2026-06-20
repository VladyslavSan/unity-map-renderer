using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// Wraps a parsed paint-property expression that may depend on feature data, zoom, both, or neither.
    /// The sibling of <see cref="PaintPropertyEvaluator"/> for S12 data-driven (per-feature) styling.
    ///
    /// Unlike <see cref="PaintPropertyEvaluator"/> (which rejects Feature/Composite expressions),
    /// this class accepts all four <see cref="ExpressionKind"/>s and passes a full
    /// <see cref="EvaluationContext"/> with both zoom and feature to the evaluator.
    ///
    /// Intended use: per-feature evaluation in the managed decode/tessellation path (MapFillBootstrap,
    /// FeatureColorBaker). Must NOT be used inside the Burst MvtDecodeJob (managed path only).
    ///
    /// Clean-room: semantics follow the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public sealed class DataDrivenPaintEvaluator
    {
        private readonly Expression _expr;

        /// <summary>The classification of the wrapped expression.</summary>
        public ExpressionKind Kind => _expr.Kind;

        /// <summary>
        /// Parse <paramref name="json"/> as a paint-property expression and wrap it.
        /// Accepts all ExpressionKinds (Constant, Zoom, Feature, Composite).
        /// </summary>
        /// <exception cref="ExpressionParseException">If the JSON is not a valid expression.</exception>
        public DataDrivenPaintEvaluator(JsonValue json)
        {
            _expr = ExpressionParser.Parse(json);
        }

        /// <summary>
        /// Parse a raw JSON string as a paint-property expression and wrap it.
        /// </summary>
        /// <exception cref="ExpressionParseException">If the string is not a valid expression.</exception>
        public DataDrivenPaintEvaluator(string json) : this(JsonParser.Parse(json)) { }

        // ---- color evaluation --------------------------------------------------------------------

        /// <summary>
        /// Evaluate the expression as a color given a zoom level and a feature.
        /// Both zoom and feature may be null/zero depending on the expression kind:
        /// a pure-constant expression ignores both; a Composite expression uses both.
        /// </summary>
        /// <param name="zoom">The current map zoom level (used by zoom-dependent stops).</param>
        /// <param name="feature">The feature being styled (used by feature-data ops like ["get",…]).
        ///   May be null for Constant or Zoom expressions.</param>
        /// <exception cref="ExpressionEvaluationException">If evaluation fails or the result is not a color.</exception>
        public Color EvaluateColor(double zoom, IFeature feature)
        {
            var ctx = new EvaluationContext(zoom, feature);
            return _expr.Evaluate(ctx).AsColor();
        }

        // ---- number evaluation -------------------------------------------------------------------

        /// <summary>
        /// Evaluate the expression as a number given a zoom level and a feature.
        /// </summary>
        /// <param name="zoom">The current map zoom level.</param>
        /// <param name="feature">The feature being styled. May be null for Constant or Zoom expressions.</param>
        /// <exception cref="ExpressionEvaluationException">If evaluation fails or the result is not a number.</exception>
        public double EvaluateNumber(double zoom, IFeature feature)
        {
            var ctx = new EvaluationContext(zoom, feature);
            return _expr.Evaluate(ctx).AsNumber();
        }

        // ---- safe evaluation (no throw) ---------------------------------------------------------

        /// <summary>
        /// Try to evaluate the expression as a color. Returns true on success.
        /// On failure (spec error, wrong type), out parameters are set to their defaults.
        /// </summary>
        public bool TryEvaluateColor(double zoom, IFeature feature, out Color result)
        {
            try
            {
                result = EvaluateColor(zoom, feature);
                return true;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        /// <summary>
        /// Try to evaluate the expression as a number. Returns true on success.
        /// </summary>
        public bool TryEvaluateNumber(double zoom, IFeature feature, out double result)
        {
            try
            {
                result = EvaluateNumber(zoom, feature);
                return true;
            }
            catch
            {
                result = 0.0;
                return false;
            }
        }
    }
}
