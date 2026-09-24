using MapRenderer.Core.Style;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The seam that builds a glyph <see cref="IGlyphSource"/> from a style document's root
    /// <c>glyphs</c> URL template (<see cref="StyleDocument.Glyphs"/>) — mirrors
    /// <see cref="TileDataSourceFactory"/> for tiles. It keeps <c>GlyphManager</c> free of any concrete
    /// transport: <c>new GlyphManager(GlyphSourceFactory.Create(style))</c>.
    /// </summary>
    internal static class GlyphSourceFactory
    {
        /// <summary>Builds the glyph source for <paramref name="style"/>'s root <c>glyphs</c> template.</summary>
        public static IGlyphSource Create(StyleDocument style) => new UnityWebRequestGlyphSource(style?.Glyphs);
    }
}
