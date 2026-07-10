// Engine-free: no UnityEngine dependency.
// BLITTABLE: this struct crosses into the S20 Jobs boundary as a NativeArray<PlacedQuad> element — the
// SymbolBillboardJob's INPUT (mirrors the Core-defines-the-struct/Jobs-creates-the-NativeArray pattern
// LineRibbonVertex/GlyphAtlasEntry already use). Keep it to blittable fields only.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One label-local <see cref="SymbolQuad"/> paired with the per-LABEL placement values it needs to
    /// become 4 screen-space <see cref="BillboardVertex"/>s — <see cref="LabelPlacementSystem"/> expands
    /// each surviving <see cref="LabelInstance"/>'s <see cref="TextLayoutResult.Quads"/> into a flat
    /// <c>NativeArray&lt;PlacedQuad&gt;</c> (one entry per glyph quad, anchor/size/color repeated across
    /// every quad of the same label) so <see cref="SymbolBillboardJob"/> stays a simple per-element map —
    /// no per-label indirection inside the Burst job.
    /// </summary>
    public struct PlacedQuad
    {
        /// <summary>The label-local, anchor-relative baked-px quad (S19).</summary>
        public SymbolQuad Quad;

        /// <summary>The label's projected anchor, in logical screen pixels (<see cref="LabelScreenProjection.TryProjectAnchor"/>).</summary>
        public float2 AnchorScreenPx;

        /// <summary>`text-size` in pixels — scales the baked quad by <c>TextSizePx / TextQuadLayout.OneEm</c>.</summary>
        public float TextSizePx;

        /// <summary>NDC depth carried through to every vertex of this quad (see <see cref="BillboardVertex.Depth"/>).</summary>
        public float Depth;

        /// <summary>Per-label vertex color (<see cref="LabelPaint.TextColor"/> × <see cref="LabelPaint.Opacity"/>).</summary>
        public float4 Color;
    }
}
