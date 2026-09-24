using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Validates the globe-fill adaptive SUBDIVISION stage (earcut → 1→4 curvature refine) through a MANAGED
    /// MIRROR of the Burst <c>GlobeFillSubdivideJob&lt;TProj&gt;</c>. Non-local invariant: the mirror must match
    /// the job step for step, because <c>GlobeSubdivisionJobParityTests</c> pins ordered output-stream equality.
    /// It measures the worst T-junction gap, flips/degenerates, per-root coverage and curvature fidelity.
    /// See docs/mesh-triangulation-robustness-design.md § "Validation instruments".
    /// </summary>
    public static class SubdivisionCoverageValidator
    {
        // Exact mirror of GlobeFillSubdivideDispatch's defaults (Assets/Code/MapRenderer.Jobs/GlobeFillSubdivider.cs).
        // DefaultMaxEdgeAngleRad is the LITERAL constant (0.05236), not a recomputed cos(3°) — π/60 ≠ 0.05236.
        public const double DefaultMaxEdgeAngleRad = 0.05236;
        public const int DefaultMaxDepth = 5;
        public const int DefaultBudget = 200_000;

        public readonly struct Report
        {
            public readonly int SubTriangles, EarcutTriangles, MaxDepthReached;
            public readonly bool BudgetFired;
            public readonly int TJunctions; // raw count — reported, NOT gated (meaningless as a threshold)
            public readonly double MaxGapMeters, MaxGapFracTile, MeanGapMeters;
            public readonly int[] GapBuckets; // <1,<10,<100,<1k,<10k,>=10k meters
            public readonly int DegenerateTris, FlippedTris;
            public readonly double CoverageAreaRelError; // PER-ROOT max (not a global total — a global total
                                                         // lets a dropped region cancel a duplicated one).
            public readonly bool Subdivided;
            public readonly double MaxLeafEdgeAngleRad;  // worst great-circle angle any emitted leaf edge still
                                                         // spans — the fix-agnostic curvature-fidelity metric.
            public readonly double2 WorstAt;

            public Report(int subTriangles, int earcutTriangles, int maxDepthReached, bool budgetFired,
                          int tJunctions, double maxGapMeters, double maxGapFracTile, double meanGapMeters,
                          int[] gapBuckets, int degenerateTris, int flippedTris, double coverageAreaRelError,
                          double maxLeafEdgeAngleRad, double2 worstAt)
            {
                SubTriangles = subTriangles;
                EarcutTriangles = earcutTriangles;
                MaxDepthReached = maxDepthReached;
                BudgetFired = budgetFired;
                TJunctions = tJunctions;
                MaxGapMeters = maxGapMeters;
                MaxGapFracTile = maxGapFracTile;
                MeanGapMeters = meanGapMeters;
                GapBuckets = gapBuckets;
                DegenerateTris = degenerateTris;
                FlippedTris = flippedTris;
                CoverageAreaRelError = coverageAreaRelError;
                Subdivided = subTriangles > earcutTriangles;
                MaxLeafEdgeAngleRad = maxLeafEdgeAngleRad;
                WorstAt = worstAt;
            }

            /// <summary>The subdivided mesh is visibly conforming AND geometrically faithful AND actually
            /// refines curvature. Curvature is gated on <see cref="MaxLeafEdgeAngleRad"/> (≤ the 3° target), not
            /// on <see cref="Subdivided"/>, so the refinement must be where the curvature is. Limitation: it
            /// assumes no Budget or MaxDepth cap (<see cref="BudgetFired"/>/<see cref="MaxDepthReached"/>).</summary>
            public bool Passes(double maxGapFracTile = 0.0005)
                => MaxGapFracTile <= maxGapFracTile && FlippedTris == 0 && DegenerateTris == 0
                   && CoverageAreaRelError <= 0.001 && Subdivided
                   && MaxLeafEdgeAngleRad <= DefaultMaxEdgeAngleRad * 1.2; // 1.2×: a well-formed leaf at the
                   // depth cap can sit modestly over the 3° target; still catches gross under-refinement
                   // (a "disable subdivision" fix leaves whole earcut edges ≫3.6° as leaf edges).

            public string Summary =>
                $"earcutTris={EarcutTriangles} subTris={SubTriangles} subdivided={Subdivided} " +
                $"maxDepthReached={MaxDepthReached} budgetFired={BudgetFired} " +
                $"tJunctions={TJunctions} maxGap={MaxGapMeters:F1}m ({MaxGapFracTile:P3} of tile) meanGap={MeanGapMeters:F1}m " +
                $"gapBuckets=[{string.Join(",", GapBuckets)}] degenerateTris={DegenerateTris} flippedTris={FlippedTris} " +
                $"coverageAreaRelError(perRoot)={CoverageAreaRelError:P3} maxLeafEdgeAngle={MaxLeafEdgeAngleRad * 180.0 / math.PI_DBL:F2}° " +
                $"worstAt=({WorstAt.x:F1},{WorstAt.y:F1})";
        }

        // -----------------------------------------------------------------------------------------------
        // Public entry points
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Validates an ALREADY-triangulated result against the managed subdivision mirror. The caller
        /// triangulates (via the Burst <c>EarcutJob</c> path) and supplies the flat triangles plus the
        /// per-vertex feature index (see <see cref="BuildRootsFromRaw"/>'s first-index feature pick).
        /// </summary>
        public static Report ValidateTriangulation(
            double2[] tileVerts, int[] triangleIndices, int[] vertexFeatureIdx, in TileId id, IProjection projection, double extent)
        {
            var roots = BuildRootsFromRaw(tileVerts, triangleIndices, vertexFeatureIdx, tileVerts.Length, triangleIndices.Length);

            double cosThresh = math.cos(DefaultMaxEdgeAngleRad);
            RunMirror(roots, id, projection, extent, new double3(0, 0, 0), cosThresh, DefaultMaxDepth, DefaultBudget,
                out var leaves, out int maxDepthReached, out bool budgetFired);

            return AnalyzeLeafStream(roots, leaves, maxDepthReached, budgetFired, id, projection, extent);
        }

        /// <summary>Build the root-triangle list from the SAME raw (tileVerts, triangleIndices,
        /// vertexFeatureIdx) triple the real Burst job is dispatched with (see
        /// <c>GlobeFillSubdivideDispatch.Run</c>'s parameters) — the first-index feature pick mirrors
        /// GlobeFillSubdivider.cs:63-64 exactly. Used by Edit 4's crafted discriminating cases.</summary>
        internal static List<RootTri> BuildRootsFromRaw(
            double2[] tileVerts, int[] triangleIndices, int[] vertexFeatureIdx, int srcVertCount, int srcIndexCount)
        {
            var roots = new List<RootTri>();
            for (int t = 0; t + 2 < srcIndexCount; t += 3)
            {
                int i0 = triangleIndices[t], i1 = triangleIndices[t + 1], i2 = triangleIndices[t + 2];
                int feat = i0 < srcVertCount ? vertexFeatureIdx[i0] : 0;
                roots.Add(new RootTri(tileVerts[i0], tileVerts[i1], tileVerts[i2], feat));
            }
            return roots;
        }

        // -----------------------------------------------------------------------------------------------
        // Shared analysis over a raw leaf stream, so GlobeSubdivisionJobParityTests can feed the real job's
        // World/Tile values (paired 1:1 by emission order with this mirror's lineage) through it.
        // -----------------------------------------------------------------------------------------------

        internal static Report AnalyzeLeafStream(
            List<RootTri> roots, List<LeafRef> leaves,
            int maxDepthReached, bool budgetFired, in TileId id, IProjection projection, double extent)
        {
            var rootAdjacency = FindRootAdjacency(roots);
            AnalyzeGaps(roots, leaves, rootAdjacency,
                out int tjCount, out double maxGap, out double meanGap, out var gapBuckets, out double2 worstAt);
            AnalyzeTriangleQuality(roots, leaves, out int degenerateTris, out int flippedTris);
            double coverageAreaRelError = ComputeCoverageAreaRelError(roots, leaves);
            double maxLeafEdgeAngleRad = ComputeMaxLeafEdgeAngleRad(roots, leaves);
            double tileSpan = ComputeTileSpanMeters(id, projection, extent);
            double maxGapFracTile = maxGap / tileSpan;

            return new Report(
                subTriangles: leaves.Count / 3, earcutTriangles: roots.Count, maxDepthReached: maxDepthReached,
                budgetFired: budgetFired, tJunctions: tjCount, maxGapMeters: maxGap, maxGapFracTile: maxGapFracTile,
                meanGapMeters: meanGap, gapBuckets: gapBuckets, degenerateTris: degenerateTris, flippedTris: flippedTris,
                coverageAreaRelError: coverageAreaRelError, maxLeafEdgeAngleRad: maxLeafEdgeAngleRad, worstAt: worstAt);
        }

        // -----------------------------------------------------------------------------------------------
        // Lineage-carrying types (internal — shared with the Unity-only parity tooth).
        // -----------------------------------------------------------------------------------------------

        /// <summary>One earcut ("root") triangle in tile space, before any subdivision. <see cref="Feature"/>
        /// mirrors the real job's first-index feature pick (GlobeFillSubdivider.cs:63-64) — unused by this
        /// class's own Report analysis, carried only so Edit 4's parity tooth can assert Feature propagation.</summary>
        internal readonly struct RootTri
        {
            public readonly double2 A, B, C;
            public readonly int Feature;
            public RootTri(double2 a, double2 b, double2 c, int feature = 0) { A = a; B = b; C = c; Feature = feature; }
        }

        /// <summary>One emitted leaf vertex, tagged with its earcut root and recursion depth, which scope
        /// T-junction detection (case (b) in <see cref="AnalyzeGaps"/>). <see cref="Up"/>/<see cref="East"/>/
        /// <see cref="Feature"/> exist only so the parity tooth can match the real job's
        /// <c>GlobeFillVertex</c> stream.</summary>
        internal readonly struct LeafRef
        {
            public readonly double3 World;
            public readonly double3 Up;
            public readonly double3 East;
            public readonly double2 Tile;
            public readonly int Feature;
            public readonly int RootIndex;
            public readonly int Depth;

            public LeafRef(double3 world, double3 up, double3 east, double2 tile, int feature, int rootIndex, int depth)
            {
                World = world; Up = up; East = east; Tile = tile; Feature = feature;
                RootIndex = rootIndex; Depth = depth;
            }
        }

        // -----------------------------------------------------------------------------------------------
        // The managed mirror of GlobeFillSubdivideJob<TProj>.Execute (LIFO stack, child order, stop test,
        // Project), plus the RootIndex/Depth lineage the real job does not carry.
        // -----------------------------------------------------------------------------------------------

        private struct V { public double3 World; public double3 Up; public double3 East; public double2 Tile; }

        /// <summary>Parameterized entry point (explicit constants, no defaults) so Edit 4's crafted
        /// discriminating cases can drive the mirror with the SAME non-default max-depth/budget/origin the
        /// real job is dispatched with.</summary>
        internal static void RunManagedMirror(
            List<RootTri> roots, in TileId id, IProjection projection, double extent, double3 origin,
            double maxEdgeAngleRad, int maxDepth, int budget,
            out List<LeafRef> leaves, out int maxDepthReached, out bool budgetFired)
            => RunMirror(roots, id, projection, extent, origin, math.cos(maxEdgeAngleRad), maxDepth, budget,
                out leaves, out maxDepthReached, out budgetFired);

        private static void RunMirror(
            List<RootTri> roots, in TileId id, IProjection projection, double extent, double3 origin,
            double cosThresh, int maxDepth, int budget,
            out List<LeafRef> leaves, out int maxDepthReached, out bool budgetFired)
        {
            leaves = new List<LeafRef>();
            maxDepthReached = 0;
            budgetFired = false;

            var stack = new List<(V a, V b, V c, int depth, int rootIdx, int feat)>();
            for (int r = 0; r < roots.Count; r++)
            {
                RootTri root = roots[r];
                stack.Clear();
                stack.Add((Project(root.A, id, projection, extent, origin), Project(root.B, id, projection, extent, origin),
                           Project(root.C, id, projection, extent, origin), 0, r, root.Feature));

                while (stack.Count > 0)
                {
                    var w = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1); // LIFO pop — GlobeFillSubdivider.cs:69-70's RemoveAtSwapBack

                    if (w.depth > maxDepthReached) maxDepthReached = w.depth;

                    // Per-EDGE marks depend on the two endpoints ALONE, so triangles sharing an edge agree
                    // without connectivity. The depth cap or an exhausted budget forces 0 marks (emit flat).
                    bool overBudget = leaves.Count >= budget;
                    if (overBudget) budgetFired = true;

                    bool canSplit = w.depth < maxDepth && !overBudget;
                    bool markAB = canSplit && math.dot(w.a.Up, w.b.Up) < cosThresh;
                    bool markBC = canSplit && math.dot(w.b.Up, w.c.Up) < cosThresh;
                    bool markCA = canSplit && math.dot(w.c.Up, w.a.Up) < cosThresh;
                    int markCount = (markAB ? 1 : 0) + (markBC ? 1 : 0) + (markCA ? 1 : 0);

                    if (markCount == 0)
                    {
                        leaves.Add(new LeafRef(w.a.World, w.a.Up, w.a.East, w.a.Tile, w.feat, w.rootIdx, w.depth));
                        leaves.Add(new LeafRef(w.b.World, w.b.Up, w.b.East, w.b.Tile, w.feat, w.rootIdx, w.depth));
                        leaves.Add(new LeafRef(w.c.World, w.c.Up, w.c.East, w.c.Tile, w.feat, w.rootIdx, w.depth));
                        continue;
                    }

                    V mAB = markAB ? Project(Mid(w.a.Tile, w.b.Tile), id, projection, extent, origin) : default;
                    V mBC = markBC ? Project(Mid(w.b.Tile, w.c.Tile), id, projection, extent, origin) : default;
                    V mCA = markCA ? Project(Mid(w.c.Tile, w.a.Tile), id, projection, extent, origin) : default;
                    int childDepth = w.depth + 1;

                    if (markCount == 3)
                    {
                        // 1→4 (today's split, unchanged). Push order = corner-A, corner-B, corner-C, centre —
                        // LIFO pop resolves centre's subtree first.
                        stack.Add((w.a, mAB, mCA, childDepth, w.rootIdx, w.feat));
                        stack.Add((mAB, w.b, mBC, childDepth, w.rootIdx, w.feat));
                        stack.Add((mCA, mBC, w.c, childDepth, w.rootIdx, w.feat));
                        stack.Add((mAB, mBC, mCA, childDepth, w.rootIdx, w.feat));
                    }
                    else if (markCount == 1)
                    {
                        // Bisect the one marked edge; the interior edge (midpoint–far vertex) is parent-
                        // private. Cyclic relabeling A→B→C→A of the AB-marked template below.
                        if (markAB)
                        {
                            stack.Add((w.a, mAB, w.c, childDepth, w.rootIdx, w.feat));
                            stack.Add((mAB, w.b, w.c, childDepth, w.rootIdx, w.feat));
                        }
                        else if (markBC)
                        {
                            stack.Add((w.b, mBC, w.a, childDepth, w.rootIdx, w.feat));
                            stack.Add((mBC, w.c, w.a, childDepth, w.rootIdx, w.feat));
                        }
                        else // markCA
                        {
                            stack.Add((w.c, mCA, w.b, childDepth, w.rootIdx, w.feat));
                            stack.Add((mCA, w.a, w.b, childDepth, w.rootIdx, w.feat));
                        }
                    }
                    else // markCount == 2 — locked rotation (the "2-mark template"): apex = the
                         // vertex opposite the UNMARKED edge (shared by both marked edges); a0 = predecessor
                         // of apex, c0 = successor of apex in the A→B→C→A cycle.
                    {
                        V apex, a0, c0, mVA0, mVC0;
                        if (!markAB)      { apex = w.c; a0 = w.b; c0 = w.a; mVA0 = mBC; mVC0 = mCA; } // unmarked=AB
                        else if (!markBC) { apex = w.a; a0 = w.c; c0 = w.b; mVA0 = mCA; mVC0 = mAB; } // unmarked=BC
                        else              { apex = w.b; a0 = w.a; c0 = w.c; mVA0 = mAB; mVC0 = mBC; } // unmarked=CA

                        stack.Add((apex, mVC0, mVA0, childDepth, w.rootIdx, w.feat)); // corner at apex
                        // Quad a0-mVA0-mVC0-c0: the SHORTER diagonal (deterministic in tile space, so parity-safe)
                        // avoids anisotropic slivers. Both choices preserve winding.
                        if (DistSq(a0.Tile, mVC0.Tile) <= DistSq(mVA0.Tile, c0.Tile))
                        {
                            stack.Add((a0, mVA0, mVC0, childDepth, w.rootIdx, w.feat));
                            stack.Add((a0, mVC0, c0, childDepth, w.rootIdx, w.feat));
                        }
                        else
                        {
                            stack.Add((a0, mVA0, c0, childDepth, w.rootIdx, w.feat));
                            stack.Add((mVA0, mVC0, c0, childDepth, w.rootIdx, w.feat));
                        }
                    }
                }
            }
        }

        private static V Project(double2 tile, in TileId id, IProjection projection, double extent)
            => Project(tile, id, projection, extent, new double3(0, 0, 0));

        private static V Project(double2 tile, in TileId id, IProjection projection, double extent, double3 origin)
        {
            double2 ll = id.ToLonLat(tile.x, tile.y, extent);
            var geo = new GeoCoordinate { Latitude = ll.y, Longitude = ll.x };
            ProjectedPoint pp = projection.ProjectPoint(geo);
            float3 e = projection.TangentBasisAt(geo).c0;
            double3 world = new double3(pp.World.x - origin.x, pp.World.y - origin.y, pp.World.z - origin.z);
            return new V { World = world, Up = pp.Up, East = new double3(e.x, e.y, e.z), Tile = tile };
        }

        private static double2 Mid(double2 a, double2 b) => new double2((a.x + b.x) * 0.5, (a.y + b.y) * 0.5);

        private static double DistSq(double2 a, double2 b) { double dx = a.x - b.x, dy = a.y - b.y; return dx * dx + dy * dy; }

        // -----------------------------------------------------------------------------------------------
        // Case (a): cross-parent T-junctions along a shared ORIGINAL earcut edge.
        // -----------------------------------------------------------------------------------------------

        /// <summary>Two earcut root triangles that share a tile-space edge, keyed by matching that edge's
        /// (unordered) endpoints across every root — an interior earcut edge has exactly two owners.</summary>
        private static List<(int i, int j, double2 P, double2 Q)> FindRootAdjacency(List<RootTri> roots)
        {
            var edgeOwners = new Dictionary<(long, long, long, long), List<(int root, int local)>>();
            for (int r = 0; r < roots.Count; r++)
            {
                RootTri t = roots[r];
                AddEdgeOwner(edgeOwners, r, 0, t.A, t.B);
                AddEdgeOwner(edgeOwners, r, 1, t.B, t.C);
                AddEdgeOwner(edgeOwners, r, 2, t.C, t.A);
            }

            var adjacency = new List<(int, int, double2, double2)>();
            foreach (var owners in edgeOwners.Values)
            {
                // Skip a mesh boundary (1 owner) or non-manifold edge (>2). Limitation: on a retraced bridge
                // seam, an asymmetric split between two of its owners is not surfaced here.
                if (owners.Count != 2) continue;
                (int root0, int local0) = owners[0];
                (int root1, _) = owners[1];
                if (root0 == root1) continue;
                double2 p = LocalEdgeStart(roots[root0], local0), q = LocalEdgeEnd(roots[root0], local0);
                adjacency.Add((root0, root1, p, q));
            }
            return adjacency;
        }

        private static void AddEdgeOwner(
            Dictionary<(long, long, long, long), List<(int, int)>> edgeOwners, int root, int local, double2 a, double2 b)
        {
            var key = EdgeKey(a, b);
            if (!edgeOwners.TryGetValue(key, out var owners)) { owners = new List<(int, int)>(2); edgeOwners[key] = owners; }
            owners.Add((root, local));
        }

        private static (long, long, long, long) EdgeKey(double2 a, double2 b)
        {
            long ax = (long)math.round(a.x * 1e6), ay = (long)math.round(a.y * 1e6);
            long bx = (long)math.round(b.x * 1e6), by = (long)math.round(b.y * 1e6);
            if (ax > bx || (ax == bx && ay > by))
            {
                (ax, bx) = (bx, ax);
                (ay, by) = (by, ay);
            }
            return (ax, ay, bx, by);
        }

        private static double2 LocalEdgeStart(RootTri t, int local) => local == 0 ? t.A : local == 1 ? t.B : t.C;
        private static double2 LocalEdgeEnd(RootTri t, int local) => local == 0 ? t.B : local == 1 ? t.C : t.A;

        // -----------------------------------------------------------------------------------------------
        // Gap / T-junction analysis (cases (a) and (b), same shared-segment primitive).
        // -----------------------------------------------------------------------------------------------

        private static readonly List<LeafRef> EmptyLeaves = new List<LeafRef>();

        private static void AnalyzeGaps(
            List<RootTri> roots, List<LeafRef> leaves, List<(int i, int j, double2 P, double2 Q)> rootAdjacency,
            out int tjCount, out double maxGap, out double meanGap, out int[] gapBuckets, out double2 worstAt)
        {
            tjCount = 0;
            double sumGap = 0;
            maxGap = 0;
            worstAt = default;
            gapBuckets = new int[6];

            // A near-zero-area root is a hole-stitching BRIDGE slit that paints nothing, so its T-junctions are
            // phantom. Excluded here as in AnalyzeTriangleQuality: an earcut concern, not a subdivision crack.
            var realRoot = new bool[roots.Count];
            for (int r = 0; r < roots.Count; r++)
                realRoot[r] = math.abs(SignedArea2(roots[r].A, roots[r].B, roots[r].C)) >= 1.0; // integer-vertex ⇒ real ⇒ |area2|>=1

            var byRoot = new Dictionary<int, List<LeafRef>>();
            foreach (var lv in leaves)
            {
                if (lv.RootIndex < realRoot.Length && !realRoot[lv.RootIndex]) continue; // skip bridge-slit descendants
                if (!byRoot.TryGetValue(lv.RootIndex, out var list)) { list = new List<LeafRef>(); byRoot[lv.RootIndex] = list; }
                list.Add(lv);
            }

            // (a) cross-parent, along a shared original earcut edge. minInteriorDepth = 0 — a genuine split-
            // inserted point on this edge is strictly deeper than the (depth-0) root.
            foreach (var (i, j, p, q) in rootAdjacency)
            {
                if (i >= realRoot.Length || j >= realRoot.Length || !realRoot[i] || !realRoot[j]) continue; // skip bridge-slit edges
                var sideA = byRoot.TryGetValue(i, out var la) ? la : EmptyLeaves;
                var sideB = byRoot.TryGetValue(j, out var lb) ? lb : EmptyLeaves;
                CheckSegment(p, q, 0, sideA, sideB, ref tjCount, ref sumGap, ref maxGap, ref worstAt, gapBuckets);
            }

            // (b) intra-parent: a crack exists where another leaf of the SAME root has a vertex strictly on this
            // leaf's edge at a STRICTLY DEEPER depth. CheckSegment runs with "mine" = the edge's 2 endpoints.
            foreach (var rootLeaves in byRoot.Values)
            {
                for (int i = 0; i + 2 < rootLeaves.Count; i += 3)
                {
                    LeafRef a = rootLeaves[i], b = rootLeaves[i + 1], c = rootLeaves[i + 2];
                    int leafDepth = a.Depth; // shared by all 3 vertices of one emitted leaf triangle
                    CheckSegment(a.Tile, b.Tile, leafDepth, new List<LeafRef> { a, b }, rootLeaves, ref tjCount, ref sumGap, ref maxGap, ref worstAt, gapBuckets);
                    CheckSegment(b.Tile, c.Tile, leafDepth, new List<LeafRef> { b, c }, rootLeaves, ref tjCount, ref sumGap, ref maxGap, ref worstAt, gapBuckets);
                    CheckSegment(c.Tile, a.Tile, leafDepth, new List<LeafRef> { c, a }, rootLeaves, ref tjCount, ref sumGap, ref maxGap, ref worstAt, gapBuckets);
                }
            }

            meanGap = tjCount > 0 ? sumGap / tjCount : 0;
        }

        /// <summary>T-junction check along ONE shared tile-space segment [P,Q]: gather each side's leaf
        /// vertices lying exactly on the segment (collinear, scale-relative tolerance), and flag any point
        /// on one side that is strictly interior to the OTHER side's straight span (i.e. the other side left
        /// that stretch whole). Render-space gap = distance from the inserted point's world position to the
        /// closest point on the other side's render-space sub-edge, clamped to [0,1] (not the infinite line).</summary>
        private static void CheckSegment(
            double2 p, double2 q, int minInteriorDepth, List<LeafRef> sideA, List<LeafRef> sideB,
            ref int tjCount, ref double sumGap, ref double maxGap, ref double2 worstAt, int[] gapBuckets)
        {
            double2 d = q - p;
            double len2 = math.dot(d, d);
            if (len2 < 1e-18) return;
            double len = math.sqrt(len2);
            double collinearTol = 1e-6 * len;

            var onA = PointsOnSegment(p, d, len2, collinearTol, minInteriorDepth, sideA);
            var onB = PointsOnSegment(p, d, len2, collinearTol, minInteriorDepth, sideB);
            if (onA.Count == 0 || onB.Count == 0) return;

            double sTol = collinearTol / len;
            CheckOneSide(onA, onB, sTol, ref tjCount, ref sumGap, ref maxGap, ref worstAt, gapBuckets);
            CheckOneSide(onB, onA, sTol, ref tjCount, ref sumGap, ref maxGap, ref worstAt, gapBuckets);
        }

        private static List<(double s, double2 tile, double3 world)> PointsOnSegment(
            double2 p, double2 d, double len2, double collinearTol, int minInteriorDepth, List<LeafRef> side)
        {
            var result = new List<(double, double2, double3)>();
            var seen = new HashSet<(double, double)>();
            foreach (var lv in side)
            {
                double2 m = lv.Tile;
                double s = math.dot(m - p, d) / len2;
                if (s < -1e-6 || s > 1.0 + 1e-6) continue;
                double sClamped = math.clamp(s, 0.0, 1.0);

                // Non-obvious why: an unrelated corner can be near-collinear by chance, so only a point deeper
                // than the segment's owner (Depth > minInteriorDepth) or an endpoint may register.
                bool isEndpoint = sClamped < 1e-9 || sClamped > 1.0 - 1e-9;
                if (!isEndpoint && lv.Depth <= minInteriorDepth) continue;

                double2 foot = p + sClamped * d;
                if (math.length(m - foot) > collinearTol) continue;

                var key = (math.round(m.x * 1e6), math.round(m.y * 1e6));
                if (!seen.Add(key)) continue;
                result.Add((sClamped, m, lv.World));
            }
            result.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return result;
        }

        private static void CheckOneSide(
            List<(double s, double2 tile, double3 world)> mine, List<(double s, double2 tile, double3 world)> other,
            double sTol, ref int tjCount, ref double sumGap, ref double maxGap, ref double2 worstAt, int[] gapBuckets)
        {
            foreach (var point in mine)
            {
                bool matched = false;
                foreach (var o in other)
                    if (math.abs(o.s - point.s) <= sTol) { matched = true; break; }
                if (matched) continue;

                // Endpoints are always shared corner vertices in a correct mirror; skip defensively.
                if (point.s <= sTol || point.s >= 1.0 - sTol) continue;

                bool haveLo = false, haveHi = false;
                double loS = double.NegativeInfinity, hiS = double.PositiveInfinity;
                double3 loWorld = default, hiWorld = default;
                foreach (var o in other)
                {
                    if (o.s < point.s && o.s > loS) { loS = o.s; loWorld = o.world; haveLo = true; }
                    if (o.s > point.s && o.s < hiS) { hiS = o.s; hiWorld = o.world; haveHi = true; }
                }
                if (!haveLo || !haveHi) continue; // no bracket — nothing to be interior to

                double3 segDir = hiWorld - loWorld;
                double segLen2 = math.dot(segDir, segDir);
                double t = segLen2 > 1e-18 ? math.dot(point.world - loWorld, segDir) / segLen2 : 0.0;
                t = math.clamp(t, 0.0, 1.0);
                double3 foot = loWorld + t * segDir;
                double gap = math.length(point.world - foot);

                tjCount++;
                sumGap += gap;
                if (gap > maxGap) { maxGap = gap; worstAt = point.tile; }
                int bucket = gap < 1 ? 0 : gap < 10 ? 1 : gap < 100 ? 2 : gap < 1000 ? 3 : gap < 10000 ? 4 : 5;
                gapBuckets[bucket]++;
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Degenerate / flipped sub-triangles.
        // -----------------------------------------------------------------------------------------------

        private static void AnalyzeTriangleQuality(List<RootTri> roots, List<LeafRef> leaves, out int degenerateTris, out int flippedTris)
        {
            degenerateTris = 0;
            flippedTris = 0;
            var rootArea2 = new double[roots.Count];
            var rootSign = new int[roots.Count];
            for (int r = 0; r < roots.Count; r++)
            {
                double area2 = SignedArea2(roots[r].A, roots[r].B, roots[r].C);
                rootArea2[r] = math.abs(area2);
                rootSign[r] = area2 > 0 ? 1 : area2 < 0 ? -1 : 0;
            }

            for (int i = 0; i + 2 < leaves.Count; i += 3)
            {
                LeafRef a = leaves[i], b = leaves[i + 1], c = leaves[i + 2];
                double parentArea2 = rootArea2[a.RootIndex];

                // Count only NEW degeneracy/flips: skip roots already degenerate (earcut bridge slits). Integer
                // ring vertices give any real root |area2| >= 1, so the absolute 1e-6 cutoff catches only slits.
                if (parentArea2 < 1e-6) continue;

                double area2 = SignedArea2(a.Tile, b.Tile, c.Tile);
                if (math.abs(area2) < 1e-9 * parentArea2) { degenerateTris++; continue; }
                int sign = area2 > 0 ? 1 : -1;
                if (rootSign[a.RootIndex] != 0 && sign != rootSign[a.RootIndex]) flippedTris++;
            }
        }

        // Coverage error is PER-ROOT: a global total lets a dropped region cancel a duplicated one. ~zero
        // roots (bridge slits) are skipped; they have no meaningful relative error.
        private static double ComputeMaxLeafEdgeAngleRad(List<RootTri> roots, List<LeafRef> leaves)
        {
            // Curvature fidelity is a property of RENDERED geometry, so zero-area bridge slits are skipped,
            // as in the gap, coverage and degenerate checks.
            var realRoot = new bool[roots.Count];
            for (int r = 0; r < roots.Count; r++)
                realRoot[r] = math.abs(SignedArea2(roots[r].A, roots[r].B, roots[r].C)) >= 1.0;

            double maxAngle = 0;
            for (int i = 0; i + 2 < leaves.Count; i += 3)
            {
                int root = leaves[i].RootIndex;
                if (root < realRoot.Length && !realRoot[root]) continue;
                // Skip NEEDLE slivers (thinness = area2/longestEdge² < 0.03, ~30:1): subdivision cannot fix their
                // angle and they paint ~nothing. Well-formed leaves (~0.3+) still trip the gate.
                double2 ta = leaves[i].Tile, tb = leaves[i + 1].Tile, tc = leaves[i + 2].Tile;
                double longestSq = math.max(DistSq(ta, tb), math.max(DistSq(tb, tc), DistSq(tc, ta)));
                double area2 = math.abs(SignedArea2(ta, tb, tc));
                if (longestSq > 1e-9 && area2 / longestSq < 0.03) continue; // needle — not a fidelity signal
                maxAngle = math.max(maxAngle, EdgeAngle(leaves[i].Up, leaves[i + 1].Up));
                maxAngle = math.max(maxAngle, EdgeAngle(leaves[i + 1].Up, leaves[i + 2].Up));
                maxAngle = math.max(maxAngle, EdgeAngle(leaves[i + 2].Up, leaves[i].Up));
            }
            return maxAngle;
        }

        private static double EdgeAngle(double3 up0, double3 up1) => math.acos(math.clamp(math.dot(up0, up1), -1.0, 1.0));

        private static double ComputeCoverageAreaRelError(List<RootTri> roots, List<LeafRef> leaves)
        {
            var subAreaByRoot = new double[roots.Count];
            for (int i = 0; i + 2 < leaves.Count; i += 3)
            {
                int root = leaves[i].RootIndex;
                subAreaByRoot[root] += math.abs(SignedArea2(leaves[i].Tile, leaves[i + 1].Tile, leaves[i + 2].Tile)) * 0.5;
            }

            double maxRelError = 0;
            for (int r = 0; r < roots.Count; r++)
            {
                double rootArea = math.abs(SignedArea2(roots[r].A, roots[r].B, roots[r].C)) * 0.5;
                if (rootArea <= 0.5) continue; // ~zero-area earcut bridge slit — no meaningful relative error
                maxRelError = math.max(maxRelError, math.abs(subAreaByRoot[r] - rootArea) / rootArea);
            }
            return maxRelError;
        }

        private static double SignedArea2(double2 a, double2 b, double2 c)
            => (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);

        // -----------------------------------------------------------------------------------------------
        // Tile render-space span — the MaxGapFracTile normalizer. Uses the DIAGONAL (max pairwise corner
        // distance), not the top edge: a z0 globe tile's top edge collapses to ~0 at the ±180° seam.
        // -----------------------------------------------------------------------------------------------

        private static double ComputeTileSpanMeters(in TileId id, IProjection projection, double extent)
        {
            var tileCorners = new[]
            {
                new double2(0, 0), new double2(extent, 0), new double2(extent, extent), new double2(0, extent),
            };
            var worldCorners = new double3[4];
            for (int k = 0; k < 4; k++) worldCorners[k] = Project(tileCorners[k], id, projection, extent).World;

            double maxSpan = 0;
            for (int a = 0; a < 4; a++)
                for (int b = a + 1; b < 4; b++)
                    maxSpan = math.max(maxSpan, math.length(worldCorners[a] - worldCorners[b]));

            if (maxSpan <= 1.0)
                throw new InvalidOperationException(
                    $"tile render-space span too small ({maxSpan}m) — MaxGapFracTile is calibrated for a z6-scale " +
                    "tile; a near-degenerate tile needs a different metric (out of scope, see the plan's scope fence).");
            return maxSpan;
        }
    }
}
