namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// How one collected symbol record is PLACED: one anchored placement, or glyphs along a path. It selects
    /// the per-kind detail array a record's <c>Detail</c> indexes in <c>SymbolTileBlock</c>, the gather
    /// mirror and <c>SymbolBatch</c>. It is independent of <see cref="SymbolKind"/> (Text vs Icon). It is
    /// top-level because the baker and the gather job use it; Burst-facing arrays store it as <c>byte</c>.
    /// </summary>
    public enum SymbolPlacementKind : byte
    {
        /// <summary>One anchored placement — the record's detail indexes the point array.</summary>
        Point = 0,

        /// <summary>Glyphs distributed along a path — the record's detail indexes the curved array.</summary>
        Curved = 1,
    }
}
