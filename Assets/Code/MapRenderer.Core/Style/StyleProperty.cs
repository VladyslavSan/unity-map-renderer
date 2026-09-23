using System;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A single parsed MapLibre paint or layout property, typed to <typeparamref name="T"/>: one parsed
    /// <see cref="Expression"/>, a typed default, and a <c>Value → T</c> projection delegate. A constant
    /// expression (or an absent property) caches the projected value at construction, so evaluation is a field
    /// read with no allocation.
    /// </summary>
    public sealed class StyleProperty<T>
    {
        private readonly Expression _expr;      // null when property was absent
        private readonly Func<Value, T> _project;
        private readonly bool _isConstant;
        private readonly T _cached;             // cached projected value for constants

        /// <summary>The typed default value used when the property is absent.</summary>
        public T DefaultValue { get; }

        /// <summary>
        /// The classification of the wrapped expression.
        /// Reports <see cref="ExpressionKind.Constant"/> when the property was absent.
        /// </summary>
        public ExpressionKind Kind => _expr?.Kind ?? ExpressionKind.Constant;

        /// <summary>True when the expression depends on feature data (Feature or Composite kind).</summary>
        public bool DependsOnFeature => ExpressionKinds.DependsOnFeature(Kind);

        /// <summary>True when the expression depends on zoom and must be re-evaluated per frame.</summary>
        public bool IsZoomDependent => ExpressionKinds.DependsOnZoom(Kind);

        // ── Constructors ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Expression-path constructor: parse <paramref name="json"/> (a <see cref="JsonValue"/>),
        /// cache the projected value when the expression is Constant.
        /// </summary>
        /// <param name="json">The JSON value of the paint/layout property.</param>
        /// <param name="defaultValue">The typed default to return when the property is absent.</param>
        /// <param name="project">Maps a runtime <see cref="Value"/> to <typeparamref name="T"/>.</param>
        /// <exception cref="ExpressionParseException">If <paramref name="json"/> is not a valid expression.</exception>
        public StyleProperty(JsonValue json, T defaultValue, Func<Value, T> project)
        {
            DefaultValue = defaultValue;
            _project = project;
            _expr = ExpressionParser.Parse(json);
            _isConstant = (_expr.Kind == ExpressionKind.Constant);
            if (_isConstant)
                _cached = EvalProjected(0.0, null);
        }

        /// <summary>
        /// Constant-construction path: use a literal <typeparamref name="T"/> value without any JSON parse.
        /// <see cref="Kind"/> reports <see cref="ExpressionKind.Constant"/>; <see cref="Evaluate(double)"/>
        /// returns <paramref name="value"/> directly. Used for absent properties, Join/Cap enums, translate
        /// components, and translate-anchor.
        /// </summary>
        /// <param name="value">The constant value (also used as <see cref="DefaultValue"/>).</param>
        public StyleProperty(T value)
        {
            DefaultValue = value;
            _project = null;
            _expr = null;
            _isConstant = true;
            _cached = value;
        }

        // ── Evaluation ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate as a uniform (no feature). Constant: cached value; Zoom: evaluate at zoom.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// If this property is Feature- or Composite-kind — it cannot be evaluated as a material uniform.
        /// The caller must use <see cref="Evaluate(double,IFeature)"/> or
        /// <see cref="TryEvaluate(double,IFeature,out T)"/> instead.
        /// </exception>
        public T Evaluate(double zoom)
        {
            if (DependsOnFeature)
                throw new ArgumentException(
                    "Data-driven paint expressions (Feature / Composite kind) are not supported by " +
                    "StyleProperty<T>.Evaluate(zoom) — per-feature styling is not supported here. " +
                    "Supply only Constant or Zoom expressions.");
            if (_isConstant) return _cached;
            return EvalProjected(zoom, null);
        }

        /// <summary>
        /// Evaluate with a full zoom + feature context. Safe for all four expression kinds.
        /// Constant expressions ignore both zoom and feature.
        /// </summary>
        public T Evaluate(double zoom, IFeature feature)
        {
            if (_isConstant) return _cached;
            return EvalProjected(zoom, feature);
        }

        /// <summary>
        /// Try to evaluate with zoom + feature. Returns true on success; on any exception returns false
        /// and sets <paramref name="value"/> to <see cref="DefaultValue"/>. Used by tile builders in the
        /// bake path where missing/malformed properties must silently degrade.
        /// </summary>
        public bool TryEvaluate(double zoom, IFeature feature, out T value)
        {
            try
            {
                value = Evaluate(zoom, feature);
                return true;
            }
            catch
            {
                value = DefaultValue;
                return false;
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────────────────────

        private T EvalProjected(double zoom, IFeature feature)
        {
            var v = _expr.Evaluate(new EvaluationContext(zoom, feature));
            return _project(v);
        }
    }
}
