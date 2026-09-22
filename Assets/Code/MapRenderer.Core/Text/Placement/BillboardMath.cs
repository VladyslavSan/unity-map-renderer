// Engine-free: no UnityEngine dependency. Pure 2D (float2 only, no float4x4/camera) — every screen-space
// position here is already resolved (SymbolScreenProjection did the 3D→screen work); this is just the
// baked-px quad → screen-px quad scale.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Builds one quad's 4 <see cref="WorldBillboardVertex"/>s for the world-anchored
    /// point/icon/curved-text draw path. Zoom-independent (the anchor moves with zoom; the quad's
    /// screen-pixel SIZE does not, because <c>emScale</c> is built from the resolved `text-size`, not
    /// from zoom/distance). Only the <c>emScale / TextQuadLayout.OneEm</c> scale is applied — layout already
    /// baked `text-offset`/`text-radial-offset` into the quad's baked-px corners, so there is no second
    /// screen-space offset here (double-apply would be a bug).
    ///
    /// <para><b><see cref="WorldBillboardVertex.Offset"/> carries TWO possible units, selected by
    /// <see cref="WorldBillboardVertex.AlignFlags"/> bit2.</b> Clear ⇒ logical screen pixels,
    /// added to <c>clip.xy</c> after projection so the glyph is a fixed screen size at
    /// any depth. Set ⇒ WORLD METRES, displaced in the ground plane at the anchor BEFORE projection, so the
    /// glyph is a fixed world size and the perspective divide foreshortens it (a map-pitched `text-size` is
    /// X px TOP-DOWN — the same principle as `line-width`). This method does not know which: it just scales
    /// baked em coordinates by whatever <paramref name="emScale"/> is, and the caller
    /// (<c>WorldSymbolRenderer.Emit</c>) picks the unit. The corners and
    /// <paramref name="translateDeltaPx"/> ALWAYS share whichever unit is in force — there is one displacement
    /// path in the shader, so a mixed <c>off</c> is not expressible.</para>
    ///
    /// <para>A <c>rotationRadians</c> spins the quad about its anchor — 0 for the default upright/viewport
    /// billboard, the map bearing for <c>text-rotation-alignment:map</c>. Along-line text reuses the same
    /// per-corner rotation per glyph.</para>
    ///
    /// <para><b>Which way it turns on screen: COUNTER-clockwise for a positive angle</b> — the corners are
    /// rotated in the quad's y-up LOCAL frame and then Y is negated (below), which lands
    /// <see cref="WorldBillboardVertex.Offset"/> in a y-DOWN screen frame, and a rotation read through a
    /// mirrored axis reverses sense. MEASURED through the real GPU path, not derived — see
    /// <see cref="SymbolBearing.IconRotationRadians"/>, which is where a style value whose own convention is
    /// clockwise-positive (<c>icon-rotate</c>) is flipped into this frame. Do not re-derive this on paper:
    /// the previous paper reading had the negation supplying a clockwise sense while also treating
    /// <c>Offset</c> as y-up, which cannot both hold.</para>
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
        /// <see cref="MapRenderer.Unity.Text.Placement.WorldSymbolRenderer"/>'s index emit follows the same
        /// TL/TR/BR/BL pattern throughout.
        ///
        /// <para><b>The Y negation:</b> the retired screen-space render path's shader baked an extra on-screen
        /// calibration flip (<c>ndc.y = -ndc.y</c>) that this world path's stock
        /// <c>TransformObjectToHClip</c> has no equivalent of — reconciled here, not in the shader, by
        /// negating each corner's (and the translate delta's) Y before it becomes <c>Offset</c>. UV stays
        /// attached to its ORIGINAL corner (no flip) — only the screen-space offset sign changes.</para>
        ///
        /// <para><b>No gamma conversion:</b> <paramref name="colorRgb"/> is carried onto
        /// <see cref="WorldBillboardVertex.ColorRGB"/> VERBATIM — the caller (<c>WorldSymbolRenderer</c>) is
        /// responsible for handing in the already-linear colour (<c>PlacedQuad.Color</c>, sRGB→linear
        /// baked once at batch build — see <c>SymbolPlacementSystem.LinearColor</c>); this method performs no
        /// second conversion.</para>
        ///
        /// <para><see cref="WorldBillboardVertex.AnchorLocal"/> is <paramref name="anchorLocal"/> on all four
        /// corners (the Level-1 RTC bake — <c>anchorRender − tileOriginRender</c>, computed by the
        /// caller). <see cref="WorldBillboardVertex.AlignFlags"/> carries <paramref name="alignFlags"/>
        /// verbatim onto every corner — 0 for point/icon (viewport-aligned only), bit1 set for the along-line
        /// rotation (curved). <paramref name="tangentLocal"/> rides onto
        /// <see cref="WorldBillboardVertex.Tangent"/> identically — <see cref="float3.zero"/> for
        /// point/icon (unread there). <paramref name="surfaceUp"/> rides onto <see cref="WorldBillboardVertex.Up"/>
        /// identically — the unit surface normal at the anchor, pre-RTC render-space direction, written here
        /// and not yet read by any shader. Opacity (stream 1) is NOT set here — the renderer appends it
        /// separately per corner (the fade × the quad's own alpha).</para>
        /// </summary>
        /// <param name="emScale">What ONE em maps to, in <see cref="WorldBillboardVertex.Offset"/>'s output
        /// unit — logical px for a screen billboard (<see cref="PlacedQuad.TextSizePx"/>), METRES for a
        /// map-pitched one (<c>PlacedQuad.TextSizePx · CandidateEmit.CornerMetresPerLogicalPixel</c>). W2: see
        /// the class doc for which <c>AlignFlags</c> bit selects it.</param>
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
        /// The four ROTATED cell corners of a quad, in the quad's y-UP LOCAL frame, before the
        /// Y-negation and before any translate: <c>Rotate(corner · emScale / OneEm, rotationRadians)</c>.
        /// <see cref="BuildWorldQuad"/> and <see cref="SymbolBox.BuildRotatedGlyph"/> both call it, so the
        /// drawn quad and the collision box are built from ONE expression.
        ///
        /// <para><b>Deliberately does NOT take the translate delta.</b> That add stays in
        /// <see cref="BuildWorldQuad"/>, next to the Y-negation it shares a convention with, so the caller's
        /// arithmetic after this call is character-identical to what it was before the extraction and its
        /// emitted <c>Offset</c> is bit-identical.</para>
        ///
        /// <para><paramref name="emScale"/> carries whichever unit the caller is working in — logical screen
        /// px for a viewport billboard, world METRES for a map-pitched one (see the class doc). The corners
        /// come out in that same unit; this method has no opinion on which.</para>
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
