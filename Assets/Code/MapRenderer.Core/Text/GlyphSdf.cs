// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The MapLibre glyph-PBF SDF bitmap convention: a fixed padding border around every glyph's own
    /// metrics, added on every side so the distance field has room to represent distance beyond the
    /// glyph outline. Buffer = 3 is the MapLibre convention. Also holds the two font/baking metrics the
    /// PBF itself does not carry — <see cref="BaselineBelowReferencePx"/> and
    /// <see cref="NominalCapHeightEm"/> — which every consumer of these bitmaps must therefore assume.
    /// </summary>
    public static class GlyphSdf
    {
        /// <summary>Padding border (pixels) added to every side of a glyph's metrics in its SDF bitmap.</summary>
        public const int Buffer = 3;

        /// <summary>
        /// How far below a font's ascent reference line the typographic baseline sits, in baked px. The
        /// glyph-PBF <c>top</c> metric is top-referenced and negative, so a layout's line origin
        /// (<see cref="TextQuadLayout"/>'s <c>baselineY</c>) is that ascent reference: a baseline-resting
        /// glyph's ink bottom sits this far below it.
        /// <para>Limitation: the value is the mode of <c>Height - Top</c> over the Latin fixture
        /// <c>NotoSansRegular/0-255.pbf.bytes</c> (129 of 189 bitmap-bearing glyphs), the same constancy
        /// <see cref="TextQuadLayout.PlaceGlyph"/> relies on. The other shipped ranges (Arabic, Arabic
        /// presentation forms, variation selectors) modally measure 27. The glyph PBF carries no font-level
        /// metrics, so the value is one constant, not per font stack (<c>docs/road-shields-design.md</c>).</para>
        /// </summary>
        public const float BaselineBelowReferencePx = 26f;

        /// <summary>
        /// Latin cap height, in ems, of the baked fonts: Noto Sans measures a 17 baked-px cap over a 24 px
        /// em, and the literal is written as that division so it carries its own derivation. Used only to
        /// place a centred text block's optical centre (<c>docs/road-shields-design.md</c>), never
        /// to size or position an individual glyph.
        /// </summary>
        public const float NominalCapHeightEm = 17f / 24f;

        /// <summary>The padded (decoded bitmap) width for a glyph of the given own-metric width.</summary>
        public static int CellWidth(int width) => width + 2 * Buffer;

        /// <summary>The padded (decoded bitmap) height for a glyph of the given own-metric height.</summary>
        public static int CellHeight(int height) => height + 2 * Buffer;
    }
}
