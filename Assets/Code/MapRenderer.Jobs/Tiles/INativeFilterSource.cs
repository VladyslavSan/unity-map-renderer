using System;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The native-filter capability seam: implemented by a tile layer that can evaluate a compiled
    /// <see cref="NativeFilterProgram"/> against its own features without going through
    /// <see cref="MapRenderer.Core.Filters.CompiledFilter"/>. Mirrors <see cref="IIndexedFeatureSource"/> —
    /// a capability probed with <c>as</c>, handing back a per-selection product — so
    /// <c>FeatureSelector</c> stays format-agnostic: a future GeoJSON native path opts in by implementing
    /// this interface too, never by an <c>is MvtLayer</c> allow-list.
    /// </summary>
    internal interface INativeFilterSource
    {
        /// <summary>
        /// A disposable per-selection matcher bound to (this layer + <paramref name="program"/>), or
        /// <c>null</c> to signal "fall back to the managed path" — e.g. the program's literal strings
        /// collide in this layer's value table (the <c>Rebind</c> ≥2-id refusal). The caller owns disposal.
        ///
        /// <para><b>No <c>zoom</c> parameter.</b> <see cref="NativeFilterProgram"/>'s accepted subset has
        /// no zoom form and requires a statically-Boolean root, so any zoom-bearing filter is refused at
        /// compile time and never reaches a matcher — a <c>zoom</c> parameter here would invite the false
        /// assumption that it is honoured.</para>
        /// </summary>
        /// <param name="program">The compiled, tile-independent program to bind against this layer.</param>
        INativeFeatureMatcher TryBindNativeFilter(NativeFilterProgram program);
    }

    /// <summary>
    /// A per-selection native predicate over one layer's features, addressed by ordinal — the product
    /// <see cref="INativeFilterSource.TryBindNativeFilter"/> hands back. Owns the rebound binding and any
    /// evaluator scratch it needs; the caller disposes it in the same <c>finally</c> as its selection loop.
    /// </summary>
    internal interface INativeFeatureMatcher : IDisposable
    {
        /// <summary>True iff the layer's feature at <paramref name="ordinal"/> matches — an evaluation
        /// error excludes, mirroring <see cref="MapRenderer.Core.Filters.CompiledFilter"/>'s
        /// caught-exception → exclude.</summary>
        bool Matches(int ordinal);
    }
}
