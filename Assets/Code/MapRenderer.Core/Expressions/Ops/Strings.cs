using System.Globalization;
using System.Text;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// String operators per the Style Spec "String" section: <c>concat</c> (coerce each argument to string
    /// and join), <c>upcase</c>, <c>downcase</c>. Case operations use the invariant culture (locale-
    /// independent; the spec notes locale-dependent case is not supported).
    /// </summary>
    public static class Strings
    {
        public static Expression Concat(Expression[] args)
            => new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
            {
                var sb = new StringBuilder();
                foreach (var v in vals)
                    sb.Append(Coercions.ToStringValue(v));
                return Value.String(sb.ToString());
            }, args, ValueType.String);

        public static Expression Upcase(Expression arg)
            => new FunctionExpression(
                (System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) => Value.String(vals[0].AsString().ToUpperInvariant()),
                new[] { arg }, ValueType.String);

        public static Expression Downcase(Expression arg)
            => new FunctionExpression(
                (System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) => Value.String(vals[0].AsString().ToLowerInvariant()),
                new[] { arg }, ValueType.String);
    }
}
