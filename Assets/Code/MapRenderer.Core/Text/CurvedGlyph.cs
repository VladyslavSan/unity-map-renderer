// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One glyph of a curved along-line label (#5): its cell plus the along-run arc distance to place it at.
    /// Produced by <see cref="CurvedTextLayout"/> at build time; the per-frame placement maps
    /// <see cref="ArcCenter"/> onto the projected screen line to get the glyph's screen point + tangent.
    /// </summary>
    public readonly struct CurvedGlyph
    {
        /// <summary>Baked-px arc distance (from the run start) to this glyph's horizontal center — the point
        /// on the line the glyph is anchored + rotated about. Placement scales this by <c>text-size/OneEm</c>.</summary>
        public float ArcCenter { get; init; }

        /// <summary>The glyph cell, centered HORIZONTALLY on <see cref="ArcCenter"/> (so <see cref="SymbolQuad.TopLeft"/>.x
        /// / <see cref="SymbolQuad.BottomRight"/>.x straddle 0) and BASELINE-relative in y (Top/Bottom keep their
        /// baseline offsets — NOT vertically centered, so ascenders/descenders ride above the line, not across it).</summary>
        public SymbolQuad Cell { get; init; }
    }
}
