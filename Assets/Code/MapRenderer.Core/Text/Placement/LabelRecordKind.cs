namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// How one collected label record is laid out: a single anchored placement, or glyphs distributed along a
    /// path. It selects which per-kind detail array a record's <c>Detail</c> index addresses, in every
    /// structure that carries collected labels — <c>SymbolTileLabelBlock</c>, the gather mirror, and the
    /// <c>SymbolLabelBatch</c> oracle.
    ///
    /// <para><b>Not <see cref="LabelKind"/>.</b> That one is Text vs Icon — what a label's glyphs are drawn
    /// FROM (which atlas). This one is Point vs Curved — how the label is PLACED. A label is independently
    /// one of each, so the two must not be merged or named alike.</para>
    ///
    /// <para><b>Why it is top-level.</b> It began as <c>SymbolLabelBatch.Kind</c>, nested inside the
    /// per-frame oracle. Production does not build that batch — the block baker and the gather job do — yet
    /// both had to reach through the oracle's type name for the discriminator, which is what kept the oracle
    /// in the production assemblies. The enum is the part production genuinely shares; the batch is not.</para>
    ///
    /// <para><b>Backing storage is deliberately still <c>byte</c></b> in the block and the mirror
    /// (<c>Kinds</c>, <c>MKinds</c>): those are Burst-facing <c>NativeArray</c>s and retyping them is a
    /// separate decision about job signatures, not part of promoting the enum. Cast at the comparison.</para>
    /// </summary>
    public enum LabelRecordKind : byte
    {
        /// <summary>One anchored placement — the record's detail indexes the point array.</summary>
        Point = 0,

        /// <summary>Glyphs distributed along a path — the record's detail indexes the curved array.</summary>
        Curved = 1,
    }
}
