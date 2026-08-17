using System.Collections.Generic;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// A per-thread free-list of <see cref="Value"/> scratch arrays for <see cref="FunctionExpression"/>'s
    /// evaluated-argument buffers, replacing a <c>new Value[]</c> on every operator evaluation (the hottest
    /// managed allocation during feature selection: features × filter-nodes, per tile).
    /// </summary>
    /// <remarks>
    /// This is a <b>free-list of whole arrays</b>, not one growable buffer with a cursor, for two
    /// non-local reasons that a single body does not reveal:
    /// <list type="bullet">
    ///   <item><b>Thread affinity.</b> The parsed expression tree is shared across the worker threads that
    ///     build tiles concurrently (<c>FeatureSelector.SelectFeatures</c> runs off-main), so the buffer
    ///     cannot live on the shared <see cref="FunctionExpression"/> instance — it is <c>[ThreadStatic]</c>
    ///     here so each thread owns its own pool.</item>
    ///   <item><b>Re-entrancy.</b> Evaluation nests — a node's argument is itself an expression that borrows
    ///     a buffer while the outer node's buffer is still half-filled — so two frames on one thread are live
    ///     at once. Distinct arrays (never a shared backing store) mean no frame can alias or reallocate
    ///     another's buffer.</item>
    /// </list>
    /// <para><b>Contract:</b> every <see cref="Rent"/> MUST be paired with a <see cref="Return"/> of the same
    /// array in a <c>finally</c> — an argument's <see cref="Expression.Evaluate"/> or a typed
    /// <see cref="Value"/> accessor can throw mid-fill, and a leaked buffer silently drains the pool back to
    /// per-call allocation (the exact cost this removes). Rented arrays are length ≥ the request; the caller
    /// slices to the exact count (<c>AsSpan(0, n)</c>) so an oversized buffer is transparent. <see cref="Return"/>
    /// clears the array before pooling, so no pooled buffer roots a (possibly reference-typed) argument.</para>
    /// </remarks>
    internal static class EvalArgBuffers
    {
        // [ThreadStatic] does NOT run a field initializer per thread (only on the thread that first touches
        // the type), so the stack is created lazily in Rent/Return, never inline.
        [System.ThreadStatic] private static Stack<Value[]> _free;

        /// <summary>Borrow a buffer of length ≥ <paramref name="length"/> for the current thread.</summary>
        /// <param name="length">The exact argument count; the returned array may be larger.</param>
        /// <returns>A buffer the caller owns until it calls <see cref="Return"/>.</returns>
        internal static Value[] Rent(int length)
        {
            Stack<Value[]> free = _free ??= new Stack<Value[]>();
            // Discard any popped buffer too small for this request: the pool then converges to max-arity
            // buffers (a small request happily reuses a large one) and stops allocating after warm-up.
            while (free.Count > 0)
            {
                Value[] buffer = free.Pop();
                if (buffer.Length >= length) return buffer;
            }
            return new Value[length];
        }

        /// <summary>Return a buffer previously handed out by <see cref="Rent"/> on this same thread.</summary>
        /// <param name="buffer">The exact array from the paired <see cref="Rent"/>.</param>
        internal static void Return(Value[] buffer)
        {
            // Clear before pooling so a returned buffer roots none of its arguments. A Value can hold a
            // string / list / dictionary, and the *oversized tail* (slots past the current request's arity,
            // hidden by the caller's AsSpan(0, count)) would otherwise retain a feature-owned graph for the
            // worker thread's lifetime. Clearing the whole array is O(arity) — negligible, and it keeps every
            // pooled buffer reference-free.
            System.Array.Clear(buffer, 0, buffer.Length);
            (_free ??= new Stack<Value[]>()).Push(buffer);
        }
    }
}
