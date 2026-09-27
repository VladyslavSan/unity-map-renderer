using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity.Jobs.Lines;

namespace MapRenderer.Unity.Rendering.Meshing
{
    public static partial class StyledLineTileBuilder
    {
        /// <summary>
        /// The stream-write node for one line layer: writes <see cref="LineGraphOutput"/>'s columns into the
        /// mesh buffers. Nested so it reads the private vertex-stream layout. It holds the whole
        /// <see cref="Md"/>, not per-stream arrays, to avoid the <c>MeshData</c>-aliasing fault in
        /// <c>docs/lessons-learned.md</c>. Non-local invariant: it copies indices VERBATIM, because
        /// <see cref="RibbonAggregateJob"/> already swapped the winding; fill's write job swaps it itself.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
        private struct LineStreamWriteJob : IJob
        {
            // ── Inputs (the line graph's output columns) ────────────────────────────────────────────
            [ReadOnly] public NativeList<LineRibbonVertex> Vertices;
            [ReadOnly] public NativeList<int>               VertexFeatureIdx;
            [ReadOnly] public NativeList<int>               Indices;

            [ReadOnly] public NativeArray<Vector4> FeatureColors;
            [ReadOnly] public NativeArray<float4>  FeaturePaintScales;

            /// <summary>The layer's own <c>Mesh.MeshData</c> — already sized (<c>SetVertexBufferParams</c>/
            /// <c>SetIndexBufferParams</c>) by <see cref="ScheduleStreamWrite"/> on the main thread. Every
            /// stream/index view is taken from this INSIDE <see cref="Execute"/> — see the type doc.</summary>
            public Mesh.MeshData Md;

            /// <summary>One element: <c>c0</c> = min, <c>c1</c> = max — the layer's tight centerline AABB
            /// (parity with the pre-graph <c>RecalculateBounds</c>, which also ignored shader width
            /// extrusion).</summary>
            public NativeArray<float3x2> OutBounds;

            public void Execute()
            {
                NativeArray<LinePositionNormal> stream0 = Md.GetVertexData<LinePositionNormal>(0);
                NativeArray<Vector3>            stream1 = Md.GetVertexData<Vector3>(1);
                NativeArray<Vector2>            stream2 = Md.GetVertexData<Vector2>(2);
                NativeArray<LineVertexPaint>    stream3 = Md.GetVertexData<LineVertexPaint>(3);
                NativeArray<int>                indices = Md.GetIndexData<int>();

                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);

                // Gap/offset/blur ride a half lane (±65504); clamp before the cast so a runaway style value
                // becomes the largest representable half instead of Infinity.
                float halfMax = (float)half.MaxValueAsHalf;

                for (int i = 0; i < Vertices.Length; i++)
                {
                    LineRibbonVertex rv = Vertices[i];
                    float3 pos    = (float3)rv.Position; // origin-relative render space
                    float3 upN    = (float3)rv.Up;       // surface up (Normal / lighting)
                    float3 across = (float3)rv.Across;   // 3D across, |across| = miter factor (Y=0 for Mercator)
                    bMin = math.min(bMin, pos);
                    bMax = math.max(bMax, pos);

                    stream0[i] = new LinePositionNormal
                    {
                        Position = new Vector3(pos.x, pos.y, pos.z),
                        Normal   = new Vector3(upN.x, upN.y, upN.z),
                    };
                    stream1[i] = new Vector3(across.x, across.y, across.z);
                    stream2[i] = new Vector2(rv.Side, (float)rv.DistanceAlong);

                    int f = VertexFeatureIdx[i];
                    float4 scale = FeaturePaintScales[f];
                    float  gap    = math.clamp(scale.y, -halfMax, halfMax);
                    float  offset = math.clamp(scale.z, -halfMax, halfMax);
                    float  blur   = math.clamp(scale.w, -halfMax, halfMax);
                    stream3[i] = new LineVertexPaint
                    {
                        WidthScale    = rv.WidthScale * scale.x,
                        Color         = FeatureColors[f],
                        GapOffsetBlur = new half4((half)gap, (half)offset, (half)blur, (half)0f),
                    };
                }

                OutBounds[0] = new float3x2(bMin, bMax);

                for (int i = 0; i < Indices.Length; i++) indices[i] = Indices[i];
            }
        }
    }
}
