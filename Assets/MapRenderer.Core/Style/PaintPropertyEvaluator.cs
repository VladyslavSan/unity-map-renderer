using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// Wraps a parsed paint-property expression (e.g. <c>line-width</c>, <c>line-color</c>,
    /// <c>line-opacity</c>, <c>fill-color</c>, <c>fill-opacity</c>) and exposes typed evaluation for
    /// the S11 per-frame uniform applier.
    ///
    /// Supports <see cref="ExpressionKind.Constant"/> and <see cref="ExpressionKind.Zoom"/> expressions
    /// only — the subset suitable for material uniforms evaluated per frame without feature data.
    /// <see cref="ExpressionKind.Feature"/> and <see cref="ExpressionKind.Composite"/> expressions are
    /// rejected at construction with an explicit "data-driven paint deferred to S12" message, so a
    /// caller that mistakenly supplies a data-driven property gets a clear error rather than a silent
    /// mis-evaluation against a null feature.
    ///
    /// Constant-kind expressions are evaluated once at construction and cached — their value never
    /// changes, so per-frame evaluation is a field read.
    ///
    /// Clean-room: semantics follow the MapLibre Style Spec "paint" section.  No MapLibre source read.
    /// </summary>
    public sealed class PaintPropertyEvaluator
    {
        private readonly Expression _expr;

        // Cached result for Constant-kind expressions (avoids re-evaluating every frame).
        private readonly bool _isConstant;
        private readonly double _constantNumber;
        private readonly Color _constantColor;

        /// <summary>The classification of the wrapped expression.</summary>
        public ExpressionKind Kind => _expr.Kind;

        /// <summary>True when the expression depends on zoom and must be re-evaluated per frame.</summary>
        public bool IsZoomDependent => ExpressionKinds.DependsOnZoom(_expr.Kind);

        /// <summary>
        /// Parse <paramref name="json"/> (a <see cref="JsonValue"/> representing a paint property value)
        /// as an expression and wrap it.  The JSON may be a bare literal number / string / color, or a
        /// full expression array.
        /// </summary>
        /// <exception cref="ExpressionParseException">If the JSON is not a valid expression.</exception>
        /// <exception cref="System.ArgumentException">
        /// If the expression is Feature- or Composite-kind (data-driven properties are deferred to S12).
        /// </exception>
        public PaintPropertyEvaluator(JsonValue json)
        {
            _expr = ExpressionParser.Parse(json);
            RejectDataDriven(_expr);
            _isConstant = (_expr.Kind == ExpressionKind.Constant);
            if (_isConstant)
            {
                var ctx = new EvaluationContext(0.0, null);
                // Try to pre-cache both types; which one is used depends on the caller.
                // Errors at this point are forwarded as-is (malformed constant).
                Value v = _expr.Evaluate(ctx);
                if (v.Type == ValueType.Number)
                    _constantNumber = v.AsNumber();
                else if (v.Type == ValueType.Color)
                    _constantColor = v.AsColor();
                // Null/string/bool: caller will evaluate and handle as needed.
            }
        }

        /// <summary>
        /// Parse a raw JSON string as a paint expression and wrap it.
        /// </summary>
        /// <exception cref="ExpressionParseException">If the string is not a valid expression.</exception>
        public PaintPropertyEvaluator(string json) : this(JsonParser.Parse(json)) { }

        // ---- numeric evaluation ------------------------------------------------------------------

        /// <summary>
        /// Evaluate the expression as a number at the given zoom level.  Constant expressions return
        /// the cached value; Zoom expressions evaluate against the supplied zoom.
        /// </summary>
        /// <exception cref="ExpressionEvaluationException">If the expression does not produce a number.</exception>
        public double EvaluateNumber(double zoom)
        {
            if (_isConstant) return _constantNumber;
            return _expr.Evaluate(new EvaluationContext(zoom, null)).AsNumber();
        }

        // ---- color evaluation --------------------------------------------------------------------

        /// <summary>
        /// Evaluate the expression as a color at the given zoom level.  Constant expressions return
        /// the cached color; Zoom expressions evaluate against the supplied zoom.
        /// </summary>
        /// <exception cref="ExpressionEvaluationException">If the expression does not produce a color.</exception>
        public Color EvaluateColor(double zoom)
        {
            if (_isConstant) return _constantColor;
            return _expr.Evaluate(new EvaluationContext(zoom, null)).AsColor();
        }

        // ---- guard -------------------------------------------------------------------------------

        private static void RejectDataDriven(Expression expr)
        {
            if (ExpressionKinds.DependsOnFeature(expr.Kind))
                throw new System.ArgumentException(
                    "Data-driven paint expressions (Feature / Composite kind) are not supported by " +
                    "PaintPropertyEvaluator — per-feature styling is deferred to S12. " +
                    "Supply only Constant or Zoom expressions.");
        }
    }
}
