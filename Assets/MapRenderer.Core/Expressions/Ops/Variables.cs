using System.Collections.Generic;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// <c>let</c>: bind one or more named values, then evaluate the final output with those bindings in
    /// scope. Bindings are evaluated lazily (only when their <c>var</c> is referenced) and memoized within a
    /// single evaluation. Spec "let"/"var".
    /// </summary>
    public sealed class LetExpression : Expression
    {
        private readonly (string name, Expression value)[] _bindings;
        private readonly Expression _body;
        private readonly ExpressionKind _kind;

        public LetExpression((string, Expression)[] bindings, Expression body)
        {
            _bindings = bindings;
            _body = body;
            // A let's value depends on whatever its body needs; the body's Kind already folds in the
            // bindings it references (VarExpression carries the bound expression's Kind). To be safe and
            // simple we fold all binding kinds in too.
            var k = body.Kind;
            foreach (var b in bindings) k = ExpressionKinds.Combine(k, b.Item2.Kind);
            _kind = k;
        }

        public override ValueType ResultType => _body.ResultType;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            // Bindings reference each other / the body via VarExpression, which holds the bound Expression
            // directly (resolved at parse time), so there is no runtime environment to thread here — the
            // body simply evaluates and each VarExpression evaluates its captured expression on demand.
            return _body.Evaluate(context);
        }
    }

    /// <summary>
    /// <c>var</c>: the value of a name bound by an enclosing <c>let</c>. Resolved at parse time to the bound
    /// expression (an unbound name is a parse error), with per-evaluation memoization so the bound
    /// expression is evaluated at most once per <c>Evaluate</c> call.
    /// </summary>
    public sealed class VarExpression : Expression
    {
        private readonly string _name;
        private readonly Expression _bound;

        public VarExpression(string name, Expression bound)
        {
            _name = name;
            _bound = bound;
        }

        public string Name => _name;
        public override ValueType ResultType => _bound.ResultType;
        public override ExpressionKind Kind => _bound.Kind;

        public override Value Evaluate(in EvaluationContext context) => _bound.Evaluate(context);
    }
}
