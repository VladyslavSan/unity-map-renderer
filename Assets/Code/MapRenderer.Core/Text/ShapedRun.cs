// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The output of <see cref="CodepointTextShaper.Shape(in ShapingRequest)"/>: an ordered run of positioned glyphs in VISUAL
    /// order (the order quads are laid out in, left-to-right on screen) plus the run's resolved
    /// direction. Render-path-agnostic — no anchor/offset/quad assumptions (the SDF glyph-atlas →
    /// text-shaping handoff).
    /// </summary>
    public sealed class ShapedRun
    {
        /// <summary>Positioned glyphs in VISUAL order (already bidi-reordered for RTL runs).</summary>
        public IReadOnlyList<PositionedGlyph> Glyphs { get; init; }

        /// <summary>The run's resolved direction (see <see cref="CodepointTextShaper"/> for how it's determined).</summary>
        public TextDirection Direction { get; init; }
    }
}
