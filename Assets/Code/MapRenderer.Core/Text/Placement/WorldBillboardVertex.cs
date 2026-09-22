// Engine-free: no UnityEngine dependency.
// BLITTABLE (mirrors BillboardVertex): ONE per billboard corner = stream 0 of the world-anchored symbol
// mesh — fed straight to Mesh.SetVertexBufferData by WorldBillboardMeshBuilder — field
// DECLARATION order is the vertex stream byte layout and MUST match
// WorldBillboardMeshBuilder.VertexDescriptors' order EXACTLY: Position (AnchorLocal), Color (ColorRGB),
// TexCoord0 (Uv), TexCoord1 (Page), TexCoord2 (Offset), TexCoord3 (AlignFlags), TexCoord5 (Tangent),
// TexCoord6 (Up), TexCoord7 (SdfWidenPx) — Unity's canonical ascending VertexAttribute enum order
// (Position=0, Color=3, TexCoord0=4, TexCoord1=5, TexCoord2=6, TexCoord3=7, TexCoord5=9, TexCoord6=10,
// TexCoord7=11; see
// BillboardVertex/StyledLineTileBuilder's identical rule). Declaring these out of order triggers Unity's
// "non-standard order" auto-adjustment, which silently reinterprets the byte layout against a DIFFERENT
// stream than this struct actually writes (BillboardVertex's header documents the exact failure mode: the
// symbol renders nothing). Keep to blittable fields only.
//
// FROZEN: a new attribute is APPENDED as the new last field, never a reshuffle of stream 0.
// Opacity is NOT here — it is stream 1 (a separate per-frame dynamic array, TexCoord4 on the mesh) so a
// fade update never touches this stream's topology.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One world-anchored billboard-corner vertex — the world-space sibling of <see cref="BillboardVertex"/>.
    /// The anchor is a real render-space position (GPU-projected via stock MVP,
    /// <c>TransformObjectToHClip</c>); the glyph corner itself is still a constant-px screen offset applied
    /// in the vertex shader, so text stays legible-sized at any distance (see
    /// <c>SymbolTextWorld_ForwardPass.hlsl</c>).
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

        /// <summary>UNROTATED glyph-corner offset from the anchor (TEXCOORD2) — the world-path analogue of
        /// <see cref="BillboardMath.BuildQuad"/>'s anchor-relative corner, minus any rotation (north-up;
        /// map-aligned bearing rotation is applied in the vertex shader once <c>AlignFlags</c> is wired —
        /// see <c>SymbolTextWorld_ForwardPass.hlsl</c>).
        /// <para><b>TWO UNITS, selected by <see cref="AlignFlags"/> bit2.</b> Bit2 CLEAR (every point/icon
        /// symbol, every viewport-pitched curved symbol): LOGICAL SCREEN PIXELS, added to
        /// <c>clip.xy</c> after projection, so the glyph is a fixed screen size at any depth. Bit2 SET
        /// (map-pitched curved): WORLD METRES, displaced in the ground plane at the anchor before projection,
        /// so the glyph is a fixed WORLD size and foreshortens with depth. <b>The field is NOT named
        /// <c>OffsetPx</c>:</b> a px suffix would be a lie under bit2's metre unit. Read the unit off bit2,
        /// never off the name.</para></summary>
        public float2 Offset;

        /// <summary>Bit flags (TEXCOORD3): bit0 = rotation-alignment map(1)/viewport(0), always 0 today and
        /// unread by the shader; bit1 = along-line tangent rotation (set only by curved; point/icon leave it
        /// clear).</summary>
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
        /// Written, but not yet read by any shader.</summary>
        public float3 Up;

        /// <summary>TEXCOORD7 — how far this corner's glyph is
        /// grown past the SDF fill edge, in DEVICE px — x pushes the edge OUT, y widens the AA transition.
        /// <para>Zero on a text run, which makes the shader's one shading path reproduce the plain fill.
        /// A halo run carries <c>text-halo-width</c>/<c>text-halo-blur</c> here and the halo colour in
        /// <see cref="ColorRGB"/>, which is the whole of what makes it a halo — the shader has no halo
        /// concept. Written by <c>WorldSymbolRenderer.Emit</c>, not by
        /// <see cref="BillboardMath.BuildWorldQuad"/> (same split as the opacity stream).</para></summary>
        public float2 SdfWidenPx;
    }
}
