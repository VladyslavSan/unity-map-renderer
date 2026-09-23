// Engine-free, BLITTABLE — this struct crosses into the Jobs boundary as a NativeArray<SymbolQuad> element (the
// same Core-defines-the-struct/Jobs-creates-the-NativeArray pattern LineRibbonVertex/GlyphAtlasEntry/
// PositionedGlyph use) — keep it to blittable fields only.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One symbol-local glyph or sprite-icon quad: an axis-aligned, anchor-relative rectangle
    /// in baked-pixel space (<see cref="TextQuadLayout.OneEm"/> = 24px) plus its normalized atlas UV
    /// rect. Glyph/sprite-AGNOSTIC — nothing text-specific (no codepoint/cluster field) — so icon layout
    /// reuses this struct with sprite UVs instead of glyph UVs. Placement scales these by
    /// <c>text-size/24</c>, places the anchor on screen, and turns them into camera-facing billboard
    /// vertices; layout itself builds no <c>Mesh</c>.
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

        /// <summary>0-based wrapped line index this quad belongs to.</summary>
        public int LineIndex { get; init; }

        /// <summary>
        /// The glyph atlas page (Texture2DArray layer) <see cref="UvTopLeft"/>/<see cref="UvBottomRight"/>
        /// sample from — copied from the source <see cref="GlyphAtlasEntry.Page"/> at layout time. 0 for
        /// every quad (including every icon/sprite quad — the sprite sheet stays single-page) until the
        /// glyph atlas overflows onto a second page.
        /// </summary>
        public int Page { get; init; }
    }
}
