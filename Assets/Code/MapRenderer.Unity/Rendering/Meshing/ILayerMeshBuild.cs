using Unity.Jobs;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// One <c>(tile, layer)</c> build — the replacement for the tagged-union
    /// <c>TileBuildGraph.LayerRequest</c>/<c>LayerBuild</c> pair (job-scheduling-design.md §3.2, §3.7): a
    /// single object per geometry kind (<see cref="FillLayerBuild"/>, <see cref="FillExtrusionLayerBuild"/>,
    /// <see cref="LineLayerBuild"/>) owns its own request columns, measure output and write output, so
    /// <c>TileBuildGraph</c> dispatches nothing per kind — it only calls these four members through the
    /// interface. Pooled (<see cref="LayerMeshBuildPool{T}"/>), never allocated per build.
    /// </summary>
    internal interface ILayerMeshBuild
    {
        /// <summary>Schedules this layer's measure graph. Returns the graph's own terminal handle,
        /// UNCOMPLETED — the caller combines it with every other layer's before completing any of them.</summary>
        /// <param name="deps">Upstream dependency this layer's graph must wait for.</param>
        JobHandle ScheduleMeasure(JobHandle deps);

        /// <summary>Reads this layer's completed measure output and, iff it is error-free and non-empty,
        /// schedules the write step. The caller must have already completed <see cref="ScheduleMeasure"/>'s
        /// returned handle. Returns <c>false</c> for an empty or faulted layer — settled as zero-vertex, no
        /// mesh registered — with <paramref name="writeHandle"/> left <c>default</c>.</summary>
        /// <param name="writeHandle">The write graph's own terminal handle, UNCOMPLETED, when this returns
        /// <c>true</c>.</param>
        bool TryScheduleWrite(out JobHandle writeHandle);

        /// <summary>Takes this layer's finished payload — the caller must have already completed the write
        /// handle <see cref="TryScheduleWrite"/> returned. Returns <c>null</c> when no write step ran.</summary>
        MeshDataPayload TakePayload();

        /// <summary>Completes whichever step is in flight, frees every owned container, and returns this
        /// instance to its pool — the last thing it does. Idempotent: a second call is a no-op. Safe to call
        /// on a build that was never scheduled.</summary>
        void Dispose();
    }
}
