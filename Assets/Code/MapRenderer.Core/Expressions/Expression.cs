namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// A parsed, ready-to-evaluate expression node. Built once by <see cref="ExpressionParser"/>; evaluated
    /// many times against an <see cref="EvaluationContext"/>. <see cref="Evaluate"/> throws
    /// <see cref="ExpressionEvaluationException"/> on a spec-defined error, and nodes let it propagate.
    /// Callers use <see cref="TryEvaluate"/>, the public boundary, which returns failure instead.
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

        /// <summary>
        /// The zero-allocation fast path for a per-frame numeric-array consumer (line-dasharray,
        /// <c>*-translate</c>): writes this expression's array-shaped result straight into
        /// <paramref name="destination"/>, never building a <see cref="Value"/> array. Returns <c>false</c> —
        /// never throwing, with <paramref name="count"/> left at 0 — on a spec error, a non-array or
        /// non-numeric result, or more entries than <paramref name="destination"/> holds; the caller falls
        /// back to <see cref="Evaluate"/> in every one of those cases.
        /// </summary>
        public bool TryEvaluateNumberArray(in EvaluationContext context, System.Span<double> destination, out int count)
        {
            try
            {
                return EvaluateNumberArrayCore(context, destination, out count);
            }
            catch (ExpressionEvaluationException)
            {
                count = 0;
                return false;
            }
        }

        /// <summary>
        /// Default: evaluate normally and unpack the resulting <see cref="Value"/> — allocation-free already
        /// for a literal or <c>step</c> result, neither of which builds a new array. Overridden only by
        /// <see cref="Ops.InterpolateExpression"/>, the one node whose general array path allocates.
        /// </summary>
        protected virtual bool EvaluateNumberArrayCore(in EvaluationContext context, System.Span<double> destination, out int count)
        {
            count = 0;
            Value v = Evaluate(context);
            if (v.Type != ValueType.Array) return false;
            var arr = v.AsArray();
            if (arr.Count > destination.Length) return false;
            // Strict AsNumber, matching InterpolateExpression's override: a malformed entry (e.g. [true, 3])
            // throws, which the caller's try/catch turns into the same "fall back to default" as before.
            for (int i = 0; i < arr.Count; i++)
                destination[i] = arr[i].AsNumber();
            count = arr.Count;
            return true;
        }
    }
}
