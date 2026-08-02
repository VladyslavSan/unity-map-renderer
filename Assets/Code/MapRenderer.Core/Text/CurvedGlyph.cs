// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One cell of a curved along-line label: its cell plus the along-run arc distance to place it at.
    /// Two producers, both riding the same field: <see cref="CurvedTextLayout"/> emits one per GLYPH of a
    /// line-placed text label (#5), and <c>StyledSymbolTileBuilder</c> emits exactly ONE, holding an
    /// <see cref="IconQuadLayout"/> sprite quad, for a map-aligned line ICON (P-B). The per-frame placement
    /// maps <see cref="ArcCenter"/> onto the projected screen line to get the cell's screen point + tangent.
    /// </summary>
    public readonly struct CurvedGlyph
    {
        /// <summary>Baked-px arc distance (from the run start) to this cell's horizontal center — the point
        /// on the line the cell is anchored + rotated about. Placement scales this by <c>text-size/OneEm</c>.
        /// A lone icon cell sits at 0 (it IS the run).</summary>
        public float ArcCenter { get; init; }

        /// <summary>The cell, centered HORIZONTALLY on <see cref="ArcCenter"/> (so <see cref="SymbolQuad.TopLeft"/>.x
        /// / <see cref="SymbolQuad.BottomRight"/>.x straddle 0). Its VERTICAL convention is the producer's:
        /// a text glyph is BASELINE-relative (Top/Bottom keep their baseline offsets, so ascenders/descenders
        /// ride above the line rather than across it), while an icon quad is vertically CENTRED on the line
        /// (<c>icon-anchor: center</c>). Placement depends on neither — the arc walk, rotation and world-quad
        /// bake are geometrically correct for any vertical placement of the cell about its anchor.</summary>
        public SymbolQuad Cell { get; init; }

        /// <summary>
        /// Baked-px transparent border baked into <see cref="Cell"/>, per side — <c>0</c> for every TEXT
        /// glyph (a glyph cell has no border), non-zero only for the lone icon cell an along-line icon emits.
        /// The cell is DRAWN with the border (that is what antialiases the icon's silhouette); everything
        /// that reasons about where the ink IS — the rotated collision box and the tangent chord probe —
        /// subtracts it first. Text is therefore byte-identical by construction: every new term is an exact
        /// <c>x - 0f</c>.
        /// </summary>
        public float CellSkirt { get; init; }
    }
}
