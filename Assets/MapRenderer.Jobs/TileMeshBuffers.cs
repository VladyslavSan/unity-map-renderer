using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Owns the native buffer lifetime for one tile's tessellation pipeline output.
    /// Holds tile-space vertices (double2), projected world vertices (double3) + per-vertex up, and
    /// triangle indices.
    /// Must be disposed on the main thread after the job chain completes and the mesh is built.
    ///
    /// Lifetime rule (NativeArray × cancellation safety):
    ///   Never call <see cref="Dispose"/> while a job referencing these buffers is still in-flight.
    ///   Always <c>JobHandle.Complete()</c> before disposing, even on cancellation.
    ///   The <see cref="TileTessellationPipeline"/> coordinator enforces this.
    ///
    /// Allocator: <see cref="Allocator.Persistent"/> — tiles live for multiple frames;
    /// TempJob has a ~4-frame safety guard and will throw an error if the job takes longer.
    /// </summary>
    public struct TileMeshBuffers : IDisposable
    {
        // ── Decode stage output ────────────────────────────────────────────────────────────────
        /// <summary>Flat vertex array in tile-space double2 coords. Length = total decoded verts.</summary>
        public NativeArray<double2> TileVertices;

        /// <summary>Per-ring start offsets into <see cref="TileVertices"/>. Length = ringCount+1 (sentinel).</summary>
        public NativeArray<int> RingOffsets;

        /// <summary>Which feature each ring belongs to. Length = ringCount.</summary>
        public NativeArray<int> RingFeatureIdx;

        // ── Assembly stage output ──────────────────────────────────────────────────────────────
        /// <summary>Outer ring index for each polygon. Length = max polygons (pre-sized).</summary>
        public NativeArray<int> PolyOuterRingIdx;
        /// <summary>Hole list start for each polygon in <see cref="HoleRingIdxs"/>.</summary>
        public NativeArray<int> PolyHoleListStart;
        /// <summary>Number of holes per polygon.</summary>
        public NativeArray<int> PolyHoleCount;
        /// <summary>Flat list of hole ring indices. Length = max holes (pre-sized).</summary>
        public NativeArray<int> HoleRingIdxs;

        // ── Earcut stage output ────────────────────────────────────────────────────────────────
        /// <summary>
        /// Flat triangle index array (indices into <see cref="TileVertices"/>).
        /// Note: earcut produces indices into the merged ring (outer+bridge+holes). The pipeline
        /// maps these back to world positions via ProjectPointsJob.
        /// </summary>
        public NativeArray<int> TriangleIndices;

        // ── Projection stage output ────────────────────────────────────────────────────────────
        /// <summary>Projected origin-relative <c>double3</c> world positions (one per tile vertex).
        /// Kept in double through projection; the fill builder casts to float3 only at mesh-write
        /// (docs §8.2). Length = total tile verts.</summary>
        public NativeArray<double3> WorldPositions;

        /// <summary>S91-A: per-vertex surface up (unit; from the projection's <c>UpAt</c>). Constant
        /// +Y for Mercator, the geodetic normal for the globe. Baked into the mesh Normal stream so the
        /// shaders make no flat-ground assumption. Length = total tile verts.</summary>
        public NativeArray<double3> VertexUp;

        /// <summary>S89 D2: feature index of each merged vertex (into the input FeatureGeometries list).
        /// Lets the fill stream-write assign per-feature color without re-deriving vertex→feature.</summary>
        public NativeArray<int> VertexFeatureIdx;

        // ── Counts written by jobs ─────────────────────────────────────────────────────────────
        public NativeArray<int> RingCount;          // [0]
        public NativeArray<int> VertexCount;        // [0]
        public NativeArray<int> PolygonCount;       // [0]
        public NativeArray<int> HoleCount;          // [0]

        // ── Final mesh counts (sum over all polygon earcut results) ───────────────────────────
        /// <summary>Total triangle indices written into <see cref="TriangleIndices"/>.</summary>
        public int TotalIndexCount;

        /// <summary>Total force-clip count across all polygons (diagnostic).</summary>
        public int TotalForceClipCount;

        /// <summary>Job handle for the full pipeline chain. Complete() before reading output.</summary>
        public JobHandle PipelineHandle;

        /// <summary>True if all native arrays have been allocated.</summary>
        public bool IsCreated;

        public void Dispose()
        {
            if (!IsCreated) return;
            IsCreated = false;
            if (TileVertices.IsCreated)     TileVertices.Dispose();
            if (RingOffsets.IsCreated)      RingOffsets.Dispose();
            if (RingFeatureIdx.IsCreated)   RingFeatureIdx.Dispose();
            if (PolyOuterRingIdx.IsCreated) PolyOuterRingIdx.Dispose();
            if (PolyHoleListStart.IsCreated) PolyHoleListStart.Dispose();
            if (PolyHoleCount.IsCreated)    PolyHoleCount.Dispose();
            if (HoleRingIdxs.IsCreated)     HoleRingIdxs.Dispose();
            if (TriangleIndices.IsCreated)  TriangleIndices.Dispose();
            if (WorldPositions.IsCreated)   WorldPositions.Dispose();
            if (VertexUp.IsCreated)         VertexUp.Dispose();
            if (VertexFeatureIdx.IsCreated) VertexFeatureIdx.Dispose();
            if (RingCount.IsCreated)        RingCount.Dispose();
            if (VertexCount.IsCreated)      VertexCount.Dispose();
            if (PolygonCount.IsCreated)     PolygonCount.Dispose();
            if (HoleCount.IsCreated)        HoleCount.Dispose();
        }
    }
}
