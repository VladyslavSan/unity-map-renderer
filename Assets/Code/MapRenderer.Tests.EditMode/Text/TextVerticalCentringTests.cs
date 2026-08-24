// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stage 4 (docs/road-shields-design.md §11, G8/D12) — a centred text block is centred on its INK,
    /// not on a line box. Teeth V1-V7 match §11's table of the same numbers, plus V0 (a precondition on
    /// this file's own ink-band helper) and V9 (the fixture-vs-constant guard). §11's V8 is not here — it
    /// is the "S19 offset/justify/RTL tests still pass verbatim" guard, which lives in those files. Keep
    /// the numbering in sync with the doc.
    /// </summary>
    [TestFixture]
    public class TextVerticalCentringTests
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

        private static ShapedRun MakeRun(TextDirection direction, params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = direction };
        }

        private static TextLayoutOptions MakeOptions(TextAnchor anchor = TextAnchor.Center, float lineHeightEm = 1.2f, float maxWidthEm = 10f)
            => new TextLayoutOptions
            {
                Anchor = anchor,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = maxWidthEm,
                LineHeightEm = lineHeightEm,
                LetterSpacingEm = 0f,
            };

        private static void AssertAllQuadsEqual(IReadOnlyList<SymbolQuad> a, IReadOnlyList<SymbolQuad> b, string label)
        {
            Assert.AreEqual(a.Count, b.Count, $"{label}: quad counts must match");
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].TopLeft.x, b[i].TopLeft.x, Tolerance, $"{label}: quad {i} TopLeft.x");
                Assert.AreEqual(a[i].TopLeft.y, b[i].TopLeft.y, Tolerance, $"{label}: quad {i} TopLeft.y");
                Assert.AreEqual(a[i].BottomRight.x, b[i].BottomRight.x, Tolerance, $"{label}: quad {i} BottomRight.x");
                Assert.AreEqual(a[i].BottomRight.y, b[i].BottomRight.y, Tolerance, $"{label}: quad {i} BottomRight.y");
            }
        }

        // The INK band of a laid-out block: the quads' cell band inset by GlyphSdf.Buffer on both edges
        // (PlaceGlyph pads symmetrically, so the cell centre IS the ink centre — asserted by V0 below).
        private static (float min, float max) InkBandY(IReadOnlyList<SymbolQuad> quads)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (SymbolQuad q in quads)
            {
                min = math.min(min, q.BottomRight.y + GlyphSdf.Buffer);
                max = math.max(max, q.TopLeft.y - GlyphSdf.Buffer);
            }
            return (min, max);
        }

        // =========================================================================================
        // V0 — precondition: InkBandY really measures ink (cell inset by Buffer), not the padded cell.
        // =========================================================================================
        [Test]
        public void InkBand_IsCellBandInsetByBuffer_Precondition()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            // Top anchor => globalY = 0, so quad y is baselineY-relative with no anchor shift --
            // isolates the ink-band computation from the anchor math this stage changes.
            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top), resultQuads);
            (float min, float max) = InkBandY(resultQuads);

            int bareHeight = entryFive.CellSize.y - 2 * GlyphSdf.Buffer;
            Assert.AreEqual(entryFive.Top, max, Tolerance, "ink top must equal the atlas entry's own Top metric");
            Assert.AreEqual(entryFive.Top - bareHeight, min, Tolerance, "ink bottom must equal Top - bare glyph height");
        }

        // =========================================================================================
        // V1 — the shield defect itself: a centred digit's ink must be centred on the anchor. RED
        // today at -3.1. The ±0.5 window is §11's acceptance band, deliberately wider than the actual
        // residual: with the cap height at 17/24 em the '5' lands at EXACTLY 0 (its ink spans
        // [-8.5, +8.5] about the anchor), because its 17 px bare height is the very cap the constant
        // measures. The band is there for glyphs whose ink is not exactly cap-high, not for slack here.
        // =========================================================================================
        [Test]
        public void CentreAnchor_SingleLineDigit_InkIsCentredOnTheAnchor()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, resultQuads);
            (float min, float max) = InkBandY(resultQuads);
            float centre = 0.5f * (min + max);

            Assert.LessOrEqual(math.abs(centre), 0.5f, $"centred ink must sit within 0.5 baked px of the anchor, was {centre}");
        }

        // =========================================================================================
        // V2 — the shield claim itself: text and icon centring must agree.
        // =========================================================================================
        [Test]
        public void CentreAnchor_TextInkCentre_CoincidesWithCentredIconInkCentre()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var textResultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, textResultQuads);
            (float textMin, float textMax) = InkBandY(textResultQuads);
            float textCentre = 0.5f * (textMin + textMax);

            // A synthetic even-sized sprite: IconQuadLayout centres box == ink exactly (design doc §11).
            var spriteEntry = new SpriteEntry { X = 0, Y = 0, Width = 20, Height = 20, PixelRatio = 1f, Sdf = false };
            SymbolQuad iconQuad = IconQuadLayout.Layout(in spriteEntry, new int2(64, 64), 1f, TextAnchor.Center, float2.zero);
            float iconCentre = 0.5f * (iconQuad.TopLeft.y + iconQuad.BottomRight.y);

            Assert.AreEqual(iconCentre, textCentre, 0.5f,
                $"a centred digit's text ink centre must coincide with a centred icon's ink centre (icon={iconCentre}, text={textCentre})");

            // …and the padded-repack border must not disturb that: SpriteSheet hands every drawable sprite a
            // one-texel transparent border, IconQuadLayout draws it, and the growth must be SYMMETRIC — an
            // asymmetric skirt would shift a shield's icon off the text it is centred behind.
            var paddedEntry = new SpriteEntry { X = 1, Y = 1, Width = 20, Height = 20, PixelRatio = 1f, Padding = 1 };
            SymbolQuad paddedQuad = IconQuadLayout.Layout(in paddedEntry, new int2(64, 64), 1f, TextAnchor.Center, float2.zero);
            float paddedCentre = 0.5f * (paddedQuad.TopLeft.y + paddedQuad.BottomRight.y);
            float paddedHeight = paddedQuad.TopLeft.y - paddedQuad.BottomRight.y;

            Assert.AreEqual(iconCentre, paddedCentre, 1e-5f,
                "the transparent border must grow the quad symmetrically — the content's centre must not move");
            Assert.AreEqual(20f + 2f, paddedHeight, 1e-5f,
                "the drawn quad must be the content plus one texel of border on EACH side");
        }

        // =========================================================================================
        // V3 — line-height independence: a single line has nothing to stack, so a centred single-line
        // block must not move when text-line-height changes. RED today (9.6 px) and RED against any
        // "subtract a constant from the old formula" impl.
        // =========================================================================================
        [Test]
        public void CentreAnchor_SingleLine_IsIndependentOfLineHeight()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var tightQuads = new List<SymbolQuad>();
            var looseQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(lineHeightEm: 1.2f), tightQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(lineHeightEm: 2.0f), looseQuads);

            AssertAllQuadsEqual(tightQuads, looseQuads, "a single-line Center block must not move when line-height changes");
        }

        // =========================================================================================
        // V4 — multi-line: a 2-line centred block's line-0 and line-1 ink centres are symmetric about
        // the anchor, and their separation is exactly one lineHeightPx (line spacing is untouched).
        // =========================================================================================
        [Test]
        public void CentreAnchor_TwoLines_AreSymmetricAboutTheAnchor()
        {
            // The SAME glyph on both lines: symmetry is a property of the block's formula, not of
            // each line's own ink shape -- putting a cap ('A') on one line and an x-height glyph ('a')
            // on the other would compare two DIFFERENT ink profiles and mask the property under test
            // (their real ink centres differ by ~2px even under a perfectly symmetric formula).
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' ']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight,
                ((uint)'5', entryFive.Advance), ((uint)' ', entrySpace.Advance), ((uint)'5', entryFive.Advance));

            // maxWidthPx strictly between "5" alone and "5 5" combined -- forces exactly one break.
            float maxWidthPx = entryFive.Advance + entrySpace.Advance * 0.5f;
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, MakeOptions(maxWidthEm: maxWidthEm), resultQuads);
            Assert.AreEqual(2, result.LineCount, "the chosen max-width must force exactly a 2-line wrap");
            Assert.AreEqual(2, resultQuads.Count, "one glyph per line -- '5' on line 0, '5' on line 1");
            Assert.AreEqual(0, resultQuads[0].LineIndex);
            Assert.AreEqual(1, resultQuads[1].LineIndex);

            (float min0, float max0) = InkBandY(new[] { resultQuads[0] });
            (float min1, float max1) = InkBandY(new[] { resultQuads[1] });
            float centre0 = 0.5f * (min0 + max0);
            float centre1 = 0.5f * (min1 + max1);
            float mean = 0.5f * (centre0 + centre1);

            Assert.LessOrEqual(math.abs(mean), 0.5f, $"the two lines' ink centres must be symmetric about the anchor, mean was {mean}");
            Assert.AreEqual(lineHeightPx, centre0 - centre1, Tolerance, "the two lines' ink centres must be separated by exactly one lineHeightPx");
        }

        // =========================================================================================
        // V5 — guard: Top/Bottom (and the four corners) keep their block-edge behaviour, hand-computed
        // from atlas entries, no production constants read back.
        // =========================================================================================
        [Test]
        public void TopAndBottomAnchors_KeepTheirBlockEdges_Golden()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance));

            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            var topQuads = new List<SymbolQuad>();
            var bottomQuads = new List<SymbolQuad>();
            var topLeftQuads = new List<SymbolQuad>();
            var topRightQuads = new List<SymbolQuad>();
            var bottomLeftQuads = new List<SymbolQuad>();
            var bottomRightQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top), topQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Bottom), bottomQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft), topLeftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopRight), topRightQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.BottomLeft), bottomLeftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.BottomRight), bottomRightQuads);

            // Top anchor => globalY = 0, so the cell's top edge sits at entry.Top + Buffer.
            float expectedTopCellTopY = entryA.Top + GlyphSdf.Buffer;
            Assert.AreEqual(expectedTopCellTopY, topQuads[0].TopLeft.y, Tolerance, "Top anchor: block top edge at y=0");

            // Bottom anchor => globalY = lineCount*lineHeightPx = lineHeightPx (lineCount==1), added to the
            // pre-shift cell edge (entry.Top + Buffer - CellSize.y).
            float expectedBottomCellBottomY = lineHeightPx + (entryA.Top + GlyphSdf.Buffer - entryA.CellSize.y);
            Assert.AreEqual(expectedBottomCellBottomY, bottomQuads[0].BottomRight.y, Tolerance, "Bottom anchor: block bottom edge at y = lineHeightPx");

            Assert.AreEqual(topQuads[0].TopLeft.y, topLeftQuads[0].TopLeft.y, Tolerance, "TopLeft shares Top's y");
            Assert.AreEqual(topQuads[0].TopLeft.y, topRightQuads[0].TopLeft.y, Tolerance, "TopRight shares Top's y");
            Assert.AreEqual(bottomQuads[0].TopLeft.y, bottomLeftQuads[0].TopLeft.y, Tolerance, "BottomLeft shares Bottom's y");
            Assert.AreEqual(bottomQuads[0].TopLeft.y, bottomRightQuads[0].TopLeft.y, Tolerance, "BottomRight shares Bottom's y");
        }

        // =========================================================================================
        // V6 — guard: the centred block's position must not depend on which glyphs are actually
        // present (kills the rejected ink-bounds-of-the-run metric).
        // =========================================================================================
        [Test]
        public void CentreAnchor_BlockPosition_DoesNotDependOnWhichGlyphsArePresent()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            GlyphAtlasEntry entryZero = atlas.Append(latin.Glyphs[(uint)'0']);
            GlyphAtlasEntry entryG = atlas.Append(latin.Glyphs[(uint)'g']);
            GlyphAtlasEntry entryX = atlas.Append(latin.Glyphs[(uint)'x']);

            ShapedRun runFive = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));
            ShapedRun runFiveZero = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance), ((uint)'0', entryZero.Advance));
            ShapedRun runFiveG = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance), ((uint)'g', entryG.Advance));
            ShapedRun runFiveX = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance), ((uint)'x', entryX.Advance));

            var fiveQuads = new List<SymbolQuad>();
            var fiveZeroQuads = new List<SymbolQuad>();
            var fiveGQuads = new List<SymbolQuad>();
            var fiveXQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(runFive, atlas, in TextLayoutOptions.Default, fiveQuads);
            TextQuadLayout.Layout(runFiveZero, atlas, in TextLayoutOptions.Default, fiveZeroQuads);
            TextQuadLayout.Layout(runFiveG, atlas, in TextLayoutOptions.Default, fiveGQuads);
            TextQuadLayout.Layout(runFiveX, atlas, in TextLayoutOptions.Default, fiveXQuads);
            float yFive = fiveQuads[0].TopLeft.y;
            float yFiveZero = fiveZeroQuads[0].TopLeft.y;
            float yFiveG = fiveGQuads[0].TopLeft.y;
            float yFiveX = fiveXQuads[0].TopLeft.y;

            Assert.AreEqual(yFive, yFiveZero, Tolerance, "'5' must be at the same y in \"5\" and \"50\"");
            Assert.AreEqual(yFive, yFiveG, Tolerance, "'5' must be at the same y in \"5\" and \"5g\" (descender)");
            Assert.AreEqual(yFive, yFiveX, Tolerance, "'5' must be at the same y in \"5\" and \"5x\" (x-height)");
        }

        // =========================================================================================
        // V7 — Left and Right (the other vertical-centre-carrying anchors) must shift vertically
        // exactly like Center; only x differs.
        // =========================================================================================
        [Test]
        public void LeftAndRightAnchors_ShiftVerticallyExactlyLikeCentre()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var leftQuads = new List<SymbolQuad>();
            var rightQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left), leftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right), rightQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Center), centerQuads);

            Assert.AreEqual(centerQuads[0].TopLeft.y, leftQuads[0].TopLeft.y, Tolerance, "Left must shift vertically exactly like Center");
            Assert.AreEqual(centerQuads[0].TopLeft.y, rightQuads[0].TopLeft.y, Tolerance, "Right must shift vertically exactly like Center");
            Assert.AreNotEqual(leftQuads[0].TopLeft.x, centerQuads[0].TopLeft.x, "sanity: x must actually differ between Left and Center");

            (float min, float max) = InkBandY(leftQuads);
            float centre = 0.5f * (min + max);
            Assert.LessOrEqual(math.abs(centre), 0.5f, $"Left-anchored ink must also be centred on the anchor, was {centre}");
        }

        // =========================================================================================
        // V9 — the constant is pinned to the FIXTURE, not just to itself. GlyphSdf.BaselineBelowReferencePx
        // is an assumption about how the glyph PBF was baked (the PBF carries no font-level metrics), so
        // nothing in the layout math can detect it going wrong: every other tooth here measures positions
        // that are all derived from the same constant, and would stay green if the fixture were regenerated
        // from a font baked against a different ascent. The other shipped ranges modally measure 27 (§11),
        // so this is a live hazard, and its symptom is silent — every centred symbol would sit one baked px
        // low with a fully green suite. This re-derives the constant from a baseline-resting reference
        // glyph's OWN metrics: for such a glyph the ink bottom IS the baseline, so its distance below the
        // line's reference origin is (bare height - Top). A digit is used deliberately — a descender ('g')
        // or an above-baseline mark does not rest on the baseline and measures 30/32 or 19/23 instead.
        // =========================================================================================
        [Test]
        public void BaselineBelowReferencePx_IsReproducedByTheFixturesOwnGlyphMetrics()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);

            int bareHeight = entryFive.CellSize.y - 2 * GlyphSdf.Buffer;
            float baselineBelowReference = bareHeight - entryFive.Top;

            Assert.AreEqual(GlyphSdf.BaselineBelowReferencePx, baselineBelowReference, Tolerance,
                $"the committed fixture's baseline-resting '5' must reproduce GlyphSdf.BaselineBelowReferencePx " +
                $"({GlyphSdf.BaselineBelowReferencePx}); measured {baselineBelowReference} from bare height " +
                $"{bareHeight} and Top {entryFive.Top}. A fixture baked against a different ascent breaks the " +
                $"constant's premise and drops every centred label by the difference.");
        }
    }
}
