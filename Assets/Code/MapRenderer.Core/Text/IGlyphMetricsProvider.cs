// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Minimal glyph-advance lookup a <see cref="ITextShaper"/> consults while shaping. Keyed by ATLAS
    /// codepoint (a presentation-form codepoint for shaped Arabic, per <see cref="PositionedGlyph.AtlasCodepoint"/>),
    /// not the source character's own codepoint. A real implementation is typically backed by one or
    /// more decoded <see cref="FontStackGlyphs"/> ranges (<see cref="SdfGlyph.Advance"/>).
    /// </summary>
    public interface IGlyphMetricsProvider
    {
        /// <summary>
        /// Looks up the horizontal advance (glyph-PBF pixel units) for <paramref name="codepoint"/>.
        /// Returns false for a codepoint with no known advance (S18 Slice 3 policy: the shaper then
        /// uses 0 rather than throwing — see <see cref="CodepointTextShaper"/>).
        /// </summary>
        bool TryGetAdvance(uint codepoint, out float advance);
    }
}
