using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="TileBuildScratch"/> instances, one per IN-FLIGHT mesh build.
    /// Mesh builds run CONCURRENTLY on ThreadPool threads (<c>TileManager.KickMeshBuild</c>/
    /// <c>KickSourcelessBackground</c> → <c>UniTask.RunOnThreadPool</c> → <see cref="TileLayerProcessorRunner.RunWorkerPass"/>/
    /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>), so two builds must never observe the same
    /// <see cref="TileBuildScratch"/> at once — a data race, since both would write through the same arrays.
    ///
    /// <para><see cref="ConcurrentBag{T}"/> makes that true structurally rather than by convention: <see cref="Rent"/>
    /// atomically REMOVES an instance from the bag before handing it to the caller, so no other thread can see it
    /// until <see cref="Return"/> puts it back — there is no window where two threads hold the same reference. A
    /// build rents exactly once at the start of its worker pass, uses that instance for every layer/feature it
    /// processes (sequential within one build), and returns it from a <c>finally</c> so a faulted or cancelled
    /// build still gives its scratch back rather than starving the pool.</para>
    /// </summary>
    internal static class TileBuildScratchPool
    {
        private static readonly ConcurrentBag<TileBuildScratch> Pool = new();

        /// <summary>Takes an existing idle instance, or mints a fresh one when the pool is empty (e.g. the
        /// first few concurrent builds, before steady state establishes how many are in flight at once).</summary>
        internal static TileBuildScratch Rent() => Pool.TryTake(out var scratch) ? scratch : new TileBuildScratch();

        /// <summary>Hands a rented instance back for reuse by the next <see cref="Rent"/> call — from ANY
        /// thread, once the renting build's worker pass has fully finished with it.</summary>
        internal static void Return(TileBuildScratch scratch)
        {
            if (scratch != null) Pool.Add(scratch);
        }
    }
}
