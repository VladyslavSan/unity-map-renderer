using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
namespace MapRenderer.Unity.Rendering.Meshing
{
    public static partial class StyledFillExtrusionTileBuilder
    {
        /// <summary>
        /// One quad per boundary edge over the flat (pre-earcut) ring vertices <see cref="RingSelectJob"/>
        /// gathers — the Burst replacement for <c>WriteWalls</c>'s per-point managed loop
        /// (job-scheduling-design.md §8 stage 5). Reads no tile coordinates: <see cref="Geo"/>/<see cref="World"/>/
        /// <see cref="Up"/> are the already-projected per-flat-vertex columns <see cref="TileToGeoJob"/> and
        /// <see cref="ProjectionDispatch"/> produced; this job only walks ring topology and emits quads.
        ///
        /// <para>No output ceiling: exactly 4 vertices and 6 indices per edge over a ring set whose total
        /// length is fixed by the input columns — there is no runtime-derived multiplier (unlike line's
        /// subdivision), so the always-bound-loops rule is already satisfied without one.</para>
        ///
        /// <para><see cref="Up"/> is already unit BY CONSTRUCTION at the source — <c>WebMercatorProjection</c>
        /// returns the literal constant <c>(0,1,0)</c>, <c>SphericalProjection</c>'s radial ECEF normal is
        /// exactly unit (<c>SphericalProjection.cs</c>'s own comment: "unit by construction, no normalize
        /// needed") — and <see cref="ProjectPointsJob{TProj}"/> passes it straight through
        /// (<c>Normals[index] = pp.Up</c>, no renormalization). This job still casts to <c>float3</c> then
        /// calls <c>math.normalize</c> on it, matching the managed arm's own order — NOT because the value
        /// needs normalizing, but for transcription fidelity with the retired loop and to guard the sub-ULP
        /// length error the <c>(float3)</c> narrowing can introduce into an otherwise-exact unit vector.</para>
        ///
        /// <para><see cref="Globe"/> is computed ONCE on the calling thread
        /// (<c>!double.IsInfinity(proj.MaxRefineAngleRad)</c>) — <c>IProjection</c> is managed, so a job
        /// cannot read the property itself.</para>
        ///
        /// <para><c>internal</c> (was <c>private</c>), and its five ring/flat-vertex fields
        /// <see cref="NativeList{T}"/> (was <see cref="NativeArray{T}"/>), resolved directly inside
        /// <see cref="Execute"/> — job-scheduling-design.md §8 stage 5, the wall-job stage:
        /// <see cref="FillExtrusionMeshGraph"/> now schedules this job, and <see cref="ProjectionDispatch.Schedule"/>
        /// requires its world/normal columns as <c>NativeList</c>s. Mirrors <c>RibbonBatchJob</c>'s own
        /// field-attribute usage — an <c>IJob</c> may hold a <c>NativeList</c> field directly, same as this
        /// job already does for its <see cref="OutPositionNormal"/> family below.</para>
        /// </summary>
        [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
        internal struct WallQuadJob : IJob
        {
            [ReadOnly] public NativeList<int>           RingOffsets;     // flat, start+sentinel; length = ringCount+1
            [ReadOnly] public NativeList<int>           RingFeatureIdx;  // flat, length = ringCount
            [ReadOnly] public NativeList<GeoCoordinate> Geo;             // per flat vertex — TileToGeoJob output
            [ReadOnly] public NativeList<double3>       World;           // per flat vertex, origin-relative
            [ReadOnly] public NativeList<double3>       Up;              // per flat vertex, unit BY CONSTRUCTION at the source (see the type doc) — this job still normalizes it
            [ReadOnly] public NativeArray<Vector4>       FeatureColors;
            [ReadOnly] public NativeArray<Vector2>       FeatureBake;
            public bool Globe;

            public NativeList<PositionNormal> OutPositionNormal;
            public NativeList<ExtrudeAndBake> OutExtrude;
            public NativeList<Vector4>        OutTangent;
            public NativeList<Vector4>        OutColor;
            public NativeList<int>            OutIndices;

            public void Execute()
            {
                int ringCount = RingOffsets.Length - 1;
                for (int r = 0; r < ringCount; r++)
                {
                    int start = RingOffsets[r];
                    int len   = RingOffsets[r + 1] - start;
                    if (len < 2) continue; // degenerate ring — no edges

                    Vector4 color = FeatureColors[RingFeatureIdx[r]];
                    Vector2 bake  = FeatureBake[RingFeatureIdx[r]];

                    for (int i = 0; i < len; i++)
                    {
                        int idxA = start + i;
                        int idxB = start + (i + 1) % len;

                        float3 posA = (float3)World[idxA];
                        float3 posB = (float3)World[idxB];
                        float3 upA  = math.normalize((float3)Up[idxA]);
                        float3 upB  = math.normalize((float3)Up[idxB]);

                        double factorA = MetresToWorldFactor(Globe, Geo[idxA].Latitude);
                        double factorB = MetresToWorldFactor(Globe, Geo[idxB].Latitude);
                        float3 extrudeUpA = upA * (float)factorA;
                        float3 extrudeUpB = upB * (float)factorB;

                        // Outward-horizontal lighting normal + along-edge tangent. cross(edgeDir, up) (NOT
                        // cross(up, edgeDir)) points AWAY from the interior — same convention WriteWalls used.
                        float3 upAvg   = math.normalize(upA + upB);
                        float3 edgeDir = math.normalize(posB - posA);
                        float3 outward = math.normalize(math.cross(edgeDir, upAvg));
                        Vector3 outwardV3 = new Vector3(outward.x, outward.y, outward.z);
                        Vector4 tangentV4 = new Vector4(edgeDir.x, edgeDir.y, edgeDir.z, 1f);

                        // 4 verts: floorA(t=0), floorB(t=0), roofB(t=1), roofA(t=1) — wall-local, against
                        // THIS request's own wall list (WallColumns' own doc).
                        int quadBase = OutPositionNormal.Length;
                        AddWallVertex(posA, outwardV3, extrudeUpA, 0f, bake, tangentV4, color, OutPositionNormal, OutExtrude, OutTangent, OutColor);
                        AddWallVertex(posB, outwardV3, extrudeUpB, 0f, bake, tangentV4, color, OutPositionNormal, OutExtrude, OutTangent, OutColor);
                        AddWallVertex(posB, outwardV3, extrudeUpB, 1f, bake, tangentV4, color, OutPositionNormal, OutExtrude, OutTangent, OutColor);
                        AddWallVertex(posA, outwardV3, extrudeUpA, 1f, bake, tangentV4, color, OutPositionNormal, OutExtrude, OutTangent, OutColor);

                        // quadBase+0=floorA, +1=floorB, +2=roofB, +3=roofA.
                        OutIndices.Add(quadBase + 0); OutIndices.Add(quadBase + 1); OutIndices.Add(quadBase + 2);
                        OutIndices.Add(quadBase + 0); OutIndices.Add(quadBase + 2); OutIndices.Add(quadBase + 3);
                    }
                }
            }
        }
    }
}
