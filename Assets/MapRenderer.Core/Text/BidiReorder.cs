// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// S18 decision 8: single-run bidi ONLY — reverses a purely-RTL logical-order sequence to visual
    /// order. This is NOT full UAX #9 (mixed LTR/RTL/neutral runs, digit shaping, embedding levels);
    /// that is a deferred follow-up that will adopt a managed ICU-derived library (ICU4N /
    /// BidiReshapeSharp), mirroring MapLibre's own <c>mapbox-gl-rtl-text</c> (an ICU subset).
    /// </summary>
    public static class BidiReorder
    {
        /// <summary>
        /// Returns <paramref name="logicalOrder"/> unchanged for <see cref="TextDirection.LeftToRight"/>
        /// (logical order == visual order for a pure LTR run), or a full reversal for
        /// <see cref="TextDirection.RightToLeft"/> (logical order reversed == visual order for a pure
        /// RTL run — the entire single-run bidi model S18 Slice 3 implements).
        /// </summary>
        public static IReadOnlyList<PositionedGlyph> ToVisualOrder(
            IReadOnlyList<PositionedGlyph> logicalOrder, TextDirection direction)
        {
            if (direction == TextDirection.LeftToRight) return logicalOrder;

            int n = logicalOrder.Count;
            var visual = new PositionedGlyph[n];
            for (int i = 0; i < n; i++)
            {
                visual[i] = logicalOrder[n - 1 - i];
            }
            return visual;
        }
    }
}
