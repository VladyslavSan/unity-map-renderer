using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Thread-safe rent/return pool of <see cref="MeshDataPayload"/> instances. A payload is minted inside
    /// <c>TileMeshLayerProcessor.Complete</c> on whichever thread runs the settle loop — a ThreadPool
    /// worker, or the MAIN THREAD under <c>InlineWorkScheduler</c> (WebGL) — and returned from
    /// <see cref="MeshDataPayload.Dispose"/> on the MAIN THREAD. Non-local invariant:
    /// <see cref="ConcurrentBag{T}"/> makes both ends safe on any thread: <see cref="Rent"/> atomically
    /// removes an instance before handing it out, so no two renters can ever observe the same reference.
    /// Mirrors <c>TileBuildBuffersPool</c>'s shape; see its doc comment for the fuller rationale.
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
