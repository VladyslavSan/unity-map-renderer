// Engine-free: no UnityEngine dependency. Pure 2D (float2 only, no float4x4/camera) — every screen-space
// position here is already resolved (LabelScreenProjection did the 3D→screen work); this is just the
// baked-px quad → screen-px quad scale, per S19 T8a / S20 decision 6.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 T3: builds the 4 screen-space <see cref="BillboardVertex"/>s for one <see cref="SymbolQuad"/> —
    /// upright (NO camera rotation applied) and zoom-independent (the anchor's screen POSITION moves with
    /// zoom; the quad's screen-pixel SIZE does not, because <paramref name="textSizePx"/> is the resolved
    /// `text-size`, not a function of zoom/distance). F7 (S20 stage doc §6): only the
    /// <c>textSizePx / TextQuadLayout.OneEm</c> scale is applied — S19 already baked
    /// `text-offset`/`text-radial-offset` into the quad's baked-px corners, so there is no second
    /// screen-space offset here (double-apply would be a bug).
    /// </summary>
    public static class BillboardMath
    {
        /// <summary>
        /// Builds one quad's 4 vertices (winding: <paramref name="topLeft"/>, <paramref name="topRight"/>,
        /// <paramref name="bottomRight"/>, <paramref name="bottomLeft"/> — 2 triangles
        /// (topLeft,topRight,bottomRight) + (topLeft,bottomRight,bottomLeft)), in logical screen-pixel space.
        /// </summary>
        public static void BuildQuad(
            in SymbolQuad quad,
            in float2 anchorScreenPx,
            float textSizePx,
            float depth,
            in float4 color,
            out BillboardVertex topLeft,
            out BillboardVertex topRight,
            out BillboardVertex bottomRight,
            out BillboardVertex bottomLeft)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;

            float2 topLeftPx = anchorScreenPx + quad.TopLeft * scale;
            float2 bottomRightPx = anchorScreenPx + quad.BottomRight * scale;
            float2 topRightPx = new float2(bottomRightPx.x, topLeftPx.y);
            float2 bottomLeftPx = new float2(topLeftPx.x, bottomRightPx.y);

            float2 uvTopLeft = quad.UvTopLeft;
            float2 uvBottomRight = quad.UvBottomRight;
            float2 uvTopRight = new float2(uvBottomRight.x, uvTopLeft.y);
            float2 uvBottomLeft = new float2(uvTopLeft.x, uvBottomRight.y);

            topLeft = MakeVertex(topLeftPx, depth, uvTopLeft, color);
            topRight = MakeVertex(topRightPx, depth, uvTopRight, color);
            bottomRight = MakeVertex(bottomRightPx, depth, uvBottomRight, color);
            bottomLeft = MakeVertex(bottomLeftPx, depth, uvBottomLeft, color);
        }

        private static BillboardVertex MakeVertex(float2 screenPx, float depth, float2 uv, float4 color)
            => new BillboardVertex { ScreenPx = screenPx, Depth = depth, Uv = uv, Color = color };
    }
}
