using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// A compiled, ready-to-evaluate filter. Wraps a parsed <see cref="Expression"/> tree. Built
    /// once per style layer via <see cref="Compile"/>; evaluated many times per feature via
    /// <see cref="Matches"/>.
    ///
    /// Routing logic:
    /// <list type="bullet">
    ///   <item>Null or absent filter → match-all sentinel (every feature passes).</item>
    ///   <item>Bare <c>true</c> → match-all.</item>
    ///   <item>Bare <c>false</c> → match-none.</item>
    ///   <item>Array whose dialect is expression → parse directly with <see cref="ExpressionParser"/>.</item>
    ///   <item>Array whose dialect is legacy → translate with <see cref="LegacyFilterTranslator"/> first,
    ///     then parse.</item>
    /// </list>
    ///
    /// <see cref="Matches"/> uses <see cref="Expression.TryEvaluate"/> (the non-throwing boundary) and
    /// maps both a spec error and a non-boolean-true result to <c>false</c>, so type errors in filter
    /// expressions never crash the host — they simply exclude the feature.
    /// </summary>
    public sealed class CompiledFilter
    {
        private readonly Expression _expr;
        private readonly bool _alwaysTrue;

        private static readonly CompiledFilter MatchAll = new CompiledFilter(null, alwaysTrue: true);
        private static readonly CompiledFilter MatchNone = new CompiledFilter(null, alwaysTrue: false);

        private CompiledFilter(Expression expr, bool alwaysTrue = false)
        {
            _expr = expr;
            _alwaysTrue = alwaysTrue;
        }

        /// <summary>
        /// Compiles <paramref name="filter"/> into a <see cref="CompiledFilter"/>.
        /// Null → match-all. Bare boolean literals → match-all or match-none.
        /// Arrays are routed by <see cref="FilterDialect.IsExpressionFilter"/>.
        /// </summary>
        /// <exception cref="ExpressionParseException">If the filter is syntactically malformed.</exception>
        public static CompiledFilter Compile(JsonValue filter)
        {
            if (filter == null || filter.IsNull)
                return MatchAll;

            if (filter.Kind == JsonKind.Bool)
                return filter.AsBool() ? MatchAll : MatchNone;

            if (!filter.IsArray)
                throw new ExpressionParseException(
                    $"Filter must be null, a boolean, or an array; got {filter.Kind}.");

            // Route by dialect.
            JsonValue exprTree;
            if (FilterDialect.IsExpressionFilter(filter))
            {
                exprTree = filter;
            }
            else
            {
                exprTree = LegacyFilterTranslator.Translate(filter);
            }

            var expr = ExpressionParser.Parse(exprTree);
            return new CompiledFilter(expr);
        }

        /// <summary>
        /// Evaluates this filter against <paramref name="feature"/> at <paramref name="zoom"/>.
        /// Returns <c>true</c> iff the feature passes; <c>false</c> on any spec error or non-true result.
        /// Never throws.
        /// </summary>
        public bool Matches(IFeature feature, double zoom = 0.0)
        {
            if (_expr == null)
                return _alwaysTrue;

            var ctx = new EvaluationContext(zoom, feature);
            if (!_expr.TryEvaluate(ctx, out Value result, out _))
                return false;

            return Coercions.ToBoolean(result);
        }
    }
}
