// Engine-free: no UnityEngine dependency. Pure 2D (float2 only, no float4x4/camera) — every screen-space
// position here is already resolved (LabelScreenProjection did the 3D→screen work); this is just the
// baked-px quad → screen-px quad scale, per S19 T8a / S20 decision 6.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 T3: builds the 4 screen-space <see cref="BillboardVertex"/>s for one <see cref="SymbolQuad"/> —
    /// zoom-independent (the anchor's screen POSITION moves with zoom; the quad's screen-pixel SIZE does not,
    /// because <paramref name="textSizePx"/> is the resolved `text-size`, not a function of zoom/distance).
    /// F7 (S20 stage doc §6): only the <c>textSizePx / TextQuadLayout.OneEm</c> scale is applied — S19 already
    /// baked `text-offset`/`text-radial-offset` into the quad's baked-px corners, so there is no second
    /// screen-space offset here (double-apply would be a bug).
    ///
    /// <para>A <c>rotationRadians</c> spins the quad about its anchor in the (y-up) screen frame — 0 for the
    /// default upright/viewport billboard, the map bearing for <c>text-rotation-alignment:map</c> (#4). The
    /// same per-corner rotation is what along-line text (#5) reuses per glyph.</para>
    /// </summary>
    public static class BillboardMath
    {
        /// <summary>
        /// Builds one quad's 4 vertices (winding: <paramref name="topLeft"/>, <paramref name="topRight"/>,
        /// <paramref name="bottomRight"/>, <paramref name="bottomLeft"/> — 2 triangles
        /// (topLeft,topRight,bottomRight) + (topLeft,bottomRight,bottomLeft)), in logical screen-pixel space.
        /// <paramref name="rotationRadians"/> rotates the quad about <paramref name="anchorScreenPx"/> (0 =
        /// upright).
        /// </summary>
        public static void BuildQuad(
            in SymbolQuad quad,
            in float2 anchorScreenPx,
            float textSizePx,
            float depth,
            in float4 color,
            float rotationRadians,
            out BillboardVertex topLeft,
            out BillboardVertex topRight,
            out BillboardVertex bottomRight,
            out BillboardVertex bottomLeft)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;

            // The four anchor-relative corners in baked-px scale (all four explicit — a rotated quad is no
            // longer axis-aligned, so topRight/bottomLeft can't be derived from just TL/BR).
            float2 tlLocal = quad.TopLeft * scale;
            float2 brLocal = quad.BottomRight * scale;
            float2 trLocal = new float2(brLocal.x, tlLocal.y);
            float2 blLocal = new float2(tlLocal.x, brLocal.y);

            math.sincos(rotationRadians, out float sin, out float cos);

            float2 topLeftPx     = anchorScreenPx + Rotate(tlLocal, sin, cos);
            float2 topRightPx    = anchorScreenPx + Rotate(trLocal, sin, cos);
            float2 bottomRightPx = anchorScreenPx + Rotate(brLocal, sin, cos);
            float2 bottomLeftPx  = anchorScreenPx + Rotate(blLocal, sin, cos);

            float2 uvTopLeft = quad.UvTopLeft;
            float2 uvBottomRight = quad.UvBottomRight;
            float2 uvTopRight = new float2(uvBottomRight.x, uvTopLeft.y);
            float2 uvBottomLeft = new float2(uvTopLeft.x, uvBottomRight.y);

            topLeft = MakeVertex(topLeftPx, depth, uvTopLeft, color);
            topRight = MakeVertex(topRightPx, depth, uvTopRight, color);
            bottomRight = MakeVertex(bottomRightPx, depth, uvBottomRight, color);
            bottomLeft = MakeVertex(bottomLeftPx, depth, uvBottomLeft, color);
        }

        // CCW rotation in a y-up frame (Burst-safe static — no capturing local function). Identity at angle 0.
        private static float2 Rotate(in float2 p, float sin, float cos)
            => new float2(cos * p.x - sin * p.y, sin * p.x + cos * p.y);

        private static BillboardVertex MakeVertex(float2 screenPx, float depth, float2 uv, float4 color)
            => new BillboardVertex { ScreenPx = screenPx, Depth = depth, Uv = uv, Color = color };
    }
}
