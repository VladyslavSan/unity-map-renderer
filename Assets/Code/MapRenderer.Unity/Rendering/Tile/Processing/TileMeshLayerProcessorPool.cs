using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="TileMeshLayerProcessor"/> instances — one per DENSE LAYER
    /// of a kick, not one per build (unlike <see cref="TileBuildScratchPool"/>'s one-per-in-flight-build
    /// cardinality). <see cref="TileMeshLayerProcessor.AllocateForKick"/> rents on the MAIN THREAD (the
    /// kick's <c>PmMeshDataAllocate</c> prologue); <see cref="TileMeshLayerProcessor.Complete"/> returns on a
    /// ThreadPool WORKER thread (the settle loop inside <see cref="TileLayerProcessorRunner.RunWorkerPass"/>,
    /// which itself only ever runs inside <c>UniTask.RunOnThreadPool</c>) — a genuine cross-thread handoff,
    /// which is exactly what <see cref="TileBuildScratchPool"/>'s own doc comment explains
    /// <see cref="ConcurrentBag{T}"/> makes safe: <see cref="Rent"/> atomically removes an instance before
    /// handing it out, so no two renters — however many dense layers across however many concurrent builds —
    /// can ever observe the same reference at once.
    /// </summary>
    internal static class TileMeshLayerProcessorPool
    {
        private static readonly ConcurrentBag<TileMeshLayerProcessor> Pool = new();

        /// <summary>Takes an existing idle instance, or mints a fresh one when the pool is empty (e.g. the
        /// first few dense layers across the first few concurrent builds, before steady state establishes
        /// how many are in flight at once).</summary>
        internal static TileMeshLayerProcessor Rent() => Pool.TryTake(out var processor) ? processor : new TileMeshLayerProcessor();

        /// <summary>Hands a rented instance back for reuse by the next <see cref="Rent"/> call — from ANY
        /// thread, once <see cref="TileMeshLayerProcessor.Complete"/> is done with it.</summary>
        internal static void Return(TileMeshLayerProcessor processor)
        {
            if (processor != null) Pool.Add(processor);
        }
    }
}
