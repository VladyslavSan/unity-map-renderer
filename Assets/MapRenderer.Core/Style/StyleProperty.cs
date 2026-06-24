using System;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// A single parsed MapLibre paint or layout property, typed to <typeparamref name="T"/>.
    /// Collapses the old triple (<c>XKind</c> + <c>PaintPropertyEvaluator X</c> +
    /// <c>DataDrivenPaintEvaluator DataDrivenX</c>) into ONE object: a single parsed
    /// <see cref="Expression"/>, a typed default <typeparamref name="T"/>, and a
    /// <c>Value → T</c> projection delegate.
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="Kind"/> reads <c>_expr.Kind</c> directly — NOT a stored field — and reports
    ///     <see cref="ExpressionKind.Constant"/> when the property was absent.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="DependsOnFeature"/> / <see cref="IsZoomDependent"/> derive from <see cref="Kind"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Evaluate(double)"/> (uniform path) throws the same
    ///     "Data-driven paint expressions … deferred to S12" <see cref="ArgumentException"/> that
    ///     <c>PaintPropertyEvaluator</c> used to throw — the render pipeline's uniformity guard.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Evaluate(double,IFeature)"/> (bake path) passes zoom + feature and is safe for all
    ///     four <see cref="ExpressionKind"/>s.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="TryEvaluate(double,IFeature,out T)"/> swallows all exceptions and returns false /
    ///     <see cref="DefaultValue"/> — the tile-builder bake path relies on this.
    ///   </description></item>
    /// </list>
    ///
    /// Constant-kind expressions (and absent properties) cache the projected <typeparamref name="T"/>
    /// value once at construction — evaluation is a field read; no allocation.
    ///
    /// Engine-free: no UnityEngine references. Clean-room: semantics from the public MapLibre Style Spec.
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
        /// Per-feature styling is deferred to S12; the caller must use
        /// <see cref="Evaluate(double,IFeature)"/> or <see cref="TryEvaluate(double,IFeature,out T)"/>.
        /// </exception>
        public T Evaluate(double zoom)
        {
            if (DependsOnFeature)
                throw new ArgumentException(
                    "Data-driven paint expressions (Feature / Composite kind) are not supported by " +
                    "StyleProperty<T>.Evaluate(zoom) — per-feature styling is deferred to S12. " +
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
