// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Rule L2 of the Unicode Bidirectional Algorithm: the visual order of one line from its embedding levels.
    /// The layouts call it once per line after line breaking, so a long label wraps in logical order and each
    /// line then reorders alone.
    /// </summary>
    public static class BidiReorder
    {
        /// <summary>
        /// Writes the visual order of the <paramref name="count"/> glyphs that start at <paramref name="start"/> to
        /// <paramref name="visualToLogical"/>: element <c>v</c> is the offset from <paramref name="start"/> of the glyph at
        /// visual position <c>v</c>, counted from the left.
        /// </summary>
        public static void ReorderLine(IReadOnlyList<byte> levels, int start, int count, Span<int> visualToLogical)
        {
            if (visualToLogical.Length < count) throw new ArgumentException("The visual order span is shorter than the line.");
            int highest = 0;
            int lowestOdd = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                visualToLogical[i] = i;
                int level = levels[start + i];
                if (level > highest) highest = level;
                if ((level & 1) == 1 && level < lowestOdd) lowestOdd = level;
            }

            for (int level = highest; level >= lowestOdd && level > 0; level--)
            {
                int i = 0;
                while (i < count)
                {
                    if (levels[start + visualToLogical[i]] < level) { i++; continue; }
                    int end = i;
                    while (end < count && levels[start + visualToLogical[end]] >= level) end++;
                    visualToLogical.Slice(i, end - i).Reverse();
                    i = end;
                }
            }
        }

        /// <summary>
        /// Like <see cref="ReorderLine"/> for the glyph range that starts at <paramref name="start"/>, with the
        /// range split into segments after every paragraph separator (P1). Each segment reorders alone.
        /// </summary>
        public static void ReorderSegments(
            IReadOnlyList<PositionedGlyph> glyphs, IReadOnlyList<byte> levels, int start, int count, Span<int> visualToLogical)
        {
            int segmentStart = 0;
            for (int i = 0; i < count; i++)
            {
                if (i < count - 1 && !IsParagraphSeparator(glyphs[start + i].AtlasCodepoint)) continue;
                int length = i + 1 - segmentStart;
                ReorderLine(levels, start + segmentStart, length, visualToLogical.Slice(segmentStart, length));
                for (int k = segmentStart; k <= i; k++) visualToLogical[k] += segmentStart;
                segmentStart = i + 1;
            }
        }

        /// <summary>True for the code points whose Bidi_Class is B: the line and paragraph separators.</summary>
        private static bool IsParagraphSeparator(uint codepoint)
            => codepoint == 0x0A || codepoint == 0x0D || (codepoint >= 0x1C && codepoint <= 0x1E)
               || codepoint == 0x85 || codepoint == 0x2029;
    }
}
