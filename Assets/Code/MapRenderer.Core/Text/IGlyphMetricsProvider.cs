// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Minimal glyph lookup a <see cref="CodepointTextShaper"/> consults while shaping, keyed by ATLAS codepoint
    /// (a presentation form for shaped Arabic, per <see cref="PositionedGlyph.AtlasCodepoint"/>). It is usually
    /// backed by decoded <see cref="FontStackGlyphs"/> ranges. It returns the resolved font with the advance,
    /// because both come from one walk down the stack and the atlas is keyed by the font.
    /// </summary>
    public interface IGlyphMetricsProvider
    {
        /// <summary>
        /// Looks up the horizontal advance (glyph-PBF pixel units) and the resolved atlas font id for
        /// <paramref name="codepoint"/>. Returns false for a codepoint found nowhere in the stack; the
        /// shaper then uses 0 rather than throwing — see <see cref="CodepointTextShaper"/>.
        /// </summary>
        bool TryResolveGlyph(uint codepoint, out float advance, out int fontId);
    }
}
