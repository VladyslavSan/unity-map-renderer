using Unity.Collections;

namespace MapRenderer.Jobs.Expressions
{
    /// <summary>
    /// A compiled, tile-independent native filter — <see cref="NativeFilterCompiler.TryCompile"/>'s
    /// output. Immutable and reusable; a tile-layer-specific <c>Rebind</c> extension (see
    /// <c>MapRenderer.Jobs.Mvt.NativeFilterRebind</c> — kept out of this format-neutral folder because it
    /// names MVT-specific types) resolves it against one tile-layer's key/value tables.
    ///
    /// <para>The two have different lifetimes, which is why only one of them is cached: this program is
    /// compiled once per filter node and memoized for the life of the style document (the
    /// <c>NativeProgramFor</c> memo on <c>MapRenderer.Jobs.Tiles.FeatureSelector</c>), whereas a rebind is
    /// constructed fresh every time — the key/value tables it resolves against are a property of the tile
    /// layer, so a rebind is not reusable across tiles.</para>
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
