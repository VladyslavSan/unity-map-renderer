using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Lines;

namespace MapRenderer.Unity.Rendering.Meshing
{
    public static partial class StyledLineTileBuilder
    {
        /// <summary>
        /// The write graph's stream-write node (job-scheduling-design.md): one instance per
        /// layer, writing <see cref="LineGraphOutput"/>'s columns into the mesh buffers as a Burst job.
        /// Nested here (not a top-level type) so it can read this class's private vertex-stream layout
        /// (<see cref="LinePositionNormal"/>, <see cref="LineWidthColor"/>) directly — <see cref="ScheduleStreamWrite"/>
        /// is its only caller.
        ///
        /// <para><b>Holds the whole <see cref="Md"/>, not four separate stream <c>NativeArray</c> fields</b> —
        /// see <c>docs/lessons-learned.md</c> for the <c>MeshData</c>-aliasing fault this shape avoids (mirrors
        /// <c>StyledFillTileBuilder.FillStreamWriteJob</c>'s own doc).</para>
        ///
        /// <para><b>Indices are copied VERBATIM</b> — the winding swap already happened in
        /// <see cref="RibbonAggregateJob"/>, offset per ring, before this job ever runs.
        /// This is the one deliberate difference from fill's write job, which does the swap here.</para>
        ///
        /// <para><b>The <c>.AsArray()</c> rule</b>: resolved inside <see cref="Execute"/>, never at schedule
        /// time.</para>
        /// </summary>
        [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
        private struct LineStreamWriteJob : IJob
        {
            // ── Inputs (the line graph's output columns) ────────────────────────────────────────────
            [ReadOnly] public NativeList<LineRibbonVertex> Vertices;
            [ReadOnly] public NativeList<int>               VertexFeatureIdx;
            [ReadOnly] public NativeList<int>               Indices;

            [ReadOnly] public NativeArray<Vector4> FeatureColors;
            [ReadOnly] public NativeArray<float>   FeatureWidths;

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
                NativeArray<LineWidthColor>     stream3 = Md.GetVertexData<LineWidthColor>(3);
                NativeArray<int>                indices = Md.GetIndexData<int>();

                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);

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
                    stream3[i] = new LineWidthColor
                    {
                        WidthScale = rv.WidthScale * FeatureWidths[f],
                        Color      = FeatureColors[f],
                    };
                }

                OutBounds[0] = new float3x2(bMin, bMax);

                for (int i = 0; i < Indices.Length; i++) indices[i] = Indices[i];
            }
        }
    }
}
