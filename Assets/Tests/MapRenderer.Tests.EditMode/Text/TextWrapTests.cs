// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S19 Slice 3 — T5 (greedy word-wrap, golden line assignment) and T6 (whitespace advances the
    /// pen but emits no quad).
    /// </summary>
    [TestFixture]
    public class TextWrapTests
    {
        private const float Tolerance = 1e-3f;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = TextDirection.LeftToRight };
        }

        // =========================================================================================
        // T5 — greedy word-wrap breaks at the correct WORD boundary (never mid-word), golden line
        // assignment. Three 2-glyph words ("Aa" x3) separated by single spaces: the chosen max-width
        // is derived generically (2.5*wordWidth + 1.5*spaceWidth) so it always lands strictly between
        // "word1 space word2" (fits) and adding "space word3" (overflows) -- independent of the exact
        // fixture metrics.
        // =========================================================================================
        [Test]
        public void Layout_GreedyWrap_BreaksAtCorrectWordBoundary()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);

            ShapedRun run = MakeRun(
                ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance),   // word1: glyphs 0,1
                ((uint)' ', entrySpace.Advance),                                // ws:    glyph  2
                ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance),   // word2: glyphs 3,4
                ((uint)' ', entrySpace.Advance),                                // ws:    glyph  5
                ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance)    // word3: glyphs 6,7
            );

            float wordWidth = entryA.Advance + entryLowerA.Advance;
            float spaceWidth = entrySpace.Advance;
            float maxWidthPx = 2.5f * wordWidth + 1.5f * spaceWidth;
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = maxWidthEm,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(2, result.LineCount, "word1+space+word2 fits; adding space+word3 must overflow to a new line");
            Assert.AreEqual(6, resultQuads.Count, "8 glyphs minus the 2 whitespace glyphs (never quaded) = 6");

            // Golden line map: word1+word2 (4 quads) on line 0, word3 (2 quads) on line 1.
            int[] expectedLineIndices = { 0, 0, 0, 0, 1, 1 };
            for (int i = 0; i < expectedLineIndices.Length; i++)
            {
                Assert.AreEqual(expectedLineIndices[i], resultQuads[i].LineIndex, $"quad {i} LineIndex");
            }

            // Teeth: a no-wrap impl would put everything on line 0.
            Assert.IsTrue(resultQuads[4].LineIndex != resultQuads[0].LineIndex, "a no-wrap impl fails: word3 must be on a different line than word1");

            // Teeth: a break-mid-word impl would split word2's 'A'/'a' across two different LineIndex
            // values (they must both be on line 0, adjacent to word1's glyphs, not word3's line).
            Assert.AreEqual(resultQuads[2].LineIndex, resultQuads[3].LineIndex, "word2's two glyphs must share the same line (no mid-word break)");
            Assert.AreNotEqual(resultQuads[3].LineIndex, resultQuads[4].LineIndex, "word2 and word3 must be on different lines");
        }

        [Test]
        public void Layout_GreedyWrap_NeverBreaksBeforeTheFirstWordOnALine()
        {
            // A single word wider than max-width must still be placed whole on line 0 (plan (e): "first
            // word always on line 0" -- generalizes to "never break before a line's first word").
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 0.01f, // absurdly narrow -- narrower than even a single glyph
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(1, result.LineCount, "a single word (no interior whitespace) can never be split -- one line regardless of max-width");
            Assert.AreEqual(2, resultQuads.Count);
            Assert.AreEqual(0, resultQuads[0].LineIndex);
            Assert.AreEqual(0, resultQuads[1].LineIndex);
        }

        // =========================================================================================
        // T6 — whitespace advances the pen but emits no quad; the following glyph is positioned as
        // if the space consumed its full advance.
        // =========================================================================================
        [Test]
        public void Layout_Whitespace_AdvancesPenButEmitsNoQuad()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);

            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)' ', entrySpace.Advance), ((uint)'a', entryLowerA.Advance));

            // Anchor=TopLeft + Justify=Left (single line, so justify is moot anyway) -> quads land at
            // their raw (unshifted) coordinates, so the golden below needs no anchor-shift term.
            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.TopLeft,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Left,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(2, resultQuads.Count, "3 glyphs minus the 1 whitespace glyph (never quaded) = 2 (N-1)");

            float penAfterSpace = entryA.Advance + entrySpace.Advance; // pen consumes the space's FULL advance
            float expectedLowerAMinX = penAfterSpace + entryLowerA.Left - GlyphSdf.Buffer;

            Assert.AreEqual(expectedLowerAMinX, resultQuads[1].TopLeft.x, Tolerance,
                "the glyph after the space must be positioned as if the space consumed its full advance");

            // Teeth: an impl that drops the space's advance would place 'a' as if right after 'A' alone.
            float wrongMinXIfSpaceDropped = entryA.Advance + entryLowerA.Left - GlyphSdf.Buffer;
            Assert.AreNotEqual(wrongMinXIfSpaceDropped, resultQuads[1].TopLeft.x, "dropping the space's advance would mis-position the following glyph");
        }
    }
}
