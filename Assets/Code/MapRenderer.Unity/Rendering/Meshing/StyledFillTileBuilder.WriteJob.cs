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
        /// The write graph's stream-write node (job-scheduling-design.md §3.1/§3.2, §8 stage 2): one
        /// instance per layer, reproducing this class's managed stream-copy loop as a Burst job over the
        /// graph's output lists. Nested here (not a top-level type) so it can read this class's private
        /// vertex-stream layout (<see cref="FillPositionNormal"/>, <see cref="PatternCoord"/>) directly —
        /// <see cref="ScheduleStreamWrite"/> is its only caller (itself called from both a test-assembly
        /// caller, reached via <c>InternalsVisibleTo</c>, and <see cref="ScheduleWrite"/> —
        /// job-scheduling-design.md §8 stage 4 Group B).
        ///
        /// <para>One job, both projections. After the §3.7 output reshape the graph hands this job ONE
        /// column set regardless of arm, and the tangent is <c>(VertexEast[i], 1)</c> on both: the flat
        /// arm's east is the constant <c>(1,0,0)</c> (<c>AggregateJob</c>'s write — see its own doc) —
        /// the retired managed writer's constant +X tangent, byte for byte; there is no curved twin here.</para>
        ///
        /// <para><b>Holds the whole <see cref="Md"/>, not four separate stream <c>NativeArray</c> fields</b>
        /// — see docs/lessons-learned.md for the MeshData-aliasing fault this shape avoids.</para>
        ///
        /// <para><b>The <c>.AsArray()</c> rule</b>: resolved inside <see cref="Execute"/>, never at schedule
        /// time — the file convention every node in this graph follows.</para>
        /// </summary>
        [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
        private struct FillStreamWriteJob : IJob
        {
            // ── Inputs (the graph's one column set, job-scheduling-design.md §3.7) ───────────────────────
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

                    // (dirEast, dirNorth, side) straight from the graph's own column — zero on every interior
                    // vertex and on the band's inner ring, an outward miter with side 1 on its outer ring.
                    // Writing it unconditionally is what covers the interior: a Mesh.MeshData vertex buffer is
                    // not guaranteed zero-initialised, so a skipped write leaves `side` reading whatever the
                    // allocator handed back, and only on the runs where that memory happens to be dirty.
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

                // Reverse triangle winding at this GPU-index boundary (docs §7.1) — same rule as
                // WriteGeometry.
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
