using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Projection
{
    /// <summary>
    /// Sizes the three projection columns — geodetic, world, up — to a gather node's EXECUTE-time vertex
    /// count, so the deferred <see cref="TileToGeoJob"/>/<c>ProjectPointsJob</c> nodes downstream have
    /// correctly sized write targets. Writes no values; only lengths.
    ///
    /// <para><b>Why a node rather than a main-thread resize.</b> A gather that CLIPS
    /// (<c>MapRenderer.Jobs.Geometry.RingClipJob</c>) has no schedule-time output length: Sutherland–Hodgman
    /// may add vertices to a ring and may drop a ring outright, so the caller's borrowed-input pre-pass sum
    /// is a capacity hint only. <c>MapRenderer.Jobs.Lines.RingGatherJob</c> ends by resizing exactly these
    /// three columns for exactly this reason; this node is that tail split out, because the fill/extrusion
    /// gather jobs (<c>RingSelectJob</c>/<c>RingClipJob</c>) are shared with call sites that must not grow
    /// three dead list fields.</para>
    ///
    /// <para><b>A resize inside a job is visible to a deferred view taken at schedule time</b> — including
    /// one that reallocates. Standing in-repo proof: <c>FillMeshGraph</c> allocates its <c>geo</c> column at
    /// capacity 1, <c>AggregateJob</c> resizes it at execute time, and the projection nodes read it through
    /// <c>AsDeferredJobArray()</c>.</para>
    ///
    /// <para><c>public</c>, not <c>internal</c> like this assembly's other non-public jobs: its consumer
    /// (<c>MapRenderer.Unity.Rendering.Meshing.FillExtrusionMeshGraph</c>) lives in another assembly, which
    /// this one grants internals to only for tests. Same reason <c>RingSelectJob</c>/<c>RingClipJob</c> are
    /// public.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct ProjectionColumnSizingJob : IJob
    {
        /// <summary>The gathered flat ring vertices whose final length the three columns must match.</summary>
        [ReadOnly] public NativeList<double2> SourceTileCoords;

        /// <summary>Geodetic column to size (never written here).</summary>
        public NativeList<GeoCoordinate> OutGeo;

        /// <summary>Origin-relative world column to size (never written here).</summary>
        public NativeList<double3> OutWorld;

        /// <summary>Per-vertex surface-normal column to size (never written here).</summary>
        public NativeList<double3> OutUp;

        /// <summary>Resizes the three columns to <see cref="SourceTileCoords"/>'s length.</summary>
        public void Execute()
        {
            int n = SourceTileCoords.Length;
            OutGeo.ResizeUninitialized(n);
            OutWorld.ResizeUninitialized(n);
            OutUp.ResizeUninitialized(n);
        }
    }
}
