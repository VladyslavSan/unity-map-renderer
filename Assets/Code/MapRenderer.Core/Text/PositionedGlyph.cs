// Engine-free, BLITTABLE — this struct crosses into Burst jobs as a NativeArray<PositionedGlyph> element at the
// MapRenderer.Jobs boundary (the same Core-defines-the-struct/Jobs-creates-the-NativeArray pattern
// LineRibbonVertex/GeoCoordinate/GlyphAtlasEntry use) — keep to blittable scalar fields only: no byte[], no
// string, no reference types.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One glyph of a <see cref="ShapedRun"/>, already resolved to the atlas codepoint the SDF atlas is
    /// keyed by (a presentation-form codepoint for shaped Arabic, the source codepoint otherwise) and
    /// positioned relative to the previous glyph in the run.
    /// </summary>
    public readonly struct PositionedGlyph
    {
        /// <summary>
        /// The codepoint that keys the <see cref="GlyphAtlas"/>/<see cref="GlyphAtlasEntry"/> lookup —
        /// NOT necessarily the source character's own codepoint (Arabic joining maps it to a
        /// presentation-form codepoint; see <see cref="ArabicJoining"/>).
        /// </summary>
        public uint AtlasCodepoint { get; init; }

        /// <summary>
        /// The atlas font id this glyph resolved to (<see cref="GlyphAtlas.FontId"/>). Per GLYPH, not per
        /// label: <see cref="FontStackResolver"/> falls back down the stack codepoint by codepoint, so one
        /// label can legitimately draw from two faces. Pairs with <see cref="AtlasCodepoint"/> to key the
        /// atlas — neither half alone identifies a bitmap.
        /// </summary>
        public int FontId { get; init; }

        /// <summary>Horizontal pen advance to the next glyph, in the glyph-PBF's pixel units.</summary>
        public float XAdvance { get; init; }

        /// <summary>Vertical pen advance (0 — the shaper does not shape vertical text).</summary>
        public float YAdvance { get; init; }

        /// <summary>Per-glyph horizontal offset from the pen position (0 — no GPOS-style kerning).</summary>
        public float XOffset { get; init; }

        /// <summary>Per-glyph vertical offset from the pen position (0 — no GPOS-style kerning).</summary>
        public float YOffset { get; init; }

        /// <summary>
        /// The UTF-16 char offset into the source <see cref="ShapingRequest.Text"/> this glyph was
        /// produced from (the first source char, for a merged ligature such as lam-alef).
        /// </summary>
        public int Cluster { get; init; }
    }
}
