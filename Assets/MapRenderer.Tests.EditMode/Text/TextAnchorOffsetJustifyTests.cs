// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
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
        // T2a — single line: TopLeft/Center/BottomRight differ by exactly the (hAlign,vAlign)*(blockWidth,blockHeight) delta.
        // =========================================================================================
        [Test]
        public void Anchor_SingleLine_ShiftsByExactBlockBboxDelta()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            float lineWidth = entryA.Advance + entryLowerA.Advance;
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            TextLayoutResult topLeft = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.TopLeft));
            TextLayoutResult center = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.Center));
            TextLayoutResult bottomRight = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.BottomRight));

            AssertConstantDelta(topLeft.Quads, center.Quads, new float2(-0.5f * lineWidth, 0.5f * lineHeightPx), "Center - TopLeft");
            AssertConstantDelta(topLeft.Quads, bottomRight.Quads, new float2(-lineWidth, lineHeightPx), "BottomRight - TopLeft");

            TextLayoutOptions Opt(TextAnchor a) => MakeOptions(anchor: a);
        }

        // =========================================================================================
        // T2b — a 2-line run: the vertical anchor delta uses lineCount * lineHeight*24, NOT a single
        // line's height. Justify held constant (Center) across variants so the pure anchor delta is
        // isolated (see TextQuadLayout's class doc: the per-line justify term cancels in a delta
        // between two Layout() calls that share options except Anchor).
        // =========================================================================================
        [Test]
        public void Anchor_TwoLineRun_VerticalDeltaUsesLineCountTimesLineHeight()
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

            TextLayoutResult topLeft = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.TopLeft));
            TextLayoutResult center = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.Center));
            TextLayoutResult bottomRight = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.BottomRight));

            Assert.AreEqual(2, topLeft.LineCount, "the chosen max-width must force exactly a 2-line wrap");

            float deltaYCenter = center.Quads[0].TopLeft.y - topLeft.Quads[0].TopLeft.y;
            float deltaYBottomRight = bottomRight.Quads[0].TopLeft.y - topLeft.Quads[0].TopLeft.y;

            Assert.AreEqual(0.5f * blockHeight, deltaYCenter, Tolerance, "Center's vertical delta must use lineCount*lineHeight, not a single line's height");
            Assert.AreEqual(blockHeight, deltaYBottomRight, Tolerance, "BottomRight's vertical delta must use lineCount*lineHeight");

            // Teeth: a per-line (not block-bbox) vertical anchor would use just ONE lineHeightPx --
            // a different, smaller number that must NOT match.
            Assert.AreNotEqual(0.5f * lineHeightPx, deltaYCenter, "a per-line vertical anchor would use a single line's height, not the block's");
            Assert.AreNotEqual(lineHeightPx, deltaYBottomRight, "a per-line vertical anchor would use a single line's height, not the block's");

            AssertConstantDelta(topLeft.Quads, center.Quads, new float2(-0.5f * blockWidth, 0.5f * blockHeight), "Center - TopLeft (2-line)");
            AssertConstantDelta(topLeft.Quads, bottomRight.Quads, new float2(-blockWidth, blockHeight), "BottomRight - TopLeft (2-line)");

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

            TextLayoutResult baseline = TextQuadLayout.Layout(run, atlas, MakeOptions(offset: float2.zero));
            TextLayoutResult offsetX = TextQuadLayout.Layout(run, atlas, MakeOptions(offset: new float2(1f, 0f)));
            TextLayoutResult offsetY = TextQuadLayout.Layout(run, atlas, MakeOptions(offset: new float2(0f, 0.5f)));

            AssertConstantDelta(baseline.Quads, offsetX.Quads, new float2(24f, 0f), "text-offset [1,0]");
            AssertConstantDelta(baseline.Quads, offsetY.Quads, new float2(0f, 12f), "text-offset [0,0.5]");
        }

        [Test]
        public void RadialOffset_CornerAnchor_ResolvesToDiagonal_AndOverridesOffset()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance));

            TextLayoutResult baseline = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft));
            TextLayoutResult radial = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, radialOffset: 1f));

            // TopLeft is a corner anchor (hAlign=0, vAlign=0): pushes away from the anchored edges,
            // i.e. further +x (away from the left edge) and further -y (away from the top edge),
            // split into a diagonal of magnitude RadialOffset/sqrt2 on each axis.
            float diagPx = 24f / math.SQRT2;
            AssertConstantDelta(baseline.Quads, radial.Quads, new float2(diagPx, -diagPx), "RadialOffset=1 @ TopLeft (corner)");

            // Radial overrides a nonzero Offset entirely (decision: "Radial overrides Offset if !=0").
            TextLayoutResult radialWithIgnoredOffset = TextQuadLayout.Layout(run, atlas,
                MakeOptions(anchor: TextAnchor.TopLeft, offset: new float2(5f, 5f), radialOffset: 1f));
            AssertAllQuadsEqual(radial.Quads, radialWithIgnoredOffset.Quads, "RadialOffset must override a nonzero Offset");
        }

        [Test]
        public void RadialOffset_PureAxisAnchors_ResolveToSingleAxis()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance));

            TextLayoutResult baselineLeft = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left));
            TextLayoutResult radialLeft = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, radialOffset: 1f));
            AssertConstantDelta(baselineLeft.Quads, radialLeft.Quads, new float2(24f, 0f), "RadialOffset=1 @ Left (pure x axis)");

            TextLayoutResult baselineTop = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top));
            TextLayoutResult radialTop = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, radialOffset: 1f));
            AssertConstantDelta(baselineTop.Quads, radialTop.Quads, new float2(0f, -24f), "RadialOffset=1 @ Top (pure y axis)");
        }

        // =========================================================================================
        // T4 — text-justify (left/center/right) shifts each line's local start relative to the
        // other, and "auto" resolves from the anchor.
        // =========================================================================================
        [Test]
        public void Justify_ShiftsEachLine_RelativeToTheOthers()
        {
            (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) = MakeTwoLineFixture();

            TextLayoutResult left = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Left, maxWidthEm: maxWidthEm));
            TextLayoutResult center = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Center, maxWidthEm: maxWidthEm));
            TextLayoutResult right = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Right, maxWidthEm: maxWidthEm));

            Assert.AreEqual(2, left.LineCount);
            Assert.AreEqual(2, left.Quads.Count);
            Assert.AreEqual(0, left.Quads[0].LineIndex);
            Assert.AreEqual(1, left.Quads[1].LineIndex);
            // (quads[0] is line 0's only glyph 'A'; quads[1] is line 1's only glyph 'a' -- the space never gets a quad.)

            float lineWidth0 = entryA.Advance;
            float lineWidth1 = entryLowerA.Advance;
            Assert.AreNotEqual(lineWidth0, lineWidth1, "sanity: the two line widths must differ for this to be a decisive test");

            // Pre-justify local start (penX=0 on each line, before any per-line correction).
            float raw0 = entryA.Left - GlyphSdf.Buffer;
            float raw1 = entryLowerA.Left - GlyphSdf.Buffer;

            float diffLeft = left.Quads[0].TopLeft.x - left.Quads[1].TopLeft.x;
            float diffCenter = center.Quads[0].TopLeft.x - center.Quads[1].TopLeft.x;
            float diffRight = right.Quads[0].TopLeft.x - right.Quads[1].TopLeft.x;

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

            TextLayoutResult autoLeftAnchor = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, justify: TextJustify.Auto, maxWidthEm: maxWidthEm));
            TextLayoutResult explicitLeft = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, justify: TextJustify.Left, maxWidthEm: maxWidthEm));
            AssertAllQuadsEqual(autoLeftAnchor.Quads, explicitLeft.Quads, "auto with a Left-ish anchor must resolve to Left");

            TextLayoutResult autoRightAnchor = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right, justify: TextJustify.Auto, maxWidthEm: maxWidthEm));
            TextLayoutResult explicitRight = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right, justify: TextJustify.Right, maxWidthEm: maxWidthEm));
            AssertAllQuadsEqual(autoRightAnchor.Quads, explicitRight.Quads, "auto with a Right-ish anchor must resolve to Right");

            TextLayoutResult autoTopAnchor = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, justify: TextJustify.Auto, maxWidthEm: maxWidthEm));
            TextLayoutResult explicitCenter = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, justify: TextJustify.Center, maxWidthEm: maxWidthEm));
            AssertAllQuadsEqual(autoTopAnchor.Quads, explicitCenter.Quads, "auto with a non-Left/Right anchor must resolve to Center");

            // Teeth: an impl that ignores the anchor for auto (always Center) fails the Left/Right cases above.
            Assert.AreNotEqual(explicitLeft.Quads[0].TopLeft.x, explicitRight.Quads[0].TopLeft.x,
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
