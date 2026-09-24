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
    /// Non-obvious why: a clipping gather (<c>RingClipJob</c>) has no schedule-time output length, and the
    /// shared gather jobs must not carry three dead list fields, so the resize is its own node. A resize
    /// inside a job stays visible to a deferred view taken at schedule time, even when it reallocates.
    /// It is <c>public</c> because its consumer (<c>FillExtrusionMeshGraph</c>) lives in another assembly.
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
