// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// I3: <see cref="IconQuadLayout.Layout"/> over the committed <c>sample-sprite.json</c> fixture's own
    /// numbers (sheet 64×64 — <c>marker</c> 16×16@1x, <c>star</c> 24×24@2x, <c>dot</c> 8×8@1x at (0,32)),
    /// hand-pinned rather than re-derived, so a formula regression (e.g. UV accidentally divided by
    /// <see cref="SpriteEntry.PixelRatio"/>) is caught by a literal mismatch. Engine-free; runs in both
    /// runners.
    ///
    /// <para>Every UV expectation below is the sprite's PADDED rect — its content rect grown by
    /// <see cref="SpriteEntry.Padding"/> texels on each side — divided by the sheet size, with NO inset.
    /// The half-texel inset the earlier expectations carried is retired: what keeps the sheet's bilinear
    /// filtering off the neighbouring sprite is now the one-texel transparent border <c>SpriteSheet</c>'s
    /// repack lays down, and DRAWING that border is what antialiases the icon's silhouette (see
    /// <see cref="IconQuadLayout"/>). The fixture entries below carry <c>Padding = 0</c> — they mirror the
    /// committed sprite JSON, which is a RAW parsed index — so their UVs are the plain rect; the padded
    /// cases live in the teeth at the bottom of this file. Each expectation carries its own derivation in a
    /// trailing comment, and they remain hand-written literals ON PURPOSE: re-deriving them from
    /// <c>entry.X / sheetSize</c> would just restate the implementation and could not fail.</para>
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
            // The UV/pixelRatio RED tooth: star is @2x, so logical size (24/2=12) and the UV rect (spanning
            // the raw 24px extent, NEVER divided by pixelRatio) must diverge from each other — asserting
            // both in one test catches an implementation that mistakenly divides the UV rect too. The UV
            // extent below is 24/64, the sprite's full 24 texels, nowhere near the 12px a pixelRatio
            // division would produce.
            SymbolQuad quad = IconQuadLayout.Layout(Star, SheetSize, 1f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-6f, quad.TopLeft.x, Eps);
            Assert.AreEqual(6f, quad.TopLeft.y, Eps);
            Assert.AreEqual(6f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-6f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0.25f,   quad.UvTopLeft.x, Eps);     // 16/64
            Assert.AreEqual(0f,      quad.UvTopLeft.y, Eps);     //  0/64
            Assert.AreEqual(0.625f,  quad.UvBottomRight.x, Eps); // 40/64
            Assert.AreEqual(0.375f,  quad.UvBottomRight.y, Eps); // 24/64
        }

        [Test]
        public void Marker_CenterAnchor_IconSize1_UvIsTheRawRect()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-8f, quad.TopLeft.x, Eps);
            Assert.AreEqual(8f, quad.TopLeft.y, Eps);
            Assert.AreEqual(8f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-8f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0f,    quad.UvTopLeft.x, Eps);     //  0/64
            Assert.AreEqual(0f,    quad.UvTopLeft.y, Eps);     //  0/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.x, Eps); // 16/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.y, Eps); // 16/64
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

            Assert.AreEqual(0f,      quad.UvTopLeft.x, Eps);     //  0/64
            Assert.AreEqual(0.5f,    quad.UvTopLeft.y, Eps);     // 32/64
            Assert.AreEqual(0.125f,  quad.UvBottomRight.x, Eps); //  8/64
            Assert.AreEqual(0.625f,  quad.UvBottomRight.y, Eps); // 40/64
        }

        [Test]
        public void Marker_CenterAnchor_IconSize2_ScalesExtentButNotUv()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-16f, quad.TopLeft.x, Eps);
            Assert.AreEqual(16f, quad.TopLeft.y, Eps);
            Assert.AreEqual(16f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-16f, quad.BottomRight.y, Eps);

            // UV is unaffected by icon-size — it indexes the sheet, not the drawn extent. Byte-identical to
            // the iconSize-1 case above, which is the whole point of this test.
            Assert.AreEqual(0f,    quad.UvTopLeft.x, Eps);     //  0/64
            Assert.AreEqual(0f,    quad.UvTopLeft.y, Eps);     //  0/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.x, Eps); // 16/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.y, Eps); // 16/64
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

        // I5a: the inline bounds formula (min/max corner ± skirt) wraps a single laid-out icon quad into
        // the same caller-owned quad list + TextLayoutBounds shape the point-text path produces, so
        // StyledSymbolTileBuilder's Pass 2 can emit an icon down the SAME point-placement path.
        [Test]
        public void IconBounds_WrapsSingleQuad_BoundsAreItsOwnMinMaxCorners()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Star, SheetSize, 1f, TextAnchor.TopLeft, new float2(3f, -1f));

            float skirtPx = IconQuadLayout.SkirtPx(Star, 1f);
            var quads = new List<SymbolQuad> { quad };
            float2 skirtV = new float2(skirtPx, skirtPx);
            float2 boundsMin = math.min(quad.TopLeft, quad.BottomRight) + skirtV;
            float2 boundsMax = math.max(quad.TopLeft, quad.BottomRight) - skirtV;

            Assert.AreEqual(1, quads.Count, "a sprite is exactly one quad");
            AssertQuadEqual(quad, quads[0]);
            Assert.AreEqual(math.min(quad.TopLeft.x, quad.BottomRight.x), boundsMin.x, Eps);
            Assert.AreEqual(math.min(quad.TopLeft.y, quad.BottomRight.y), boundsMin.y, Eps);
            Assert.AreEqual(math.max(quad.TopLeft.x, quad.BottomRight.x), boundsMax.x, Eps);
            Assert.AreEqual(math.max(quad.TopLeft.y, quad.BottomRight.y), boundsMax.y, Eps);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The padded-repack teeth. SpriteSheet hands every drawable sprite a one-texel transparent border
        // and reports it as SpriteEntry.Padding; IconQuadLayout draws that border (which is what gives the
        // silhouette a ramp bilinear can antialias) and grows the quad by exactly its drawn size.
        //
        // Padded twins of the fixture sprites — SAME rect, Padding = 1.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private static readonly SpriteEntry PaddedMarker = new SpriteEntry { X = 5, Y = 5, Width = 16, Height = 16, PixelRatio = 1f, Padding = 1 };
        private static readonly SpriteEntry PaddedStar = new SpriteEntry { X = 30, Y = 7, Width = 24, Height = 24, PixelRatio = 2f, Padding = 1 };
        private static readonly SpriteEntry PaddedDot = new SpriteEntry { X = 1, Y = 40, Width = 8, Height = 8, PixelRatio = 1f, Padding = 1 };

        /// <summary>
        /// C1 — the CONTENT box is exactly what it was before the border existed, for every anchor. The
        /// literals are the same nine rows the un-padded <see cref="AnchorSweep_MatchesHAlignVAlignConvention"/>
        /// pins for a 16×16 @1x marker at icon-size 1, which is the point: adding a border must not move or
        /// resize a single anchor's box. This is the ink-size invariant AND the collision-box invariant in
        /// one, and it is what makes "icons cannot silently grow" checkable rather than merely intended.
        /// </summary>
        [TestCase(TextAnchor.Center, -8f, 8f, 8f, -8f)]
        [TestCase(TextAnchor.Left, 0f, 8f, 16f, -8f)]
        [TestCase(TextAnchor.Right, -16f, 8f, 0f, -8f)]
        [TestCase(TextAnchor.Top, -8f, 0f, 8f, -16f)]
        [TestCase(TextAnchor.Bottom, -8f, 16f, 8f, 0f)]
        [TestCase(TextAnchor.TopLeft, 0f, 0f, 16f, -16f)]
        [TestCase(TextAnchor.TopRight, -16f, 0f, 0f, -16f)]
        [TestCase(TextAnchor.BottomLeft, 0f, 16f, 16f, 0f)]
        [TestCase(TextAnchor.BottomRight, -16f, 16f, 0f, 0f)]
        public void PaddedAnchorSweep_ContentBoxIsUnchangedByTheBorder(
            TextAnchor anchor, float expectedTopLeftX, float expectedTopLeftY,
            float expectedBottomRightX, float expectedBottomRightY)
        {
            SymbolQuad quad = IconQuadLayout.Layout(PaddedMarker, SheetSize, 1f, anchor, float2.zero);
            float skirtPx = IconQuadLayout.SkirtPx(PaddedMarker, 1f);
            float2 skirtV = new float2(skirtPx, skirtPx);
            float2 boundsMin = math.min(quad.TopLeft, quad.BottomRight) + skirtV;
            float2 boundsMax = math.max(quad.TopLeft, quad.BottomRight) - skirtV;

            Assert.AreEqual(math.min(expectedTopLeftX, expectedBottomRightX), boundsMin.x, Eps, $"anchor {anchor}: BoundsMin.x");
            Assert.AreEqual(math.min(expectedTopLeftY, expectedBottomRightY), boundsMin.y, Eps, $"anchor {anchor}: BoundsMin.y");
            Assert.AreEqual(math.max(expectedTopLeftX, expectedBottomRightX), boundsMax.x, Eps, $"anchor {anchor}: BoundsMax.x");
            Assert.AreEqual(math.max(expectedTopLeftY, expectedBottomRightY), boundsMax.y, Eps, $"anchor {anchor}: BoundsMax.y");
        }

        /// <summary>C1, the general case: for every fixture sprite × icon-size × anchor × offset, the padded
        /// entry's CONTENT box equals the un-padded entry's quad exactly. A single formula error anywhere in
        /// the grow/un-grow pair shows up here.</summary>
        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void PaddedContentBox_EqualsTheUnpaddedQuad_ForEverySpriteAndAnchor(float iconSize)
        {
            var pairs = new[] { (Marker, PaddedMarker), (Star, PaddedStar), (Dot, PaddedDot) };
            var anchors = new[]
            {
                TextAnchor.Center, TextAnchor.Left, TextAnchor.Right, TextAnchor.Top, TextAnchor.Bottom,
                TextAnchor.TopLeft, TextAnchor.TopRight, TextAnchor.BottomLeft, TextAnchor.BottomRight,
            };
            var offset = new float2(3f, -2f);

            foreach ((SpriteEntry bare, SpriteEntry padded) in pairs)
            {
                foreach (TextAnchor anchor in anchors)
                {
                    SymbolQuad bareQuad = IconQuadLayout.Layout(bare, SheetSize, iconSize, anchor, offset);
                    SymbolQuad paddedQuad = IconQuadLayout.Layout(padded, SheetSize, iconSize, anchor, offset);
                    float skirtPx = IconQuadLayout.SkirtPx(padded, iconSize);
                    float2 skirtV = new float2(skirtPx, skirtPx);
                    float2 boundsMin = math.min(paddedQuad.TopLeft, paddedQuad.BottomRight) + skirtV;
                    float2 boundsMax = math.max(paddedQuad.TopLeft, paddedQuad.BottomRight) - skirtV;

                    string what = $"{bare.Width}x{bare.Height}@{bare.PixelRatio} {anchor} size {iconSize}";
                    Assert.AreEqual(math.min(bareQuad.TopLeft.x, bareQuad.BottomRight.x), boundsMin.x, Eps, $"{what}: min.x");
                    Assert.AreEqual(math.min(bareQuad.TopLeft.y, bareQuad.BottomRight.y), boundsMin.y, Eps, $"{what}: min.y");
                    Assert.AreEqual(math.max(bareQuad.TopLeft.x, bareQuad.BottomRight.x), boundsMax.x, Eps, $"{what}: max.x");
                    Assert.AreEqual(math.max(bareQuad.TopLeft.y, bareQuad.BottomRight.y), boundsMax.y, Eps, $"{what}: max.y");
                }
            }
        }

        /// <summary>C2 — the UV rect covers the sprite's PADDED rect exactly (no inset, no half-texel), and
        /// the quad's extent is the padded rect's own logical size. "Pad the atlas but leave the quad
        /// nominal" fails the second half; "grow the quad but leave the UV on the content" fails the
        /// first.</summary>
        [Test]
        public void PaddedSprite_UvRectAndQuadExtentBothSpanTheWholeCell()
        {
            const float iconSize = 1.5f;
            SymbolQuad quad = IconQuadLayout.Layout(PaddedStar, SheetSize, iconSize, TextAnchor.Center, float2.zero);

            float uvTexelsX = (quad.UvBottomRight.x - quad.UvTopLeft.x) * SheetSize.x;
            float uvTexelsY = (quad.UvBottomRight.y - quad.UvTopLeft.y) * SheetSize.y;
            Assert.AreEqual(PaddedStar.Width + 2 * PaddedStar.Padding, uvTexelsX, Eps, "UV must span the padded width");
            Assert.AreEqual(PaddedStar.Height + 2 * PaddedStar.Padding, uvTexelsY, Eps, "UV must span the padded height");

            float quadWidth = quad.BottomRight.x - quad.TopLeft.x;
            float quadHeight = quad.TopLeft.y - quad.BottomRight.y;
            Assert.AreEqual((PaddedStar.Width + 2 * PaddedStar.Padding) / PaddedStar.PixelRatio * iconSize, quadWidth, Eps);
            Assert.AreEqual((PaddedStar.Height + 2 * PaddedStar.Padding) / PaddedStar.PixelRatio * iconSize, quadHeight, Eps);
        }

        /// <summary>
        /// C3 — the identity the whole design hangs on: <b>texels-per-drawn-pixel is the same for the border
        /// as for the content</b>, i.e. <c>uvWidth × sheetWidth / quadWidth == PixelRatio / iconSize</c>,
        /// independent of the sprite and of the padding. It catches BOTH shallow implementations in one
        /// assertion — a quad grown without widening the UV rect makes the ratio too small (fewer texels per
        /// drawn pixel: the content is stretched across the padded quad, so the icon <b>grew</b>), a UV rect
        /// widened without growing the quad makes it too large (more texels per drawn pixel: the padded rect
        /// is squeezed into the nominal quad, so the icon <b>shrank</b>).
        /// </summary>
        [TestCase(0.75f)]
        [TestCase(3f)]
        public void TexelsPerDrawnPixel_IsTheSameForBorderAndContent(float iconSize)
        {
            foreach (SpriteEntry entry in new[] { PaddedMarker, PaddedStar, PaddedDot, Marker, Dot })
            {
                SymbolQuad quad = IconQuadLayout.Layout(entry, SheetSize, iconSize, TextAnchor.Center, float2.zero);
                float uvTexels = (quad.UvBottomRight.x - quad.UvTopLeft.x) * SheetSize.x;
                float quadWidth = quad.BottomRight.x - quad.TopLeft.x;

                Assert.AreEqual(entry.PixelRatio / iconSize, uvTexels / quadWidth, Eps,
                    $"{entry.Width}x{entry.Height}@{entry.PixelRatio} pad {entry.Padding}, size {iconSize}: " +
                    $"a drawn pixel must cover PixelRatio/iconSize texels — the SAME rate inside the border " +
                    $"as inside the content, or the ink is being scaled by the padding.");
            }
        }

        /// <summary>
        /// A malformed sheet may declare an explicit <c>"pixelRatio": 0</c>, which parses straight through
        /// (<c>SpriteIndex</c> only DEFAULTS the field to 1). Unguarded, an unpadded such entry makes the
        /// skirt <c>0 / 0f</c> — NaN, not the infinity the size maths produces — and NaN bounds compare false
        /// against every collision test rather than swallowing the screen. Mirrors the identical guard
        /// <c>FillPattern.TryResolve</c> already carries for the same malformed field.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        public void SkirtPx_WithAMalformedZeroPixelRatio_IsFinite(int padding)
        {
            var malformed = new SpriteEntry
            {
                X = 0, Y = 0, Width = 8, Height = 8, PixelRatio = 0f, Padding = padding,
            };

            float skirt = IconQuadLayout.SkirtPx(malformed, 2f);

            Assert.IsFalse(float.IsNaN(skirt), $"padding {padding}: the skirt must never be NaN");
            Assert.IsFalse(float.IsInfinity(skirt), $"padding {padding}: nor infinite");
            Assert.AreEqual(padding * 2f, skirt, Eps, "a zero pixelRatio falls back to 1, exactly as FillPattern's does");
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
