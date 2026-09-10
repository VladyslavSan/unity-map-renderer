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
    /// the first's index instead of allocating storage.
    ///
    /// <para>Stated as a PREDICATE, not a fixed field list: <b>any attribute a downstream shader reads that
    /// can differ between two vertices sharing a tile coordinate participates in identity.</b> Keying the
    /// WHOLE struct — rather than hand-picking <c>Tile</c>/<c>Feature</c> and treating <c>World</c>/<c>Up</c>/
    /// <c>East</c> as redundant functions of <c>Tile</c> — satisfies that predicate automatically and stays
    /// correct when a column is added (e.g. the per-vertex band/side attribute on the parked
    /// <c>feat/fill-boundary-antialiasing</c> branch, absent on <c>main</c>): a hand-picked key silently
    /// omits a new column at rebase time. NOTE the key is still enumerated FIELD BY FIELD below, so adding a
    /// column to <see cref="GlobeFillVertex"/> does NOT extend it automatically — <c>Band</c> had to be added
    /// here by hand when this branch rebased. GlobeFillVertexKeySizeTests is the guard that makes the next
    /// added column fail loudly instead of silently merging distinct vertices. <c>Mid()</c> is exactly order-symmetric
    /// ((a+b)*0.5 commutes bit-for-bit) and marking is per-edge from the two endpoints alone (no
    /// connectivity), so two triangles sharing a split edge compute bit-identical derived fields for it —
    /// the whole-struct key merges exactly what a canonical-input key would, with no under-merge risk
    /// (T-C5 is the tooth that would catch this reasoning being wrong).</para></summary>
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
    /// S91-C (C-3): adaptive curvature subdivision for globe fills, as a Burst job. Earcut triangulates in flat
    /// tile space; on a curved projection the straight triangle edges chord THROUGH the sphere (fills sink /
    /// facet at low zoom). This refines each earcut triangle by PER-EDGE marking — an edge is marked iff it
    /// subtends more than <c>acos(CosThresh)</c> — and one of 3 conforming templates keyed by the triangle's
    /// mark count (0 emit / 1 bisect / 2 the "1→3" split / 3 the "1→4" split, at edge midpoints in tile space,
    /// re-projected onto the sphere): a mark is a function of an edge's two endpoints alone, so two triangles
    /// sharing an edge compute the identical mark — conforming without connectivity, no T-junctions at any
    /// depth (mesh-triangulation-robustness-design.md §6.2, fix candidate A). Bounded by <c>MaxDepth</c> and
    /// the per-tile <see cref="InteriorBudget"/> so a whole-globe z0 tile can't explode. A flat projection
    /// (constant up) never splits — it passes straight through.
    ///
    /// <para><b>Emitted vertices are SHARED</b> (<see cref="GlobeFillVertexKey"/>): <see cref="Emit"/> reuses an
    /// existing <c>OutVerts</c> slot for a bit-identical vertex instead of tripling every leaf triangle's
    /// corners, so <c>OutVerts</c> is the UNIQUE count and <c>OutIndices</c> the EMITTED count — a
    /// representation-only change, the split path and every emitted vertex's bytes are unaffected.</para>
    ///
    /// <para>OUTPUT WINDING: each refined triangle preserves its parent's vertex order, so the output stays CCW —
    /// the pipeline's single canonical winding (inherited from <c>Earcut</c>), reversed once to Unity-front at the
    /// mesh-write boundary (<c>StyledFillTileBuilder</c>) for stock Cull Back; see <c>docs §7.1</c>.</para>
    ///
    /// <para><b>Burst.</b> The projection is the generic struct <typeparamref name="TProj"/> (the
    /// <see cref="ProjectPointsJob{TProj}"/> pattern) so Burst devirtualises + inlines <c>ProjectPoint</c> /
    /// <c>TangentBasisAt</c> — no managed call. The recursion is an EXPLICIT stack (Burst does not reliably
    /// support real recursion); the stack is DFS-bounded (~<c>3·MaxDepth</c> entries), a Temp allocation freed
    /// at job end, same as the vertex-key map. Managed dispatch by projection type lives in
    /// <see cref="GlobeFillSubdivideDispatch"/>.</para>
    ///
    /// <para><b>Residuals (known, not hit by the corpus/z2-quad teeth — both are depth-1 or uniformly-curved,
    /// so every triangle reaches its stop test in lockstep).</b> A per-triangle FORCED stop — either the
    /// <see cref="MaxDepth"/> cap or either vertex budget (<see cref="InteriorBudget"/> /
    /// <see cref="TotalBudget"/>) — makes that ONE triangle emit flat regardless of its own marks,
    /// INCLUDING a still-marked edge it shares with a neighbour that has not
    /// (yet) been forced to stop: on a tile with genuinely NON-uniform curvature (real-world geometry, unlike
    /// the z2-quad's single flat square), the two triangles sharing that edge can reach depth 5 at different
    /// times — one side keeps splitting the marked edge, the other is capped and leaves it whole — a
    /// T-junction. Measured on the z0 "countries" fixture (unrelated to this stage's teeth):
    /// <c>maxDepthReached=5, budgetFired=false, tJunctions=22, maxGap≈0.12% of tile</c> — i.e. the CAP alone
    /// (not just the budget) reproduces this. Same class of gap as before this fix, at a different (smaller)
    /// magnitude; scope-fenced here (see the plan's Scope fence — sliver-optimal cap handling is a follow-up,
    /// not this stage).</para>
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

        /// <summary>job-scheduling-design.md §8 stage 4 Group B: the count rule — <see cref="TileVerts"/>/
        /// <see cref="TriangleIndices"/>'s own lengths are the valid lengths, always. The only caller is
        /// <see cref="GlobeFillSubdivideDispatch.Schedule"/>, which passes <c>AsDeferredJobArray()</c> views
        /// resolved at EXECUTE time (both arrays are <c>AggregateJob</c>'s outputs, unknown at schedule
        /// time) — the explicit-count/deferred-count split this job used to carry (for a since-retired
        /// synchronous caller) is gone with it.</summary>
        public void Execute()
        {
            int srcVertCount  = TileVerts.Length;
            int srcIndexCount = TriangleIndices.Length;

            int interiorVerts = 0;
            var stack = new NativeList<Tri>(64, Allocator.Temp);
            // Job-local, freed at Execute's end (job-scheduling-design.md §3.6: an Allocator.Temp container
            // is only valid as a LOCAL, never a field — this job is .Schedule()'d, matching `stack` above).
            // Keyed on the bit patterns a shared vertex WOULD write (GlobeFillVertexKey) so two split paths
            // that reach the same tile coordinate — the same feature's shared triangle edge, the shared
            // diagonal between two earcut roots — share one OutVerts slot instead of tripling it. Seeded
            // from srcIndexCount (a sound lower bound on the emitted vertex count) rather than a small
            // constant — this map can grow to ~45k entries on a real z0 tile, and a small seed means ~10
            // reallocate-and-rehash passes, each copying every entry inserted so far, inside the hot path
            // this whole change exists to speed up.
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

                    // Per-EDGE marking (candidate A, mesh-triangulation-robustness-design.md §6.2): a mark is
                    // a function of an edge's two endpoints ALONE, so two triangles sharing an edge compute
                    // the identical mark — conforming without connectivity. Force 0 marks at the depth cap or
                    // once the budget is exhausted (emit flat, same as the old per-triangle stop test).
                    // MUST match SubdivisionCoverageValidator.RunMirror byte-for-byte (parity tooth).
                    //
                    // TWO bounds, two different jobs. InteriorBudget is the SUBDIVISION guard and counts only
                    // interior vertices: the band adds ~2 triangles per ring vertex, enough on a boundary-heavy
                    // layer to exhaust a shared budget and force the INTERIOR to emit flat — the band degrading
                    // geometry that is not its own. Measured on z0 countries: one shared budget emitted 313 953
                    // vertices, split it emits 349 401, so 35 448 vertices of interior subdivision were being
                    // suppressed by the band. TotalBudget is the ALLOCATION backstop.
                    //
                    // BOTH count EMITTED vertices, never unique storage: vertex sharing must not move a split
                    // decision, or the job stops being representation-only and diverges from the mirror. That is
                    // also what keeps the mirror's single budget a valid collapse of these two under the parity
                    // fixture's all-zero VertexBand, where every triangle is interior and interiorVerts tracks
                    // OutIndices.Length exactly. TotalBudget therefore reads the emitted count and is
                    // conservative for an allocation guard (emitted >= unique) — deliberately, since a backstop
                    // that fires early is safe and one that depends on sharing is not.
                    //
                    // Both feed ONE overBudget, deliberately: a band quad's long edges duplicate the interior
                    // boundary edge, so the two must stop splitting at the same moment or the band keeps refining
                    // an edge the interior has been forced to leave whole — a T-junction between fill and band.
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
                    else // markCount == 2 — locked rotation (plan's "2-mark template" section): apex = the
                         // vertex opposite the UNMARKED edge (shared by both marked edges); a0 = predecessor
                         // of apex, c0 = successor of apex in the A→B→C→A cycle.
                    {
                        V apex, a0, c0, mVA0, mVC0;
                        if (!markAB)      { apex = w.C; a0 = w.B; c0 = w.A; mVA0 = mBC; mVC0 = mCA; } // unmarked=AB
                        else if (!markBC) { apex = w.A; a0 = w.C; c0 = w.B; mVA0 = mCA; mVC0 = mAB; } // unmarked=BC
                        else              { apex = w.B; a0 = w.A; c0 = w.C; mVA0 = mAB; mVC0 = mBC; } // unmarked=CA

                        stack.Add(new Tri { A = apex, B = mVC0, C = mVA0, Depth = childDepth, Feat = w.Feat }); // corner at apex
                        // Quad a0-mVA0-mVC0-c0 → SHORTER interior diagonal (deterministic tile-space choice →
                        // identical in mirror & job, parity-safe): keeps cap sub-triangles better-shaped
                        // (less anisotropic) than the fixed long diagonal. Both choices preserve winding.
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
    /// schedules the matching Burst specialisation. job-scheduling-design.md §8 stage 4 Group B: the
    /// synchronous entry point (<c>Run</c>/<c>RunTyped</c>) is retired with its two synchronous callers
    /// (<c>StyledFillTileBuilder.WriteGlobeSubdivided</c>, <c>StyledFillExtrusionTileBuilder.WriteGlobeRoof</c>)
    /// — <see cref="Schedule"/> is the only entry point left.</summary>
    public static class GlobeFillSubdivideDispatch
    {
        /// <summary>Split an edge until it subtends less than this (≈3°): sagitta ≈ R(1−cos(θ/2)) ≈ 0.05% of R.</summary>
        public const double DefaultMaxEdgeAngleRad   = 0.05236;   // 3 degrees
        /// <summary>Hard recursion cap (4^depth worst-case fan-out) — the low-zoom runaway backstop.</summary>
        public const int    DefaultMaxDepth          = 5;
        /// <summary>Per-tile budget for the INTERIOR's subdivided vertices; once reached, remaining triangles
        /// emit flat (no deeper split). The boundary band's own vertices do not count against it — see
        /// <see cref="GlobeFillSubdivideJob{TProj}.InteriorBudget"/> for why the two are separate.
        /// <para><b>Not a headroom claim.</b> This used to be documented as never reached in production. It is
        /// not: the shipped z0 countries fixture measures 164 535 interior vertices, 82% of this value, with
        /// no band at all. <c>GlobeFillBandTests.TheCurvedArmsInteriorKeepsHeadroomUnderItsBudget</c> is the
        /// tooth that stops that going unnoticed again.</para></summary>
        public const int    DefaultMaxInteriorVertices = 200_000;

        /// <summary>Per-tile ceiling on TOTAL emitted vertices (interior + boundary band) — the ALLOCATION
        /// backstop, a different job from <see cref="DefaultMaxInteriorVertices"/>. That one bounds how far
        /// the interior may SUBDIVIDE, which is the exponential low-zoom case; this one bounds how much
        /// memory one tile may take, which the band makes linear-but-large rather than exponential.
        /// <para><b>Derivation.</b> The shipped z0 countries fixture measures 349 401 total vertices
        /// (164 535 interior + the band). 600 000 is 1.72x that — margin for a more boundary-heavy layer
        /// than any in the fixture corpus — and 3x the interior budget, so the two constants stay legible
        /// against each other. Round, and deliberately so: it is a backstop nobody should reach, not a
        /// working limit, and a value derived to more precision would imply otherwise.</para></summary>
        public const int    DefaultMaxTotalVertices = 600_000;

        /// <summary>The fill graph's curved-arm subdivide node (job-scheduling-design.md §3.2).
        /// <paramref name="tileVerts"/>/<paramref name="triangleIndices"/>/<paramref name="vertexFeatureIdx"/>
        /// are the graph's own <c>AggregateJob</c> outputs, resolved via <c>AsDeferredJobArray()</c> at
        /// execute time (<see cref="GlobeFillSubdivideJob{TProj}.Execute"/>'s own count rule). <c>case
        /// null</c> is dead on the production path: a null projection is flat
        /// (<c>ProjectionDispatch.cs</c>'s own default), and the curved arm is the only caller of this
        /// dispatcher.</summary>
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
