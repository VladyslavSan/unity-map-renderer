using MapRenderer.Core.Style;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// S18 Unity-side batch: the seam that builds a glyph <see cref="IGlyphSource"/> from a style
    /// document's root <c>glyphs</c> URL template (<see cref="StyleDocument.Glyphs"/>) — mirrors
    /// <see cref="TileDataSourceFactory"/> for tiles. Kept separate from <c>GlyphManager</c> so that
    /// class stays decoupled from any concrete transport and compiles engine-free (see its file header).
    /// Not yet called in S18 (which stops before any renderer wiring — that is S19); this is the seam a
    /// future <c>GlyphManager</c> construction site uses, e.g. <c>new GlyphManager(GlyphSourceFactory.Create(style))</c>.
    /// </summary>
    internal static class GlyphSourceFactory
    {
        /// <summary>Builds the glyph source for <paramref name="style"/>'s root <c>glyphs</c> template.</summary>
        public static IGlyphSource Create(StyleDocument style) => new UnityWebRequestGlyphSource(style?.Glyphs);
    }
}
