// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Top-level `using Unity.Mathematics;` + unqualified float2/float4 (namespace-
// collision trap — see LabelScreenProjection.cs's header comment).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T3: <see cref="BillboardMath.BuildQuad"/> golden (element-by-element corners/UVs) + the
    /// decisive zoom-independence tooth (same label at two different anchor screen positions — i.e. two
    /// different camera zooms — must yield IDENTICAL screen-pixel width/height; only the anchor moves).
    /// </summary>
    [TestFixture]
    public class BillboardMathTests
    {
        private static SymbolQuad MakeQuad()
            => new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f),
                BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.10f, 0.20f),
                UvBottomRight = new float2(0.30f, 0.50f),
                LineIndex = 0,
            };

        // ── Golden: scale = 1 (textSizePx == TextQuadLayout.OneEm) — corners == anchor + baked corners ──
        [Test]
        public void BuildQuad_UnitScale_CornersMatchAnchorPlusBakedPx()
        {
            SymbolQuad quad = MakeQuad();
            var anchor = new float2(100f, 200f);
            const float depth = 0.42f;
            var color = new float4(0.1f, 0.2f, 0.3f, 0.4f);

            BillboardMath.BuildQuad(in quad, in anchor, TextQuadLayout.OneEm, depth, in color, 0f,
                out BillboardVertex topLeft, out BillboardVertex topRight,
                out BillboardVertex bottomRight, out BillboardVertex bottomLeft);

            Assert.AreEqual(anchor.x + quad.TopLeft.x, topLeft.ScreenPx.x, 1e-5f);
            Assert.AreEqual(anchor.y + quad.TopLeft.y, topLeft.ScreenPx.y, 1e-5f);

            Assert.AreEqual(anchor.x + quad.BottomRight.x, topRight.ScreenPx.x, 1e-5f, "top-right shares BottomRight's x");
            Assert.AreEqual(anchor.y + quad.TopLeft.y, topRight.ScreenPx.y, 1e-5f, "top-right shares TopLeft's y");

            Assert.AreEqual(anchor.x + quad.BottomRight.x, bottomRight.ScreenPx.x, 1e-5f);
            Assert.AreEqual(anchor.y + quad.BottomRight.y, bottomRight.ScreenPx.y, 1e-5f);

            Assert.AreEqual(anchor.x + quad.TopLeft.x, bottomLeft.ScreenPx.x, 1e-5f, "bottom-left shares TopLeft's x");
            Assert.AreEqual(anchor.y + quad.BottomRight.y, bottomLeft.ScreenPx.y, 1e-5f, "bottom-left shares BottomRight's y");

            // UVs: no flip — topLeft gets UvTopLeft verbatim, bottomRight gets UvBottomRight verbatim.
            Assert.AreEqual(quad.UvTopLeft.x, topLeft.Uv.x, 1e-6f);
            Assert.AreEqual(quad.UvTopLeft.y, topLeft.Uv.y, 1e-6f);
            Assert.AreEqual(quad.UvBottomRight.x, bottomRight.Uv.x, 1e-6f);
            Assert.AreEqual(quad.UvBottomRight.y, bottomRight.Uv.y, 1e-6f);
            Assert.AreEqual(quad.UvBottomRight.x, topRight.Uv.x, 1e-6f, "top-right shares UvBottomRight's u");
            Assert.AreEqual(quad.UvTopLeft.y, topRight.Uv.y, 1e-6f, "top-right shares UvTopLeft's v");

            // Depth + color pass through to every vertex unchanged.
            foreach (BillboardVertex v in new[] { topLeft, topRight, bottomRight, bottomLeft })
            {
                Assert.AreEqual(depth, v.Depth, 1e-6f);
                Assert.AreEqual(color.x, v.Color.x, 1e-6f);
                Assert.AreEqual(color.w, v.Color.w, 1e-6f);
            }
        }

        // ── Golden: half scale (textSizePx == OneEm/2) halves the baked-px extent around the anchor ──
        [Test]
        public void BuildQuad_HalfScale_CornersHalveTheBakedExtentAroundAnchor()
        {
            SymbolQuad quad = MakeQuad();
            var anchor = new float2(0f, 0f);
            float halfSize = TextQuadLayout.OneEm * 0.5f;

            BillboardMath.BuildQuad(in quad, in anchor, halfSize, 0f, in float4.zero, 0f,
                out BillboardVertex topLeft, out _, out BillboardVertex bottomRight, out _);

            Assert.AreEqual(quad.TopLeft.x * 0.5f, topLeft.ScreenPx.x, 1e-5f);
            Assert.AreEqual(quad.TopLeft.y * 0.5f, topLeft.ScreenPx.y, 1e-5f);
            Assert.AreEqual(quad.BottomRight.x * 0.5f, bottomRight.ScreenPx.x, 1e-5f);
            Assert.AreEqual(quad.BottomRight.y * 0.5f, bottomRight.ScreenPx.y, 1e-5f);
        }

        // ── I5a: BillboardMath is glyph/sprite-AGNOSTIC by design (SymbolQuad.cs) — an icon's SymbolQuad
        //    (from IconQuadLayout, laid out over a sprite-sheet rect) flows through the SAME BuildQuad an
        //    icon PlacedQuad would carry (StagePoint copies quads verbatim), producing 4 vertices with the
        //    icon's own sprite UVs — no separate icon code path needed at this layer. ──
        [Test]
        public void BuildQuad_IconSymbolQuad_FourVerticesCarryTheIconUvs()
        {
            SymbolQuad iconQuad = IconQuadLayout.Layout(
                new MapRenderer.Core.Text.Sprites.SpriteEntry { X = 16, Y = 0, Width = 24, Height = 24, PixelRatio = 2f, Sdf = false },
                new int2(64, 64), iconSize: 1f, TextAnchor.Center, float2.zero);
            var anchor = new float2(50f, 75f);

            var white = new float4(1f, 1f, 1f, 1f);
            BillboardMath.BuildQuad(in iconQuad, in anchor, TextQuadLayout.OneEm, 0f, in white, 0f,
                out BillboardVertex topLeft, out BillboardVertex topRight,
                out BillboardVertex bottomRight, out BillboardVertex bottomLeft);

            Assert.AreEqual(iconQuad.UvTopLeft.x, topLeft.Uv.x, 1e-6f);
            Assert.AreEqual(iconQuad.UvTopLeft.y, topLeft.Uv.y, 1e-6f);
            Assert.AreEqual(iconQuad.UvBottomRight.x, bottomRight.Uv.x, 1e-6f);
            Assert.AreEqual(iconQuad.UvBottomRight.y, bottomRight.Uv.y, 1e-6f);
            Assert.AreEqual(iconQuad.UvBottomRight.x, topRight.Uv.x, 1e-6f);
            Assert.AreEqual(iconQuad.UvTopLeft.y, topRight.Uv.y, 1e-6f);
            Assert.AreEqual(iconQuad.UvTopLeft.x, bottomLeft.Uv.x, 1e-6f);
            Assert.AreEqual(iconQuad.UvBottomRight.y, bottomLeft.Uv.y, 1e-6f);

            Assert.AreEqual(anchor.x + iconQuad.TopLeft.x, topLeft.ScreenPx.x, 1e-4f);
            Assert.AreEqual(anchor.y + iconQuad.TopLeft.y, topLeft.ScreenPx.y, 1e-4f);
        }

        // ── Decisive tooth: zoom-independence — same label, same text-size, two different anchor screen
        //    positions (simulating two camera zooms) must produce IDENTICAL screen-pixel width/height. ──
        [Test]
        public void BuildQuad_SameLabelTwoAnchors_IdenticalScreenPixelSize()
        {
            SymbolQuad quad = MakeQuad();
            const float textSizePx = 32f;
            var anchorZoomedOut = new float2(50f, 60f);
            var anchorZoomedIn = new float2(730f, 210f); // a totally different screen position

            BillboardMath.BuildQuad(in quad, in anchorZoomedOut, textSizePx, 0f, in float4.zero, 0f,
                out BillboardVertex topLeftOut, out _, out BillboardVertex bottomRightOut, out _);
            BillboardMath.BuildQuad(in quad, in anchorZoomedIn, textSizePx, 0f, in float4.zero, 0f,
                out BillboardVertex topLeftIn, out _, out BillboardVertex bottomRightIn, out _);

            float widthOut = bottomRightOut.ScreenPx.x - topLeftOut.ScreenPx.x;
            float heightOut = topLeftOut.ScreenPx.y - bottomRightOut.ScreenPx.y;
            float widthIn = bottomRightIn.ScreenPx.x - topLeftIn.ScreenPx.x;
            float heightIn = topLeftIn.ScreenPx.y - bottomRightIn.ScreenPx.y;

            Assert.AreEqual(widthOut, widthIn, 1e-5f, "billboard screen-pixel WIDTH must not depend on anchor position (zoom-independent)");
            Assert.AreEqual(heightOut, heightIn, 1e-5f, "billboard screen-pixel HEIGHT must not depend on anchor position (zoom-independent)");

            // Teeth: the two anchors are genuinely different, so this isn't trivially true because both
            // builds happened to collapse to the same point.
            Assert.AreNotEqual(topLeftOut.ScreenPx.x, topLeftIn.ScreenPx.x, "test precondition: the two anchors must differ");
        }

        // ── Zero rotation: axis-aligned — top edge stays horizontal, left edge stays vertical, for ANY anchor. ──
        [Test]
        public void BuildQuad_ZeroRotation_IsAxisAligned()
        {
            SymbolQuad quad = MakeQuad();
            var anchor = new float2(17f, -42f);

            BillboardMath.BuildQuad(in quad, in anchor, TextQuadLayout.OneEm, 0f, in float4.zero, 0f,
                out BillboardVertex topLeft, out BillboardVertex topRight,
                out BillboardVertex bottomRight, out BillboardVertex bottomLeft);

            Assert.AreEqual(topLeft.ScreenPx.y, topRight.ScreenPx.y, 1e-5f, "top edge must be horizontal (axis-aligned, no rotation)");
            Assert.AreEqual(topLeft.ScreenPx.x, bottomLeft.ScreenPx.x, 1e-5f, "left edge must be vertical (axis-aligned, no rotation)");
            Assert.AreEqual(bottomRight.ScreenPx.y, bottomLeft.ScreenPx.y, 1e-5f, "bottom edge must be horizontal");
            Assert.AreEqual(topRight.ScreenPx.x, bottomRight.ScreenPx.x, 1e-5f, "right edge must be vertical");
        }

        // ── Rotation (#4): a +90° rotation about the anchor maps each anchor-relative corner (x,y) -> (-y,x)
        //    in the y-up frame. Explicit angle (no bearing/sign ambiguity) — the headless red-proof for the
        //    rotation capability that along-line text (#5) reuses. ──
        [Test]
        public void BuildQuad_NinetyDegrees_RotatesEveryCornerAboutTheAnchor()
        {
            SymbolQuad quad = MakeQuad();
            var anchor = new float2(100f, 200f);
            float halfPi = math.PI / 2f;

            BillboardMath.BuildQuad(in quad, in anchor, TextQuadLayout.OneEm, 0f, in float4.zero, halfPi,
                out BillboardVertex topLeft, out BillboardVertex topRight,
                out BillboardVertex bottomRight, out BillboardVertex bottomLeft);

            // Local corner (x,y) rotated +90° (CCW, y-up) -> (-y, x), then translated to the anchor.
            AssertRotated90(anchor, quad.TopLeft, topLeft.ScreenPx, "topLeft");
            AssertRotated90(anchor, new float2(quad.BottomRight.x, quad.TopLeft.y), topRight.ScreenPx, "topRight");
            AssertRotated90(anchor, quad.BottomRight, bottomRight.ScreenPx, "bottomRight");
            AssertRotated90(anchor, new float2(quad.TopLeft.x, quad.BottomRight.y), bottomLeft.ScreenPx, "bottomLeft");

            // Teeth: the quad is genuinely rotated — the top edge is no longer horizontal (it's vertical now).
            Assert.AreNotEqual(topLeft.ScreenPx.y, topRight.ScreenPx.y, "a rotated quad's top edge must not stay horizontal");
        }

        private static void AssertRotated90(float2 anchor, float2 local, float2 actual, string label)
        {
            var expected = new float2(anchor.x - local.y, anchor.y + local.x); // (x,y)->(-y,x) about anchor
            Assert.AreEqual(expected.x, actual.x, 1e-4f, $"{label} x after +90°");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, $"{label} y after +90°");
        }
    }
}
