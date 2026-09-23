namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// How one collected symbol record is laid out: a single anchored placement, or glyphs distributed along a
    /// path. It selects which per-kind detail array a record's <c>Detail</c> index addresses, in every
    /// structure that carries collected symbols — <c>SymbolTileBlock</c>, the gather mirror, and the
    /// <c>SymbolBatch</c> oracle.
    ///
    /// <para><b>Not <see cref="SymbolKind"/>.</b> That one is Text vs Icon — what a symbol's glyphs are drawn
    /// FROM (which atlas). This one is Point vs Curved — how the symbol is PLACED. A symbol is independently
    /// one of each, so the two must not be merged or named alike.</para>
    ///
    /// <para><b>Top-level, not nested in <c>SymbolBatch</c>.</b> The block baker and the gather job both need
    /// the discriminator, and production does not build that batch. Only the enum is shared production code, so
    /// nesting it would tie production to the oracle's type name.</para>
    ///
    /// <para>Backing storage is <c>byte</c> in the block and the mirror (<c>Kinds</c>, <c>MKinds</c>): those
    /// are Burst-facing <c>NativeArray</c>s, and their element type is a decision about job signatures. Cast at
    /// the comparison.</para>
    /// </summary>
    public enum SymbolPlacementKind : byte
    {
        /// <summary>One anchored placement — the record's detail indexes the point array.</summary>
        Point = 0,

        /// <summary>Glyphs distributed along a path — the record's detail indexes the curved array.</summary>
        Curved = 1,
    }
}
