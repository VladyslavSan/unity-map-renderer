namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// Literal / type operators: <c>typeof</c> and the coercions <c>to-number</c>/<c>to-string</c>/
    /// <c>to-boolean</c>/<c>to-color</c>/<c>to-rgba</c>. Semantics from the Style Spec "Types" section.
    /// </summary>
    public static class Literals
    {
        public static Expression TypeOf(Expression arg)
            => new FunctionExpression(
                (System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) => Value.String(ValueTypes.TypeOfName(vals[0].Type)),
                new[] { arg }, ValueType.String);

        public static Expression ToNumber(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                foreach (var v in vals)
                    if (Coercions.TryToNumber(v, out double n))
                        return Value.Number(n);
                throw new ExpressionEvaluationException(
                    "to-number: none of the inputs could be converted to a number.");
            }, args, ValueType.Number);

        public static Expression ToString(Expression arg)
            => new FunctionExpression(
                (System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) => Value.String(Coercions.ToStringValue(vals[0])),
                new[] { arg }, ValueType.String);

        public static Expression ToBoolean(Expression arg)
            => new FunctionExpression(
                (System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) => Value.Bool(Coercions.ToBoolean(vals[0])),
                new[] { arg }, ValueType.Boolean);

        public static Expression ToColor(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                foreach (var v in vals)
                    if (Coercions.TryToColor(v, out Color c))
                        return Value.OfColor(c);
                throw new ExpressionEvaluationException(
                    "to-color: none of the inputs could be converted to a color.");
            }, args, ValueType.Color);

        public static Expression ToRgba(Expression arg)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                if (vals[0].Type != ValueType.Color)
                    throw new ExpressionEvaluationException("to-rgba: expected a color.");
                double[] rgba = vals[0].AsColor().ToRgbaArray();
                return Value.Array(new[]
                {
                    Value.Number(rgba[0]), Value.Number(rgba[1]),
                    Value.Number(rgba[2]), Value.Number(rgba[3])
                });
            }, new[] { arg }, ValueType.Array);
    }
}
