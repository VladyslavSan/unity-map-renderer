using System;
using System.Collections.Generic;

namespace MapRenderer.Core.Expressions
{
    /// <summary>A constant value (the <c>literal</c> expression and bare JSON literals).</summary>
    public sealed class LiteralExpression : Expression
    {
        private readonly Value _value;

        public LiteralExpression(Value value) { _value = value; }

        public override ValueType ResultType => _value.Type;
        public override ExpressionKind Kind => ExpressionKind.Constant;
        public override Value Evaluate(in EvaluationContext context) => _value;
    }

    /// <summary>
    /// A general operator node whose arguments are all evaluated eagerly, then handed to a delegate. Covers
    /// the bulk of ops (math, string, coercions, comparisons, lookups). Control-flow ops that must NOT
    /// evaluate every argument (case/match/coalesce/all/any/let) use their own node types instead.
    /// </summary>
    public sealed class FunctionExpression : Expression
    {
        /// <summary>The operator body, invoked with the already-evaluated arguments.</summary>
        /// <param name="args">The evaluated arguments, in declared order. This is a per-thread pooled buffer
        /// borrowed only for the duration of the call — read from it, but never store, convert, or return it
        /// (see <see cref="EvalArgBuffers"/> and <see cref="Evaluate"/>'s rent/return).</param>
        /// <param name="context">The evaluation context (zoom, feature, …).</param>
        /// <returns>The operator's result value.</returns>
        public delegate Value Impl(ReadOnlySpan<Value> args, in EvaluationContext context);

        private readonly Impl _impl;
        private readonly Expression[] _args;
        private readonly ValueType _resultType;
        private readonly ExpressionKind _kind;
        // Captures context-direct dependence (e.g. the bare `zoom` op) independent of args.
        public FunctionExpression(Impl impl, Expression[] args, ValueType resultType)
        {
            _impl = impl;
            _args = args ?? System.Array.Empty<Expression>();
            _resultType = resultType;
            _kind = KindOf(_args);
        }

        private static ExpressionKind KindOf(Expression[] args)
        {
            var k = ExpressionKind.Constant;
            foreach (var a in args)
                k = ExpressionKinds.Combine(k, a.Kind);
            return k;
        }

        public override ValueType ResultType => _resultType;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            // Rent a per-thread scratch buffer instead of a per-call `new Value[]` — this node is the hottest
            // managed allocator during feature selection. The buffer MUST be returned in `finally`: an arg's
            // Evaluate or a typed Value accessor can throw mid-fill, and a leaked buffer drains the pool back
            // to allocating. See EvalArgBuffers for the thread-affinity + re-entrancy contract.
            int count = _args.Length;
            Value[] buffer = EvalArgBuffers.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                    buffer[i] = _args[i].Evaluate(context);
                return _impl(buffer.AsSpan(0, count), context);
            }
            finally
            {
                EvalArgBuffers.Return(buffer);
            }
        }
    }
}
