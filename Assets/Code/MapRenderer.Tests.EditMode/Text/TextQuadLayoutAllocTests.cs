// Unity-only (UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() — the ONLY trustworthy
// allocation meter on Unity Mono per docs/lessons-learned.md's "Measuring per-frame GC allocation" note).
// Excluded from core-tests.csproj.
//
// S19 Slice 4: the layout hot path (steady, single-line, no-wrap) must allocate ZERO managed garbage
// once the caller's output List capacity has stabilized -- see TextQuadLayout's class doc for why (an
// in-place List index write per emitted quad, no auxiliary per-line array; the word-wrap lookahead
// branch is scoped out of this steady path entirely, mirroring CodepointTextShaper's no-alloc
// guarantee being scoped to its own steady LTR path, not every input).

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    [TestFixture]
    public class TextQuadLayoutAllocTests
    {
        // =========================================================================================
        // Steady no-wrap path: a short run with no whitespace at all -- the word-wrap lookahead
        // (MeasureRange) branch is never even entered, so this isolates the plain per-glyph placement
        // + per-line justify bake-in + final block-shift pass, all of which are in-place List writes.
        // =========================================================================================
        [Test]
        public void Layout_SteadyNoWrapPath_IntoCallerBuffer_AllocatesNoGCMemory()
        {
            var atlas = new GlyphAtlas();
            for (uint codepoint = 1; codepoint <= 5; codepoint++)
            {
                atlas.Append(MakeSyntheticGlyph(codepoint, width: 10, height: 12, advance: 14));
            }

            var glyphs = new List<PositionedGlyph>();
            for (uint codepoint = 1; codepoint <= 5; codepoint++)
            {
                glyphs.Add(new PositionedGlyph { AtlasCodepoint = codepoint, XAdvance = 14f, Cluster = (int)codepoint - 1 });
            }
            var run = new ShapedRun { Glyphs = glyphs, Direction = TextDirection.LeftToRight };

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 10f, // default -- wide enough that this 5-glyph run never wraps
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };
            var output = new List<SymbolQuad>(8);

            // Warm-up: the FIRST call legitimately allocates (List<T>'s backing array grows from
            // empty). Stabilizes `output`'s capacity so the measured call below reuses it.
            TextQuadLayout.Layout(run, atlas, in options, output);

            // Block-bodied lambda (not an expression lambda): the overload returns a value
            // (TextLayoutBounds), and Assert.That needs a void TestDelegate here.
            Assert.That(() => { TextQuadLayout.Layout(run, atlas, in options, output); }, Is.Not.AllocatingGCMemory(),
                "Layout(..., output) must not allocate on the steady no-wrap path once `output`'s capacity " +
                "has stabilized from the warm-up call -- no whitespace glyph in this run means the word-wrap " +
                "lookahead branch (MeasureRange) is never entered at all.");
        }

        private static SdfGlyph MakeSyntheticGlyph(uint codepoint, int width, int height, int advance)
        {
            int2 cellSize = new int2(width + 2 * GlyphSdf.Buffer, height + 2 * GlyphSdf.Buffer);
            return new SdfGlyph
            {
                Codepoint = codepoint,
                Width = width,
                Height = height,
                Left = 0,
                Top = 0,
                Advance = advance,
                Bitmap = new byte[cellSize.x * cellSize.y],
            };
        }
    }
}
