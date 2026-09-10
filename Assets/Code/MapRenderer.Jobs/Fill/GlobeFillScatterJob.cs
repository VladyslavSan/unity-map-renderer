using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's curved-arm bridge from <see cref="GlobeFillSubdivideJob{TProj}"/>'s output to the
    /// graph's one column set (job-scheduling-design.md §3.7): scatters each subdivided
    /// <see cref="GlobeFillVertex"/> into <see cref="FillGraphOutput"/>'s output columns
    /// (<c>World → WorldPositions</c>, <c>Up → VertexUp</c>, <c>East → VertexEast</c>,
    /// <c>Tile → TileVertices</c>, <c>Band → VertexBand</c>, <c>Feature → VertexFeatureIdx</c>) and copies
    /// <see cref="Indices"/> into
    /// <see cref="OutTriangleIndices"/> verbatim.
    ///
    /// <para><b>Survives job-scheduling-design.md §8 stage 4 Group B, which retires its two former
    /// synchronous callers (<c>StyledFillTileBuilder.WriteGlobeSubdivided</c>,
    /// <c>StyledFillExtrusionTileBuilder</c>'s <c>WriteGlobeRoof</c>).</b> This job is the curved arm's own
    /// bridge to the graph's one column set, not a wrapper around either retired caller: the subdivide job
    /// (<see cref="GlobeFillSubdivideJob{TProj}"/>) emits <see cref="GlobeFillVertex"/> records — one struct
    /// per vertex, the shape its recursive template naturally builds — and this job is what turns that into
    /// the separate columns <see cref="FillGraphOutput"/> carries. The scatter is real work regardless of
    /// how many callers subdivide feeds; it stays widened to the graph's whole column set.</para>
    ///
    /// <para>The <c>.AsArray()</c> rule (this file's convention, matching <c>FillMeshGraph.cs</c>): hold the
    /// lists, resolve inside <see cref="Execute"/> — never at schedule time.</para>
    ///
    /// <para>Does not report a vertex/index total anywhere: the output columns are what a reader takes
    /// <c>.Length</c> off once completed, so there is no separate scalar that could drift from them. It
    /// holds <see cref="Counts"/> only to CLEAR the two band scalars, never to write a total.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct GlobeFillScatterJob : IJob
    {
        [ReadOnly] public NativeList<GlobeFillVertex> Vertices;
        [ReadOnly] public NativeList<int> Indices;

        public NativeList<double3> OutWorldPositions;
        public NativeList<double3> OutVertexUp;
        public NativeList<double3> OutVertexEast;

        /// <summary>The boundary band, carried through subdivision on <see cref="GlobeFillVertex.Band"/> and
        /// scattered here like any other column. A curved band vertex is NOT a suffix of this column — the
        /// subdivider re-emits every vertex, so band and interior interleave; see <see cref="Counts"/>.</summary>
        public NativeList<float3> OutVertexBand;

        public NativeList<double2> OutTileVertices;
        public NativeList<int> OutVertexFeatureIdx;
        public NativeList<int> OutTriangleIndices;

        /// <summary>[0]'s two band scalars are ZEROED here. <see cref="FillBandJob"/> writes them as counts of
        /// a contiguous suffix, which is true of its own output and false of this job's: subdivision re-emits
        /// every vertex in traversal order, so band and interior interleave and no prefix/suffix split exists
        /// on the curved arm. Band-ness there is the per-vertex <see cref="OutVertexBand"/> attribute, and a
        /// stale suffix count would be a scalar that lies rather than one that is merely absent.</summary>
        public NativeArray<FillGraphCounts> Counts;

        public void Execute()
        {
            FillGraphCounts counts = Counts[0];
            counts.BandVertexCount = 0;
            counts.BandIndexCount  = 0;
            Counts[0] = counts;

            NativeArray<GlobeFillVertex> vertices = Vertices.AsArray();
            int n = vertices.Length;

            OutWorldPositions.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexUp.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexEast.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexBand.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutTileVertices.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexFeatureIdx.Resize(n, NativeArrayOptions.UninitializedMemory);

            NativeArray<double3> worldPositions = OutWorldPositions.AsArray();
            NativeArray<double3> vertexUp = OutVertexUp.AsArray();
            NativeArray<double3> vertexEast = OutVertexEast.AsArray();
            NativeArray<float3> vertexBand = OutVertexBand.AsArray();
            NativeArray<double2> tileVertices = OutTileVertices.AsArray();
            NativeArray<int> vertexFeatureIdx = OutVertexFeatureIdx.AsArray();

            for (int i = 0; i < n; i++)
            {
                GlobeFillVertex v = vertices[i];
                worldPositions[i] = v.World;
                vertexUp[i] = v.Up;
                vertexEast[i] = v.East;
                vertexBand[i] = v.Band;
                tileVertices[i] = v.Tile;
                vertexFeatureIdx[i] = v.Feature;
            }

            NativeArray<int> indices = Indices.AsArray();
            OutTriangleIndices.Resize(indices.Length, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> outTriangleIndices = OutTriangleIndices.AsArray();
            for (int i = 0; i < indices.Length; i++)
                outTriangleIndices[i] = indices[i];
        }
    }
}
