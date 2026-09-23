using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="TileMeshLayerProcessor"/> instances — one per DENSE LAYER
    /// of a kick, not one per build (unlike <see cref="TileBuildBuffersPool"/>'s one-per-in-flight-build
    /// cardinality). <see cref="TileMeshLayerProcessor.AllocateForKick"/> rents on the MAIN THREAD;
    /// <see cref="TileMeshLayerProcessor.Release"/> returns from the settle loop — a ThreadPool worker
    /// under <c>ThreadPoolWorkScheduler</c> (a cross-thread handoff), or the MAIN THREAD under
    /// <c>InlineWorkScheduler</c> (WebGL, same-thread). Non-local invariant: same as
    /// <see cref="TileBuildBuffersPool"/> — <see cref="Rent"/> atomically removes an instance before
    /// handing it out, so no two renters can ever observe the same reference.
    /// </summary>
    internal static class TileMeshLayerProcessorPool
    {
        private static readonly ConcurrentBag<TileMeshLayerProcessor> Pool = new();

        /// <summary>Takes an existing idle instance, or mints a fresh one when the pool is empty (e.g. the
        /// first few dense layers across the first few concurrent builds, before steady state establishes
        /// how many are in flight at once).</summary>
        internal static TileMeshLayerProcessor Rent() => Pool.TryTake(out var processor) ? processor : new TileMeshLayerProcessor();

        /// <summary>Hands a rented instance back for reuse by the next <see cref="Rent"/> call — from ANY
        /// thread, once <see cref="TileMeshLayerProcessor.Release"/> is done with it.</summary>
        internal static void Return(TileMeshLayerProcessor processor)
        {
            if (processor != null) Pool.Add(processor);
        }
    }
}
