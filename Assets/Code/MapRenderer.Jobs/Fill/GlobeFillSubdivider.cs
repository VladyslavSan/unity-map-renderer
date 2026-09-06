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
        public int     Feature; // → per-feature color index
    }

    /// <summary>
    /// S91-C (C-3): adaptive curvature subdivision for globe fills, as a Burst job. Earcut triangulates in flat
    /// tile space; on a curved projection the straight triangle edges chord THROUGH the sphere (fills sink /
    /// facet at low zoom). This refines each earcut triangle by PER-EDGE marking — an edge is marked iff it
    /// subtends more than <c>acos(CosThresh)</c> — and one of 3 conforming templates keyed by the triangle's
    /// mark count (0 emit / 1 bisect / 2 the "1→3" split / 3 the "1→4" split, at edge midpoints in tile space,
    /// re-projected onto the sphere): a mark is a function of an edge's two endpoints alone, so two triangles
    /// sharing an edge compute the identical mark — conforming without connectivity, no T-junctions at any
    /// depth (mesh-triangulation-robustness-design.md §6.2, fix candidate A). Bounded by <c>MaxDepth</c> and a
    /// per-tile <c>Budget</c> so a whole-globe z0 tile can't explode. A flat projection (constant up) never
    /// splits — it passes straight through.
    ///
    /// <para>OUTPUT WINDING: each refined triangle preserves its parent's vertex order, so the output stays CCW —
    /// the pipeline's single canonical winding (inherited from <c>Earcut</c>), reversed once to Unity-front at the
    /// mesh-write boundary (<c>StyledFillTileBuilder</c>) for stock Cull Back; see <c>docs §7.1</c>.</para>
    ///
    /// <para><b>Burst.</b> The projection is the generic struct <typeparamref name="TProj"/> (the
    /// <see cref="ProjectPointsJob{TProj}"/> pattern) so Burst devirtualises + inlines <c>ProjectPoint</c> /
    /// <c>TangentBasisAt</c> — no managed call. The recursion is an EXPLICIT stack (Burst does not reliably
    /// support real recursion); the stack is DFS-bounded (~<c>3·MaxDepth</c> entries), a Temp allocation freed
    /// at job end. Managed dispatch by projection type lives in <see cref="GlobeFillSubdivideDispatch"/>.</para>
    ///
    /// <para><b>Residuals (known, not hit by the corpus/z2-quad teeth — both are depth-1 or uniformly-curved,
    /// so every triangle reaches its stop test in lockstep).</b> A per-triangle FORCED stop — either the
    /// <see cref="MaxDepth"/> cap or the <see cref="Budget"/> cutoff — makes that ONE triangle emit flat
    /// regardless of its own marks, INCLUDING a still-marked edge it shares with a neighbour that has not
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
        public TileId  Id;
        public double  Extent, CosThresh;
        public double3 Origin;
        public int     MaxDepth, Budget;

        public NativeList<GlobeFillVertex> OutVerts;
        public NativeList<int>             OutIndices;

        private struct V   { public double3 World, Up, East; public double2 Tile; }
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

            var stack = new NativeList<Tri>(64, Allocator.Temp);
            for (int t = 0; t + 2 < srcIndexCount; t += 3)
            {
                int i0 = TriangleIndices[t], i1 = TriangleIndices[t + 1], i2 = TriangleIndices[t + 2];
                int feat = i0 < srcVertCount ? VertexFeatureIdx[i0] : 0;
                stack.Add(new Tri { A = Project(TileVerts[i0]), B = Project(TileVerts[i1]), C = Project(TileVerts[i2]), Depth = 0, Feat = feat });

                while (stack.Length > 0)
                {
                    Tri w = stack[stack.Length - 1];
                    stack.RemoveAtSwapBack(stack.Length - 1); // LIFO pop (order-independent)

                    // Per-EDGE marking (candidate A, mesh-triangulation-robustness-design.md §6.2): a mark is
                    // a function of an edge's two endpoints ALONE, so two triangles sharing an edge compute
                    // the identical mark — conforming without connectivity. Force 0 marks at the depth cap or
                    // once the budget is exhausted (emit flat, same as the old per-triangle stop test).
                    // MUST match SubdivisionCoverageValidator.RunMirror byte-for-byte (parity tooth).
                    bool overBudget = OutVerts.Length >= Budget;
                    bool canSplit = w.Depth < MaxDepth && !overBudget;
                    bool markAB = canSplit && math.dot(w.A.Up, w.B.Up) < CosThresh;
                    bool markBC = canSplit && math.dot(w.B.Up, w.C.Up) < CosThresh;
                    bool markCA = canSplit && math.dot(w.C.Up, w.A.Up) < CosThresh;
                    int markCount = (markAB ? 1 : 0) + (markBC ? 1 : 0) + (markCA ? 1 : 0);

                    if (markCount == 0) { Emit(w.A, w.Feat); Emit(w.B, w.Feat); Emit(w.C, w.Feat); continue; }

                    V mAB = markAB ? Project(Mid(w.A.Tile, w.B.Tile)) : default;
                    V mBC = markBC ? Project(Mid(w.B.Tile, w.C.Tile)) : default;
                    V mCA = markCA ? Project(Mid(w.C.Tile, w.A.Tile)) : default;
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
        /// <summary>Per-tile vertex budget; once reached, remaining triangles emit flat (no deeper split).</summary>
        public const int    DefaultMaxOutputVertices = 200_000;

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
            in TileId id, double extent, double3 originRender,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices,
            NativeList<GlobeFillVertex> outVerts, NativeList<int> outIndices,
            JobHandle deps)
        {
            switch (projection)
            {
                case SphericalProjection sp:   return ScheduleTyped(sp, tileVerts, triangleIndices, vertexFeatureIdx, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices, deps);
                case WebMercatorProjection wm: return ScheduleTyped(wm, tileVerts, triangleIndices, vertexFeatureIdx, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices, deps);
                case null:                     return ScheduleTyped(new WebMercatorProjection(), tileVerts, triangleIndices, vertexFeatureIdx, id, extent, originRender, maxEdgeAngleRad, maxDepth, maxOutputVertices, outVerts, outIndices, deps); // default planar — dead on the production path
                default:
                    throw new NotSupportedException(
                        $"No GlobeFillSubdivideJob dispatch for projection type {projection.GetType().Name}. " +
                        "Add a case here + a RegisterGenericJobType line in GlobeFillSubdivider.");
            }
        }

        private static JobHandle ScheduleTyped<TProj>(
            TProj projection,
            NativeList<double2> tileVerts, NativeList<int> triangleIndices, NativeList<int> vertexFeatureIdx,
            in TileId id, double extent, double3 originRender,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices,
            NativeList<GlobeFillVertex> outVerts, NativeList<int> outIndices,
            JobHandle deps)
            where TProj : struct, IProjection
            => new GlobeFillSubdivideJob<TProj>
            {
                Projection = projection,
                TileVerts = tileVerts.AsDeferredJobArray(), TriangleIndices = triangleIndices.AsDeferredJobArray(),
                VertexFeatureIdx = vertexFeatureIdx.AsDeferredJobArray(),
                Id = id, Extent = extent, Origin = originRender,
                CosThresh = math.cos(maxEdgeAngleRad), MaxDepth = maxDepth, Budget = maxOutputVertices,
                OutVerts = outVerts, OutIndices = outIndices,
            }.Schedule(deps);
    }
}
