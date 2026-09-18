// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Minimal glyph lookup a <see cref="ITextShaper"/> consults while shaping. Keyed by ATLAS codepoint
    /// (a presentation-form codepoint for shaped Arabic, per <see cref="PositionedGlyph.AtlasCodepoint"/>),
    /// not the source character's own codepoint. A real implementation is typically backed by one or
    /// more decoded <see cref="FontStackGlyphs"/> ranges (<see cref="SdfGlyph.Advance"/>).
    ///
    /// <para>It returns the resolved FONT alongside the advance because both come from the same walk down
    /// the stack, and the shaper must record which face answered — the atlas is keyed by it.</para>
    /// </summary>
    public interface IGlyphMetricsProvider
    {
        /// <summary>
        /// Looks up the horizontal advance (glyph-PBF pixel units) and the resolved atlas font id for
        /// <paramref name="codepoint"/>. Returns false for a codepoint found nowhere in the stack (S18
        /// Slice 3 policy: the shaper then uses 0 rather than throwing — see <see cref="CodepointTextShaper"/>).
        /// </summary>
        bool TryResolveGlyph(uint codepoint, out float advance, out int fontId);
    }
}
