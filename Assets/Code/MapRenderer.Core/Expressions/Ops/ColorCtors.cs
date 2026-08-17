namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// Color constructors <c>rgb</c>/<c>rgba</c> per the Style Spec "Color" section: channels are numbers in
    /// 0..255, alpha in 0..1. An out-of-range channel is an evaluation error.
    /// </summary>
    public static class ColorCtors
    {
        public static Expression Rgb(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                double r = Channel(vals[0]), g = Channel(vals[1]), b = Channel(vals[2]);
                return Value.OfColor(Color.From255(r, g, b, 1.0));
            }, args, ValueType.Color);

        public static Expression Rgba(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                double r = Channel(vals[0]), g = Channel(vals[1]), b = Channel(vals[2]);
                double a = Alpha(vals[3]);
                return Value.OfColor(Color.From255(r, g, b, a));
            }, args, ValueType.Color);

        private static double Channel(Value v)
        {
            double c = v.AsNumber();
            if (c < 0.0 || c > 255.0)
                throw new ExpressionEvaluationException(
                    $"rgb/rgba: channel {Value.FormatNumber(c)} out of range [0, 255].");
            return c;
        }

        private static double Alpha(Value v)
        {
            double a = v.AsNumber();
            if (a < 0.0 || a > 1.0)
                throw new ExpressionEvaluationException(
                    $"rgba: alpha {Value.FormatNumber(a)} out of range [0, 1].");
            return a;
        }
    }
}
