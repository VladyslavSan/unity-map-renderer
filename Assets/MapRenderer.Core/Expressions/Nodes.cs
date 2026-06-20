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
        public delegate Value Impl(Value[] args, in EvaluationContext context);

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
            var vals = new Value[_args.Length];
            for (int i = 0; i < _args.Length; i++)
                vals[i] = _args[i].Evaluate(context);
            return _impl(vals, context);
        }
    }
}
