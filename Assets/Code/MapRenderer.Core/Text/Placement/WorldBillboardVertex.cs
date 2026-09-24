// BLITTABLE: one per billboard corner, stream 0 of the world-anchored symbol mesh (WorldBillboardMeshBuilder).
// Non-local invariant: declaration order is the byte layout and must match VertexDescriptors in Unity's
// ascending VertexAttribute order (Position, Color, TexCoord0-3, 5-7); out of order, Unity silently remaps the
// layout and the symbol renders nothing. Append new attributes last. Opacity is stream 1 (TexCoord4), so a fade
// update never touches this stream.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One world-anchored billboard-corner vertex — the world-space sibling of <see cref="BillboardVertex"/>.
    /// The anchor is a real render-space position (GPU-projected via stock MVP,
    /// <c>TransformObjectToHClip</c>); the glyph corner is an offset the vertex shader applies — constant
    /// screen px, so text stays legible-sized at any distance, except a map-pitched glyph's, which is world
    /// metres (see <see cref="Offset"/> and <c>SymbolTextWorld_ForwardPass.hlsl</c>).
    /// </summary>
    public struct WorldBillboardVertex
    {
        /// <summary>Tile-local render-space position (POSITION) — <c>anchorRender − tileOriginRender</c>,
        /// baked ONCE at emit (Level 1 of the two-level RTC, <c>FloatingOrigin</c>).
        /// Never rebaked on camera motion; the object-to-world transform supplies Level 2 every frame.</summary>
        public float3 AnchorLocal;

        /// <summary>Per-vertex color (COLOR): this RUN's colour, LINEAR — <see cref="SymbolPaint.TextColor"/>
        /// .rgb on a text run and <see cref="SymbolPaint.HaloColor"/>.rgb on a halo one, converted by
        /// <c>SymbolPlacementSystem.LinearColor</c>/<c>LinearHaloColor</c> (not verbatim). WHITE whenever
        /// that colour rides the <c>_TextColor</c>/<c>_HaloColor</c> uniform instead (a CONSTANT
        /// expression). Opacity is stream 1, not this field.</summary>
        public float3 ColorRGB;

        /// <summary>Normalized atlas UV (TEXCOORD0) — straight from <see cref="SymbolQuad.UvTopLeft"/>/
        /// <see cref="SymbolQuad.UvBottomRight"/>, no flip (mirrors <see cref="BillboardVertex.Uv"/>).</summary>
        public float2 Uv;

        /// <summary>The glyph atlas Texture2DArray layer (TEXCOORD1) <see cref="Uv"/> samples from — straight
        /// from <see cref="SymbolQuad.Page"/> (mirrors <see cref="BillboardVertex.Page"/>); a <c>float</c>,
        /// not an <c>int</c>, because it rides a Float32x1 vertex stream.</summary>
        public float Page;

        /// <summary>Glyph-corner offset from the anchor (TEXCOORD2), with the CPU rotation from
        /// <see cref="BillboardMath.BuildWorldQuad"/> baked in; an along-line glyph (<see cref="AlignFlags"/> bit1)
        /// is also oriented by <see cref="Tangent"/> in <c>SymbolTextWorld_ForwardPass.hlsl</c>. Non-local invariant:
        /// the unit comes from bit2, not the name: clear = LOGICAL SCREEN PX added to <c>clip.xy</c> (fixed screen
        /// size), set = WORLD METRES in the ground plane before projection (map-pitched curved).</summary>
        public float2 Offset;

        /// <summary>Bit flags (TEXCOORD3): bit0 = rotation-alignment map(1)/viewport(0), always 0 and
        /// unread by the shader; bit1 = along-line tangent rotation (set only by curved; point/icon leave it
        /// clear); bit2 = map pitch alignment (set only with bit1; <see cref="Offset"/> is then metres).</summary>
        public float AlignFlags;

        /// <summary>TEXCOORD5 — for the curved (along-line) arm:
        /// tile-local WORLD direction along the line at this glyph (unit-normalized, the keep-upright
        /// negation already baked in by <see cref="MapRenderer.Core.Text.Placement.SymbolStagingMath"/>).
        /// Read by the shader ONLY when <see cref="AlignFlags"/> bit1 is set (curved); <see cref="float3.zero"/>
        /// for point/icon (an unread attribute — their render stays byte-identical).</summary>
        public float3 Tangent;

        /// <summary>TEXCOORD6 — unit surface normal at the anchor, pre-RTC render-space DIRECTION (the
        /// Level-1 RTC translation does not apply to a direction), supplied by
        /// <c>IProjection.ProjectPoint(...).Up</c> via <see cref="BillboardMath.BuildWorldQuad"/>.
        /// The shader reads it only for a map-pitched glyph (<see cref="AlignFlags"/> bit2), to build its
        /// ground frame.</summary>
        public float3 Up;

        /// <summary>TEXCOORD7: how far this glyph grows past the SDF fill edge, in DEVICE px (x pushes the edge
        /// out, y widens the AA transition). Zero on a text run; a halo run carries <c>text-halo-width</c>/
        /// <c>text-halo-blur</c> here and the halo colour in <see cref="ColorRGB"/>, since the shader has no halo
        /// concept. <c>WorldSymbolRenderer.Emit</c> writes it, not
        /// <see cref="BillboardMath.BuildWorldQuad"/>.</summary>
        public float2 SdfWidenPx;
    }
}
