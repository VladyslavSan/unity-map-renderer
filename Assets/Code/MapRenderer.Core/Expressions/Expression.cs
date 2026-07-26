namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// A parsed, ready-to-evaluate expression node. Built once by <see cref="ExpressionParser"/>; evaluated
    /// many times against an <see cref="EvaluationContext"/>.
    ///
    /// <see cref="Evaluate"/> may throw <see cref="ExpressionEvaluationException"/> on a spec-defined error
    /// (coercion failure, out-of-range, type mismatch). Internal nodes call each other through
    /// <see cref="Evaluate"/> and let it propagate. Callers should use <see cref="TryEvaluate"/>, the public
    /// boundary, which catches the error and returns failure rather than crashing the host.
    /// </summary>
    public abstract class Expression
    {
        /// <summary>The static result type, where known; <see cref="ValueType.Value"/> when it varies.</summary>
        public abstract ValueType ResultType { get; }

        /// <summary>Constant / Zoom / Feature / Composite — computed at parse time.</summary>
        public abstract ExpressionKind Kind { get; }

        /// <summary>Evaluate; may throw <see cref="ExpressionEvaluationException"/> on a spec error.</summary>
        public abstract Value Evaluate(in EvaluationContext context);

        /// <summary>
        /// The public evaluation boundary: evaluate and return <c>true</c> with the result, or <c>false</c>
        /// with the spec error message, never throwing <see cref="ExpressionEvaluationException"/>. This is
        /// how the acceptance's "type errors handled per spec, not crashes" is honored.
        /// </summary>
        public bool TryEvaluate(in EvaluationContext context, out Value result, out string error)
        {
            try
            {
                result = Evaluate(context);
                error = null;
                return true;
            }
            catch (ExpressionEvaluationException ex)
            {
                result = Value.Null;
                error = ex.Message;
                return false;
            }
        }
    }
}
