using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="TileBuildBuffers"/> instances, one per IN-FLIGHT mesh build.
    /// A source tile's mesh build dispatches through <c>TileManager.KickMeshBuild</c> →
    /// <c>IWorkScheduler.Schedule</c> → <see cref="TileLayerProcessorRunner.RunWorkerPass"/> —
    /// CONCURRENTLY on ThreadPool worker threads under <c>ThreadPoolWorkScheduler</c> (desktop/editor), or
    /// one at a time on the MAIN THREAD
    /// under <c>InlineWorkScheduler</c> (WebGL, no concurrency at all — see that policy's doc). Either way,
    /// two builds must never observe the same <see cref="TileBuildBuffers"/> at once — a data race, since
    /// both would write through the same arrays.
    ///
    /// <para><see cref="ConcurrentBag{T}"/> makes that true structurally rather than by convention: <see cref="Rent"/>
    /// atomically REMOVES an instance from the bag before handing it to the caller, so no other thread can see it
    /// until <see cref="Return"/> puts it back — there is no window where two threads hold the same reference. A
    /// build rents exactly once at the start of its worker pass, uses that instance for every layer/feature it
    /// processes (sequential within one build), and returns it from a <c>finally</c> so a faulted or cancelled
    /// build still gives its buffers back rather than starving the pool.</para>
    /// </summary>
    internal static class TileBuildBuffersPool
    {
        private static readonly ConcurrentBag<TileBuildBuffers> Pool = new();

        /// <summary>Takes an existing idle instance, or mints a fresh one when the pool is empty (e.g. the
        /// first few concurrent builds, before steady state establishes how many are in flight at once).</summary>
        internal static TileBuildBuffers Rent() => Pool.TryTake(out var buffers) ? buffers : new TileBuildBuffers();

        /// <summary>Hands a rented instance back for reuse by the next <see cref="Rent"/> call — from ANY
        /// thread, once the renting build's worker pass has fully finished with it.</summary>
        internal static void Return(TileBuildBuffers buffers)
        {
            if (buffers != null) Pool.Add(buffers);
        }
    }
}
