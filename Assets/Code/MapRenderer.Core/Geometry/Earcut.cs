using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Clean-room ear-clipping polygon triangulator. Implements the ear-clipping technique from
    /// first principles: a vertex is an "ear" if it is convex and the candidate triangle contains
    /// no reflex vertex. Holes are bridged into the outer ring by finding a non-crossing, locally-
    /// inside visible vertex pair (<see cref="FindBridgeVertex"/>, guarded by <see cref="LocallyInside"/>
    /// and the <see cref="SectorContainsSector"/> tiebreak) and splicing duplicate vertices.
    ///
    /// API: call Triangulate() to get both the flat vertex array (in the internal order Earcut
    /// uses, which includes bridge-duplicate verts) and the triangle indices into that array.
    /// This avoids any out-of-band reconstruction of the flat vert list.
    ///
    /// Winding normalisation: outer ring is normalised to CCW-on-screen (negative shoelace in
    /// Y-down tile space); holes to CW-on-screen (positive shoelace). This is done internally;
    /// callers need not pre-arrange winding.
    /// OUTPUT WINDING: triangles are emitted CCW — the pipeline's single canonical winding. It is reversed
    /// once to Unity-front at the mesh-write boundary (`StyledFillTileBuilder`) for stock Cull Back; the
    /// producer never bakes the render convention. See `docs/coordinates-and-projections.md` §7.1.
    ///
    /// Stall cascade (mesh-triangulation-robustness §5, direction W): on a full pass with no ear,
    /// a first stall does a full ear-status refresh (unchanged — catches stale isEar values). A
    /// second stall on the SAME ring invokes a mapbox-style failure cascade that never emits an
    /// overlapping/inverted triangle: CureLocalIntersections resolves a local bridging-seam bowtie;
    /// failing that, SplitPolygon finds a valid diagonal and recurses the ear loop on the two
    /// independent halves; failing that, the locus is dropped cleanly (§3.1) — no triangle is ever
    /// folded. The bridge selection is validate-and-fallback provably non-crossing (see the hole
    /// loop and LocallyInside), so on clean input the merged ring stays simple and the cascade never
    /// fires — it is a genuinely-degenerate-input escape only, never the success path. Splits are
    /// bounded (MaxSplits) and their per-attempt search is bounded (scanBudget).
    /// Degenerate rings (|area| below threshold) are pre-skipped in PolygonAssembler.
    /// </summary>
    public static class Earcut
    {
        /// <summary>
        /// Result of a triangulation: the flat vertex array (internal order, including bridge
        /// duplicates), the triangle indices into that array, and diagnostic counts.
        /// </summary>
        public readonly struct Result
        {
            public readonly double2[] Vertices;  // flat vertex array in Earcut's internal order
            public readonly int[] Indices;       // triangle indices into Vertices
            /// <summary>
            /// Number of loci the stall cascade could not resolve and dropped cleanly (never a
            /// fold — see the class doc's stall-cascade note). A non-zero value means the cure →
            /// split → drop cascade exhausted every non-folding option for at least one
            /// locus; the input ring is likely genuinely self-intersecting or degenerate there, and
            /// that locus is simply missing from the output rather than filled with garbage.
            /// Exposed via return value to keep Core free of UnityEngine logging.
            /// </summary>
            public readonly int ForceClips;

            public Result(double2[] vertices, int[] indices, int forceClips = 0)
            {
                Vertices = vertices;
                Indices = indices;
                ForceClips = forceClips;
            }
        }

        /// <summary>
        /// Triangulate a polygon with optional holes. Returns both the flat vertex array
        /// (in the internal bridge-merged order) and the triangle indices.
        /// </summary>
        public static Result Triangulate(List<double2> outer, List<List<double2>> holes)
        {
            if (outer == null || outer.Count < 3)
                return new Result(Array.Empty<double2>(), Array.Empty<int>(), 0);

            int outerCount = outer.Count;

            // Sort valid holes by (leftmost-x, min-y, original-hole-index) for fully-deterministic
            // bridging order. The three-level sort matches the jobified pipeline's Array.Sort in
            // FillMeshPipeline (which also sorts by leftmost-x then min-y then ring-index),
            // guaranteeing bit-identical output between the managed and Burst paths.
            // List.Sort is unstable; without the tiebreaks, equal-leftmost-x holes produce
            // non-deterministic ordering that breaks the parity hash tests.
            var validHoles = new List<List<double2>>();
            if (holes != null)
                foreach (var h in holes)
                    if (h != null && h.Count >= 3) validHoles.Add(h);
            var holeOrigIdx = new int[validHoles.Count];
            for (int hi = 0; hi < validHoles.Count; hi++) holeOrigIdx[hi] = hi;
            Array.Sort(holeOrigIdx, (ia, ib) =>
            {
                int cmp = LeftmostX(validHoles[ia]).CompareTo(LeftmostX(validHoles[ib]));
                if (cmp != 0) return cmp;
                cmp = MinY(validHoles[ia]).CompareTo(MinY(validHoles[ib]));
                if (cmp != 0) return cmp;
                return ia.CompareTo(ib);  // hole index tiebreak for total order
            });
            var sortedHoles = new List<List<double2>>(validHoles.Count);
            foreach (int hi in holeOrigIdx) sortedHoles.Add(validHoles[hi]);
            validHoles = sortedHoles;

            // Capacity: outer + each hole + 2 bridge verts per hole.
            int capacity = outerCount;
            foreach (var h in validHoles) capacity += h.Count + 2;

            var vx            = new double[capacity];
            var vy            = new double[capacity];
            var prev          = new int[capacity];
            var next          = new int[capacity];
            // isBridgeCopy[i] = true for the two duplicate vertices added per hole to close the
            // seam (copyHoleLM and copyOuter). Bridge copies are position-duplicates of real ring
            // vertices; they must not block ear detection for other vertices because their spatial
            // position is already covered by the original vertex in the point-in-triangle test.
            var isBridgeCopy  = new bool[capacity];
            int total = 0;

            // ---- Insert outer ring, normalised to CCW-on-screen (area2 < 0 in Y-down). ----
            double outerArea2 = ComputeArea2List(outer);
            bool reverseOuter = outerArea2 > 0.0; // positive = CW on screen → reverse to get CCW
            for (int i = 0; i < outerCount; i++)
            {
                int src = reverseOuter ? (outerCount - 1 - i) : i;
                vx[total] = outer[src].x;
                vy[total] = outer[src].y;
                prev[total] = total - 1;
                next[total] = total + 1;
                total++;
            }
            prev[0] = outerCount - 1;
            next[outerCount - 1] = 0;
            int mergedRingStart = 0; // start of the ring that holes get spliced into (always 0)

            // ---- Bridge each hole into the outer (merged) ring. ----
            int mergedRingCount = outerCount; // grows as we splice

            foreach (var hole in validHoles)
            {
                int holeStart = total;
                int holeCount = hole.Count;

                // Normalise hole to CW-on-screen (area2 > 0 in Y-down).
                double holeArea2 = ComputeArea2List(hole);
                bool reverseHole = holeArea2 < 0.0; // negative = CCW on screen → reverse to CW
                for (int i = 0; i < holeCount; i++)
                {
                    int src = reverseHole ? (holeCount - 1 - i) : i;
                    vx[total] = hole[src].x;
                    vy[total] = hole[src].y;
                    prev[total] = total - 1;
                    next[total] = total + 1;
                    total++;
                }
                prev[holeStart] = total - 1;
                next[total - 1] = holeStart;

                // Find the hole's leftmost vertex.
                int holeLM = HoleLeftmostIndex(vx, vy, holeStart, holeCount);

                // Find the outer-ring vertex to bridge to (leftward-ray heuristic + reflex refinement).
                int outerBridge = FindBridgeVertex(vx, vy, next, prev, holeLM, mergedRingStart, mergedRingCount);

                // Provably-non-crossing guarantee. The heuristic above picks a good bridge on the common
                // case but is NOT guaranteed non-crossing for a concave outer (its leftward-ray endpoint
                // pick can land "around" a reflex notch). A crossing bridge makes the spliced merged ring
                // self-intersecting → a reversed residual pocket → the ear loop clips a triangle OUTSIDE
                // the polygon (silent overlap, ForceClips still 0). So VALIDATE the chosen bridge against
                // BOTH the merged ring AND this hole's own ring; if it crosses either (or isn't locally
                // inside), REPLACE it with the nearest vertex whose bridge is provably clear. This keeps
                // the merged ring simple throughout, so no reversed residual can arise on clean input.
                // The heuristic result is kept whenever it is already valid ⇒ clean cases stay byte-
                // identical; only genuinely-crossing bridges change.
                bool BridgeValid(int cand)
                    => LocallyInside(vx, vy, prev, next, cand, holeLM)
                       && !BridgeCrossesRing(vx, vy, next, holeLM, cand, mergedRingStart, mergedRingCount)
                       && !BridgeCrossesRing(vx, vy, next, holeLM, cand, holeStart, holeCount);

                if (!BridgeValid(outerBridge))
                {
                    int bestCand = -1;
                    double bestDist = double.MaxValue;
                    int scan = mergedRingStart;
                    for (int i = 0; i < mergedRingCount; i++)
                    {
                        if (BridgeValid(scan))
                        {
                            double bdx = vx[scan] - vx[holeLM], bdy = vy[scan] - vy[holeLM];
                            double bd  = bdx * bdx + bdy * bdy;
                            if (bd < bestDist) { bestDist = bd; bestCand = scan; }
                        }
                        scan = next[scan];
                    }
                    if (bestCand >= 0) outerBridge = bestCand; // else: dirty input; cure/split/drop backstop
                }

                // Reserve 2 bridge-copy slots:
                //   copyHoleLM  — a copy of holeLM inserted at the END of the hole traversal
                //   copyOuter   — a copy of outerBridge inserted after copyHoleLM
                // This creates a symmetric slit: outerBridge→holeLM (forward) and
                // copyHoleLM→copyOuter (backward along the same line), so the seam contributes
                // zero net area to the merged ring.
                int copyHoleLM = total;
                int copyOuter  = total + 1;
                total += 2;

                if (total > capacity)
                    throw new InvalidOperationException($"Earcut capacity overflow (needed {total}, had {capacity}).");

                vx[copyHoleLM] = vx[holeLM];
                vy[copyHoleLM] = vy[holeLM];
                vx[copyOuter]  = vx[outerBridge];
                vy[copyOuter]  = vy[outerBridge];
                isBridgeCopy[copyHoleLM] = true;
                isBridgeCopy[copyOuter]  = true;

                // Splice: outerBridge → holeLM → [hole ring] → holePrevLM → copyHoleLM → copyOuter → outerNext
                int outerNext = next[outerBridge];

                // holePrevLM = last vertex before holeLM in the hole ring (for the exit seam).
                int holePrevLM = prev[holeLM]; // capture before we relink

                // outerBridge → holeLM (entering the hole ring — forward seam)
                next[outerBridge] = holeLM;
                prev[holeLM]      = outerBridge;

                // holePrevLM → copyHoleLM (exiting the hole ring)
                next[holePrevLM] = copyHoleLM;
                prev[copyHoleLM] = holePrevLM;

                // copyHoleLM → copyOuter (backward seam — symmetric with outerBridge→holeLM)
                next[copyHoleLM] = copyOuter;
                prev[copyOuter]  = copyHoleLM;

                // copyOuter → outerNext (continues outer ring)
                next[copyOuter] = outerNext;
                prev[outerNext] = copyOuter;

                // The merged ring now has outerCount + holeCount + 2 vertices.
                mergedRingCount = total; // all slots up to here form the merged ring
            }

            // ---- Ear-clipping loop, with a cure → split → retry failure cascade on stall. ----
            //
            // The old stall guard used to force-clip a non-ear vertex to escape a stall — that emits
            // an overlapping/inverted triangle (a fold) whenever bridging tangled the merged ring
            // (design §2 root cause). It is replaced by a mapbox-style cascade that NEVER folds:
            //   1. CureLocalIntersections — resolve a local self-touching bowtie (the seam-crossing
            //      pattern bridging creates) by cutting the one valid triangle and splicing the two
            //      offending vertices out; then retry the ear loop.
            //   2. SplitPolygon + retry — if curing can't unstick it, find a valid diagonal between
            //      two non-adjacent live vertices, split the ring into two independent rings, and
            //      recurse the ear loop on each half.
            //   3. If neither can make progress on a genuinely degenerate locus, drop that locus
            //      cleanly (§3.1) — emit nothing for it. `forceClipCount` now counts only these
            //      clean drops (0 on the clean corpus); it is never incremented for a fold.
            var indices = new List<int>(math.max(0, (total - 2) * 3));
            var removed = new bool[total];
            var isEar   = new bool[total];
            int forceClipCount = 0;

            // Bounding-box index over the merged ring — answer-preserving per
            // docs/mesh-triangulation-robustness-design.md §2.1/§6.1 (Stage 1's invariant).
            var grid = BuildEarGrid(vx, vy, total);

            // capacity currently == total (set by the bridging phase above); EnsureCapacity grows
            // every parallel array together (grow-on-demand, Edit 3) the rare times SplitPolygon
            // needs a fresh slot. Splits are a failure-path escape only — bounded below.
            int splitsUsed = 0;
            const int MaxSplits = 512; // finite ceiling (lessons: never an unbounded data-derived loop)

            void EnsureCapacity(int needed)
            {
                if (needed <= capacity) return;
                int newCap = math.max(needed, capacity * 2);
                Array.Resize(ref vx, newCap);
                Array.Resize(ref vy, newCap);
                Array.Resize(ref prev, newCap);
                Array.Resize(ref next, newCap);
                Array.Resize(ref removed, newCap);
                Array.Resize(ref isBridgeCopy, newCap);
                Array.Resize(ref isEar, newCap);
                capacity = newCap;
            }

            // mapbox splitPolygon: duplicate a and b into two fresh vertices (a2, b2) and rewire so
            // the ring splits into two independent cycles — [a → b → … → a] and [a2 → … → b2 → a2].
            // Split-added vertices are real ring vertices (isBridgeCopy = false), unlike the
            // zero-width bridge-seam copies, so they participate fully in point-in-triangle tests.
            int SplitPolygon(int a, int b)
            {
                EnsureCapacity(total + 2);
                int a2 = total++;
                int b2 = total++;

                vx[a2] = vx[a]; vy[a2] = vy[a];
                vx[b2] = vx[b]; vy[b2] = vy[b];
                isBridgeCopy[a2] = false;
                isBridgeCopy[b2] = false;
                removed[a2] = false;
                removed[b2] = false;
                // Split-added vertices postdate the grid build; they go to its overflow list instead
                // (ponytail: O(splits) per call — a CSR rebuild is the upgrade path; see EarGrid's doc).
                grid.Overflow.Add(a2);
                grid.Overflow.Add(b2);

                int an = next[a];
                int bp = prev[b];

                next[a] = b;  prev[b] = a;
                next[a2] = an; prev[an] = a2;
                next[b2] = a2; prev[a2] = b2;
                next[bp] = b2; prev[b2] = bp;

                return b2;
            }

            // mapbox cureLocalIntersections: at vertex p, edges (a=prev[p]→p) and (next[p]→b=next[next[p]])
            // may cross (the seam-touching pattern bridging creates). If so — and the shortcut a→b is
            // locally valid on both ends — cut triangle (a, p, b), splice p and next[p] out of the ring,
            // and continue. Bounded by the ring's own live-vertex count; never emits a crossing triangle.
            bool CureLocalIntersections(ref int start, ref int remaining)
            {
                bool curedAny = false;
                int p = start;
                int guard = 0;
                int maxIter = remaining + 8;
                do
                {
                    if (removed[p]) { p = next[p]; guard++; continue; }
                    int a  = prev[p];
                    int pn = next[p];
                    int b  = next[pn];

                    if (a != b && a != p && pn != p && !removed[a] && !removed[pn] && !removed[b] &&
                        !(vx[a] == vx[b] && vy[a] == vy[b]) &&
                        Intersects(vx, vy, a, p, pn, b) &&
                        LocallyInside(vx, vy, prev, next, a, b) &&
                        LocallyInside(vx, vy, prev, next, b, a))
                    {
                        indices.Add(a);
                        indices.Add(p);
                        indices.Add(b);

                        removed[p]  = true;
                        removed[pn] = true;
                        next[a] = b;
                        prev[b] = a;
                        remaining -= 2;
                        curedAny = true;

                        isEar[a] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, a);
                        isEar[b] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, b);

                        start = b;
                        p = b;
                        if (remaining <= 3) break;
                    }
                    p = next[p];
                    guard++;
                } while (p != start && guard < maxIter);

                return curedAny;
            }

            // mapbox isValidDiagonal: a candidate split diagonal (a, b) must not be an existing edge,
            // must not cross any other edge of the ring, must be locally inside at BOTH endpoints, and
            // its midpoint must fall inside the ring (guards against a diagonal that tunnels through a
            // reflex notch between two edges without technically crossing either one).
            bool IsValidDiagonal(int a, int b, int ringGuardBound)
            {
                if (a == b || next[a] == b || prev[a] == b || removed[a] || removed[b]) return false;
                if (IntersectsRing(a, b, ringGuardBound)) return false;
                if (!LocallyInside(vx, vy, prev, next, a, b)) return false;
                if (!LocallyInside(vx, vy, prev, next, b, a)) return false;
                if (!MiddleInside(a, b, ringGuardBound)) return false;
                return true;
            }

            bool IntersectsRing(int a, int b, int ringGuardBound)
            {
                int p = a;
                int guard = 0;
                do
                {
                    if (!removed[p])
                    {
                        int q = next[p];
                        if (!removed[q] && p != a && p != b && q != a && q != b &&
                            Intersects(vx, vy, p, q, a, b))
                            return true;
                    }
                    p = next[p];
                    guard++;
                } while (p != a && guard < ringGuardBound);
                return false;
            }

            bool MiddleInside(int a, int b, int ringGuardBound)
            {
                double mx = (vx[a] + vx[b]) * 0.5;
                double my = (vy[a] + vy[b]) * 0.5;
                bool inside = false;
                int p = a;
                int guard = 0;
                do
                {
                    if (!removed[p])
                    {
                        int q = next[p];
                        double px = vx[p], py = vy[p], qx = vx[q], qy = vy[q];
                        if ((py > my) != (qy > my))
                        {
                            double ix = px + (my - py) / (qy - py) * (qx - px);
                            if (ix > mx) inside = !inside;
                        }
                    }
                    p = next[p];
                    guard++;
                } while (p != a && guard < ringGuardBound);
                return inside;
            }

            // mapbox splitEarcut: scan candidate pairs (a, b) for the first valid diagonal, split on
            // it, and recurse the ear loop on both halves. O(n²) candidate pairs on the STUCK ring
            // only (a failure-path escape, never the success path) — bounded so a pathological locus
            // degrades to a clean drop instead of hanging (lessons: always bound data-derived loops).
            bool SplitAndRetry(int start, int remaining)
            {
                if (splitsUsed >= MaxSplits) return false;
                int ringGuardBound = remaining + 8;
                int scanBudget = math.min(remaining, 400); // bounded candidate-pair search width

                int a = start;
                for (int ai = 0; ai < scanBudget; ai++)
                {
                    if (!removed[a])
                    {
                        int b = next[next[a]];
                        for (int bi = 0; bi < scanBudget && b != prev[a]; bi++)
                        {
                            if (!removed[b] && a != b && IsValidDiagonal(a, b, ringGuardBound))
                            {
                                splitsUsed++;
                                int c = SplitPolygon(a, b);

                                isEar[a] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, a);
                                isEar[b] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, b);
                                isEar[c] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, c);
                                int a2 = prev[c];
                                isEar[a2] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, a2);

                                int remA = CountRing(a, remaining + 8);
                                int remC = CountRing(c, remaining + 8);
                                EarClipRing(a, remA);
                                EarClipRing(c, remC);
                                return true;
                            }
                            b = next[b];
                        }
                    }
                    a = next[a];
                }
                return false;
            }

            int CountRing(int start, int guardBound)
            {
                int count = 0;
                int p = start;
                int guard = 0;
                do
                {
                    if (!removed[p]) count++;
                    p = next[p];
                    guard++;
                } while (p != start && guard < guardBound);
                return count;
            }

            // Ear-clip exactly one ring (walked via next/prev from `start`) to completion: normal
            // ear removal, the existing first-stall full ear-status refresh, then — on a second stall
            // — the cure → split → drop cascade. Recurses (via SplitAndRetry) for split-derived rings.
            // No reversed-residual handling is needed here: the bridge selection above is provably
            // non-crossing (see the hole loop's validate-and-fallback), so the merged ring stays simple
            // and CCW-on-screen throughout, and split/cure preserve orientation — every ring reaching
            // this function is already CCW. (A genuinely dirty, self-intersecting input can still stall
            // past cure+split; that locus is dropped cleanly per design §3.1, never folded.)
            void EarClipRing(int start, int remaining)
            {
                if (remaining < 3) return;

                // Refresh ear status for this ring's own live vertices on entry. For the top-level
                // (unsplit) ring this recomputes values already correct from construction — no
                // observable change; for a split/cure-derived ring it is the correctness-necessary
                // refresh around the newly rewired seam.
                {
                    int p = start, guard = 0, bound = remaining + 8;
                    do
                    {
                        if (!removed[p]) isEar[p] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, p);
                        p = next[p];
                        guard++;
                    } while (p != start && guard < bound);
                }

                while (remaining > 3)
                {
                    if (removed[start])
                    {
                        int s = start, guard = 0;
                        while (removed[s] && guard < total + 8) { s = next[s]; guard++; }
                        start = s;
                    }

                    int stallLimit = remaining;
                    int stallIter  = 0;
                    bool clippedAny = false;
                    bool didFullRefresh = false;

                    int v = start;
                    for (int iter = 0; iter < remaining * 4 && remaining > 3; iter++)
                    {
                        if (removed[v]) { v = next[v]; continue; }

                        if (isEar[v])
                        {
                            int p = prev[v], n = next[v];
                            indices.Add(p);
                            indices.Add(v);
                            indices.Add(n);

                            next[p] = n;
                            prev[n] = p;
                            removed[v] = true;
                            remaining--;
                            clippedAny = true;
                            stallIter = 0;
                            didFullRefresh = false;

                            isEar[p] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, p);
                            isEar[n] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, n);

                            if (start == v) start = n;
                            v = n;
                        }
                        else
                        {
                            v = next[v];
                            stallIter++;
                            if (stallIter > stallLimit)
                            {
                                if (!didFullRefresh)
                                {
                                    // First stall: full refresh of this ring's remaining ear statuses
                                    // (unchanged escape from the original — catches stale isEar values
                                    // from a distant ear removal).
                                    int p2 = v, guard2 = 0, bound2 = remaining + 8;
                                    do
                                    {
                                        if (!removed[p2])
                                            isEar[p2] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, grid, p2);
                                        p2 = next[p2];
                                        guard2++;
                                    } while (p2 != v && guard2 < bound2);
                                    stallIter = 0;
                                    didFullRefresh = true;
                                    stallLimit = remaining;
                                }
                                else
                                {
                                    // Second stall: cascade. Never fold.
                                    int cureStart = v;
                                    if (CureLocalIntersections(ref cureStart, ref remaining))
                                    {
                                        start = cureStart;
                                        v = cureStart;
                                        stallIter = 0;
                                        didFullRefresh = false;
                                        clippedAny = true;
                                        if (remaining <= 3) break;
                                        continue;
                                    }

                                    if (SplitAndRetry(v, remaining))
                                    {
                                        remaining = 0; // handed off to the two recursive halves
                                        clippedAny = true;
                                        break;
                                    }

                                    // Genuinely stuck: neither cure nor split could make progress
                                    // (only reachable on a self-intersecting/degenerate input, since
                                    // the non-crossing bridge keeps clean input's merged ring simple).
                                    // Drop this locus cleanly — no triangle folds. ForceClips counts
                                    // exactly these clean drops (0 on clean input), a VISIBLE signal.
                                    forceClipCount++;
                                    remaining = 0;
                                    clippedAny = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!clippedAny) break;
                }

                // Final triangle for this ring: the 3 remaining live vertices of THIS ring, listed in
                // ascending vertex-index order — matches the original single-ring algorithm's
                // tie-break (index order, not ring-walk order) so the top-level (unsplit) ring's
                // final triangle stays byte-identical. The Area2>0 swap enforces CCW-on-screen output
                // winding unconditionally: a no-op on every ring here (all are CCW — non-crossing
                // bridge + orientation-preserving split), it is a cheap correctness guarantee that the
                // emitted triangle can never be a fold even if a dirty-input split ever left a ring
                // wound the other way.
                if (remaining == 3)
                {
                    var live = new List<int>(3);
                    int p = start, guard = 0, bound = remaining + 8;
                    do
                    {
                        if (!removed[p]) live.Add(p);
                        p = next[p];
                        guard++;
                    } while (p != start && guard < bound);
                    if (live.Count >= 3)
                    {
                        live.Sort();
                        int t0 = live[0], t1 = live[1], t2 = live[2];
                        if (Area2(vx[t0], vy[t0], vx[t1], vy[t1], vx[t2], vy[t2]) > 0.0)
                            (t1, t2) = (t2, t1);
                        indices.Add(t0);
                        indices.Add(t1);
                        indices.Add(t2);
                    }
                }
            }

            EarClipRing(mergedRingStart, total);

            // Build the flat vertex array to return alongside indices (current, possibly grown total).
            var vertArray = new double2[total];
            for (int i = 0; i < total; i++)
                vertArray[i] = new double2(vx[i], vy[i]);

            return new Result(vertArray, indices.ToArray(), forceClipCount);
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Standard cross-product shoelace formula: 2A = Σ (x_i*y_{i+1} − x_{i+1}*y_i).
        /// Positive = CCW in Y-up = CW on screen in Y-down (MVT exterior).
        /// </summary>
        private static double ComputeArea2List(List<double2> ring)
        {
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        private static double LeftmostX(List<double2> ring)
        {
            double minX = double.MaxValue;
            foreach (var p in ring) if (p.x < minX) minX = p.x;
            return minX;
        }

        private static double MinY(List<double2> ring)
        {
            double minY = double.MaxValue;
            foreach (var p in ring) if (p.y < minY) minY = p.y;
            return minY;
        }

        private static int HoleLeftmostIndex(double[] vx, double[] vy, int holeStart, int holeCount)
        {
            int idx = holeStart;
            double minX = vx[holeStart];
            for (int i = 1; i < holeCount; i++)
            {
                int j = holeStart + i;
                if (vx[j] < minX || (vx[j] == minX && vy[j] < vy[idx]))
                {
                    minX = vx[j];
                    idx  = j;
                }
            }
            return idx;
        }

        /// <summary>
        /// Find the outer-ring vertex most visible from the hole's leftmost vertex (holeLM).
        ///
        /// Algorithm (canonical ear-clipping bridge, leftward-ray variant):
        ///   1. Cast a horizontal ray leftward from holeLM (toward −x). Because holeLM is the
        ///      leftmost vertex of the hole and the hole is enclosed by the outer ring, there are
        ///      always outer-ring edges to the left of holeLM.
        ///   2. Among all outer edges that straddle y = hy and whose intersection is ≤ hx, pick
        ///      the one whose intersection is farthest right (closest to holeLM). Call this point M
        ///      and let P be the vertex of the straddling edge with the greater x-coordinate.
        ///   3. Scan all live ring vertices for reflex-vertex occlusion: if any vertex Q is strictly
        ///      inside the sector triangle (holeLM, M, P), replace P with Q only if Q yields a
        ///      smaller polar angle from holeLM (i.e. the tangent |dy|/dx is smaller, meaning Q is
        ///      more "directly left" and less occluded). Ties broken by largest qx.
        ///   4. Fall back to nearest vertex by distance if no ray intersection is found.
        ///
        /// The leftward ray guarantees that the bridge from holeLM to the chosen vertex stays inside
        /// the filled region and does not cross any ring edge.
        /// </summary>
        private static int FindBridgeVertex(
            double[] vx, double[] vy, int[] next, int[] prev,
            int holeLM, int mergedRingStart, int mergedRingCount)
        {
            double hx = vx[holeLM];
            double hy = vy[holeLM];

            int    bestVert      = -1;
            double bestIntersectX = double.NegativeInfinity; // want largest ix ≤ hx (nearest to the left)

            // ── Pass 1: find nearest left-ray intersection ────────────────────────────────────
            int cur = mergedRingStart;
            for (int iter = 0; iter < mergedRingCount * 2; iter++)
            {
                int nc = next[cur];
                double ax = vx[cur], ay = vy[cur];
                double bx = vx[nc],  by = vy[nc];

                // Half-open straddle test: one endpoint strictly above, one at-or-below y=hy.
                bool straddles = (ay > hy) != (by > hy);
                if (straddles)
                {
                    double t  = (hy - ay) / (by - ay);
                    double ix = ax + t * (bx - ax);
                    // Only intersections to the LEFT of (or exactly at) holeLM.
                    if (ix <= hx && ix > bestIntersectX)
                    {
                        bestIntersectX = ix;
                        // Candidate: the edge endpoint with the GREATER x (closer to holeLM).
                        bestVert = (ax >= bx) ? cur : nc;
                    }
                }

                cur = next[cur];
                if (cur == mergedRingStart && iter > 0) break;
            }

            // ── Pass 2: reflex-vertex refinement ─────────────────────────────────────────────
            // For concave outer rings a reflex outer vertex may lie between holeLM and the
            // candidate P, meaning the direct bridge would cross an outer edge. Scan all live ring
            // vertices for any Q strictly inside the sector triangle (H=holeLM, M, P) and replace
            // P with Q if Q has a smaller polar angle from H (|qy−hy|/(hx−qx)), meaning it is
            // "more directly left." Ties broken by largest qx.
            //
            // Triangle (H, M, P): H=(hx,hy), M=(bestIntersectX,hy), P=(candX,candY).
            // Q is inside iff Sign(H,M,Q) == Sign(H,M,P) and Sign(M,P,Q) == Sign(M,P,H)
            //                                               and Sign(P,H,Q) == Sign(P,H,M).
            // (Equality of sign allows Q on the boundary.)
            if (bestVert >= 0)
            {
                double mx    = bestIntersectX; // M.x (M.y == hy)
                double candX = vx[bestVert];
                double candY = vy[bestVert];

                // S(H,M,P): H=(hx,hy), M=(mx,hy), P=(candX,candY)
                //   = (mx-hx)*(candY-hy) — note mx < hx so sign(sHMP) == -sign(candY-hy).
                double sHMP = (mx - hx) * (candY - hy);

                // When P is collinear with H and M (candY == hy), the triangle is degenerate.
                // In that case P is already the best horizontal candidate; scan only for vertices
                // strictly between M and P on the same horizontal (better x, same angle = 0).
                if (math.abs(sHMP) < 1e-10)
                {
                    // Degenerate: P is on y=hy. Pick the rightmost vertex on y=hy in (mx, hx).
                    cur = mergedRingStart;
                    for (int iter = 0; iter < mergedRingCount * 2; iter++)
                    {
                        double qx = vx[cur], qy = vy[cur];
                        if (cur != holeLM && math.abs(qy - hy) < 1e-10 && qx > candX && qx < hx)
                        {
                            bestVert = cur;
                            candX    = qx;
                        }
                        cur = next[cur];
                        if (cur == mergedRingStart && iter > 0) break;
                    }
                }
                else
                {
                    // Non-degenerate: scan for reflex vertices inside the sector triangle (H, M, P).
                    // Tangent of the polar angle from holeLM to the current candidate P.
                    double bestTan = (hx - candX) > 1e-12
                        ? math.abs(candY - hy) / (hx - candX)
                        : double.MaxValue;

                    cur = mergedRingStart;
                    for (int iter = 0; iter < mergedRingCount * 2; iter++)
                    {
                        double qx = vx[cur], qy = vy[cur];

                        // Q must be strictly inside the x-band (mx, hx) and not be holeLM itself.
                        if (qx > mx && qx < hx && cur != holeLM)
                        {
                            // Full triangle containment test for Q in (H, M, P):
                            // Signed areas (2x): S(A,B,C) = (bx-ax)*(cy-ay) - (cx-ax)*(by-ay)
                            // S(H,M,Q) = (mx-hx)*(qy-hy)
                            double sHMQ = (mx - hx) * (qy - hy);
                            // S(M,P,Q) = (candX-mx)*(qy-hy) - (candY-hy)*(qx-mx)
                            double sMPQ = (candX - mx) * (qy - hy) - (candY - hy) * (qx - mx);
                            // S(M,P,H) = -(candY-hy)*(hx-mx)
                            double sMPH = -(candY - hy) * (hx - mx);
                            // S(P,H,Q) = (hx-candX)*(qy-candY) - (hy-candY)*(qx-candX)
                            double sPHQ = (hx - candX) * (qy - candY) - (hy - candY) * (qx - candX);
                            // S(P,H,M) = (hy-candY)*(hx-mx)
                            double sPHM = (hy - candY) * (hx - mx);

                            // Q inside iff all three cross-product signs match the reference signs.
                            bool inside =
                                sHMP * sHMQ >= 0.0 &&
                                sMPH * sMPQ >= 0.0 &&
                                sPHM * sPHQ >= 0.0;

                            // Non-crossing guard: the candidate bridge cur→holeLM must be locally
                            // inside at cur (mapbox locallyInside) — this is the guard the old code
                            // lacked, and is what stops the bridge from crossing an already-merged
                            // seam at scale (design §2 root cause). Additive: only rejects candidates
                            // the containment test already flagged as inside the sector.
                            if (inside && LocallyInside(vx, vy, prev, next, cur, holeLM))
                            {
                                double dx  = hx - qx; // > 0 (qx < hx guaranteed by band check)
                                double tan = dx > 1e-12 ? math.abs(qy - hy) / dx : double.MaxValue;
                                bool tanTie = math.abs(tan - bestTan) < 1e-14;
                                // Prefer smaller polar angle; ties broken by larger qx (existing outer
                                // tier); an exact qx tie too falls to the sectorContainsSector equal-
                                // angle tiebreak (mapbox) for a deterministic, non-crossing choice.
                                bool accept =
                                    tan < bestTan ? true :
                                    tanTie && qx > candX ? true :
                                    tanTie && qx == candX ? SectorContainsSector(vx, vy, prev, next, bestVert, cur) :
                                    false;
                                if (accept)
                                {
                                    bestTan  = tan;
                                    bestVert = cur;
                                    candX    = qx;
                                    candY    = qy;
                                }
                            }
                        }

                        cur = next[cur];
                        if (cur == mergedRingStart && iter > 0) break;
                    }
                }
            }

            // ── Fallback: nearest vertex by distance ─────────────────────────────────────────
            if (bestVert < 0)
            {
                double bestDist = double.MaxValue;
                cur = mergedRingStart;
                for (int iter = 0; iter < mergedRingCount; iter++)
                {
                    double dx = vx[cur] - hx, dy = vy[cur] - hy;
                    double d  = dx * dx + dy * dy;
                    if (d < bestDist) { bestDist = d; bestVert = cur; }
                    cur = next[cur];
                    if (cur == mergedRingStart && iter > 0) break;
                }
            }

            return bestVert < 0 ? mergedRingStart : bestVert;
        }

        /// <summary>
        /// Twice the signed area of triangle (p, q, r): cross((q−p), (r−p)). Matches the sign
        /// convention already used by <see cref="IsEar"/>'s triArea2 (convex ⟺ ≤ 0 for a ring
        /// normalised CCW-on-screen in Y-down space); this is also mapbox earcut's `area(p, q, r)`
        /// (algebraically identical — cross((q−p),(r−p)) == cross((q−p),(r−q))), so the mapbox
        /// convexity/locally-inside logic below ports without a sign flip.
        /// </summary>
        private static double Area2(double px, double py, double qx, double qy, double rx, double ry)
            => (qx - px) * (ry - py) - (rx - px) * (qy - py);

        /// <summary>
        /// mapbox earcut's `locallyInside(a, b)`, expressed directly in THIS triangulator's convention
        /// (CCW-on-screen ring ⇒ a vertex is convex ⟺ Area2(prev,a,next) ≤ 0, and a point X is on the
        /// interior side of a directed edge P→Q ⟺ Area2(P,Q,X) ≤ 0 — both consistent with
        /// <see cref="IsEar"/>). "Locally inside" means the diagonal a→b enters the polygon interior at
        /// a: for a convex a the interior is the intersection of the two edges' half-planes (AND); for a
        /// reflex a it is their union (OR). This is the non-crossing guard the bridge selection needs.
        ///
        /// NOTE: an earlier revision ported mapbox's sign literals verbatim, which is WRONG here —
        /// mapbox's y-up `area` equals −Area2 in this y-down convention, so the branch/comparison signs
        /// invert. That inversion silently rejected valid bridge candidates (and accepted crossing
        /// ones), leaving the merged ring self-intersecting. Derived-from-geometry form below is
        /// verified against convex and reflex reference vertices.
        /// </summary>
        private static bool LocallyInside(double[] vx, double[] vy, int[] prev, int[] next, int a, int b)
        {
            int ap = prev[a], an = next[a];
            double ax = vx[a], ay = vy[a];
            double apx = vx[ap], apy = vy[ap];
            double anx = vx[an], any = vy[an];
            double bx = vx[b], by = vy[b];

            // Convex a ⟺ Area2(prev,a,next) ≤ 0. Interior side of edge (ap→a): Area2(ap,a,b) ≤ 0;
            // of edge (a→an): Area2(a,an,b) ≤ 0. Convex ⇒ AND, reflex ⇒ OR.
            bool aConvex = Area2(apx, apy, ax, ay, anx, any) <= 0.0;
            bool insidePrevEdge = Area2(apx, apy, ax, ay, bx, by) <= 0.0;
            bool insideNextEdge = Area2(ax, ay, anx, any, bx, by) <= 0.0;
            return aConvex ? (insidePrevEdge && insideNextEdge)
                           : (insidePrevEdge || insideNextEdge);
        }

        /// <summary>
        /// Mapbox earcut's `sectorContainsSector(m, p)`: does candidate p's incident-edge sector
        /// nest inside the current-best m's sector? Used only as the innermost tiebreak in
        /// <see cref="FindBridgeVertex"/> — when two reflex candidates have both an equal polar
        /// angle AND an equal x (the existing qx tiebreak also ties) — to keep the choice
        /// deterministic without picking a candidate whose own sector could re-cross the bridge.
        /// </summary>
        private static bool SectorContainsSector(double[] vx, double[] vy, int[] prev, int[] next, int m, int p)
        {
            int mp = prev[m], mn = next[m];
            int pp = prev[p], pn = next[p];
            return Area2(vx[mp], vy[mp], vx[m], vy[m], vx[pp], vy[pp]) < 0.0 &&
                   Area2(vx[pn], vy[pn], vx[m], vy[m], vx[mn], vy[mn]) < 0.0;
        }

        /// <summary>
        /// Running count of ear-test candidate visits in <see cref="IsEar"/> since last reset.
        /// Test-only tooth for <c>EarcutEarTestScanBoundTests</c> — counts visits, not
        /// <see cref="PointInTriangle"/> calls, so a prune-only implementation that still visits
        /// every candidate cannot pass by skipping just the predicate.
        /// </summary>
        internal static long CandidateVisitCount;

        /// <summary>Test-only override: forces every <see cref="IsEar"/> call onto the full linear
        /// scan, bypassing <see cref="EarGrid"/> — the RED/parity arm for
        /// <c>EarcutEarTestScanBoundTests</c>.</summary>
        internal static bool ForceLinearEarScan;

        /// <summary>
        /// Uniform bucket grid (CSR layout) over the merged ring, built once so <see cref="IsEar"/>
        /// visits only nearby vertices. CellStart/CellItems hold the base merged-ring vertices (cell
        /// c = CellItems[CellStart[c]..CellStart[c+1])). Overflow holds vertices <c>SplitPolygon</c>
        /// adds after the grid is built — a failure-path escape only, normally empty.
        /// </summary>
        private readonly struct EarGrid
        {
            public readonly int Dim;
            public readonly double MinX, MinY, InvCellW, InvCellH;
            public readonly int[] CellStart;
            public readonly int[] CellItems;
            public readonly List<int> Overflow;

            public EarGrid(int dim, double minX, double minY, double invCellW, double invCellH,
                int[] cellStart, int[] cellItems, List<int> overflow)
            {
                Dim = dim; MinX = minX; MinY = minY; InvCellW = invCellW; InvCellH = invCellH;
                CellStart = cellStart; CellItems = cellItems; Overflow = overflow;
            }

            public int CellX(double x) => math.clamp((int)((x - MinX) * InvCellW), 0, Dim - 1);
            public int CellY(double y) => math.clamp((int)((y - MinY) * InvCellH), 0, Dim - 1);
        }

        /// <summary>Builds <see cref="EarGrid"/> over vx/vy[0..total) by counting sort: one pass to
        /// count per-cell occupancy, one to place items — no per-insert allocation.</summary>
        private static EarGrid BuildEarGrid(double[] vx, double[] vy, int total)
        {
            int dim = math.clamp((int)math.ceil(math.sqrt(total)), 1, 256);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < total; i++)
            {
                if (vx[i] < minX) minX = vx[i];
                if (vx[i] > maxX) maxX = vx[i];
                if (vy[i] < minY) minY = vy[i];
                if (vy[i] > maxY) maxY = vy[i];
            }
            double invCellW = dim / math.max(maxX - minX, 1e-9);
            double invCellH = dim / math.max(maxY - minY, 1e-9);
            var grid = new EarGrid(dim, minX, minY, invCellW, invCellH,
                new int[dim * dim + 1], new int[total], new List<int>());

            for (int i = 0; i < total; i++)
                grid.CellStart[grid.CellY(vy[i]) * dim + grid.CellX(vx[i]) + 1]++;
            for (int c = 0; c < dim * dim; c++)
                grid.CellStart[c + 1] += grid.CellStart[c];

            // Place items using CellStart itself as the write cursor — no second dim²-sized array.
            // Afterwards CellStart[c] holds the ORIGINAL CellStart[c+1], so shifting right by one
            // cell restores the correct start-of-cell boundaries.
            for (int i = 0; i < total; i++)
            {
                int cell = grid.CellY(vy[i]) * dim + grid.CellX(vx[i]);
                grid.CellItems[grid.CellStart[cell]++] = i;
            }
            for (int c = dim * dim; c > 0; c--)
                grid.CellStart[c] = grid.CellStart[c - 1];
            grid.CellStart[0] = 0;

            return grid;
        }

        /// <summary>
        /// True iff vertex v is an ear: convex, and no other live vertex lies inside or on triangle
        /// (prev[v], v, next[v]). Bridge-copy vertices are skipped (position-duplicates of a live
        /// vertex already tested). Scans only <paramref name="grid"/>'s AABB-overlapping cells
        /// (fallback: a full scan, on a wide AABB or when <see cref="ForceLinearEarScan"/> is set) —
        /// answer-preserving; proof in docs/mesh-triangulation-robustness-design.md §2.1.
        /// </summary>
        private static bool IsEar(
            double[] vx, double[] vy, int[] prev, int[] next, bool[] removed, bool[] isBridgeCopy, int total,
            in EarGrid grid, int v)
        {
            if (removed[v]) return false;
            int p = prev[v], n = next[v];
            if (removed[p] || removed[n]) return false;

            double ax = vx[p], ay = vy[p];
            double bx = vx[v], by = vy[v];
            double cx = vx[n], cy = vy[n];

            // Signed area of triangle (p, v, n).
            // In our normalised ring (outer is CCW-on-screen, Y-down → area2 < 0), a convex vertex
            // produces a triangle that is also CCW-on-screen, i.e. triArea2 <= 0.
            double triArea2 = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (triArea2 > 1e-10) return false; // reflex vertex

            // True if live vertex i blocks the ear: not the triangle's own corners, not a bridge-
            // seam duplicate (spatial coverage already tested through the vertex it duplicates), not
            // a position duplicate of a corner, and inside or on the candidate triangle.
            bool TestCandidate(int i)
            {
                if (removed[i] || i == p || i == v || i == n) return false;
                if (isBridgeCopy[i]) return false;
                double vxi = vx[i], vyi = vy[i];
                if ((vxi == ax && vyi == ay) || (vxi == bx && vyi == by) || (vxi == cx && vyi == cy))
                    return false;
                return PointInTriangle(ax, ay, bx, by, cx, cy, vxi, vyi);
            }

            double triMinX = math.min(ax, math.min(bx, cx)), triMaxX = math.max(ax, math.max(bx, cx));
            double triMinY = math.min(ay, math.min(by, cy)), triMaxY = math.max(ay, math.max(by, cy));
            int cx0 = grid.CellX(triMinX), cx1 = grid.CellX(triMaxX);
            int cy0 = grid.CellY(triMinY), cy1 = grid.CellY(triMaxY);
            long overlappedCells = (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);

            // Wide-AABB guard: fall back to the linear scan rather than walk more cells than a
            // linear pass would cost anyway. Same answer either way — this only bounds the worst case.
            if (ForceLinearEarScan || overlappedCells > (long)grid.Dim * grid.Dim / 4)
            {
                for (int i = 0; i < total; i++)
                {
                    CandidateVisitCount++;
                    if (TestCandidate(i)) return false;
                }
                return true;
            }

            for (int gy = cy0; gy <= cy1; gy++)
            {
                int rowBase = gy * grid.Dim;
                for (int gx = cx0; gx <= cx1; gx++)
                {
                    int cell = rowBase + gx;
                    int start = grid.CellStart[cell], end = grid.CellStart[cell + 1];
                    for (int k = start; k < end; k++)
                    {
                        CandidateVisitCount++;
                        if (TestCandidate(grid.CellItems[k])) return false;
                    }
                }
            }
            for (int oi = 0; oi < grid.Overflow.Count; oi++)
            {
                CandidateVisitCount++;
                if (TestCandidate(grid.Overflow[oi])) return false;
            }
            return true;
        }

        /// <summary>True if P lies in or on triangle ABC. A degenerate (collinear) ABC is the segment
        /// hull of its corners, not the whole plane: without the explicit branch below, a point at ANY
        /// distance on the shared line reads as contained. <c>internal</c>: no production caller outside
        /// this file, and the one test that needs it lives in an assembly Core already grants
        /// <c>InternalsVisibleTo</c>.</summary>
        internal static bool PointInTriangle(
            double ax, double ay, double bx, double by, double cx, double cy,
            double px, double py)
        {
            double d1 = Cross(ax, ay, bx, by, px, py);
            double d2 = Cross(bx, by, cx, cy, px, py);
            double d3 = Cross(cx, cy, ax, ay, px, py);
            if (d1 == 0.0 && d2 == 0.0 && d3 == 0.0)
            {
                // Degenerate candidate: the corners are collinear, so the triangle's point set is the
                // segment hull of its corners and containment is the bounding-box test.
                double minx = ax < bx ? (ax < cx ? ax : cx) : (bx < cx ? bx : cx);
                double maxx = ax > bx ? (ax > cx ? ax : cx) : (bx > cx ? bx : cx);
                double miny = ay < by ? (ay < cy ? ay : cy) : (by < cy ? by : cy);
                double maxy = ay > by ? (ay > cy ? ay : cy) : (by > cy ? by : cy);
                return px >= minx && px <= maxx && py >= miny && py <= maxy;
            }
            bool hasNeg = (d1 < 0.0) || (d2 < 0.0) || (d3 < 0.0);
            bool hasPos = (d1 > 0.0) || (d2 > 0.0) || (d3 > 0.0);
            return !(hasNeg && hasPos);
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py)
            => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        /// <summary>
        /// Proper segment-intersection test (mapbox earcut's `intersects`): true if segment p1q1
        /// crosses segment p2q2, including the collinear-and-overlapping case. Used by
        /// <c>CureLocalIntersections</c> (detect the bowtie a local bridging seam creates) and
        /// <c>IsValidDiagonal</c> (a split diagonal must not cross any other ring edge).
        /// </summary>
        private static bool Intersects(double[] vx, double[] vy, int p1, int q1, int p2, int q2)
        {
            double o1 = Area2(vx[p1], vy[p1], vx[q1], vy[q1], vx[p2], vy[p2]);
            double o2 = Area2(vx[p1], vy[p1], vx[q1], vy[q1], vx[q2], vy[q2]);
            double o3 = Area2(vx[p2], vy[p2], vx[q2], vy[q2], vx[p1], vy[p1]);
            double o4 = Area2(vx[p2], vy[p2], vx[q2], vy[q2], vx[q1], vy[q1]);

            bool s1 = o1 > 0.0, s1n = o1 < 0.0;
            bool s2 = o2 > 0.0, s2n = o2 < 0.0;
            bool s3 = o3 > 0.0, s3n = o3 < 0.0;
            bool s4 = o4 > 0.0, s4n = o4 < 0.0;

            if ((s1 != s2 || s1n != s2n) && (s3 != s4 || s3n != s4n)) return true; // general case

            if (o1 == 0.0 && OnSegment(vx, vy, p1, p2, q1)) return true; // p2 lies on p1q1
            if (o2 == 0.0 && OnSegment(vx, vy, p1, q2, q1)) return true; // q2 lies on p1q1
            if (o3 == 0.0 && OnSegment(vx, vy, p2, p1, q2)) return true; // p1 lies on p2q2
            if (o4 == 0.0 && OnSegment(vx, vy, p2, q1, q2)) return true; // q1 lies on p2q2

            return false;
        }

        /// <summary>Given p, q, r already collinear, is q within the bounding box of segment p-r?</summary>
        private static bool OnSegment(double[] vx, double[] vy, int p, int q, int r)
        {
            double px = vx[p], py = vy[p], qx = vx[q], qy = vy[q], rx = vx[r], ry = vy[r];
            return qx <= math.max(px, rx) && qx >= math.min(px, rx) &&
                   qy <= math.max(py, ry) && qy >= math.min(py, ry);
        }

        /// <summary>
        /// STRICT proper segment crossing (interiors intersect at a single point) — endpoint touches
        /// and collinear overlaps return false. Distinct from <see cref="Intersects"/> (which reports
        /// those as true); the bridge-clearance test wants only genuine crossings so a bridge that
        /// merely shares/touches a ring vertex is not rejected.
        /// </summary>
        private static bool ProperlyCross(
            double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy)
        {
            double d1 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            double d2 = (bx - ax) * (dy - ay) - (by - ay) * (dx - ax);
            double d3 = (dx - cx) * (ay - cy) - (dy - cy) * (ax - cx);
            double d4 = (dx - cx) * (by - cy) - (dy - cy) * (bx - cx);
            return ((d1 > 0.0 && d2 < 0.0) || (d1 < 0.0 && d2 > 0.0)) &&
                   ((d3 > 0.0 && d4 < 0.0) || (d3 < 0.0 && d4 > 0.0));
        }

        /// <summary>
        /// Does the candidate bridge segment (holeLM → cand) properly cross any edge of the ring that
        /// starts at <paramref name="ringStart"/> and spans <paramref name="ringCount"/> vertices via
        /// next[]? Edges incident to holeLM or cand are skipped (they share an endpoint with the
        /// bridge, not a crossing). Called for BOTH the already-merged ring AND the current hole's own
        /// ring, so the accepted bridge is provably non-crossing against every existing edge — which
        /// keeps the spliced merged ring simple (the invariant that makes reversed residuals, and thus
        /// the pre-stall overlap the reactive mirror used to mask, impossible on clean input).
        /// </summary>
        private static bool BridgeCrossesRing(
            double[] vx, double[] vy, int[] next,
            int holeLM, int cand, int ringStart, int ringCount)
        {
            double ax = vx[holeLM], ay = vy[holeLM], bx = vx[cand], by = vy[cand];
            int c = ringStart;
            for (int i = 0; i < ringCount; i++)
            {
                int nc = next[c];
                if (c != cand && nc != cand && c != holeLM && nc != holeLM)
                {
                    if (ProperlyCross(ax, ay, bx, by, vx[c], vy[c], vx[nc], vy[nc]))
                        return true;
                }
                c = nc;
            }
            return false;
        }
    }
}
