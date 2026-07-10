// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
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
    /// S19 Slice 4 — T7: RTL single-line correctness. Reuses S18's "marhaba" (Arabic for "hello")
    /// golden setup from <c>TextShapingTests</c> (same fixtures, same shaper) so this test operates on
    /// a real, independently-verified visual-order <see cref="ShapedRun"/> rather than a synthetic one.
    /// </summary>
    [TestFixture]
    public class TextRtlLayoutTests
    {
        private const float Tolerance = 1e-3f;

        // "marhaba" (Arabic for "hello") as explicit codepoint escapes -- avoids embedding a raw RTL
        // string in this (LTR) source file (mirrors TextShapingTests). meem (U+0645), reh (U+0631),
        // hah (U+062D), beh (U+0628), alef (U+0627), in that logical (typed) order.
        private const string Marhaba = "\u0645\u0631\u062D\u0628\u0627";

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

        private sealed class FixtureGlyphMetricsProvider : IGlyphMetricsProvider
        {
            private readonly Dictionary<uint, float> _advances;
            public FixtureGlyphMetricsProvider(IReadOnlyDictionary<uint, float> advances) => _advances = new Dictionary<uint, float>(advances);
            public bool TryGetAdvance(uint codepoint, out float advance) => _advances.TryGetValue(codepoint, out advance);
        }

        /// <summary>Builds a real <see cref="GlyphAtlas"/> (and a matching advance-only metrics provider for shaping) from BOTH presentation-form fixture ranges.</summary>
        private static (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) BuildPresentationFormAtlas()
        {
            FontStackGlyphs presentationFormsA = GlyphPbfDecoder.Decode(LoadFixture("64256-64511.pbf.bytes")).Stacks[0];
            FontStackGlyphs presentationFormsB = GlyphPbfDecoder.Decode(LoadFixture("65024-65279.pbf.bytes")).Stacks[0];

            var atlas = new GlyphAtlas();
            var advances = new Dictionary<uint, float>();
            foreach (var kv in presentationFormsA.Glyphs) { atlas.Append(kv.Value); advances[kv.Key] = kv.Value.Advance; }
            foreach (var kv in presentationFormsB.Glyphs) { atlas.Append(kv.Value); advances[kv.Key] = kv.Value.Advance; }

            return (atlas, new FixtureGlyphMetricsProvider(advances));
        }

        private static ShapedRun ShapeMarhaba(IGlyphMetricsProvider metrics)
        {
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = Marhaba, FontStack = new FontStack { Names = new[] { "Noto Sans Regular" } }, Metrics = metrics };
            return shaper.Shape(in request);
        }

        // =========================================================================================
        // T7 — RTL single-line correctness: forced single line, correct total advance, first VISUAL
        // glyph at the left edge.
        // =========================================================================================
        [Test]
        public void Layout_Rtl_SingleLine_FirstVisualGlyphAtLeftEdge_AndTotalAdvanceMatches()
        {
            (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) = BuildPresentationFormAtlas();
            ShapedRun run = ShapeMarhaba(metrics);

            Assert.AreEqual(TextDirection.RightToLeft, run.Direction, "pure Arabic text must resolve to RTL (S18 T1)");
            Assert.AreEqual(5, run.Glyphs.Count);
            // Sanity: reuse S18's own golden -- the first VISUAL glyph is ALEF (U+FE8E). If this ever
            // regresses it means S18 changed, not S19 -- fail loudly here rather than silently.
            Assert.AreEqual(0xFE8Eu, run.Glyphs[0].AtlasCodepoint, "S18 golden: first visual glyph is ALEF final");

            // Anchor=Left (hAlign=0) -> no horizontal anchor shift, so quads land at their raw pen positions.
            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Left,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            TextLayoutResult result = TextQuadLayout.Layout(run, atlas, in options);

            Assert.AreEqual(1, result.LineCount, "RTL is forced single-line in S19 (decision 4 / fork 3)");
            Assert.AreEqual(5, result.Quads.Count, "no whitespace in \"marhaba\" -- one quad per glyph");

            // First visual glyph (ALEF) sits at the line's left edge: penX = 0.
            Assert.IsTrue(atlas.TryGetEntry(run.Glyphs[0].AtlasCodepoint, out GlyphAtlasEntry entryFirst));
            float expectedFirstMinX = 0f + entryFirst.Left - GlyphSdf.Buffer;
            Assert.AreEqual(expectedFirstMinX, result.Quads[0].TopLeft.x, Tolerance, "the first VISUAL glyph must sit at the line's left edge");

            // Pen accumulates the sum of ALL preceding glyphs' advances (in visual order) -- total
            // advance = sum of atlas entry Advances (letter-spacing = 0).
            float expectedTotalAdvance = 0f;
            foreach (PositionedGlyph g in run.Glyphs)
            {
                Assert.IsTrue(atlas.TryGetEntry(g.AtlasCodepoint, out GlyphAtlasEntry e));
                expectedTotalAdvance += e.Advance;
            }

            Assert.IsTrue(atlas.TryGetEntry(run.Glyphs[4].AtlasCodepoint, out GlyphAtlasEntry entryLast));
            float expectedLastMinX = (expectedTotalAdvance - entryLast.Advance) + entryLast.Left - GlyphSdf.Buffer;
            Assert.AreEqual(expectedLastMinX, result.Quads[4].TopLeft.x, Tolerance, "pen must accumulate every preceding glyph's advance, in visual order");
        }

        // =========================================================================================
        // Teeth: re-reversing the (already-visual-order) run would put MEEM (the LOGICALLY-first,
        // visually-LAST glyph) at the left edge instead of ALEF -- a different quad width, since the
        // two glyphs' cell sizes differ.
        // =========================================================================================
        [Test]
        public void Layout_Rtl_Teeth_DoesNotReReverseTheAlreadyVisualOrderRun()
        {
            (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) = BuildPresentationFormAtlas();
            ShapedRun run = ShapeMarhaba(metrics);

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Left,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };
            TextLayoutResult result = TextQuadLayout.Layout(run, atlas, in options);

            Assert.IsTrue(atlas.TryGetEntry(0xFE8Eu, out GlyphAtlasEntry entryAlef)); // visual position 0
            Assert.IsTrue(atlas.TryGetEntry(0xFEE3u, out GlyphAtlasEntry entryMeem)); // visual position 4
            Assert.AreNotEqual(entryAlef.CellSize.x, entryMeem.CellSize.x, "sanity: ALEF and MEEM cell widths must differ for this tooth to be decisive");

            float actualFirstWidth = result.Quads[0].BottomRight.x - result.Quads[0].TopLeft.x;
            Assert.AreEqual(entryAlef.CellSize.x, actualFirstWidth, Tolerance, "the first emitted quad must be ALEF's (visual position 0)");
            Assert.AreNotEqual(entryMeem.CellSize.x, actualFirstWidth, "a re-reversing implementation would put MEEM first instead of ALEF");
        }

        // =========================================================================================
        // Mis-anchoring tooth: RTL respects the same anchor math as LTR -- Left vs Center anchor
        // differ by exactly the standard -0.5*lineWidth delta (T2's formula), not some RTL-special path.
        // =========================================================================================
        [Test]
        public void Layout_Rtl_AnchorMathMatchesTheGeneralFormula()
        {
            (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) = BuildPresentationFormAtlas();
            ShapedRun run = ShapeMarhaba(metrics);

            var leftOptions = new TextLayoutOptions { Anchor = TextAnchor.Left, Offset = float2.zero, RadialOffset = 0f, Justify = TextJustify.Auto, MaxWidthEm = 10f, LineHeightEm = 1.2f, LetterSpacingEm = 0f };
            var centerOptions = new TextLayoutOptions { Anchor = TextAnchor.Center, Offset = float2.zero, RadialOffset = 0f, Justify = TextJustify.Auto, MaxWidthEm = 10f, LineHeightEm = 1.2f, LetterSpacingEm = 0f };

            TextLayoutResult left = TextQuadLayout.Layout(run, atlas, in leftOptions);
            TextLayoutResult center = TextQuadLayout.Layout(run, atlas, in centerOptions);

            float lineWidth = 0f;
            foreach (PositionedGlyph g in run.Glyphs)
            {
                Assert.IsTrue(atlas.TryGetEntry(g.AtlasCodepoint, out GlyphAtlasEntry e));
                lineWidth += e.Advance;
            }

            for (int i = 0; i < left.Quads.Count; i++)
            {
                Assert.AreEqual(-0.5f * lineWidth, center.Quads[i].TopLeft.x - left.Quads[i].TopLeft.x, Tolerance, $"quad {i}: Center-Left anchor delta");
            }
        }
    }
}
