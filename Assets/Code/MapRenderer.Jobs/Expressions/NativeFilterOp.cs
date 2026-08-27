namespace MapRenderer.Jobs.Expressions
{
    /// <summary>
    /// The native filter VM's op subset (see <see cref="NativeFilterCompiler"/>'s accepted-filter
    /// predicate) — literals, the constant-key lookup, geometry-type equality, generic equality, negation,
    /// and <c>all</c>'s short-circuit step/terminator. Byte-backed: one word per accepted filter node.
    /// </summary>
    internal enum NativeOp : byte
    {
        LitNum,
        LitBool,
        LitStr,
        Get,
        GeomEq,
        Eq,
        Not,
        AllStep,
        PushTrue
    }

    /// <summary>
    /// One instruction of a compiled <see cref="NativeFilterProgram"/>. <see cref="Operand"/> and
    /// <see cref="Immediate"/> are read per <see cref="Op"/>:
    /// <list type="bullet">
    ///   <item><see cref="NativeOp.LitNum"/> — <see cref="Immediate"/> is the literal value.</item>
    ///   <item><see cref="NativeOp.LitBool"/> — <see cref="Operand"/> is 0/1.</item>
    ///   <item><see cref="NativeOp.LitStr"/>/<see cref="NativeOp.Get"/> — <see cref="Operand"/> is a slot
    ///     index into the per-tile-layer binding <c>MapRenderer.Jobs.Mvt.NativeFilterRebind.Rebind</c>
    ///     resolves.</item>
    ///   <item><see cref="NativeOp.GeomEq"/> — <see cref="Operand"/> is the target
    ///     <c>TileGeometryType</c> int (-1 = no feature can match); <see cref="Immediate"/> is 0/1
    ///     negate.</item>
    ///   <item><see cref="NativeOp.Eq"/> — <see cref="Immediate"/> is 0/1 negate (<c>==</c> vs <c>!=</c>),
    ///     uniform with <see cref="NativeOp.GeomEq"/>; <see cref="Operand"/> unused.</item>
    ///   <item><see cref="NativeOp.AllStep"/> — <see cref="Operand"/> is the forward jump target taken on
    ///     a false/short-circuited argument (the index right after the enclosing <c>all</c>'s
    ///     <see cref="NativeOp.PushTrue"/>).</item>
    ///   <item><see cref="NativeOp.Not"/>/<see cref="NativeOp.PushTrue"/> — neither field used.</item>
    /// </list>
    /// </summary>
    internal readonly struct NativeFilterOp
    {
        internal readonly NativeOp Op;
        internal readonly int Operand;
        internal readonly double Immediate;

        internal NativeFilterOp(NativeOp op, int operand = 0, double immediate = 0.0)
        {
            Op = op;
            Operand = operand;
            Immediate = immediate;
        }
    }
}
