// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The output of the text shaper: an ordered run of positioned glyphs in LOGICAL order and an embedding
    /// level per glyph. The layouts place each line's glyphs in visual order (<see cref="BidiReorder"/>).
    /// Render-path-agnostic — no anchor/offset/quad assumptions (the SDF glyph-atlas → text-shaping handoff).
    /// </summary>
    public sealed class ShapedRun
    {
        /// <summary>Positioned glyphs in LOGICAL (reading) order.</summary>
        public IReadOnlyList<PositionedGlyph> Glyphs { get; init; }

        /// <summary>
        /// The embedding level of each glyph, parallel to <see cref="Glyphs"/>. Null or empty means every level
        /// is 0, and the layouts then skip the reordering.
        /// </summary>
        public IReadOnlyList<byte> Levels { get; init; }
    }
}
