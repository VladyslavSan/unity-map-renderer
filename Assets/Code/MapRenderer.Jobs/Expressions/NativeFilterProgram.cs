using Unity.Collections;

namespace MapRenderer.Jobs.Expressions
{
    /// <summary>
    /// A compiled, tile-independent native filter — <see cref="NativeFilterCompiler.TryCompile"/>'s
    /// output. Immutable and reusable; a tile-layer-specific <c>Rebind</c> extension (see
    /// <c>MapRenderer.Jobs.Mvt.NativeFilterRebind</c> — kept out of this format-neutral folder because it
    /// names MVT-specific types) resolves it against one tile-layer's key/value tables, constructed fresh
    /// per rebind (this stage does not memoise a native program — the design doc's deferred-scope fence).
    /// </summary>
    internal sealed class NativeFilterProgram
    {
        /// <summary>The compiled opcode program: post-order, with <c>all</c> short-circuit jumps.</summary>
        internal readonly FixedList512Bytes<NativeFilterOp> Ops;

        /// <summary>Constant-key <c>get</c> names, in emission order — a <see cref="NativeOp.Get"/>'s
        /// <c>Operand</c> is an index here.</summary>
        internal readonly string[] KeyNames;

        /// <summary>String-literal <c>==</c>/<c>!=</c> operands, in emission order — a
        /// <see cref="NativeOp.LitStr"/>'s <c>Operand</c> is <c>KeyNames.Length</c> plus an index
        /// here.</summary>
        internal readonly string[] LiteralStrings;

        internal NativeFilterProgram(
            FixedList512Bytes<NativeFilterOp> ops, string[] keyNames, string[] literalStrings)
        {
            Ops = ops;
            KeyNames = keyNames;
            LiteralStrings = literalStrings;
        }
    }
}
