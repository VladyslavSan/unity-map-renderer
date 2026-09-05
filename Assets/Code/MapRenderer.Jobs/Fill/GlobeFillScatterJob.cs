using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's curved-arm bridge from <see cref="GlobeFillSubdivideJob{TProj}"/>'s output to the
    /// graph's one column set (job-scheduling-design.md §3.7): scatters each subdivided
    /// <see cref="GlobeFillVertex"/> into <see cref="FillGraphOutput"/>'s six output columns
    /// (<c>World → WorldPositions</c>, <c>Up → VertexUp</c>, <c>East → VertexEast</c>,
    /// <c>Tile → TileVertices</c>, <c>Feature → VertexFeatureIdx</c>) and copies <see cref="Indices"/> into
    /// <see cref="OutTriangleIndices"/> verbatim.
    ///
    /// <para><b>Survives job-scheduling-design.md §8 stage 4 Group B, which retires its two former
    /// synchronous callers (<c>StyledFillTileBuilder.WriteGlobeSubdivided</c>,
    /// <c>StyledFillExtrusionTileBuilder</c>'s <c>WriteGlobeRoof</c>).</b> This job is the curved arm's own
    /// bridge to the graph's one column set, not a wrapper around either retired caller: the subdivide job
    /// (<see cref="GlobeFillSubdivideJob{TProj}"/>) emits <see cref="GlobeFillVertex"/> records — one struct
    /// per vertex, the shape its recursive template naturally builds — and this job is what turns that into
    /// the six separate columns <see cref="FillGraphOutput"/> carries. The scatter is real work regardless of
    /// how many callers subdivide feeds; it stays widened to six columns.</para>
    ///
    /// <para>The <c>.AsArray()</c> rule (this file's convention, matching <c>FillMeshGraph.cs</c>): hold the
    /// lists, resolve inside <see cref="Execute"/> — never at schedule time.</para>
    ///
    /// <para>Does not report a vertex/index count anywhere: the six output columns are what a reader takes
    /// <c>.Length</c> off once completed, so there is no separate scalar that could drift from them.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct GlobeFillScatterJob : IJob
    {
        [ReadOnly] public NativeList<GlobeFillVertex> Vertices;
        [ReadOnly] public NativeList<int> Indices;

        public NativeList<double3> OutWorldPositions;
        public NativeList<double3> OutVertexUp;
        public NativeList<double3> OutVertexEast;
        public NativeList<double2> OutTileVertices;
        public NativeList<int> OutVertexFeatureIdx;
        public NativeList<int> OutTriangleIndices;

        public void Execute()
        {
            NativeArray<GlobeFillVertex> vertices = Vertices.AsArray();
            int n = vertices.Length;

            OutWorldPositions.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexUp.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexEast.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutTileVertices.Resize(n, NativeArrayOptions.UninitializedMemory);
            OutVertexFeatureIdx.Resize(n, NativeArrayOptions.UninitializedMemory);

            NativeArray<double3> worldPositions = OutWorldPositions.AsArray();
            NativeArray<double3> vertexUp = OutVertexUp.AsArray();
            NativeArray<double3> vertexEast = OutVertexEast.AsArray();
            NativeArray<double2> tileVertices = OutTileVertices.AsArray();
            NativeArray<int> vertexFeatureIdx = OutVertexFeatureIdx.AsArray();

            for (int i = 0; i < n; i++)
            {
                GlobeFillVertex v = vertices[i];
                worldPositions[i] = v.World;
                vertexUp[i] = v.Up;
                vertexEast[i] = v.East;
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
