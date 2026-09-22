using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's curved-arm bridge from <see cref="GlobeFillSubdivideJob{TProj}"/>'s output to the
    /// graph's one column set: scatters each subdivided <see cref="GlobeFillVertex"/> into
    /// <see cref="FillGraphOutput"/>'s output columns (<c>World → WorldPositions</c>, <c>Up → VertexUp</c>,
    /// <c>East → VertexEast</c>, <c>Tile → TileVertices</c>, <c>Band → VertexBand</c>,
    /// <c>Feature → VertexFeatureIdx</c>) and copies <see cref="Indices"/> into
    /// <see cref="OutTriangleIndices"/> verbatim. The subdivide job emits one struct per vertex, the shape
    /// its recursive template builds; this job turns that into columns.
    ///
    /// <para>The <c>.AsArray()</c> rule, as in <c>FillMeshGraph.cs</c>: hold the lists, resolve inside
    /// <see cref="Execute"/>, never at schedule time.</para>
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

        /// <summary>[0]'s two band scalars are ZEROED here. <see cref="FillBandJob"/> writes them as counts
        /// of a contiguous suffix, which is true of its own output and false of this job's: subdivision
        /// re-emits every vertex in traversal order, so band and interior interleave. Band-ness on the
        /// curved arm is the per-vertex <see cref="OutVertexBand"/> attribute, and a stale suffix count
        /// would be a scalar that lies rather than one that is absent.</summary>
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
