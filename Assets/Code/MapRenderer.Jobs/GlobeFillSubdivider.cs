using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

// Burst specialises + AOT-compiles one instantiation per projection struct (Editor JITs without this).
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.GlobeFillSubdivideJob<MapRenderer.Core.Geo.SphericalProjection>))]
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.GlobeFillSubdivideJob<MapRenderer.Core.Geo.WebMercatorProjection>))]

namespace MapRenderer.Jobs
{
    /// <summary>One refined fill vertex (origin-relative). <see cref="Tile"/> keeps the tile-space coord the
    /// caller normalises into a UV; the rest is ready-to-stream Position / Normal / Tangent data.</summary>
    public struct GlobeFillVertex
    {
        public double3 World;   // origin-relative render position (→ Position)
        public double3 Up;      // geodetic surface up            (→ Normal)
        public double3 East;    // geodetic surface east          (→ Tangent.xyz)
        public double2 Tile;    // tile-space coord               (→ UV via ×1/extent)
        public int     Feature; // → per-feature color index
    }

    /// <summary>
    /// S91-C (C-3): adaptive curvature subdivision for globe fills, as a Burst job. Earcut triangulates in flat
    /// tile space; on a curved projection the straight triangle edges chord THROUGH the sphere (fills sink /
    /// facet at low zoom). This refines each earcut triangle — splitting 1→4 at edge midpoints (in tile space,
    /// re-projected onto the sphere) — until every edge subtends less than <c>acos(CosThresh)</c>, bounded by
    /// <c>MaxDepth</c> and a per-tile <c>Budget</c> so a whole-globe z0 tile can't explode. A flat projection
    /// (constant up) never splits — it passes straight through.
    ///
    /// <para><b>Burst.</b> The projection is the generic struct <typeparamref name="TProj"/> (the
    /// <see cref="ProjectPointsJob{TProj}"/> pattern) so Burst devirtualises + inlines <c>ProjectPoint</c> /
    /// <c>TangentBasisAt</c> — no managed call. The recursion is an EXPLICIT stack (Burst does not reliably
    /// support real recursion); the stack is DFS-bounded (~<c>3·MaxDepth</c> entries), a Temp allocation freed
    /// at job end. Managed dispatch by projection type lives in <see cref="GlobeFillSubdivideDispatch"/>.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct GlobeFillSubdivideJob<TProj> : IJob where TProj : struct, IProjection
    {
        [ReadOnly] public TProj                  Projection;
        [ReadOnly] public NativeArray<double2>   TileVerts;         // earcut vertices (tile space)
        [ReadOnly] public NativeArray<int>       TriangleIndices;   // earcut triangles into TileVerts
        [ReadOnly] public NativeArray<int>       VertexFeatureIdx;  // feature index per TileVert
        public int     SrcVertCount, SrcIndexCount;                 // valid lengths (arrays may be oversized)
        public TileId  Id;
        public double  Extent, CosThresh;
        public double3 Origin;
        public int     MaxDepth, Budget;

        public NativeList<GlobeFillVertex> OutVerts;
        public NativeList<int>             OutIndices;

        private struct V   { public double3 World, Up, East; public double2 Tile; }
        private struct Tri { public V A, B, C; public int Depth, Feat; }

        public void Execute()
        {
            var stack = new NativeList<Tri>(64, Allocator.Temp);
            for (int t = 0; t + 2 < SrcIndexCount; t += 3)
            {
                int i0 = TriangleIndices[t], i1 = TriangleIndices[t + 1], i2 = TriangleIndices[t + 2];
                int feat = i0 < SrcVertCount ? VertexFeatureIdx[i0] : 0;
                stack.Add(new Tri { A = Project(TileVerts[i0]), B = Project(TileVerts[i1]), C = Project(TileVerts[i2]), Depth = 0, Feat = feat });

                while (stack.Length > 0)
                {
                    Tri w = stack[stack.Length - 1];
                    stack.RemoveAtSwapBack(stack.Length - 1); // LIFO pop (order-independent)

                    bool flat = w.Depth >= MaxDepth
                        || (math.dot(w.A.Up, w.B.Up) >= CosThresh
                         && math.dot(w.B.Up, w.C.Up) >= CosThresh
                         && math.dot(w.C.Up, w.A.Up) >= CosThresh);
                    if (flat || OutVerts.Length >= Budget) { Emit(w.A, w.Feat); Emit(w.B, w.Feat); Emit(w.C, w.Feat); continue; }

                    V ab = Project(Mid(w.A.Tile, w.B.Tile));
                    V bc = Project(Mid(w.B.Tile, w.C.Tile));
                    V ca = Project(Mid(w.C.Tile, w.A.Tile));
                    stack.Add(new Tri { A = w.A, B = ab,   C = ca,   Depth = w.Depth + 1, Feat = w.Feat });
                    stack.Add(new Tri { A = ab,  B = w.B,  C = bc,   Depth = w.Depth + 1, Feat = w.Feat });
                    stack.Add(new Tri { A = ca,  B = bc,   C = w.C,  Depth = w.Depth + 1, Feat = w.Feat });
                    stack.Add(new Tri { A = ab,  B = bc,   C = ca,   Depth = w.Depth + 1, Feat = w.Feat }); // centre
                }
            }
            stack.Dispose();
        }

        private void Emit(in V v, int feat)
        {
            OutVerts.Add(new GlobeFillVertex { World = v.World, Up = v.Up, East = v.East, Tile = v.Tile, Feature = feat });
            OutIndices.Add(OutVerts.Length - 1); // no dedup: sequential indices
        }

        private V Project(double2 tile)
        {
            double2 ll = Id.ToLonLat(tile.x, tile.y, Extent);       // (lon, lat)
            var geo = new GeoCoordinate { Latitude = ll.y, Longitude = ll.x };
            ProjectedPoint pp = Projection.ProjectPoint(geo);
            float3 e = Projection.TangentBasisAt(geo).c0;
            double3 world = new double3(pp.World.x - Origin.x, pp.World.y - Origin.y, pp.World.z - Origin.z);
            return new V { World = world, Up = pp.Up, East = new double3(e.x, e.y, e.z), Tile = tile };
        }

        private static double2 Mid(double2 a, double2 b) => new double2((a.x + b.x) * 0.5, (a.y + b.y) * 0.5);
    }

    /// <summary>Managed side of the globe-fill subdivide job: picks the concrete projection struct and runs the
    /// matching Burst specialisation synchronously on the caller's (worker) thread — the S89 worker-write path.</summary>
    public static class GlobeFillSubdivideDispatch
    {
        /// <summary>Split an edge until it subtends less than this (≈3°): sagitta ≈ R(1−cos(θ/2)) ≈ 0.05% of R.</summary>
        public const double DefaultMaxEdgeAngleRad   = 0.05236;   // 3 degrees
        /// <summary>Hard recursion cap (4^depth worst-case fan-out) — the low-zoom runaway backstop.</summary>
        public const int    DefaultMaxDepth          = 5;
        /// <summary>Per-tile vertex budget; once reached, remaining triangles emit flat (no deeper split).</summary>
        public const int    DefaultMaxOutputVertices = 200_000;

        public static void Run(
            IProjection projection,
            NativeArray<double2> tileVerts, NativeArray<int> triangleIndices, NativeArray<int> vertexFeatureIdx,
            int srcVertCount, int srcIndexCount, in TileId id, double extent, double3 originRender,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices,
            NativeList<GlobeFillVertex> outVerts, NativeList<int> outIndices)
        {
            switch (projection)
            {
                case SphericalProjection sp:   RunTyped(sp, tileVerts, triangleIndices, vertexFeatureIdx, srcVertCount, srcIndexCount, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices); break;
                case WebMercatorProjection wm: RunTyped(wm, tileVerts, triangleIndices, vertexFeatureIdx, srcVertCount, srcIndexCount, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices); break;
                case null:                     RunTyped(new WebMercatorProjection(), tileVerts, triangleIndices, vertexFeatureIdx, srcVertCount, srcIndexCount, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices); break;
                default:
                    throw new NotSupportedException(
                        $"No GlobeFillSubdivideJob dispatch for projection type {projection.GetType().Name}. " +
                        "Add a case here + a RegisterGenericJobType line in GlobeFillSubdivider.");
            }
        }

        private static void RunTyped<TProj>(
            TProj projection,
            NativeArray<double2> tileVerts, NativeArray<int> triangleIndices, NativeArray<int> vertexFeatureIdx,
            int srcVertCount, int srcIndexCount, in TileId id, double extent, double3 originRender,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices,
            NativeList<GlobeFillVertex> outVerts, NativeList<int> outIndices)
            where TProj : struct, IProjection
            => new GlobeFillSubdivideJob<TProj>
            {
                Projection = projection,
                TileVerts = tileVerts, TriangleIndices = triangleIndices, VertexFeatureIdx = vertexFeatureIdx,
                SrcVertCount = srcVertCount, SrcIndexCount = srcIndexCount,
                Id = id, Extent = extent, Origin = originRender,
                CosThresh = math.cos(maxEdgeAngleRad), MaxDepth = maxDepth, Budget = maxOutputVertices,
                OutVerts = outVerts, OutIndices = outIndices,
            }.Run();
    }
}
