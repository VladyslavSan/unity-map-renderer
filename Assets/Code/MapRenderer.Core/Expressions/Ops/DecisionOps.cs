namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// The eager decision operators: equality (<c>==</c>/<c>!=</c>), ordered comparisons
    /// (<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c>), and <c>!</c>. (The lazy/variadic decision ops —
    /// case/match/coalesce/all/any — are their own node types.) Semantics from the Style Spec "Decision"
    /// section: equality is deep value-equality; ordered comparisons require both operands to be the same
    /// type and that type to be number or string (otherwise an evaluation error).
    /// </summary>
    public static class Decision
    {
        public static Expression Eq(Expression[] args, bool negate)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                bool eq = vals[0].Equals(vals[1]);
                return Value.Bool(negate ? !eq : eq);
            }, args, ValueType.Boolean);

        public static Expression Compare(string op, Expression[] args)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                int cmp = CompareValues(op, vals[0], vals[1]);
                bool result;
                switch (op)
                {
                    case "<": result = cmp < 0; break;
                    case "<=": result = cmp <= 0; break;
                    case ">": result = cmp > 0; break;
                    case ">=": result = cmp >= 0; break;
                    default: result = false; break;
                }
                return Value.Bool(result);
            }, args, ValueType.Boolean);

        private static int CompareValues(string op, Value a, Value b)
        {
            if (a.Type != b.Type)
                throw new ExpressionEvaluationException(
                    $"{op}: cannot compare {ValueTypes.TypeOfName(a.Type)} with {ValueTypes.TypeOfName(b.Type)}.");
            if (a.Type == ValueType.Number)
                return a.AsNumber().CompareTo(b.AsNumber());
            if (a.Type == ValueType.String)
                return string.CompareOrdinal(a.AsString(), b.AsString());
            throw new ExpressionEvaluationException(
                $"{op}: ordered comparison requires number or string, got {ValueTypes.TypeOfName(a.Type)}.");
        }

        public static Expression Not(Expression arg)
            => new FunctionExpression(
                (Value[] vals, in EvaluationContext ctx) => Value.Bool(!vals[0].AsBool()),
                new[] { arg }, ValueType.Boolean);
    }
}
