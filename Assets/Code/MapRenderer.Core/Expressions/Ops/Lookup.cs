using Unity.Mathematics;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// Lookup operators: <c>at</c> (array index), <c>in</c> (substring / array membership), <c>length</c>
    /// (string / array). (<c>get</c>/<c>has</c> are built in the parser because the 1-arg forms read the
    /// feature.) Semantics from the Style Spec "Lookup" section.
    /// </summary>
    public static class Lookup
    {
        /// <summary><c>["at", index, array]</c>: the element at index; out-of-range is an error.</summary>
        public static Expression At(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                double idxD = vals[0].AsNumber();
                var array = vals[1].AsArray();
                if (idxD != math.floor(idxD) || idxD < 0 || idxD >= array.Count)
                    throw new ExpressionEvaluationException(
                        $"at: index {Value.FormatNumber(idxD)} out of range [0, {array.Count}).");
                return array[(int)idxD];
            }, args, ValueType.Value);

        /// <summary>
        /// <c>["in", needle, haystack]</c>: substring membership when haystack is a string; element
        /// membership when haystack is an array. Returns a boolean.
        /// </summary>
        public static Expression In(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                Value needle = vals[0];
                Value haystack = vals[1];
                if (haystack.Type == ValueType.String)
                {
                    if (needle.Type != ValueType.String)
                        throw new ExpressionEvaluationException(
                            "in: searching a string requires a string needle.");
                    return Value.Bool(haystack.AsString().IndexOf(needle.AsString(),
                        System.StringComparison.Ordinal) >= 0);
                }
                if (haystack.Type == ValueType.Array)
                {
                    foreach (var item in haystack.AsArray())
                        if (item.Equals(needle))
                            return Value.Bool(true);
                    return Value.Bool(false);
                }
                throw new ExpressionEvaluationException(
                    $"in: expected string or array haystack, got {ValueTypes.TypeOfName(haystack.Type)}.");
            }, args, ValueType.Boolean);

        /// <summary><c>["length", x]</c>: length of a string (chars) or array (elements).</summary>
        public static Expression Length(Expression arg)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                Value v = vals[0];
                if (v.Type == ValueType.String) return Value.Number(v.AsString().Length);
                if (v.Type == ValueType.Array) return Value.Number(v.AsArray().Count);
                throw new ExpressionEvaluationException(
                    $"length: expected string or array, got {ValueTypes.TypeOfName(v.Type)}.");
            }, new[] { arg }, ValueType.Number);
    }
}
