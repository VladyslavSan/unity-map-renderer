// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One cell of a curved along-line symbol: its cell plus the along-run arc distance to place it at.
    /// Two producers, both riding the same field: <see cref="CurvedTextLayout"/> emits one per GLYPH of a
    /// line-placed text symbol, and <c>StyledSymbolTileBuilder</c> emits exactly ONE, holding an
    /// <see cref="IconQuadLayout"/> sprite quad, for a map-aligned line ICON. The per-frame placement
    /// maps <see cref="ArcCenter"/> onto the projected screen line to get the cell's screen point + tangent.
    /// </summary>
    public readonly struct CurvedGlyph
    {
        /// <summary>Baked-px arc distance (from the run start) to this cell's horizontal center — the point
        /// on the line the cell is anchored + rotated about. Placement scales this by <c>text-size/OneEm</c>.
        /// A lone icon cell sits at 0 (it IS the run).</summary>
        public float ArcCenter { get; init; }

        /// <summary>The cell, centred HORIZONTALLY on <see cref="ArcCenter"/> (its x-extent straddles 0) and
        /// VERTICALLY on the line: a text glyph on the run's optical cap-band centre
        /// (<c>TextQuadLayout.OpticalCentreBelowReferencePx</c>, as a centred point symbol), an icon quad on its
        /// box centre. The cell→screen/world map is LINEAR about the anchor (<c>BillboardMath.BuildWorldQuad</c>,
        /// <c>SymbolBox.BuildRotatedGlyph</c>), so placement stays correct for any vertical cell placement.</summary>
        public SymbolQuad Cell { get; init; }

        /// <summary>
        /// Transparent border baked into <see cref="Cell"/>, per side, in baked px: <c>0</c> for every TEXT glyph,
        /// non-zero only for the lone cell an along-line icon emits. The cell is DRAWN with the border, which
        /// antialiases the icon's silhouette. Everything that reasons about where the ink IS (the rotated
        /// collision box, the tangent chord probe) subtracts it first; for text that is an exact <c>x - 0f</c>.
        /// </summary>
        public float CellSkirt { get; init; }
    }
}
