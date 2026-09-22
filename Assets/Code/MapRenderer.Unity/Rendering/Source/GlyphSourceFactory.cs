using MapRenderer.Core.Style;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The seam that builds a glyph <see cref="IGlyphSource"/> from a style document's root
    /// <c>glyphs</c> URL template (<see cref="StyleDocument.Glyphs"/>) — mirrors
    /// <see cref="TileDataSourceFactory"/> for tiles. Kept separate from <c>GlyphManager</c> so that
    /// class stays decoupled from any concrete transport and compiles engine-free (see its file header).
    /// A <c>GlyphManager</c> construction site calls it as
    /// <c>new GlyphManager(GlyphSourceFactory.Create(style))</c>.
    /// </summary>
    internal static class GlyphSourceFactory
    {
        /// <summary>Builds the glyph source for <paramref name="style"/>'s root <c>glyphs</c> template.</summary>
        public static IGlyphSource Create(StyleDocument style) => new UnityWebRequestGlyphSource(style?.Glyphs);
    }
}
