// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The MapLibre glyph-PBF SDF bitmap convention: a fixed padding border around every glyph's own
    /// metrics, added on every side so the distance field has room to represent distance beyond the
    /// glyph outline. Buffer = 3 is the MapLibre convention.
    /// </summary>
    public static class GlyphSdf
    {
        /// <summary>Padding border (pixels) added to every side of a glyph's metrics in its SDF bitmap.</summary>
        public const int Buffer = 3;

        /// <summary>The padded (decoded bitmap) width for a glyph of the given own-metric width.</summary>
        public static int CellWidth(int width) => width + 2 * Buffer;

        /// <summary>The padded (decoded bitmap) height for a glyph of the given own-metric height.</summary>
        public static int CellHeight(int height) => height + 2 * Buffer;
    }
}
