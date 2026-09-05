using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="MeshDataPayload"/> instances. A payload is minted inside
    /// <c>TileMeshLayerProcessor.Complete</c> on whichever thread runs the settle loop inside
    /// <c>TileLayerProcessorRunner.RunWorkerPass</c> — a ThreadPool worker under
    /// <c>ThreadPoolWorkScheduler</c>, or the MAIN THREAD itself under <c>InlineWorkScheduler</c> (the WebGL
    /// policy) — and returned to the pool from <see cref="MeshDataPayload.Dispose"/> on the MAIN THREAD
    /// (<c>TileManager.ConsumeMeshBuild</c>). So the mint-to-return handoff crosses a thread boundary under
    /// ThreadPool but not under Inline (both ends land on main there) — <see cref="ConcurrentBag{T}"/> makes
    /// it safe either way, not just the crossing case: <see cref="Rent"/> atomically removes an instance
    /// before handing it out, so no two renters, on any thread (main included), can ever observe the same
    /// reference at once. Mirrors <c>TileBuildBuffersPool</c>'s shape exactly; see its doc comment for the
    /// fuller rationale.
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
