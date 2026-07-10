using System;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// Thrown by the <see cref="ExpressionParser"/> when an expression is structurally invalid (unknown
    /// operator, wrong arity, mis-placed <c>zoom</c>, unbound <c>var</c>, malformed literal). Parse errors
    /// are surfaced to the caller — a style with an invalid expression is a hard error, distinct from a
    /// per-feature evaluation error (see <see cref="ExpressionEvaluationException"/>).
    /// </summary>
    public sealed class ExpressionParseException : Exception
    {
        public ExpressionParseException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown internally by expression nodes when, at evaluation time, an operation fails per the spec
    /// (coercion failure, index out of range, type mismatch in a comparison, etc.). The spec frames these
    /// as "the expression is an error"; the public evaluation boundary
    /// (<see cref="Expression.TryEvaluate"/>) catches this and returns failure rather than propagating —
    /// evaluation never crashes the host. Nodes may throw this freely; only the boundary catches it.
    /// </summary>
    public sealed class ExpressionEvaluationException : Exception
    {
        public ExpressionEvaluationException(string message) : base(message) { }
    }
}
