// BLITTABLE: Burst jobs read it as a NativeArray<GlyphAtlasEntry> element, like LineRibbonVertex and
// GeoCoordinate. Keep to blittable fields only: no byte[], no string, no reference types.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One packed glyph's location + metrics inside a <see cref="GlyphAtlas"/>. Does NOT bake a UV rect —
    /// Core stays float/precision-free. The quad builder computes <c>uv = AtlasOrigin / atlasSize</c>
    /// (dividing by the <see cref="GlyphAtlas.Size"/> the atlas exposes) at the point it needs UVs.
    /// </summary>
    public readonly struct GlyphAtlasEntry
    {
        /// <summary>The Unicode codepoint this entry represents (matches <see cref="SdfGlyph.Codepoint"/>).</summary>
        public uint Codepoint { get; init; }

        /// <summary>Top-left pixel origin of this glyph's packed cell within the atlas.</summary>
        public int2 AtlasOrigin { get; init; }

        /// <summary>
        /// Packed cell size in pixels: <c>(Width + 2*GlyphSdf.Buffer, Height + 2*GlyphSdf.Buffer)</c> —
        /// identical to the source <see cref="SdfGlyph.CellSize"/> this entry was appended from.
        /// </summary>
        public int2 CellSize { get; init; }

        /// <summary>Left side-bearing in pixels (may be negative) — copied from the source glyph.</summary>
        public int Left { get; init; }

        /// <summary>Distance from the baseline to the glyph's top in pixels (may be negative).</summary>
        public int Top { get; init; }

        /// <summary>Horizontal advance in pixels.</summary>
        public int Advance { get; init; }

        /// <summary>
        /// Which <see cref="GlyphAtlas"/> page (Texture2DArray layer) this glyph was packed
        /// into. 0 for every glyph until the atlas overflows a page's fixed capacity (single-page
        /// behaviour is byte-identical — every entry stays <c>Page == 0</c>).
        /// </summary>
        public int Page { get; init; }
    }
}
