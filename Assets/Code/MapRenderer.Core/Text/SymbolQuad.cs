// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.
// BLITTABLE (T8b): this struct crosses into the S20 Jobs boundary as a NativeArray<SymbolQuad> element
// (the same Core-defines-the-struct/Jobs-creates-the-NativeArray pattern LineRibbonVertex/
// GlyphAtlasEntry/PositionedGlyph already use) — keep it to blittable fields only.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One label-local glyph (or, later, sprite icon) quad: an axis-aligned, anchor-relative rectangle
    /// in baked-pixel space (<see cref="TextQuadLayout.OneEm"/> = 24px) plus its normalized atlas UV
    /// rect. Deliberately glyph/sprite-AGNOSTIC — nothing text-specific (no codepoint/cluster field) —
    /// so a later icon-layout stage reuses this exact struct with sprite UVs instead of glyph UVs (the
    /// icon seam; see S19 stage doc §1). S20 scales these by <c>text-size/24</c>, places the anchor on
    /// screen, and turns them into camera-facing billboard vertices — S19 builds no <c>Mesh</c>.
    /// </summary>
    public readonly struct SymbolQuad
    {
        /// <summary>Anchor-relative baked-px corner: (min x, max y) — the visually top-left corner in a y-up frame.</summary>
        public float2 TopLeft { get; init; }

        /// <summary>Anchor-relative baked-px corner: (max x, min y) — the visually bottom-right corner in a y-up frame.</summary>
        public float2 BottomRight { get; init; }

        /// <summary>Normalized atlas UV at <see cref="TopLeft"/>'s texel (<c>AtlasOrigin / atlasSize</c>).</summary>
        public float2 UvTopLeft { get; init; }

        /// <summary>Normalized atlas UV at <see cref="BottomRight"/>'s texel (<c>(AtlasOrigin + CellSize) / atlasSize</c>).</summary>
        public float2 UvBottomRight { get; init; }

        /// <summary>0-based wrapped line index this quad belongs to (see <see cref="TextLayoutResult.LineCount"/>).</summary>
        public int LineIndex { get; init; }
    }
}
