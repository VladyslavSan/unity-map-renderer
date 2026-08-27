using System;
using System.Collections.Generic;
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
        private static readonly IReadOnlyList<string> EmptyKeyLayout = Array.Empty<string>();

        private readonly Expression _expr;
        private readonly bool _alwaysTrue;

        /// <summary>
        /// The string→id key hoist's ordered constant-key <c>get</c>/<c>has</c> names for this filter's
        /// expression — a bind site (<c>FeatureSelector</c>) resolves each name into a key index once per
        /// layer, building the <see cref="Expressions.EvaluationContext.KeyBinding"/> array
        /// <see cref="Matches(IFeature, double, int[])"/> evaluates against. Empty for the match-all/
        /// match-none sentinels. Immutable after <see cref="Compile"/> returns — this instance is shared
        /// cross-thread via <c>FeatureSelector</c>'s <c>ConditionalWeakTable</c> memo, so that immutability
        /// is what makes concurrent binds against the same filter race-free.
        /// </summary>
        public IReadOnlyList<string> KeyLayout { get; }

        private static readonly CompiledFilter MatchAll =
            new CompiledFilter(null, EmptyKeyLayout, alwaysTrue: true);
        private static readonly CompiledFilter MatchNone =
            new CompiledFilter(null, EmptyKeyLayout, alwaysTrue: false);

        private CompiledFilter(Expression expr, IReadOnlyList<string> keyLayout, bool alwaysTrue = false)
        {
            _expr = expr;
            KeyLayout = CopyImmutable(keyLayout);
            _alwaysTrue = alwaysTrue;
        }

        /// <summary>Materializes <paramref name="layout"/> into an immutable array copy — the parser hands
        /// out a live <c>List&lt;string&gt;</c>, but this instance is shared cross-thread and the race-free
        /// binding contract (see <see cref="KeyLayout"/>) rests on it never mutating after <see cref="Compile"/>.
        /// Making that a copy makes the invariant structural, not a caller's promise. The empty sentinels
        /// reuse <see cref="EmptyKeyLayout"/> (no allocation).</summary>
        /// <param name="layout">The per-filter key layout to freeze; null or empty ⇒ the shared empty layout.</param>
        private static IReadOnlyList<string> CopyImmutable(IReadOnlyList<string> layout)
        {
            if (layout == null || layout.Count == 0) return EmptyKeyLayout;
            var copy = new string[layout.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = layout[i];
            return copy;
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

            var expr = ExpressionParser.Parse(exprTree, out IReadOnlyList<string> keyLayout);
            return new CompiledFilter(expr, keyLayout);
        }

        /// <summary>
        /// Evaluates this filter against <paramref name="feature"/> at <paramref name="zoom"/>, on the
        /// string key-lookup path (no <see cref="Expressions.EvaluationContext.KeyBinding"/>). Returns
        /// <c>true</c> iff the feature passes; <c>false</c> on any spec error or non-true result. Never
        /// throws.
        /// </summary>
        public bool Matches(IFeature feature, double zoom = 0.0) => Matches(feature, zoom, keyBinding: null);

        /// <summary>
        /// The string→id key hoist's binding-aware overload: <paramref name="keyBinding"/> is a per-layer
        /// resolved <c>slot→key-index</c> map built by the caller against THIS filter's
        /// <see cref="KeyLayout"/> (see <c>FeatureSelector</c>'s bind step) — pass <c>null</c> to force the
        /// string path (e.g. when the feature source is not index-capable). Otherwise identical to
        /// <see cref="Matches(IFeature, double)"/>.
        /// </summary>
        public bool Matches(IFeature feature, double zoom, int[] keyBinding)
        {
            if (_expr == null)
                return _alwaysTrue;

            var ctx = new EvaluationContext(zoom, feature, keyBinding);
            if (!_expr.TryEvaluate(ctx, out Value result, out _))
                return false;

            return Coercions.ToBoolean(result);
        }
    }
}
