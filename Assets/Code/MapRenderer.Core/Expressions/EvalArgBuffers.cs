using System.Collections.Generic;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// A per-thread free-list of <see cref="Value"/> scratch arrays for <see cref="FunctionExpression"/>'s
    /// evaluated-argument buffers, replacing a <c>new Value[]</c> on every operator evaluation (the hottest
    /// managed allocation during feature selection: features × filter-nodes, per tile).
    /// </summary>
    /// <remarks>
    /// Non-local invariant: this is a free-list of whole arrays, not one growable buffer, because the shared
    /// expression tree evaluates on several threads (hence <c>[ThreadStatic]</c>) and evaluation nests, so two
    /// frames on one thread hold buffers at once. Every <see cref="Rent"/> pairs with a <see cref="Return"/> in a
    /// <c>finally</c>, because an argument can throw mid-fill and a leak drains the pool. A rented array may be
    /// longer than requested, so the caller slices <c>AsSpan(0, n)</c>; <see cref="Return"/> clears it.
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
            // Clear before pooling: a Value can hold a string/list/dictionary, and the oversized tail past this
            // request's arity would otherwise root a feature-owned graph for the worker thread's lifetime.
            System.Array.Clear(buffer, 0, buffer.Length);
            (_free ??= new Stack<Value[]>()).Push(buffer);
        }
    }
}
