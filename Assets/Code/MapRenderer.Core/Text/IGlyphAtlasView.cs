// Engine-free: no UnityEngine dependency.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The read-only glyph-metrics + atlas-size view <see cref="TextQuadLayout"/> consumes — the seam
    /// that keeps layout agnostic of the concrete <see cref="GlyphAtlas"/> (a test double backed by a
    /// handful of synthetic entries implements this with no atlas/packer machinery at all).
    /// </summary>
    public interface IGlyphAtlasView
    {
        /// <summary>Looks up a previously appended glyph's atlas entry by (font, codepoint). The font half
        /// is load-bearing — one atlas serves every layer, and two faces' same codepoint are two different
        /// bitmaps. A view with only one face (a sprite sheet, a test double) ignores it.</summary>
        bool TryGetEntry(int fontId, uint codepoint, out GlyphAtlasEntry entry);

        /// <summary>Current atlas size in pixels — the UV-normalization denominator.</summary>
        int2 Size { get; }
    }
}
