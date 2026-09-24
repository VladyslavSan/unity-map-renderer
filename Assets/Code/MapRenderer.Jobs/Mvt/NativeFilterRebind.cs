using System;
using Unity.Collections;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// <see cref="NativeFilterProgram"/>'s per-tile-layer binding step — an extension method rather than an
    /// instance method on <see cref="NativeFilterProgram"/> itself, so the format-neutral
    /// <c>MapRenderer.Jobs.Expressions</c> folder never names an MVT-specific type in a member signature
    /// (see <see cref="NativeFilterEvaluationJob"/>'s doc for why that matters — <c>NeutralGeometryPathTests</c>).
    /// </summary>
    internal static class NativeFilterRebind
    {
        /// <summary>
        /// Resolves each key name to its <paramref name="resolver"/> key index (-1 if absent) and each literal
        /// string to its <see cref="MvtLayerPropertyResolver.ValueStrings"/> id by a full ordinal scan, because
        /// string dedup is an encoder property, not a decoder guarantee. A literal with no match resolves to -1,
        /// which never equals a real id. A literal with two or more matches makes a single-id compare unsound,
        /// so the rebind is refused and the layer stays on the managed path.
        /// </summary>
        /// <param name="program">The compiled, tile-independent program to bind.</param>
        /// <param name="resolver">The tile-layer's shared key/value tables.</param>
        /// <param name="allocator">The <paramref name="binding"/> allocator: <see cref="Allocator.Persistent"/>
        /// off the main thread, because TempJob's 4-frame cap counts main-thread frames.</param>
        /// <param name="binding">The resolved <c>slot→id</c> array, caller-owned, on success; <c>default</c>
        /// on refusal.</param>
        /// <returns>False iff a literal string resolves to two or more distinct ids in this layer.</returns>
        internal static bool Rebind(
            this NativeFilterProgram program, MvtLayerPropertyResolver resolver, Allocator allocator,
            out NativeArray<int> binding)
        {
            int keyCount = program.KeyNames.Length;
            int total = keyCount + program.LiteralStrings.Length;
            var result = new NativeArray<int>(total, allocator);

            for (int i = 0; i < keyCount; i++)
                result[i] = resolver.TryGetKeyIndex(program.KeyNames[i], out int keyIndex) ? keyIndex : -1;

            string[] valueStrings = resolver.ValueStrings;
            for (int i = 0; i < program.LiteralStrings.Length; i++)
            {
                string literal = program.LiteralStrings[i];
                int match = -1;
                int matchCount = 0;
                for (int v = 0; v < valueStrings.Length; v++)
                {
                    if (!string.Equals(valueStrings[v], literal, StringComparison.Ordinal)) continue;
                    match = v;
                    matchCount++;
                }
                if (matchCount >= 2)
                {
                    result.Dispose();
                    binding = default;
                    return false;
                }
                result[keyCount + i] = matchCount == 0 ? -1 : match;
            }

            binding = result;
            return true;
        }
    }
}
