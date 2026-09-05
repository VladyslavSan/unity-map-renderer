using System.Collections.Concurrent;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// Thread-safe rent/return pool of one <see cref="ILayerMeshBuild"/>-implementing type — one closed
    /// generic per kind (<c>LayerMeshBuildPool&lt;FillLayerBuild&gt;</c>,
    /// <c>LayerMeshBuildPool&lt;FillExtrusionLayerBuild&gt;</c>, <c>LayerMeshBuildPool&lt;LineLayerBuild&gt;</c>)
    /// backed by independent static state, since a generic type's static fields are per-closed-type. Rent
    /// happens on the worker thread (a render layer's <c>BuildGraphRequest</c> inside
    /// <c>ProcessOnWorker</c>) or the main thread (<c>TileManager.KickSourcelessBackground</c>, and
    /// <c>ProcessOnWorker</c> itself under the WebGL <c>InlineWorkScheduler</c> policy); Return happens from
    /// <see cref="ILayerMeshBuild.Dispose"/>, last, on whichever thread disposes. <see cref="ConcurrentBag{T}"/>
    /// makes both ends safe on any thread, main included — see <c>MeshDataPayloadPool</c>'s own doc for the
    /// fuller thread-safety argument; it is not restated here.
    /// </summary>
    internal static class LayerMeshBuildPool<T> where T : class, ILayerMeshBuild, new()
    {
        private static readonly ConcurrentBag<T> Pool = new();

        /// <summary>Takes an existing idle instance, or mints a fresh one when the pool is empty.</summary>
        internal static T Rent() => Pool.TryTake(out T build) ? build : new T();

        /// <summary>Hands a rented instance back for reuse by the next <see cref="Rent"/> call — called ONLY
        /// from the instance's own <see cref="ILayerMeshBuild.Dispose"/>, last, once no path still holds the
        /// reference (that type's own doc enumerates the exit paths).</summary>
        internal static void Return(T build)
        {
            if (build != null) Pool.Add(build);
        }
    }
}
