// Unity EditMode only. NOT registered in core-tests.csproj (LabelInstance's consumers are engine-side).
//
// Extracted verbatim from SymbolProcessorParityTests, which owned the only deep LabelInstance comparison in
// the suite, when a SECOND differential (SymbolParkedRedecodeTests — parked vs un-parked commit) needed the
// same one. Field-by-field rather than a struct Equals: LabelInstance carries arrays and a TextLayoutResult
// reference, so the default value comparison would pass on two labels whose glyph quads differ.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class LabelInstanceAssert
    {
        /// <summary>Deep-equality over the geometry, layout, paint and identity stamps
        /// (FeatureIndex / TileKey / MaterialIndex) a build can get wrong. <paramref name="index"/> only
        /// names the position in the failure message.
        ///
        /// <para><b>Not exhaustive, deliberately stated.</b> It does not compare
        /// <c>IconRotateRadians</c>, <c>PitchAlignment</c>, <c>PairRole</c>, <c>PairId</c> or
        /// <c>PairOptional</c>. Both current callers drive fixtures where those are style constants, so they
        /// cannot diverge between the two sides of either differential — but a caller whose style varies
        /// them would need them added. The docstring used to claim "every field a build can get wrong";
        /// review arm 2 measured that false, so it says what it does instead.</para></summary>
        internal static void AreEqual(LabelInstance expected, LabelInstance actual, int index)
        {
            string at = $" at index {index}";
            Assert.AreEqual(expected.AnchorRender, actual.AnchorRender, "AnchorRender" + at);
            // UpRender/PathUpRender are decode-DERIVED, exactly like AnchorRender/PathRender beside them, so
            // a bug isolated to the surface-normal fields would otherwise slip through a decode differential
            // untouched. Both review arms converged on this pair as the one real gap in the extracted set.
            Assert.AreEqual(expected.UpRender, actual.UpRender, "UpRender" + at);
            Assert.AreEqual(expected.Placement, actual.Placement, "Placement" + at);
            AssertLayoutEqual(expected.Layout, actual.Layout, at);
            CollectionAssert.AreEqual(expected.PathRender, actual.PathRender, "PathRender" + at);
            CollectionAssert.AreEqual(expected.PathUpRender, actual.PathUpRender, "PathUpRender" + at);
            CollectionAssert.AreEqual(expected.LineAnchors, actual.LineAnchors, "LineAnchors" + at);
            AssertCurvedGlyphsEqual(expected.CurvedGlyphs, actual.CurvedGlyphs, at);
            Assert.AreEqual(expected.Kind, actual.Kind, "Kind" + at);
            Assert.AreEqual(expected.IconImage, actual.IconImage, "IconImage" + at);
            Assert.AreEqual(expected.Text, actual.Text, "Text" + at);
            Assert.AreEqual(expected.Paint, actual.Paint, "Paint" + at);
            Assert.AreEqual(expected.TextSizePx, actual.TextSizePx, "TextSizePx" + at);
            Assert.AreEqual(expected.PaddingPx, actual.PaddingPx, "PaddingPx" + at);
            Assert.AreEqual(expected.SortKey, actual.SortKey, "SortKey" + at);
            Assert.AreEqual(expected.MaxAngleDeg, actual.MaxAngleDeg, "MaxAngleDeg" + at);
            Assert.AreEqual(expected.KeepUpright, actual.KeepUpright, "KeepUpright" + at);
            Assert.AreEqual(expected.FeatureIndex, actual.FeatureIndex, "FeatureIndex" + at);
            Assert.AreEqual(expected.TileKey, actual.TileKey, "TileKey" + at);
            Assert.AreEqual(expected.MaterialIndex, actual.MaterialIndex, "MaterialIndex" + at);
            Assert.AreEqual(expected.AllowOverlap, actual.AllowOverlap, "AllowOverlap" + at);
            Assert.AreEqual(expected.IgnorePlacement, actual.IgnorePlacement, "IgnorePlacement" + at);
            Assert.AreEqual(expected.TranslatePx, actual.TranslatePx, "TranslatePx" + at);
            Assert.AreEqual(expected.TranslateAnchor, actual.TranslateAnchor, "TranslateAnchor" + at);
            Assert.AreEqual(expected.RotationAlignment, actual.RotationAlignment, "RotationAlignment" + at);
        }

        private static void AssertLayoutEqual(TextLayoutResult expected, TextLayoutResult actual, string at)
        {
            if (expected == null || actual == null)
            {
                Assert.AreEqual(expected == null, actual == null, "Layout null-ness" + at);
                return;
            }
            Assert.AreEqual(expected.BoundsMin, actual.BoundsMin, "Layout.BoundsMin" + at);
            Assert.AreEqual(expected.BoundsMax, actual.BoundsMax, "Layout.BoundsMax" + at);
            Assert.AreEqual(expected.LineCount, actual.LineCount, "Layout.LineCount" + at);
            CollectionAssert.AreEqual(
                new List<SymbolQuad>(expected.Quads ?? System.Array.Empty<SymbolQuad>()),
                new List<SymbolQuad>(actual.Quads ?? System.Array.Empty<SymbolQuad>()),
                "Layout.Quads" + at);
        }

        private static void AssertCurvedGlyphsEqual(IReadOnlyList<CurvedGlyph> expected, IReadOnlyList<CurvedGlyph> actual, string at)
        {
            if (expected == null || actual == null)
            {
                Assert.AreEqual(expected == null, actual == null, "CurvedGlyphs null-ness" + at);
                return;
            }
            CollectionAssert.AreEqual(new List<CurvedGlyph>(expected), new List<CurvedGlyph>(actual), "CurvedGlyphs" + at);
        }
    }
}
