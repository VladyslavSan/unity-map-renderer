// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests
{
    /// <summary>
    /// I3: <see cref="IconQuadLayout.Layout"/> over the committed <c>sample-sprite.json</c> fixture's own
    /// numbers (sheet 64×64 — <c>marker</c> 16×16@1x, <c>star</c> 24×24@2x, <c>dot</c> 8×8@1x at (0,32)),
    /// hand-pinned rather than re-derived, so a formula regression (e.g. UV accidentally divided by
    /// <see cref="SpriteEntry.PixelRatio"/>) is caught by a literal mismatch. Engine-free; runs in both
    /// runners.
    /// </summary>
    [TestFixture]
    public class IconQuadLayoutTests
    {
        private static readonly int2 SheetSize = new int2(64, 64);

        // Fixture sprites (mirrors Assets/Fixtures/sprites/sample-sprite.json).
        private static readonly SpriteEntry Marker = new SpriteEntry { X = 0, Y = 0, Width = 16, Height = 16, PixelRatio = 1f, Sdf = false };
        private static readonly SpriteEntry Star = new SpriteEntry { X = 16, Y = 0, Width = 24, Height = 24, PixelRatio = 2f, Sdf = false };
        private static readonly SpriteEntry Dot = new SpriteEntry { X = 0, Y = 32, Width = 8, Height = 8, PixelRatio = 1f, Sdf = false };

        private const float Eps = 1e-5f;

        [Test]
        public void Star_CenterAnchor_IconSize1_LogicalSizeAndUvBothCorrect()
        {
            // The UV/pixelRatio RED tooth: star is @2x, so logical size (24/2=12) and the UV rect (raw
            // 24px extent, NEVER divided by pixelRatio) must diverge from each other — asserting both in
            // one test catches an implementation that mistakenly divides the UV rect too.
            SymbolQuad quad = IconQuadLayout.Layout(Star, SheetSize, 1f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-6f, quad.TopLeft.x, Eps);
            Assert.AreEqual(6f, quad.TopLeft.y, Eps);
            Assert.AreEqual(6f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-6f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0.25f, quad.UvTopLeft.x, Eps);
            Assert.AreEqual(0f, quad.UvTopLeft.y, Eps);
            Assert.AreEqual(0.625f, quad.UvBottomRight.x, Eps);
            Assert.AreEqual(0.375f, quad.UvBottomRight.y, Eps);
        }

        [Test]
        public void Marker_CenterAnchor_IconSize1_UvMatchesRawRect()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-8f, quad.TopLeft.x, Eps);
            Assert.AreEqual(8f, quad.TopLeft.y, Eps);
            Assert.AreEqual(8f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-8f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0f, quad.UvTopLeft.x, Eps);
            Assert.AreEqual(0f, quad.UvTopLeft.y, Eps);
            Assert.AreEqual(0.25f, quad.UvBottomRight.x, Eps);
            Assert.AreEqual(0.25f, quad.UvBottomRight.y, Eps);
        }

        [Test]
        public void Dot_TopLeftAnchor_IconSize1_QuadAndUvCorrect()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Dot, SheetSize, 1f, TextAnchor.TopLeft, float2.zero);

            // Top-left anchor: the box's own top-left corner sits at the anchor (0,0), extending +x/-y.
            Assert.AreEqual(0f, quad.TopLeft.x, Eps);
            Assert.AreEqual(0f, quad.TopLeft.y, Eps);
            Assert.AreEqual(8f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-8f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0f, quad.UvTopLeft.x, Eps);
            Assert.AreEqual(0.5f, quad.UvTopLeft.y, Eps);
            Assert.AreEqual(0.125f, quad.UvBottomRight.x, Eps);
            Assert.AreEqual(0.625f, quad.UvBottomRight.y, Eps);
        }

        [Test]
        public void Marker_CenterAnchor_IconSize2_ScalesExtentButNotUv()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-16f, quad.TopLeft.x, Eps);
            Assert.AreEqual(16f, quad.TopLeft.y, Eps);
            Assert.AreEqual(16f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-16f, quad.BottomRight.y, Eps);

            // UV is unaffected by icon-size — it indexes the sheet, not the drawn extent.
            Assert.AreEqual(0f, quad.UvTopLeft.x, Eps);
            Assert.AreEqual(0f, quad.UvTopLeft.y, Eps);
            Assert.AreEqual(0.25f, quad.UvBottomRight.x, Eps);
            Assert.AreEqual(0.25f, quad.UvBottomRight.y, Eps);
        }

        [Test]
        public void Marker_CenterAnchor_IconSize2_OffsetShiftsByOffsetTimesIconSize()
        {
            SymbolQuad unshifted = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, float2.zero);
            SymbolQuad shifted = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, new float2(2f, 0f));

            // icon-offset.x(2) * icon-size(2) == +4 on every corner's x; y untouched (offset.y == 0).
            Assert.AreEqual(unshifted.TopLeft.x + 4f, shifted.TopLeft.x, Eps);
            Assert.AreEqual(unshifted.BottomRight.x + 4f, shifted.BottomRight.x, Eps);
            Assert.AreEqual(unshifted.TopLeft.y, shifted.TopLeft.y, Eps);
            Assert.AreEqual(unshifted.BottomRight.y, shifted.BottomRight.y, Eps);
        }

        [Test]
        public void Marker_OffsetY_IsNegatedOnceLikeTextOffset()
        {
            // Style icon-offset is y-DOWN as authored; TextLayoutOptionsBuilder negates text-offset.y exactly
            // once — icon must match (never double-flip).
            SymbolQuad unshifted = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, float2.zero);
            SymbolQuad shifted = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, new float2(0f, 3f));

            // A positive (down) icon-offset.y must DECREASE the quad's y (move down in the y-up frame).
            Assert.AreEqual(unshifted.TopLeft.y - 3f, shifted.TopLeft.y, Eps);
            Assert.AreEqual(unshifted.BottomRight.y - 3f, shifted.BottomRight.y, Eps);
        }

        // 16x16 marker at iconSize 1: minX=-hAlign*16, maxX=(1-hAlign)*16, maxY=vAlign*16, minY=-(1-vAlign)*16
        // — the SAME hAlign/vAlign convention TextQuadLayout.ResolveAlignFactors uses (Left/Right/Top/Bottom
        // 0 or 1, Center/unset 0.5), so icon and text anchors agree.
        [TestCase(TextAnchor.Center, -8f, 8f, 8f, -8f)]
        [TestCase(TextAnchor.Left, 0f, 8f, 16f, -8f)]
        [TestCase(TextAnchor.Right, -16f, 8f, 0f, -8f)]
        [TestCase(TextAnchor.Top, -8f, 0f, 8f, -16f)]
        [TestCase(TextAnchor.Bottom, -8f, 16f, 8f, 0f)]
        [TestCase(TextAnchor.TopLeft, 0f, 0f, 16f, -16f)]
        [TestCase(TextAnchor.TopRight, -16f, 0f, 0f, -16f)]
        [TestCase(TextAnchor.BottomLeft, 0f, 16f, 16f, 0f)]
        [TestCase(TextAnchor.BottomRight, -16f, 16f, 0f, 0f)]
        public void AnchorSweep_MatchesHAlignVAlignConvention(
            TextAnchor anchor, float expectedTopLeftX, float expectedTopLeftY, float expectedBottomRightX, float expectedBottomRightY)
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 1f, anchor, float2.zero);
            Assert.AreEqual(expectedTopLeftX, quad.TopLeft.x, Eps, $"anchor {anchor}: TopLeft.x");
            Assert.AreEqual(expectedTopLeftY, quad.TopLeft.y, Eps, $"anchor {anchor}: TopLeft.y");
            Assert.AreEqual(expectedBottomRightX, quad.BottomRight.x, Eps, $"anchor {anchor}: BottomRight.x");
            Assert.AreEqual(expectedBottomRightY, quad.BottomRight.y, Eps, $"anchor {anchor}: BottomRight.y");
        }

        // I5a: ToLayoutResult wraps a single laid-out icon quad into the same TextLayoutResult shape the
        // point-text path produces, so StyledSymbolTileBuilder's Pass 2 can emit an icon down the SAME
        // point-placement path.
        [Test]
        public void ToLayoutResult_WrapsSingleQuad_BoundsAreItsOwnMinMaxCorners()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Star, SheetSize, 1f, TextAnchor.TopLeft, new float2(3f, -1f));

            TextLayoutResult result = IconQuadLayout.ToLayoutResult(quad);

            Assert.AreEqual(1, result.Quads.Count, "a sprite is exactly one quad");
            AssertQuadEqual(quad, result.Quads[0]);
            Assert.AreEqual(1, result.LineCount, "an icon has no line concept — pinned at 1");
            Assert.AreEqual(math.min(quad.TopLeft.x, quad.BottomRight.x), result.BoundsMin.x, Eps);
            Assert.AreEqual(math.min(quad.TopLeft.y, quad.BottomRight.y), result.BoundsMin.y, Eps);
            Assert.AreEqual(math.max(quad.TopLeft.x, quad.BottomRight.x), result.BoundsMax.x, Eps);
            Assert.AreEqual(math.max(quad.TopLeft.y, quad.BottomRight.y), result.BoundsMax.y, Eps);
        }

        private static void AssertQuadEqual(in SymbolQuad expected, in SymbolQuad actual)
        {
            Assert.AreEqual(expected.TopLeft.x, actual.TopLeft.x, Eps);
            Assert.AreEqual(expected.TopLeft.y, actual.TopLeft.y, Eps);
            Assert.AreEqual(expected.BottomRight.x, actual.BottomRight.x, Eps);
            Assert.AreEqual(expected.BottomRight.y, actual.BottomRight.y, Eps);
            Assert.AreEqual(expected.UvTopLeft.x, actual.UvTopLeft.x, Eps);
            Assert.AreEqual(expected.UvTopLeft.y, actual.UvTopLeft.y, Eps);
            Assert.AreEqual(expected.UvBottomRight.x, actual.UvBottomRight.x, Eps);
            Assert.AreEqual(expected.UvBottomRight.y, actual.UvBottomRight.y, Eps);
        }
    }
}
