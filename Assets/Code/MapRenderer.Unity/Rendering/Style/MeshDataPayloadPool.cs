using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="MeshDataPayload"/> instances. Unlike
    /// <see cref="MapRenderer.Unity.Rendering.Tile.Processing.TileMeshLayerProcessorPool"/>, BOTH ends of
    /// this pool's lifecycle cross the thread boundary: a payload is minted inside
    /// <c>TileMeshLayerProcessor.Complete</c> on a ThreadPool WORKER thread (the settle loop inside
    /// <c>TileLayerProcessorRunner.RunWorkerPass</c>, itself only ever run inside
    /// <c>UniTask.RunOnThreadPool</c>) and returned to the pool from <see cref="MeshDataPayload.Dispose"/> on
    /// the MAIN THREAD (<c>TileManager.ConsumeMeshBuild</c>). <see cref="ConcurrentBag{T}"/> is what makes
    /// that handoff safe — <see cref="Rent"/> atomically removes an instance before handing it out, so no
    /// two renters, on any thread, can ever observe the same reference at once. Mirrors
    /// <c>TileBuildScratchPool</c>'s shape exactly; see its doc comment for the fuller rationale.
    /// </summary>
    internal static class MeshDataPayloadPool
    {
        private static readonly ConcurrentBag<MeshDataPayload> Pool = new();

        /// <summary>Takes an existing idle instance, or mints a fresh one when the pool is empty.</summary>
        internal static MeshDataPayload Rent() => Pool.TryTake(out var payload) ? payload : new MeshDataPayload();

        /// <summary>Hands a rented instance back for reuse by the next <see cref="Rent"/> call — from ANY
        /// thread, once <see cref="MeshDataPayload.Dispose"/> is the last thing ever touching it (see that
        /// method's doc for why the return happens ONLY there, never from <see cref="MeshDataPayload.Upload"/>).</summary>
        internal static void Return(MeshDataPayload payload)
        {
            if (payload != null) Pool.Add(payload);
        }
    }
}
