using System.Collections.Generic;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// A per-thread free-list of <c>int[]</c> scratch arrays for <see cref="FeatureSelector"/>'s per-layer
    /// key-binding step (the string→id key hoist), so binding a filter's <c>KeyLayout</c> against a
    /// resolver does not <c>new int[]</c> once per <c>SelectFeatures</c> call. Same shape as
    /// <c>MapRenderer.Core.Expressions.EvalArgBuffers</c> — a free-list of WHOLE arrays, <c>[ThreadStatic]</c>
    /// because <see cref="FeatureSelector.SelectFeatures(Core.Style.StyleLayer, Tiles.IDecodedTile, double)"/>
    /// can run on any thread — a ThreadPool worker under the desktop/editor policy, or the MAIN THREAD itself
    /// under the WebGL (Inline) one — and each thread must own its own free-list regardless of which.
    /// </summary>
    /// <remarks>
    /// <b>Contract:</b> every <see cref="Rent"/> MUST be paired with a <see cref="Return"/> in a
    /// <c>finally</c>. Rented arrays are length ≥ the request; the caller indexes by slot and never reads
    /// <c>.Length</c> (an oversized buffer is transparent — the same rule <c>EvalArgBuffers</c> states).
    /// </remarks>
    internal static class KeyBindingBuffers
    {
        [System.ThreadStatic] private static Stack<int[]> _free;

        /// <summary>Borrow a buffer of length ≥ <paramref name="length"/> for the current thread.</summary>
        internal static int[] Rent(int length)
        {
            Stack<int[]> free = _free ??= new Stack<int[]>();
            while (free.Count > 0)
            {
                int[] buffer = free.Pop();
                if (buffer.Length >= length) return buffer;
            }
            return new int[length];
        }

        /// <summary>Return a buffer previously handed out by <see cref="Rent"/> on this same thread. Unlike
        /// <c>EvalArgBuffers.Return</c>, this does NOT clear the array first — an <c>int</c> element roots
        /// nothing, so a stale oversized-tail value (never read; the node indexes by slot, never
        /// <c>.Length</c>) carries no reference-graph retention risk to clear against.</summary>
        internal static void Return(int[] buffer)
        {
            (_free ??= new Stack<int[]>()).Push(buffer);
        }
    }
}
