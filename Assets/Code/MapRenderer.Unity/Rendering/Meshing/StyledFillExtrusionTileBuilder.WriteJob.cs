using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Projection;
namespace MapRenderer.Unity.Rendering.Meshing
{
    public static partial class StyledFillExtrusionTileBuilder
    {
        /// <summary>
        /// The write graph's stream-write node for one fill-extrusion layer (job-scheduling-design.md §3.1/
        /// §3.2, §8 stage 4): one instance per layer, writing the roof at <c>[0, Vr)</c> then the walls at
        /// <c>[Vr, Vr+Vw)</c> — <see cref="ScheduleStreamWrite"/> is its only caller (itself called from
        /// both a test-assembly caller (via <c>InternalsVisibleTo</c>) and <see cref="ScheduleWrite"/> —
        /// job-scheduling-design.md §8 stage 4 Group B). Nested here (not a top-level type) so it can read this class's private
        /// vertex-stream layout (<see cref="PositionNormal"/>,
        /// <see cref="ExtrudeAndBake"/>) directly, mirroring <see cref="StyledFillTileBuilder.FillStreamWriteJob"/>'s
        /// shape.
        ///
        /// <para><b>Roof</b> is computed here, per vertex — the same sec-φ bake the retired managed
        /// flat/globe roof writers used to apply. <b>Walls</b> are a straight
        /// COPY: <see cref="FillExtrusionMeshGraph.Schedule"/> already computed every wall stream value
        /// (position, extrude, tangent, colour) via <c>WallQuadJob</c>, so this job's wall half only relocates
        /// those bytes into the mesh buffer at the roof-rebased offset — nothing about a wall vertex is
        /// recomputed here.</para>
        ///
        /// <para>Holds the whole <see cref="Md"/>, not separate stream <c>NativeArray</c> fields — see
        /// docs/lessons-learned.md for the MeshData-aliasing fault this shape avoids. The <c>.AsArray()</c>
        /// rule is resolved inside <see cref="Execute"/>, never at schedule time.</para>
        /// </summary>
        [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
        private struct FillExtrusionStreamWriteJob : IJob
        {
            // ── Roof inputs — the graph's one column set (job-scheduling-design.md §3.7). ──────────────────
            [ReadOnly] public NativeList<double3> WorldPositions;
            [ReadOnly] public NativeList<double3> VertexUp;
            [ReadOnly] public NativeList<double3> VertexEast;
            [ReadOnly] public NativeList<double2> TileVertices;
            [ReadOnly] public NativeList<int>     VertexFeatureIdx;
            [ReadOnly] public NativeList<int>     TriangleIndices;

            [ReadOnly] public NativeArray<Vector4> FeatureColors;
            [ReadOnly] public NativeArray<Vector2> FeatureBake;

            // ── Wall inputs — already fully computed by FillExtrusionMeshGraph.Schedule; copied verbatim below.
            [ReadOnly] public NativeList<PositionNormal> WallPositionNormal;
            [ReadOnly] public NativeList<ExtrudeAndBake> WallExtrude;
            [ReadOnly] public NativeList<Vector4>        WallTangent;
            [ReadOnly] public NativeList<Vector4>        WallColor;
            [ReadOnly] public NativeList<int>            WallIndices;

            public TileId Tile;
            public double Extent;

            /// <summary>Set once, at schedule time, from the layer's own projection — never re-derived per
            /// vertex (mirrors the retired synchronous <c>WriteMeshData</c>'s own single call site).</summary>
            public bool Globe;

            /// <summary>The layer's own <c>Mesh.MeshData</c> — already sized by
            /// <see cref="ScheduleStreamWrite"/> on the main thread. Every stream/index view is taken from
            /// this INSIDE <see cref="Execute"/>.</summary>
            public Mesh.MeshData Md;

            /// <summary>One element: <c>c0</c> = min, <c>c1</c> = max — the layer's tight AABB, over roof AND
            /// wall vertices.</summary>
            public NativeArray<float3x2> OutBounds;

            public void Execute()
            {
                NativeArray<double3> worldPositions   = WorldPositions.AsArray();
                NativeArray<double3> vertexUp         = VertexUp.AsArray();
                NativeArray<double3> vertexEast       = VertexEast.AsArray();
                NativeArray<double2> tileVertices     = TileVertices.AsArray();
                NativeArray<int>     vertexFeatureIdx = VertexFeatureIdx.AsArray();
                NativeArray<int>     triangleIndices  = TriangleIndices.AsArray();

                NativeArray<PositionNormal> wallPos = WallPositionNormal.AsArray();
                NativeArray<ExtrudeAndBake> wallExt = WallExtrude.AsArray();
                NativeArray<Vector4>        wallTan = WallTangent.AsArray();
                NativeArray<Vector4>        wallCol = WallColor.AsArray();
                NativeArray<int>            wallIdx = WallIndices.AsArray();

                NativeArray<PositionNormal> s0 = Md.GetVertexData<PositionNormal>(0);
                NativeArray<ExtrudeAndBake> s1 = Md.GetVertexData<ExtrudeAndBake>(1);
                NativeArray<Vector4>        s2 = Md.GetVertexData<Vector4>(2);
                NativeArray<Vector4>        s3 = Md.GetVertexData<Vector4>(3);
                NativeArray<int>            indices = Md.GetIndexData<int>();

                int vr = worldPositions.Length;

                float3 bMin = new float3(float.MaxValue);
                float3 bMax = new float3(float.MinValue);

                // ── Roof: [0, vr) — same sec-φ bake the retired managed flat/globe roof writers used. ──
                for (int i = 0; i < vr; i++)
                {
                    float3 v  = (float3)worldPositions[i];
                    float3 up = (float3)vertexUp[i];
                    bMin = math.min(bMin, v);
                    bMax = math.max(bMax, v);

                    s0[i] = new PositionNormal
                    {
                        Position = new Vector3(v.x, v.y, v.z), Normal = new Vector3(up.x, up.y, up.z),
                    };

                    if (Globe)
                    {
                        // Globe factor = 1.0 (OQ1) — extrudeUp is the plain surface up, un-scaled.
                        s1[i] = new ExtrudeAndBake
                        {
                            ExtrudeUpAndT   = new Vector4(up.x, up.y, up.z, 1f), // t=1 roof
                            BakedBaseHeight = FeatureBake[vertexFeatureIdx[i]],
                        };
                    }
                    else
                    {
                        double lat = TileToGeoJob.GeoAt(Tile, Extent, tileVertices[i]).Latitude;
                        double f   = MetresToWorldFactor(globe: false, lat);
                        s1[i] = new ExtrudeAndBake
                        {
                            ExtrudeUpAndT   = new Vector4((float)(up.x * f), (float)(up.y * f), (float)(up.z * f), 1f),
                            BakedBaseHeight = FeatureBake[vertexFeatureIdx[i]],
                        };
                    }

                    // (VertexEast, 1) on both arms: the flat arm's east is the constant (1,0,0), written by
                    // AggregateJob — the retired managed roof writer's constant +X tangent, byte for
                    // byte — same argument as FillStreamWriteJob's own doc; no arm branch needed here either.
                    float3 east = (float3)vertexEast[i];
                    s2[i] = new Vector4(east.x, east.y, east.z, 1f);

                    s3[i] = FeatureColors[vertexFeatureIdx[i]];
                }

                // Reverse triangle winding at this GPU-index boundary (docs §7.1) — roof indices land at
                // [0, triangleIndices.Length), no base offset (the roof is always vertex 0..vr-1).
                for (int i = 0; i + 2 < triangleIndices.Length; i += 3)
                {
                    indices[i + 0] = triangleIndices[i + 0];
                    indices[i + 1] = triangleIndices[i + 2]; // 2nd/3rd
                    indices[i + 2] = triangleIndices[i + 1]; // swapped
                }

                // ── Walls: [vr, vr+vw) — a straight copy; indices rebased by +vr. ───────────────────────
                int vw = wallPos.Length;
                for (int j = 0; j < vw; j++)
                {
                    int i = vr + j;
                    PositionNormal pn = wallPos[j];
                    s0[i] = pn;
                    s1[i] = wallExt[j];
                    s2[i] = wallTan[j];
                    s3[i] = wallCol[j];

                    float3 p = new float3(pn.Position.x, pn.Position.y, pn.Position.z);
                    bMin = math.min(bMin, p);
                    bMax = math.max(bMax, p);
                }

                int roofIndexCount = triangleIndices.Length;
                for (int j = 0; j < wallIdx.Length; j++)
                    indices[roofIndexCount + j] = vr + wallIdx[j];

                OutBounds[0] = new float3x2(bMin, bMax);
            }
        }
    }
}
