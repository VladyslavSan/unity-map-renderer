using Unity.Collections;

namespace MapRenderer.Jobs.Expressions
{
    /// <summary>
    /// A compiled, tile-independent native filter, <see cref="NativeFilterCompiler.TryCompile"/>'s output. It
    /// is immutable and memoized per filter node for the style's life
    /// (<c>FeatureSelector.NativeProgramFor</c>).
    /// The MVT-specific <c>Mvt.NativeFilterRebind</c> resolves it against one tile layer's key/value tables,
    /// fresh per tile, because those tables belong to the tile layer.
    /// </summary>
    internal sealed class NativeFilterProgram
    {
        /// <summary>The compiled opcode program: post-order, with <c>all</c> short-circuit jumps.</summary>
        internal readonly FixedList512Bytes<NativeFilterOperation> Operations;

        /// <summary>Constant-key <c>get</c> names, in emission order — a <c>Get</c>'s <c>Operand</c> is an
        /// index here.</summary>
        internal readonly string[] KeyNames;

        /// <summary>String-literal <c>==</c>/<c>!=</c> operands, in emission order — a <c>LiteralString</c>'s
        /// <c>Operand</c> is <c>KeyNames.Length</c> plus an index here.</summary>
        internal readonly string[] LiteralStrings;

        internal NativeFilterProgram(
            FixedList512Bytes<NativeFilterOperation> operations, string[] keyNames, string[] literalStrings)
        {
            Operations = operations;
            KeyNames = keyNames;
            LiteralStrings = literalStrings;
        }
    }
}
