using System;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The native-filter capability seam: implemented by a tile layer that can evaluate a compiled
    /// <see cref="NativeFilterProgram"/> against its own features without going through
    /// <see cref="MapRenderer.Core.Filters.CompiledFilter"/>. Like <see cref="IIndexedFeatureSource"/>, it
    /// is a capability probed with <c>as</c>, so <c>FeatureSelector</c> stays format-agnostic: a layer type
    /// opts in by implementing it, never by an <c>is MvtLayer</c> allow-list.
    /// </summary>
    internal interface INativeFilterSource
    {
        /// <summary>
        /// A disposable per-selection matcher bound to (this layer + <paramref name="program"/>), or
        /// <c>null</c> to signal "fall back to the managed path" — e.g. the program's literal strings
        /// collide in this layer's value table (the <c>Rebind</c> ≥2-id refusal). The caller owns disposal.
        /// There is no <c>zoom</c> parameter, because the compiler refuses any zoom-bearing filter.
        /// </summary>
        /// <param name="program">The compiled, tile-independent program to bind against this layer.</param>
        INativeFeatureMatcher TryBindNativeFilter(NativeFilterProgram program);
    }

    /// <summary>
    /// A per-selection native predicate over one layer's features — the product
    /// <see cref="INativeFilterSource.TryBindNativeFilter"/> hands back. Owns the rebound binding and any
    /// evaluator scratch it needs; the caller disposes it in the same <c>finally</c> as its selection loop.
    /// </summary>
    internal interface INativeFeatureMatcher : IDisposable
    {
        /// <summary>Evaluates every feature of this matcher's own layer, ordinal <c>[0, featureCount)</c>,
        /// against the bound program in ONE Burst job; <c>[i] != 0</c> means feature i matched, and an
        /// evaluation error excludes, as in <see cref="MapRenderer.Core.Filters.CompiledFilter"/>. The
        /// matcher owns the bound and the array, valid until <see cref="IDisposable.Dispose"/>.</summary>
        NativeArray<byte> MatchAll();
    }
}
