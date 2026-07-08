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
        /// <summary>Looks up a previously appended glyph's atlas entry by codepoint.</summary>
        bool TryGetEntry(uint codepoint, out GlyphAtlasEntry entry);

        /// <summary>Current atlas size in pixels — the UV-normalization denominator.</summary>
        int2 Size { get; }
    }
}
