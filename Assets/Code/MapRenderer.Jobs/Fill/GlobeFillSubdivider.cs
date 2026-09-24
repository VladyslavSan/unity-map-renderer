using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

// Burst specialises + AOT-compiles one instantiation per projection struct (Editor JITs without this).
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.Fill.GlobeFillSubdivideJob<MapRenderer.Core.Geo.SphericalProjection>))]
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.Fill.GlobeFillSubdivideJob<MapRenderer.Core.Geo.WebMercatorProjection>))]

namespace MapRenderer.Jobs.Fill
{
    /// <summary>One refined fill vertex (origin-relative). <see cref="Tile"/> keeps the tile-space coord the
    /// caller normalises into a UV; the rest is ready-to-stream Position / Normal / Tangent data.</summary>
    public struct GlobeFillVertex
    {
        public double3 World;   // origin-relative render position (→ Position)
        public double3 Up;      // geodetic surface up            (→ Normal)
        public double3 East;    // geodetic surface east          (→ Tangent.xyz)
        public double2 Tile;    // tile-space coord               (→ UV via ×1/extent)
        public float3  Band;    // boundary-band attribute        (→ TEXCOORD3)
        public int     Feature; // → per-feature color index
    }

    /// <summary>Bitwise vertex key for <see cref="GlobeFillSubdivideJob{TProj}.Emit"/>: two emitted vertices
    /// with an equal key would write IDENTICAL <see cref="GlobeFillVertex"/> bytes, so the second may reuse
    /// the first's index. Non-local invariant: the key covers every <see cref="GlobeFillVertex"/> field a
    /// shader reads, and a new field must be added by hand (<c>GlobeFillVertexKeySizeTests</c> fails
    /// otherwise). <c>Mid()</c> is order-symmetric, so both triangles on a split edge key it identically.
    /// </summary>
    internal readonly struct GlobeFillVertexKey : IEquatable<GlobeFillVertexKey>
    {
        private readonly ulong _worldX, _worldY, _worldZ;
        private readonly ulong _upX, _upY, _upZ;
        private readonly ulong _eastX, _eastY, _eastZ;
        private readonly ulong _tileX, _tileY;
        private readonly uint  _bandX, _bandY, _bandZ;
        private readonly int   _feature;

        public GlobeFillVertexKey(in GlobeFillVertex v)
        {
            _worldX = math.asulong(v.World.x); _worldY = math.asulong(v.World.y); _worldZ = math.asulong(v.World.z);
            _upX = math.asulong(v.Up.x);       _upY = math.asulong(v.Up.y);       _upZ = math.asulong(v.Up.z);
            _eastX = math.asulong(v.East.x);   _eastY = math.asulong(v.East.y);   _eastZ = math.asulong(v.East.z);
            _tileX = math.asulong(v.Tile.x);   _tileY = math.asulong(v.Tile.y);
            _bandX = math.asuint(v.Band.x);    _bandY = math.asuint(v.Band.y);    _bandZ = math.asuint(v.Band.z);
            _feature = v.Feature;
        }

        public bool Equals(GlobeFillVertexKey other) =>
            _worldX == other._worldX && _worldY == other._worldY && _worldZ == other._worldZ &&
            _upX == other._upX && _upY == other._upY && _upZ == other._upZ &&
            _eastX == other._eastX && _eastY == other._eastY && _eastZ == other._eastZ &&
            _tileX == other._tileX && _tileY == other._tileY &&
            _bandX == other._bandX && _bandY == other._bandY && _bandZ == other._bandZ &&
            _feature == other._feature;

        public override bool Equals(object obj) => obj is GlobeFillVertexKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                long h = (long)_worldX;
                h = h * -1521134295L + (long)_worldY; h = h * -1521134295L + (long)_worldZ;
                h = h * -1521134295L + (long)_upX;    h = h * -1521134295L + (long)_upY;    h = h * -1521134295L + (long)_upZ;
                h = h * -1521134295L + (long)_eastX;  h = h * -1521134295L + (long)_eastY;  h = h * -1521134295L + (long)_eastZ;
                h = h * -1521134295L + (long)_tileX;  h = h * -1521134295L + (long)_tileY;
                h = h * -1521134295L + _bandX;        h = h * -1521134295L + _bandY;        h = h * -1521134295L + _bandZ;
                h = h * -1521134295L + _feature;
                return (int)(h ^ (h >> 32));
            }
        }
    }

    /// <summary>
    /// Adaptive curvature subdivision for globe fills: splits each earcut triangle by per-edge marks (an edge
    /// subtending more than <c>acos(CosThresh)</c>) and a template keyed by mark count, bounded by
    /// <see cref="MaxDepth"/> and two vertex budgets. Children keep the parent's order, so output stays CCW.
    /// <see cref="Emit"/> shares bit-identical vertices: <c>OutVerts</c> is the unique count, <c>OutIndices</c>
    /// the emitted count. It uses an explicit stack because Burst does not reliably support recursion.
    /// Limitation: a forced stop can leave a T-junction on a non-uniformly curved tile. See
    /// docs/mesh-triangulation-robustness-design.md § "Globe subdivision must be conforming".
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct GlobeFillSubdivideJob<TProj> : IJob where TProj : struct, IProjection
    {
        [ReadOnly] public TProj                  Projection;
        [ReadOnly] public NativeArray<double2>   TileVerts;         // earcut vertices (tile space)
        [ReadOnly] public NativeArray<int>       TriangleIndices;   // earcut triangles into TileVerts
        [ReadOnly] public NativeArray<int>       VertexFeatureIdx;  // feature index per TileVert
        [ReadOnly] public NativeArray<float3>    VertexBand;        // boundary-band attribute per TileVert
        public TileId  Id;
        public double  Extent, CosThresh;
        public double3 Origin;
        public int     MaxDepth, InteriorBudget, TotalBudget;

        public NativeList<GlobeFillVertex> OutVerts;
        public NativeList<int>             OutIndices;

        private struct V   { public double3 World, Up, East; public float3 Band; public double2 Tile; }
        private struct Tri { public V A, B, C; public int Depth, Feat; }

        /// <summary>The count rule: <see cref="TileVerts"/>/<see cref="TriangleIndices"/>'s own lengths are
        /// the valid lengths, always. The only caller is
        /// <see cref="GlobeFillSubdivideDispatch.Schedule"/>, which passes <c>AsDeferredJobArray()</c> views
        /// resolved at EXECUTE time, because both arrays are <c>AggregateJob</c> outputs whose length is
        /// unknown at schedule time.</summary>
        public void Execute()
        {
            int srcVertCount  = TileVerts.Length;
            int srcIndexCount = TriangleIndices.Length;

            int interiorVerts = 0;
            var stack = new NativeList<Tri>(64, Allocator.Temp);
            // A local, because a Temp container is invalid as a field of a scheduled job. Seeded from
            // srcIndexCount, a lower bound on emitted vertices; a small seed costs a rehash per growth.
            var indexByVertex = new NativeHashMap<GlobeFillVertexKey, int>(srcIndexCount, Allocator.Temp);
            for (int t = 0; t + 2 < srcIndexCount; t += 3)
            {
                int i0 = TriangleIndices[t], i1 = TriangleIndices[t + 1], i2 = TriangleIndices[t + 2];
                int feat = i0 < srcVertCount ? VertexFeatureIdx[i0] : 0;

                // A band quad's two triangles each carry at least one outer vertex (side 1); an interior
                // triangle carries none. The distinction drives the budget below and nothing else.
                bool bandTri = VertexBand[i0].z != 0f || VertexBand[i1].z != 0f || VertexBand[i2].z != 0f;
                stack.Add(new Tri
                {
                    A = Project(TileVerts[i0], VertexBand[i0]),
                    B = Project(TileVerts[i1], VertexBand[i1]),
                    C = Project(TileVerts[i2], VertexBand[i2]),
                    Depth = 0, Feat = feat,
                });

                while (stack.Length > 0)
                {
                    Tri w = stack[stack.Length - 1];
                    stack.RemoveAtSwapBack(stack.Length - 1); // LIFO pop (order-independent)

                    // Non-local invariant: this must match SubdivisionCoverageValidator.RunMirror byte-for-byte,
                    // so both budgets count EMITTED vertices, never unique storage (sharing must not move a split).
                    // InteriorBudget bounds subdivision and skips band vertices, so the band cannot starve the
                    // interior; TotalBudget bounds allocation. Both feed ONE overBudget, so band and interior stop
                    // splitting a shared edge together, with no T-junction between them.
                    bool overBudget = interiorVerts >= InteriorBudget || OutIndices.Length >= TotalBudget;
                    bool canSplit = w.Depth < MaxDepth && !overBudget;
                    bool markAB = canSplit && math.dot(w.A.Up, w.B.Up) < CosThresh;
                    bool markBC = canSplit && math.dot(w.B.Up, w.C.Up) < CosThresh;
                    bool markCA = canSplit && math.dot(w.C.Up, w.A.Up) < CosThresh;
                    int markCount = (markAB ? 1 : 0) + (markBC ? 1 : 0) + (markCA ? 1 : 0);

                    if (markCount == 0)
                    {
                        Emit(w.A, w.Feat, ref indexByVertex); Emit(w.B, w.Feat, ref indexByVertex); Emit(w.C, w.Feat, ref indexByVertex);
                        if (!bandTri) interiorVerts += 3;
                        continue;
                    }

                    V mAB = markAB ? Split(w.A, w.B) : default;
                    V mBC = markBC ? Split(w.B, w.C) : default;
                    V mCA = markCA ? Split(w.C, w.A) : default;
                    int childDepth = w.Depth + 1;

                    if (markCount == 3)
                    {
                        // 1→4 (unchanged). Push order = corner-A, corner-B, corner-C, centre — LIFO pop
                        // resolves centre's subtree first.
                        stack.Add(new Tri { A = w.A, B = mAB,  C = mCA,  Depth = childDepth, Feat = w.Feat });
                        stack.Add(new Tri { A = mAB, B = w.B,  C = mBC,  Depth = childDepth, Feat = w.Feat });
                        stack.Add(new Tri { A = mCA, B = mBC,  C = w.C,  Depth = childDepth, Feat = w.Feat });
                        stack.Add(new Tri { A = mAB, B = mBC,  C = mCA,  Depth = childDepth, Feat = w.Feat }); // centre
                    }
                    else if (markCount == 1)
                    {
                        // Bisect the one marked edge; the interior edge (midpoint–far vertex) is parent-
                        // private. Cyclic relabeling A→B→C→A of the AB-marked template.
                        if (markAB)
                        {
                            stack.Add(new Tri { A = w.A, B = mAB, C = w.C, Depth = childDepth, Feat = w.Feat });
                            stack.Add(new Tri { A = mAB, B = w.B, C = w.C, Depth = childDepth, Feat = w.Feat });
                        }
                        else if (markBC)
                        {
                            stack.Add(new Tri { A = w.B, B = mBC, C = w.A, Depth = childDepth, Feat = w.Feat });
                            stack.Add(new Tri { A = mBC, B = w.C, C = w.A, Depth = childDepth, Feat = w.Feat });
                        }
                        else // markCA
                        {
                            stack.Add(new Tri { A = w.C, B = mCA, C = w.B, Depth = childDepth, Feat = w.Feat });
                            stack.Add(new Tri { A = mCA, B = w.A, C = w.B, Depth = childDepth, Feat = w.Feat });
                        }
                    }
                    else // markCount == 2 — locked rotation: apex = the vertex opposite the UNMARKED edge
                         // (shared by both marked edges); a0 = predecessor of apex, c0 = successor of apex
                         // in the A→B→C→A cycle.
                    {
                        V apex, a0, c0, mVA0, mVC0;
                        if (!markAB)      { apex = w.C; a0 = w.B; c0 = w.A; mVA0 = mBC; mVC0 = mCA; } // unmarked=AB
                        else if (!markBC) { apex = w.A; a0 = w.C; c0 = w.B; mVA0 = mCA; mVC0 = mAB; } // unmarked=BC
                        else              { apex = w.B; a0 = w.A; c0 = w.C; mVA0 = mAB; mVC0 = mBC; } // unmarked=CA

                        stack.Add(new Tri { A = apex, B = mVC0, C = mVA0, Depth = childDepth, Feat = w.Feat }); // corner at apex
                        // Quad a0-mVA0-mVC0-c0 → the SHORTER diagonal, a deterministic tile-space test the mirror
                        // repeats; it keeps the sub-triangles less anisotropic. Both choices preserve winding.
                        if (math.distancesq(a0.Tile, mVC0.Tile) <= math.distancesq(mVA0.Tile, c0.Tile))
                        {
                            stack.Add(new Tri { A = a0, B = mVA0, C = mVC0, Depth = childDepth, Feat = w.Feat });
                            stack.Add(new Tri { A = a0, B = mVC0, C = c0,   Depth = childDepth, Feat = w.Feat });
                        }
                        else
                        {
                            stack.Add(new Tri { A = a0,   B = mVA0, C = c0, Depth = childDepth, Feat = w.Feat });
                            stack.Add(new Tri { A = mVA0, B = mVC0, C = c0, Depth = childDepth, Feat = w.Feat });
                        }
                    }
                }
            }
            indexByVertex.Dispose();
            stack.Dispose();
        }

        /// <summary>Emits one triangle-corner vertex: a bit-identical vertex already in <see cref="OutVerts"/>
        /// (<see cref="GlobeFillVertexKey"/> — the whole emitted struct) is reused by index; otherwise a new
        /// one is appended. Every call adds exactly one <see cref="OutIndices"/> entry regardless of which
        /// branch runs, so <c>OutIndices.Length</c> is always the emitted count and <c>OutVerts.Length</c> is
        /// always the unique count — the split apart the caller's budget check relies on.</summary>
        private void Emit(in V v, int feat, ref NativeHashMap<GlobeFillVertexKey, int> indexByVertex)
        {
            var vertex = new GlobeFillVertex { World = v.World, Up = v.Up, East = v.East, Tile = v.Tile, Band = v.Band, Feature = feat };
            var key = new GlobeFillVertexKey(vertex);
            if (indexByVertex.TryGetValue(key, out int existing))
            {
                OutIndices.Add(existing);
                return;
            }

            int index = OutVerts.Length;
            OutVerts.Add(vertex);
            OutIndices.Add(index);
            indexByVertex.Add(key, index);
        }

        /// <summary>Splits one edge at its tile-space midpoint — the position half is
        /// <see cref="Project"/>'s, the band half is the plain average of the endpoints'.</summary>
        /// <param name="a">The edge's first endpoint.</param>
        /// <param name="b">The edge's second endpoint.</param>
        /// <returns>The re-projected midpoint carrying the interpolated band attribute.</returns>
        private V Split(in V a, in V b) => Project(Mid(a.Tile, b.Tile), (a.Band + b.Band) * 0.5f);

        /// <summary>Projects one tile-space point onto the surface, carrying the band attribute through
        /// unchanged.</summary>
        /// <param name="tile">The tile-space coordinate.</param>
        /// <param name="band">The boundary-band attribute this vertex carries.</param>
        /// <returns>The projected vertex.</returns>
        private V Project(double2 tile, float3 band)
        {
            double2 ll = Id.ToLonLat(tile.x, tile.y, Extent);       // (lon, lat)
            var geo = new GeoCoordinate { Latitude = ll.y, Longitude = ll.x };
            ProjectedPoint pp = Projection.ProjectPoint(geo);
            float3 e = Projection.TangentBasisAt(geo).c0;
            double3 world = new double3(pp.World.x - Origin.x, pp.World.y - Origin.y, pp.World.z - Origin.z);
            return new V { World = world, Up = pp.Up, East = new double3(e.x, e.y, e.z), Tile = tile, Band = band };
        }

        private static double2 Mid(double2 a, double2 b) => new double2((a.x + b.x) * 0.5, (a.y + b.y) * 0.5);
    }

    /// <summary>Managed side of the globe-fill subdivide job: picks the concrete projection struct and
    /// schedules the matching Burst specialisation. <see cref="Schedule"/> is the only entry point.</summary>
    public static class GlobeFillSubdivideDispatch
    {
        /// <summary>Split an edge until it subtends less than this (≈3°): sagitta ≈ R(1−cos(θ/2)) ≈ 0.05% of R.</summary>
        public const double DefaultMaxEdgeAngleRad   = 0.05236;   // 3 degrees
        /// <summary>Hard recursion cap (4^depth worst-case fan-out) — the low-zoom runaway backstop.</summary>
        public const int    DefaultMaxDepth          = 5;
        /// <summary>Per-tile budget for the INTERIOR's subdivided vertices; once reached, remaining triangles
        /// emit flat. Band vertices do not count against it. It is not a headroom claim: a low-zoom fixture
        /// reaches most of it, which <c>TheCurvedArmsInteriorKeepsHeadroomUnderItsBudget</c> keeps visible.
        /// </summary>
        public const int    DefaultMaxInteriorVertices = 200_000;

        /// <summary>Per-tile ceiling on TOTAL emitted vertices (interior + boundary band) — the ALLOCATION
        /// backstop, where <see cref="DefaultMaxInteriorVertices"/> bounds interior subdivision. It is 3x the
        /// interior budget, a margin for layers more boundary-heavy than the fixtures; nothing should reach it.
        /// </summary>
        public const int    DefaultMaxTotalVertices = 600_000;

        /// <summary>The fill graph's curved-arm subdivide node.
        /// <paramref name="tileVerts"/>/<paramref name="triangleIndices"/>/<paramref name="vertexFeatureIdx"/>
        /// are the graph's own <c>AggregateJob</c> outputs, resolved through <c>AsDeferredJobArray()</c> at
        /// execute time. <c>case null</c> is dead on the production path: a null projection is flat, and the
        /// curved arm is this dispatcher's only caller.</summary>
        public static JobHandle Schedule(
            IProjection projection,
            NativeList<double2> tileVerts, NativeList<int> triangleIndices, NativeList<int> vertexFeatureIdx,
            NativeList<float3> vertexBand,
            in TileId id, double extent, double3 originRender,
            double maxEdgeAngleRad, int maxDepth, int maxInteriorVertices, int maxTotalVertices,
            NativeList<GlobeFillVertex> outVerts, NativeList<int> outIndices,
            JobHandle deps)
        {
            switch (projection)
            {
                case SphericalProjection sp:   return ScheduleTyped(sp, tileVerts, triangleIndices, vertexFeatureIdx, vertexBand, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxInteriorVertices, maxTotalVertices, outVerts, outIndices, deps);
                case WebMercatorProjection wm: return ScheduleTyped(wm, tileVerts, triangleIndices, vertexFeatureIdx, vertexBand, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxInteriorVertices, maxTotalVertices, outVerts, outIndices, deps);
                case null:                     return ScheduleTyped(new WebMercatorProjection(), tileVerts, triangleIndices, vertexFeatureIdx, vertexBand, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxInteriorVertices, maxTotalVertices, outVerts, outIndices, deps); // default planar — dead on the production path
                default:
                    throw new NotSupportedException(
                        $"No GlobeFillSubdivideJob dispatch for projection type {projection.GetType().Name}. " +
                        "Add a case here + a RegisterGenericJobType line in GlobeFillSubdivider.");
            }
        }

        private static JobHandle ScheduleTyped<TProj>(
            TProj projection,
            NativeList<double2> tileVerts, NativeList<int> triangleIndices, NativeList<int> vertexFeatureIdx,
            NativeList<float3> vertexBand,
            in TileId id, double extent, double3 originRender,
            double maxEdgeAngleRad, int maxDepth, int maxInteriorVertices, int maxTotalVertices,
            NativeList<GlobeFillVertex> outVerts, NativeList<int> outIndices,
            JobHandle deps)
            where TProj : struct, IProjection
            => new GlobeFillSubdivideJob<TProj>
            {
                Projection = projection,
                TileVerts = tileVerts.AsDeferredJobArray(), TriangleIndices = triangleIndices.AsDeferredJobArray(),
                VertexFeatureIdx = vertexFeatureIdx.AsDeferredJobArray(),
                VertexBand = vertexBand.AsDeferredJobArray(),
                Id = id, Extent = extent, Origin = originRender,
                CosThresh = math.cos(maxEdgeAngleRad), MaxDepth = maxDepth,
                InteriorBudget = maxInteriorVertices, TotalBudget = maxTotalVertices,
                OutVerts = outVerts, OutIndices = outIndices,
            }.Schedule(deps);
    }
}
