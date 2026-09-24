using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Unity.Rendering.Meshing
{
    public static partial class StyledFillTileBuilder
    {

        /// <summary>
        /// The stream-write node for one fill layer: writes the graph's output lists into the mesh buffers.
        /// Nested so it reads the private vertex-stream layout. One job serves both projections: the tangent
        /// is <c>(VertexEast[i], 1)</c>, and <c>AggregateJob</c> writes the flat arm's east as <c>(1,0,0)</c>.
        /// It holds the whole <see cref="Md"/>, not per-stream arrays, to avoid the MeshData-aliasing fault in
        /// docs/lessons-learned.md, and resolves <c>.AsArray()</c> inside <see cref="Execute"/>.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
        private struct FillStreamWriteJob : IJob
        {
            // ── Inputs (the graph's one column set, job-scheduling-design.md) ───────────────────────────
            [ReadOnly] public NativeList<double3> WorldPositions;
            [ReadOnly] public NativeList<double3> VertexUp;
            [ReadOnly] public NativeList<double3> VertexEast;
            [ReadOnly] public NativeList<float3>  VertexBand;
            [ReadOnly] public NativeList<double2> TileVertices;
            [ReadOnly] public NativeList<int>     VertexFeatureIdx;
            [ReadOnly] public NativeList<int>     TriangleIndices;

            [ReadOnly] public NativeArray<Vector4> FeatureColors;

            public double ExtentInv;
            public double TileSpanWorldUnits;

            /// <summary>The layer's own <c>Mesh.MeshData</c> — already sized (<c>SetVertexBufferParams</c>/
            /// <c>SetIndexBufferParams</c>) by <see cref="ScheduleStreamWrite"/> on the main thread. Every
            /// stream/index view is taken from this INSIDE <see cref="Execute"/> — see the type doc.</summary>
            public Mesh.MeshData Md;

            /// <summary>One element: <c>c0</c> = min, <c>c1</c> = max — the layer's tight AABB.</summary>
            public NativeArray<float3x2> OutBounds;

            public void Execute()
            {
                NativeArray<double3> worldPositions   = WorldPositions.AsArray();
                NativeArray<double3> vertexUp         = VertexUp.AsArray();
                NativeArray<double3> vertexEast       = VertexEast.AsArray();
                NativeArray<float3>  vertexBand       = VertexBand.AsArray();
                NativeArray<double2> tileVertices     = TileVertices.AsArray();
                NativeArray<int>     vertexFeatureIdx = VertexFeatureIdx.AsArray();
                NativeArray<int>     triangleIndices  = TriangleIndices.AsArray();

                NativeArray<FillPositionNormal> stream0 = Md.GetVertexData<FillPositionNormal>(0);
                NativeArray<FillPatternUvBand> stream1 = Md.GetVertexData<FillPatternUvBand>(1);
                NativeArray<Vector4> stream2 = Md.GetVertexData<Vector4>(2);
                NativeArray<Vector4> stream3 = Md.GetVertexData<Vector4>(3);
                NativeArray<int>     indices = Md.GetIndexData<int>();

                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);

                for (int i = 0; i < worldPositions.Length; i++)
                {
                    float3 v = (float3)worldPositions[i];
                    bMin = math.min(bMin, v);
                    bMax = math.max(bMax, v);
                    float3 up = (float3)vertexUp[i];
                    stream0[i] = new FillPositionNormal
                    {
                        Position = new Vector3(v.x, v.y, v.z),
                        Normal   = new Vector3(up.x, up.y, up.z),
                    };

                    // (dirEast, dirNorth, side), zero on interior and inner-ring vertices. Always written:
                    // a MeshData vertex buffer is not zero-initialised, so a skipped write reads garbage.
                    float3 band = vertexBand[i];
                    stream1[i] = new FillPatternUvBand
                    {
                        PatternUv = PatternCoord(tileVertices[i], ExtentInv, TileSpanWorldUnits),
                        Band      = new Vector3(band.x, band.y, band.z),
                    };

                    float3 east = (float3)vertexEast[i];
                    stream2[i] = new Vector4(east.x, east.y, east.z, 1f); // w=+1: same TBN handedness both arms

                    stream3[i] = FeatureColors[vertexFeatureIdx[i]];
                }

                OutBounds[0] = new float3x2(bMin, bMax);

                // Reverse triangle winding at this GPU-index boundary
                // (docs/coordinates-and-projections.md).
                for (int i = 0; i + 2 < triangleIndices.Length; i += 3)
                {
                    indices[i + 0] = triangleIndices[i + 0];
                    indices[i + 1] = triangleIndices[i + 2]; // 2nd/3rd
                    indices[i + 2] = triangleIndices[i + 1]; // swapped
                }
            }
        }
    }
}
