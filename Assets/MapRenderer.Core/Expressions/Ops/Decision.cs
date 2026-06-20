using System.Collections.Generic;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// <c>case</c>: evaluate condition/output pairs in order; return the output of the first true condition,
    /// else the fallback. Conditions and unselected outputs are NOT evaluated (lazy). Spec "case".
    /// </summary>
    public sealed class CaseExpression : Expression
    {
        private readonly Expression[] _conditions;
        private readonly Expression[] _outputs;
        private readonly Expression _fallback;
        private readonly ExpressionKind _kind;

        public CaseExpression(Expression[] conditions, Expression[] outputs, Expression fallback)
        {
            _conditions = conditions;
            _outputs = outputs;
            _fallback = fallback;
            var k = fallback.Kind;
            foreach (var c in conditions) k = ExpressionKinds.Combine(k, c.Kind);
            foreach (var o in outputs) k = ExpressionKinds.Combine(k, o.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Value;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            for (int i = 0; i < _conditions.Length; i++)
                if (Coercions.ToBoolean(_conditions[i].Evaluate(context)))
                    return _outputs[i].Evaluate(context);
            return _fallback.Evaluate(context);
        }
    }

    /// <summary>
    /// <c>match</c>: evaluate the input, then return the output whose label set contains it; else the
    /// default. Labels are literal numbers or strings (or arrays of them). Spec "match".
    /// </summary>
    public sealed class MatchExpression : Expression
    {
        private readonly Expression _input;
        private readonly List<(Value[] labels, Expression output)> _cases;
        private readonly Expression _default;
        private readonly ExpressionKind _kind;

        public MatchExpression(Expression input, List<(Value[], Expression)> cases, Expression fallback)
        {
            _input = input;
            _cases = cases;
            _default = fallback;
            var k = ExpressionKinds.Combine(input.Kind, fallback.Kind);
            foreach (var c in cases) k = ExpressionKinds.Combine(k, c.Item2.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Value;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            Value input = _input.Evaluate(context);
            foreach (var (labels, output) in _cases)
                foreach (var label in labels)
                    if (label.Equals(input))
                        return output.Evaluate(context);
            return _default.Evaluate(context);
        }
    }

    /// <summary>
    /// <c>coalesce</c>: evaluate each argument in order; return the first that is non-null and does not
    /// error. Spec "coalesce".
    /// </summary>
    public sealed class CoalesceExpression : Expression
    {
        private readonly Expression[] _args;
        private readonly ExpressionKind _kind;

        public CoalesceExpression(Expression[] args)
        {
            _args = args;
            var k = ExpressionKind.Constant;
            foreach (var a in args) k = ExpressionKinds.Combine(k, a.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Value;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            Value last = Value.Null;
            foreach (var a in _args)
            {
                try
                {
                    Value v = a.Evaluate(context);
                    if (!v.IsNull) return v;
                    last = v;
                }
                catch (ExpressionEvaluationException)
                {
                    // skip erroring branch (spec: coalesce skips arguments that error)
                }
            }
            return last;
        }
    }

    /// <summary>
    /// <c>all</c>: true iff every argument is true; short-circuits on the first false. Spec "all".
    /// Arguments must evaluate to boolean (an error otherwise).
    /// </summary>
    public sealed class AllExpression : Expression
    {
        private readonly Expression[] _args;
        private readonly ExpressionKind _kind;

        public AllExpression(Expression[] args)
        {
            _args = args;
            var k = ExpressionKind.Constant;
            foreach (var a in args) k = ExpressionKinds.Combine(k, a.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Boolean;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            foreach (var a in _args)
                if (!a.Evaluate(context).AsBool())
                    return Value.Bool(false);
            return Value.Bool(true);
        }
    }

    /// <summary>
    /// <c>any</c>: true iff at least one argument is true; short-circuits on the first true. Spec "any".
    /// </summary>
    public sealed class AnyExpression : Expression
    {
        private readonly Expression[] _args;
        private readonly ExpressionKind _kind;

        public AnyExpression(Expression[] args)
        {
            _args = args;
            var k = ExpressionKind.Constant;
            foreach (var a in args) k = ExpressionKinds.Combine(k, a.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Boolean;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            foreach (var a in _args)
                if (a.Evaluate(context).AsBool())
                    return Value.Bool(true);
            return Value.Bool(false);
        }
    }
}
