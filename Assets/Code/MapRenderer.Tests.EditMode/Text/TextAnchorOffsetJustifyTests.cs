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
    /// S19 Slice 2 — T2 (text-anchor shifts the whole multi-line block bbox, H and V), T3
    /// (text-offset / text-radial-offset, ems -&gt; baked px), T4 (text-justify incl. auto).
    /// </summary>
    [TestFixture]
    public class TextAnchorOffsetJustifyTests
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

        private static TextLayoutOptions MakeOptions(
            TextAnchor anchor = TextAnchor.Center,
            float2 offset = default,
            float radialOffset = 0f,
            TextJustify justify = TextJustify.Auto,
            float maxWidthEm = 10f,
            float lineHeightEm = 1.2f,
            float letterSpacingEm = 0f)
            => new TextLayoutOptions
            {
                Anchor = anchor,
                Offset = offset,
                RadialOffset = radialOffset,
                Justify = justify,
                MaxWidthEm = maxWidthEm,
                LineHeightEm = lineHeightEm,
                LetterSpacingEm = letterSpacingEm,
            };

        private static void AssertConstantDelta(IReadOnlyList<SymbolQuad> baseline, IReadOnlyList<SymbolQuad> shifted, float2 expectedDelta, string label)
        {
            Assert.AreEqual(baseline.Count, shifted.Count, $"{label}: quad counts must match");
            for (int i = 0; i < baseline.Count; i++)
            {
                Assert.AreEqual(expectedDelta.x, shifted[i].TopLeft.x - baseline[i].TopLeft.x, Tolerance, $"{label}: quad {i} TopLeft.x");
                Assert.AreEqual(expectedDelta.y, shifted[i].TopLeft.y - baseline[i].TopLeft.y, Tolerance, $"{label}: quad {i} TopLeft.y");
                Assert.AreEqual(expectedDelta.x, shifted[i].BottomRight.x - baseline[i].BottomRight.x, Tolerance, $"{label}: quad {i} BottomRight.x");
                Assert.AreEqual(expectedDelta.y, shifted[i].BottomRight.y - baseline[i].BottomRight.y, Tolerance, $"{label}: quad {i} BottomRight.y");
            }
        }

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

        // =========================================================================================
        // T2a — single line: TopLeft/Center/BottomRight differ by exactly the anchor delta. BottomRight
        // uses the block's bbox height (unchanged); Center uses the optical-centre shift (§11 D12) --
        // NOT the arithmetic midpoint of Top and BottomRight, which is the point of this stage.
        // =========================================================================================
        [Test]
        public void Anchor_SingleLine_BottomShiftsByBlockHeight_CentreByOpticalCentre()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            float lineWidth = entryA.Advance + entryLowerA.Advance;
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            var topLeftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            var bottomRightQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.TopLeft), topLeftQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.Center), centerQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.BottomRight), bottomRightQuads);

            // Optical-centre shift (§11 D12): GlyphSdf.BaselineBelowReferencePx (26) minus half a
            // cap height (0.5 * (17/24)em * 24 = 8.5) = 17.5 -- a hand-derived literal, not read
            // back from the production constants it exists to check.
            AssertConstantDelta(topLeftQuads, centerQuads, new float2(-0.5f * lineWidth, 17.5f), "Center - TopLeft");
            AssertConstantDelta(topLeftQuads, bottomRightQuads, new float2(-lineWidth, lineHeightPx), "BottomRight - TopLeft");

            TextLayoutOptions Opt(TextAnchor a) => MakeOptions(anchor: a);
        }

        // =========================================================================================
        // T2b — a 2-line run: BottomRight's vertical delta uses lineCount * lineHeight*24 (unchanged);
        // Center's uses the line-span midpoint (§11 D12) -- the midpoint between the FIRST line's
        // optical centre and the LAST line's, i.e. one line's span (0.5*lineHeightPx) above the
        // single-line optical-centre shift, NOT half of lineCount*lineHeight. Justify held constant
        // (Center) across variants so the pure anchor delta is isolated (see TextQuadLayout's class
        // doc: the per-line justify term cancels in a delta between two Layout() calls that share
        // options except Anchor).
        // =========================================================================================
        [Test]
        public void Anchor_TwoLineRun_BottomUsesLineCountTimesLineHeight_CentreUsesLineSpanMidpoint()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' ']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);

            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)' ', entrySpace.Advance), ((uint)'a', entryLowerA.Advance));

            // maxWidthPx strictly between "A" alone and "A a" combined -- forces exactly one break.
            float maxWidthPx = entryA.Advance + entrySpace.Advance * 0.5f;
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;
            float blockWidth = math.max(entryA.Advance, entryLowerA.Advance);
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;
            float blockHeight = 2 * lineHeightPx;

            // 17.5 is the single-line optical-centre shift derived in
            // Anchor_SingleLine_BottomShiftsByBlockHeight_CentreByOpticalCentre above; a 2-line block's
            // centre sits one line's span (0.5*lineHeightPx) above it.
            float deltaYCenterExpected = 17.5f + 0.5f * lineHeightPx;

            var topLeftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            var bottomRightQuads = new List<SymbolQuad>();
            TextLayoutBounds topLeft = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.TopLeft), topLeftQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.Center), centerQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.BottomRight), bottomRightQuads);

            Assert.AreEqual(2, topLeft.LineCount, "the chosen max-width must force exactly a 2-line wrap");

            float deltaYCenter = centerQuads[0].TopLeft.y - topLeftQuads[0].TopLeft.y;
            float deltaYBottomRight = bottomRightQuads[0].TopLeft.y - topLeftQuads[0].TopLeft.y;

            Assert.AreEqual(deltaYCenterExpected, deltaYCenter, Tolerance, "Center's vertical delta must be the line-span midpoint, not half of lineCount*lineHeight");
            Assert.AreEqual(blockHeight, deltaYBottomRight, Tolerance, "BottomRight's vertical delta must use lineCount*lineHeight");

            // Teeth: a per-line (not block-bbox) vertical anchor would use just ONE lineHeightPx for
            // BottomRight, and the OLD (rejected) box-midpoint formula would give 0.5*blockHeight for
            // Center -- both different, smaller/larger numbers that must NOT match.
            Assert.AreNotEqual(0.5f * lineHeightPx, deltaYCenter, "a per-line vertical anchor would use a single line's height, not the line-span midpoint");
            Assert.AreNotEqual(0.5f * blockHeight, deltaYCenter, "the old box-midpoint formula (0.5*blockHeight) must not match the optical-centre formula");
            Assert.AreNotEqual(lineHeightPx, deltaYBottomRight, "a per-line vertical anchor would use a single line's height, not the block's");

            AssertConstantDelta(topLeftQuads, centerQuads, new float2(-0.5f * blockWidth, deltaYCenterExpected), "Center - TopLeft (2-line)");
            AssertConstantDelta(topLeftQuads, bottomRightQuads, new float2(-blockWidth, blockHeight), "BottomRight - TopLeft (2-line)");

            TextLayoutOptions Opt(TextAnchor a) => MakeOptions(anchor: a, justify: TextJustify.Center, maxWidthEm: maxWidthEm);
        }

        // =========================================================================================
        // T3 — text-offset / text-radial-offset (ems -> baked px, x24).
        // =========================================================================================
        [Test]
        public void Offset_TranslatesEveryQuad_ByEmsTimes24()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var baselineQuads = new List<SymbolQuad>();
            var offsetXQuads = new List<SymbolQuad>();
            var offsetYQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(offset: float2.zero), baselineQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(offset: new float2(1f, 0f)), offsetXQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(offset: new float2(0f, 0.5f)), offsetYQuads);

            AssertConstantDelta(baselineQuads, offsetXQuads, new float2(24f, 0f), "text-offset [1,0]");
            AssertConstantDelta(baselineQuads, offsetYQuads, new float2(0f, 12f), "text-offset [0,0.5]");
        }

        [Test]
        public void RadialOffset_CornerAnchor_ResolvesToDiagonal_AndOverridesOffset()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance));

            var baselineQuads = new List<SymbolQuad>();
            var radialQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft), baselineQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, radialOffset: 1f), radialQuads);

            // TopLeft is a corner anchor (hAlign=0, vAlign=0): pushes away from the anchored edges,
            // i.e. further +x (away from the left edge) and further -y (away from the top edge),
            // split into a diagonal of magnitude RadialOffset/sqrt2 on each axis.
            float diagPx = 24f / math.SQRT2;
            AssertConstantDelta(baselineQuads, radialQuads, new float2(diagPx, -diagPx), "RadialOffset=1 @ TopLeft (corner)");

            // Radial overrides a nonzero Offset entirely (decision: "Radial overrides Offset if !=0").
            var radialWithIgnoredOffsetQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas,
                MakeOptions(anchor: TextAnchor.TopLeft, offset: new float2(5f, 5f), radialOffset: 1f), radialWithIgnoredOffsetQuads);
            AssertAllQuadsEqual(radialQuads, radialWithIgnoredOffsetQuads, "RadialOffset must override a nonzero Offset");
        }

        [Test]
        public void RadialOffset_PureAxisAnchors_ResolveToSingleAxis()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance));

            var baselineLeftQuads = new List<SymbolQuad>();
            var radialLeftQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left), baselineLeftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, radialOffset: 1f), radialLeftQuads);
            AssertConstantDelta(baselineLeftQuads, radialLeftQuads, new float2(24f, 0f), "RadialOffset=1 @ Left (pure x axis)");

            var baselineTopQuads = new List<SymbolQuad>();
            var radialTopQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top), baselineTopQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, radialOffset: 1f), radialTopQuads);
            AssertConstantDelta(baselineTopQuads, radialTopQuads, new float2(0f, -24f), "RadialOffset=1 @ Top (pure y axis)");
        }

        // =========================================================================================
        // T4 — text-justify (left/center/right) shifts each line's local start relative to the
        // other, and "auto" resolves from the anchor.
        // =========================================================================================
        [Test]
        public void Justify_ShiftsEachLine_RelativeToTheOthers()
        {
            (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) = MakeTwoLineFixture();

            var leftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            var rightQuads = new List<SymbolQuad>();
            TextLayoutBounds left = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Left, maxWidthEm: maxWidthEm), leftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Center, maxWidthEm: maxWidthEm), centerQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Right, maxWidthEm: maxWidthEm), rightQuads);

            Assert.AreEqual(2, left.LineCount);
            Assert.AreEqual(2, leftQuads.Count);
            Assert.AreEqual(0, leftQuads[0].LineIndex);
            Assert.AreEqual(1, leftQuads[1].LineIndex);
            // (quads[0] is line 0's only glyph 'A'; quads[1] is line 1's only glyph 'a' -- the space never gets a quad.)

            float lineWidth0 = entryA.Advance;
            float lineWidth1 = entryLowerA.Advance;
            Assert.AreNotEqual(lineWidth0, lineWidth1, "sanity: the two line widths must differ for this to be a decisive test");

            // Pre-justify local start (penX=0 on each line, before any per-line correction).
            float raw0 = entryA.Left - GlyphSdf.Buffer;
            float raw1 = entryLowerA.Left - GlyphSdf.Buffer;

            float diffLeft = leftQuads[0].TopLeft.x - leftQuads[1].TopLeft.x;
            float diffCenter = centerQuads[0].TopLeft.x - centerQuads[1].TopLeft.x;
            float diffRight = rightQuads[0].TopLeft.x - rightQuads[1].TopLeft.x;

            // Left (factor 0): no per-line correction at all -- each line's own local start (raw0/raw1) is untouched.
            float expectedDiffLeft = raw0 - raw1;
            Assert.AreEqual(expectedDiffLeft, diffLeft, Tolerance, "justify:left leaves each line's own local start untouched");

            // Right (factor 1): corrected_i = raw_i - lineWidth_i -- every line's END lands at local x=0.
            float expectedDiffRight = (raw0 - lineWidth0) - (raw1 - lineWidth1);
            Assert.AreEqual(expectedDiffRight, diffRight, Tolerance);

            // Center (factor .5): corrected_i = raw_i - 0.5*lineWidth_i.
            float expectedDiffCenter = (raw0 - 0.5f * lineWidth0) - (raw1 - 0.5f * lineWidth1);
            Assert.AreEqual(expectedDiffCenter, diffCenter, Tolerance);

            Assert.AreNotEqual(diffLeft, diffRight, "left and right justify must produce different relative line offsets");
            Assert.AreNotEqual(diffLeft, diffCenter, "left and center justify must produce different relative line offsets");
        }

        [Test]
        public void Justify_Auto_ResolvesFromAnchor()
        {
            (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) = MakeTwoLineFixture();

            var autoLeftAnchorQuads = new List<SymbolQuad>();
            var explicitLeftQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, justify: TextJustify.Auto, maxWidthEm: maxWidthEm), autoLeftAnchorQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, justify: TextJustify.Left, maxWidthEm: maxWidthEm), explicitLeftQuads);
            AssertAllQuadsEqual(autoLeftAnchorQuads, explicitLeftQuads, "auto with a Left-ish anchor must resolve to Left");

            var autoRightAnchorQuads = new List<SymbolQuad>();
            var explicitRightQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right, justify: TextJustify.Auto, maxWidthEm: maxWidthEm), autoRightAnchorQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right, justify: TextJustify.Right, maxWidthEm: maxWidthEm), explicitRightQuads);
            AssertAllQuadsEqual(autoRightAnchorQuads, explicitRightQuads, "auto with a Right-ish anchor must resolve to Right");

            var autoTopAnchorQuads = new List<SymbolQuad>();
            var explicitCenterQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, justify: TextJustify.Auto, maxWidthEm: maxWidthEm), autoTopAnchorQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, justify: TextJustify.Center, maxWidthEm: maxWidthEm), explicitCenterQuads);
            AssertAllQuadsEqual(autoTopAnchorQuads, explicitCenterQuads, "auto with a non-Left/Right anchor must resolve to Center");

            // Teeth: an impl that ignores the anchor for auto (always Center) fails the Left/Right cases above.
            Assert.AreNotEqual(explicitLeftQuads[0].TopLeft.x, explicitRightQuads[0].TopLeft.x,
                "sanity: Left vs Right justify must actually differ for the auto-resolution assertions above to be decisive");
        }

        private static (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) MakeTwoLineFixture()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' ']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)' ', entrySpace.Advance), ((uint)'a', entryLowerA.Advance));

            float maxWidthPx = entryA.Advance + entrySpace.Advance * 0.5f; // forces exactly one break
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;
            return (atlas, run, entryA, entrySpace, entryLowerA, maxWidthEm);
        }
    }
}
