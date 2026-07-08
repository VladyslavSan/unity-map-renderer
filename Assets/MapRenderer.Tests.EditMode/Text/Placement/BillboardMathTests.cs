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

            BillboardMath.BuildQuad(in quad, in anchor, TextQuadLayout.OneEm, depth, in color,
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

            BillboardMath.BuildQuad(in quad, in anchor, halfSize, 0f, in float4.zero,
                out BillboardVertex topLeft, out _, out BillboardVertex bottomRight, out _);

            Assert.AreEqual(quad.TopLeft.x * 0.5f, topLeft.ScreenPx.x, 1e-5f);
            Assert.AreEqual(quad.TopLeft.y * 0.5f, topLeft.ScreenPx.y, 1e-5f);
            Assert.AreEqual(quad.BottomRight.x * 0.5f, bottomRight.ScreenPx.x, 1e-5f);
            Assert.AreEqual(quad.BottomRight.y * 0.5f, bottomRight.ScreenPx.y, 1e-5f);
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

            BillboardMath.BuildQuad(in quad, in anchorZoomedOut, textSizePx, 0f, in float4.zero,
                out BillboardVertex topLeftOut, out _, out BillboardVertex bottomRightOut, out _);
            BillboardMath.BuildQuad(in quad, in anchorZoomedIn, textSizePx, 0f, in float4.zero,
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

        // ── Axis-aligned: no camera rotation is ever applied — top edge stays horizontal, left edge stays
        //    vertical, for ANY anchor. ──
        [Test]
        public void BuildQuad_IsAxisAligned_NoRotationApplied()
        {
            SymbolQuad quad = MakeQuad();
            var anchor = new float2(17f, -42f);

            BillboardMath.BuildQuad(in quad, in anchor, TextQuadLayout.OneEm, 0f, in float4.zero,
                out BillboardVertex topLeft, out BillboardVertex topRight,
                out BillboardVertex bottomRight, out BillboardVertex bottomLeft);

            Assert.AreEqual(topLeft.ScreenPx.y, topRight.ScreenPx.y, 1e-5f, "top edge must be horizontal (axis-aligned, no rotation)");
            Assert.AreEqual(topLeft.ScreenPx.x, bottomLeft.ScreenPx.x, 1e-5f, "left edge must be vertical (axis-aligned, no rotation)");
            Assert.AreEqual(bottomRight.ScreenPx.y, bottomLeft.ScreenPx.y, 1e-5f, "bottom edge must be horizontal");
            Assert.AreEqual(topRight.ScreenPx.x, bottomRight.ScreenPx.x, 1e-5f, "right edge must be vertical");
        }
    }
}
