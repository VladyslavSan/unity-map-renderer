// Engine-free, pure 2D (float2 only, no float4x4/camera) — every screen-space position here is already
// resolved (SymbolScreenProjection did the 3D→screen work); this is just the baked-px quad → screen-px quad scale.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Builds one quad's 4 <see cref="WorldBillboardVertex"/>s for the world-anchored symbol path. Layout already
    /// baked <c>text-offset</c> into the corners, so no offset is applied here; <c>rotationRadians</c> spins the
    /// quad about its anchor. Non-local invariant: <see cref="WorldBillboardVertex.Offset"/>'s unit follows
    /// <see cref="WorldBillboardVertex.AlignFlags"/> bit2 (screen px or ground metres), and the caller picks it
    /// through <c>emScale</c> for corners and translate delta alike. A positive angle turns counter-clockwise
    /// on screen, measured through the GPU path; <see cref="SymbolBearing.IconRotationRadians"/> converts
    /// <c>icon-rotate</c> into it.
    /// </summary>
    public static class BillboardMath
    {
        // CCW rotation in a y-up frame (Burst-safe static — no capturing local function). Identity at angle 0.
        private static float2 Rotate(in float2 p, float sin, float cos)
            => new float2(cos * p.x - sin * p.y, sin * p.x + cos * p.y);

        /// <summary>
        /// Builds one quad's 4 <see cref="WorldBillboardVertex"/>s in TL, TR, BR, BL order, triangles (TL,TR,BR)
        /// and (TL,BR,BL), as <see cref="MapRenderer.Unity.Text.Placement.WorldSymbolRenderer"/>'s index emit expects.
        /// <c>Offset</c> is y-down, so each corner's (and the translate delta's) Y is negated.
        /// <paramref name="colorRgb"/> is already linear and copied verbatim; anchor, flags, tangent and
        /// surface-up go onto all four corners, and the renderer appends opacity.
        /// </summary>
        /// <param name="emScale">What ONE em maps to in <see cref="WorldBillboardVertex.Offset"/>'s unit: logical
        /// px (<see cref="PlacedQuad.TextSizePx"/>) or METRES (<c>TextSizePx · CornerMetresPerLogicalPixel</c>).</param>
        public static void BuildWorldQuad(
            in SymbolQuad quad,
            in float3 anchorLocal,
            float emScale,
            in float3 colorRgb,
            float rotationRadians,
            in float2 translateDeltaPx,
            in float3 tangentLocal,
            in float3 surfaceUp,
            float alignFlags,
            out WorldBillboardVertex topLeft,
            out WorldBillboardVertex topRight,
            out WorldBillboardVertex bottomRight,
            out WorldBillboardVertex bottomLeft)
        {
            QuadCornersLocal(in quad, emScale, rotationRadians,
                out float2 tlCorner, out float2 trCorner, out float2 brCorner, out float2 blCorner);

            // Negate the corner's Y (and the translate delta's Y, same convention) — UV stays
            // attached to its ORIGINAL corner (no flip).
            float2 translateOffset = new float2(translateDeltaPx.x, -translateDeltaPx.y);
            float2 tlOffset = new float2(tlCorner.x, -tlCorner.y) + translateOffset;
            float2 trOffset = new float2(trCorner.x, -trCorner.y) + translateOffset;
            float2 brOffset = new float2(brCorner.x, -brCorner.y) + translateOffset;
            float2 blOffset = new float2(blCorner.x, -blCorner.y) + translateOffset;

            float2 uvTopLeft = quad.UvTopLeft;
            float2 uvBottomRight = quad.UvBottomRight;
            float2 uvTopRight = new float2(uvBottomRight.x, uvTopLeft.y);
            float2 uvBottomLeft = new float2(uvTopLeft.x, uvBottomRight.y);

            float page = quad.Page;

            topLeft     = MakeWorldVertex(anchorLocal, tlOffset, uvTopLeft, page, colorRgb, tangentLocal, surfaceUp, alignFlags);
            topRight    = MakeWorldVertex(anchorLocal, trOffset, uvTopRight, page, colorRgb, tangentLocal, surfaceUp, alignFlags);
            bottomRight = MakeWorldVertex(anchorLocal, brOffset, uvBottomRight, page, colorRgb, tangentLocal, surfaceUp, alignFlags);
            bottomLeft  = MakeWorldVertex(anchorLocal, blOffset, uvBottomLeft, page, colorRgb, tangentLocal, surfaceUp, alignFlags);
        }

        /// <summary>
        /// The four ROTATED cell corners of a quad in its y-UP LOCAL frame, before the Y-negation and any
        /// translate: <c>Rotate(corner · emScale / OneEm, rotationRadians)</c>. <see cref="BuildWorldQuad"/> and
        /// <see cref="SymbolBox.BuildRotatedGlyph"/> both call it, so the drawn quad and the collision box come
        /// from ONE expression. The translate delta stays in <see cref="BuildWorldQuad"/>, next to the Y-negation
        /// whose convention it shares. The corners come out in <paramref name="emScale"/>'s unit.
        /// </summary>
        internal static void QuadCornersLocal(
            in SymbolQuad quad, float emScale, float rotationRadians,
            out float2 topLeft, out float2 topRight, out float2 bottomRight, out float2 bottomLeft)
        {
            float scale = emScale / TextQuadLayout.OneEm;

            float2 tlLocal = quad.TopLeft * scale;
            float2 brLocal = quad.BottomRight * scale;
            float2 trLocal = new float2(brLocal.x, tlLocal.y);
            float2 blLocal = new float2(tlLocal.x, brLocal.y);

            math.sincos(rotationRadians, out float sin, out float cos);

            topLeft     = Rotate(tlLocal, sin, cos);
            topRight    = Rotate(trLocal, sin, cos);
            bottomRight = Rotate(brLocal, sin, cos);
            bottomLeft  = Rotate(blLocal, sin, cos);
        }

        private static WorldBillboardVertex MakeWorldVertex(
            in float3 anchorLocal, float2 offsetPx, float2 uv, float page, in float3 colorRgb,
            in float3 tangentLocal, in float3 surfaceUp, float alignFlags)
            => new WorldBillboardVertex
            {
                AnchorLocal = anchorLocal,
                ColorRGB = colorRgb,
                Uv = uv,
                Page = page,
                Offset = offsetPx,
                AlignFlags = alignFlags,
                Tangent = tangentLocal,
                Up = surfaceUp,
            };
    }
}
