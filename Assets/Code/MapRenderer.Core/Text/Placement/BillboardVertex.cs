// Engine-free: no UnityEngine dependency.
// BLITTABLE (mirrors LineRibbonVertex / GlyphAtlasEntry): this struct crosses into the S20 Jobs boundary
// as a NativeArray<BillboardVertex> element (SymbolBillboardJob's output) AND is fed straight to
// Mesh.SetVertexBufferData by LabelPlacementSystem — field DECLARATION order is the vertex stream byte
// layout and MUST match LabelPlacementSystem.VertexDescriptors' order EXACTLY: Position (ScreenPx.xy +
// Depth), Color, TexCoord0 (Uv) — Unity's canonical ascending VertexAttribute enum order (Position=0,
// Color=3, TexCoord0=4; see StyledLineTileBuilder's identical rule). Declaring TexCoord0 before Color
// (the S20 Slice 1 bug this fixes) triggers Unity's "non-standard order" auto-adjustment, which silently
// reinterprets the byte layout against a DIFFERENT stream than this struct actually writes — the label
// renders nothing (SymbolAtlasOrientationSnapshotTests caught this: 0 ink pixels). Keep to blittable
// fields only.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One camera-facing billboard vertex, in LOGICAL screen-pixel space (not world/clip space — the
    /// <c>Map/Symbol</c> shader's vertex stage converts px→clip via <c>_ScreenParamsLogical</c>, bypassing
    /// the normal object/view/projection transform entirely; see <c>Shaders/Map/Symbol/README.md</c>).
    /// </summary>
    public struct BillboardVertex
    {
        /// <summary>Logical screen-pixel position (y-up, origin bottom-left — matches
        /// <see cref="LabelScreenProjection.TryProjectAnchor"/>'s output convention).</summary>
        public float2 ScreenPx;

        /// <summary>NDC depth (<c>clip.z/clip.w</c> from the real camera projection) — carried through for
        /// a future depth-aware submission; Slice 1's shader renders with depth test disabled (unlit,
        /// UI-like, always-on-top text — see the shader README's F2 divergence note).</summary>
        public float Depth;

        /// <summary>Per-vertex color: <see cref="LabelPaint.TextColor"/> with <see cref="LabelPaint.Opacity"/> baked into alpha.</summary>
        public float4 Color;

        /// <summary>Normalized atlas UV (straight from <see cref="SymbolQuad.UvTopLeft"/>/<see cref="SymbolQuad.UvBottomRight"/> — no flip).</summary>
        public float2 Uv;
    }
}
