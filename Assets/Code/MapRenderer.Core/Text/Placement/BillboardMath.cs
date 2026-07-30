// Engine-free: no UnityEngine dependency. Pure 2D (float2 only, no float4x4/camera) — every screen-space
// position here is already resolved (LabelScreenProjection did the 3D→screen work); this is just the
// baked-px quad → screen-px quad scale, per S19 T8a / S20 decision 6.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Epic A / A1 (world-anchored-labels-design.md §11 A1): builds one quad's 4
    /// <see cref="WorldBillboardVertex"/>s for the world-anchored point/icon/curved-text draw path.
    /// zoom-independent (the anchor moves with zoom; the quad's screen-pixel SIZE does not, because
    /// <c>textSizePx</c> is the resolved `text-size`, not a function of zoom/distance). F7 (S20 stage doc §6):
    /// only the <c>textSizePx / TextQuadLayout.OneEm</c> scale is applied — S19 already baked
    /// `text-offset`/`text-radial-offset` into the quad's baked-px corners, so there is no second screen-space
    /// offset here (double-apply would be a bug).
    ///
    /// <para>A <c>rotationRadians</c> spins the quad about its anchor — 0 for the default upright/viewport
    /// billboard, the map bearing for <c>text-rotation-alignment:map</c> (#4). The same per-corner rotation is
    /// what along-line text (#5) reuses per glyph.</para>
    ///
    /// <para><b>Which way it turns on screen: COUNTER-clockwise for a positive angle</b> — the corners are
    /// rotated in the quad's y-up LOCAL frame and then Y is negated (A0-F2, below), which lands
    /// <see cref="WorldBillboardVertex.OffsetPx"/> in a y-DOWN screen frame, and a rotation read through a
    /// mirrored axis reverses sense. MEASURED through the real GPU path, not derived — see
    /// <see cref="LabelBearing.IconRotationRadians"/>, which is where a style value whose own convention is
    /// clockwise-positive (<c>icon-rotate</c>) is flipped into this frame. Do not re-derive this on paper:
    /// the previous paper reading had the negation supplying a clockwise sense while also treating
    /// <c>OffsetPx</c> as y-up, which cannot both hold.</para>
    /// </summary>
    public static class BillboardMath
    {
        // CCW rotation in a y-up frame (Burst-safe static — no capturing local function). Identity at angle 0.
        private static float2 Rotate(in float2 p, float sin, float cos)
            => new float2(cos * p.x - sin * p.y, sin * p.x + cos * p.y);

        /// <summary>
        /// Builds one quad's 4 <see cref="WorldBillboardVertex"/>s for the world-anchored point/icon draw
        /// path. Winding: topLeft, topRight, bottomRight, bottomLeft — 2 triangles
        /// (topLeft,topRight,bottomRight) + (topLeft,bottomRight,bottomLeft) — so
        /// <see cref="MapRenderer.Unity.Text.Placement.WorldLabelRenderer"/>'s index emit follows the same
        /// TL/TR/BR/BL pattern throughout.
        ///
        /// <para><b>A0-F2 negation (carried from the A0 test scaffold into production, per the design's
        /// carried finding):</b> the retired screen-space render path's shader baked an extra on-screen
        /// calibration flip (<c>ndc.y = -ndc.y</c>) that this world path's stock
        /// <c>TransformObjectToHClip</c> has no equivalent of — reconciled here, not in the shader, by
        /// negating each corner's (and the translate delta's) Y before it becomes <c>OffsetPx</c>. UV stays
        /// attached to its ORIGINAL corner (no flip) — only the screen-space offset sign changes.</para>
        ///
        /// <para><b>NEW-F2 — no gamma conversion:</b> <paramref name="colorRgb"/> is carried onto
        /// <see cref="WorldBillboardVertex.ColorRGB"/> VERBATIM — the caller (<c>WorldLabelRenderer</c>) is
        /// responsible for handing in the already-linear colour (<c>PlacedQuad.Color</c>, sRGB→linear
        /// baked once at batch build — see <c>LabelPlacementSystem.LinearColor</c>); this method performs no
        /// second conversion (a double-convert would break A/B colour equivalence).</para>
        ///
        /// <para><see cref="WorldBillboardVertex.AnchorLocal"/> is <paramref name="anchorLocal"/> on all four
        /// corners (the Level-1 RTC bake, §3.4 — <c>anchorRender − tileOriginRender</c>, computed by the
        /// caller). <see cref="WorldBillboardVertex.AlignFlags"/> carries <paramref name="alignFlags"/>
        /// verbatim onto every corner — 0 for point/icon (A1: viewport-aligned only; the map-bearing shader
        /// branch is A3), bit1 set for Stage AC's along-line rotation (curved). <paramref name="tangentLocal"/>
        /// rides onto <see cref="WorldBillboardVertex.Tangent"/> identically — <see cref="float3.zero"/> for
        /// point/icon (unread there). Opacity (stream 1) is NOT set here — the renderer appends it separately
        /// per corner (the A-4 fade × the quad's own alpha).</para>
        /// </summary>
        public static void BuildWorldQuad(
            in SymbolQuad quad,
            in float3 anchorLocal,
            float textSizePx,
            in float3 colorRgb,
            float rotationRadians,
            in float2 translateDeltaPx,
            in float3 tangentLocal,
            float alignFlags,
            out WorldBillboardVertex topLeft,
            out WorldBillboardVertex topRight,
            out WorldBillboardVertex bottomRight,
            out WorldBillboardVertex bottomLeft)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;

            float2 tlLocal = quad.TopLeft * scale;
            float2 brLocal = quad.BottomRight * scale;
            float2 trLocal = new float2(brLocal.x, tlLocal.y);
            float2 blLocal = new float2(tlLocal.x, brLocal.y);

            math.sincos(rotationRadians, out float sin, out float cos);

            float2 tlCorner = Rotate(tlLocal, sin, cos);
            float2 trCorner = Rotate(trLocal, sin, cos);
            float2 brCorner = Rotate(brLocal, sin, cos);
            float2 blCorner = Rotate(blLocal, sin, cos);

            // A0-F2: negate the corner's Y (and the translate delta's Y, same convention) — UV stays
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

            topLeft     = MakeWorldVertex(anchorLocal, tlOffset, uvTopLeft, page, colorRgb, tangentLocal, alignFlags);
            topRight    = MakeWorldVertex(anchorLocal, trOffset, uvTopRight, page, colorRgb, tangentLocal, alignFlags);
            bottomRight = MakeWorldVertex(anchorLocal, brOffset, uvBottomRight, page, colorRgb, tangentLocal, alignFlags);
            bottomLeft  = MakeWorldVertex(anchorLocal, blOffset, uvBottomLeft, page, colorRgb, tangentLocal, alignFlags);
        }

        private static WorldBillboardVertex MakeWorldVertex(
            in float3 anchorLocal, float2 offsetPx, float2 uv, float page, in float3 colorRgb,
            in float3 tangentLocal, float alignFlags)
            => new WorldBillboardVertex
            {
                AnchorLocal = anchorLocal,
                ColorRGB = colorRgb,
                Uv = uv,
                Page = page,
                OffsetPx = offsetPx,
                AlignFlags = alignFlags,
                Tangent = tangentLocal,
            };
    }
}
