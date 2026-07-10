using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Clean-room ear-clipping polygon triangulator. Implements the ear-clipping technique from
    /// first principles: a vertex is an "ear" if it is convex and the candidate triangle contains
    /// no reflex vertex. Holes are bridged into the outer ring by finding a visible vertex pair
    /// and splicing duplicate vertices.
    ///
    /// API: call Triangulate() to get both the flat vertex array (in the internal order Earcut
    /// uses, which includes bridge-duplicate verts) and the triangle indices into that array.
    /// This avoids any out-of-band reconstruction of the flat vert list.
    ///
    /// Winding normalisation: outer ring is normalised to CCW-on-screen (negative shoelace in
    /// Y-down tile space); holes to CW-on-screen (positive shoelace). This is done internally;
    /// callers need not pre-arrange winding.
    ///
    /// Stall guard: if a full pass yields no ear, bail with a force-clip rather than looping.
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
            /// Number of times the stall guard fired a force-clip during triangulation.
            /// A non-zero value means at least one vertex was force-clipped without passing the
            /// ear test (the input ring is likely self-intersecting or degenerate).
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
            // TileMeshPipeline (which also sorts by leftmost-x then min-y then ring-index),
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

                // Find the outer-ring vertex to bridge to.
                int outerBridge = FindBridgeVertex(vx, vy, next, prev, holeLM, mergedRingStart, mergedRingCount);

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

            // ---- Ear-clipping loop. ----
            var indices = new List<int>(math.max(0, (total - 2) * 3));
            var removed = new bool[total];
            int forceClipCount = 0;

            // Precompute ear status.
            var isEar = new bool[total];
            for (int i = 0; i < total; i++)
                isEar[i] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, i);

            int remaining = total;

            while (remaining > 3)
            {
                // Find first live vertex for ring walk.
                int start = -1;
                for (int i = 0; i < total; i++)
                    if (!removed[i]) { start = i; break; }
                if (start < 0) break;

                // stallLimit: if we complete a full pass (remaining steps) without clipping an ear,
                // refresh all ear statuses (catches stale isEar values from distant ear removals)
                // before resorting to a force-clip as the true last resort.
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

                        // Update ear status for the two neighbors whose candidate triangle changed.
                        isEar[p] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, p);
                        isEar[n] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, n);

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
                                // First stall: do a full refresh of ALL remaining ear statuses.
                                // This handles the case where a distant ear removal made a vertex
                                // eligible (its blocking vertex was removed but isEar[v] was stale).
                                // This is the correct escape before resorting to force-clip.
                                for (int i = 0; i < total; i++)
                                    if (!removed[i])
                                        isEar[i] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, i);
                                stallIter = 0;
                                didFullRefresh = true;
                                stallLimit = remaining; // reset limit for the post-refresh pass
                            }
                            else
                            {
                                // Stall guard: even after full refresh, no ear found. Force-clip to
                                // escape degenerate/self-intersecting geometry.
                                // (No UnityEngine dependency in Core; log via System.Diagnostics if needed.)
                                int p = prev[v], n = next[v];
                                indices.Add(p);
                                indices.Add(v);
                                indices.Add(n);
                                next[p] = n;
                                prev[n] = p;
                                removed[v] = true;
                                remaining--;
                                forceClipCount++;
                                isEar[p] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, p);
                                isEar[n] = IsEar(vx, vy, prev, next, removed, isBridgeCopy, total, n);
                                v = n;
                                stallIter = 0;
                                didFullRefresh = false;
                                clippedAny = true;
                            }
                        }
                    }
                }

                if (!clippedAny) break;
            }

            // Final triangle.
            if (remaining >= 3)
            {
                int v0 = -1, v1 = -1, v2 = -1;
                for (int i = 0; i < total; i++)
                {
                    if (removed[i]) continue;
                    if (v0 < 0)      v0 = i;
                    else if (v1 < 0) v1 = i;
                    else             { v2 = i; break; }
                }
                if (v0 >= 0 && v1 >= 0 && v2 >= 0)
                {
                    indices.Add(v0);
                    indices.Add(v1);
                    indices.Add(v2);
                }
            }

            // Build the flat vertex array to return alongside indices.
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

                            if (inside)
                            {
                                double dx  = hx - qx; // > 0 (qx < hx guaranteed by band check)
                                double tan = dx > 1e-12 ? math.abs(qy - hy) / dx : double.MaxValue;
                                // Prefer smaller polar angle; break ties by larger qx.
                                if (tan < bestTan || (math.abs(tan - bestTan) < 1e-14 && qx > candX))
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
        /// Tests if vertex v is an ear.
        /// v is an ear if: (1) it is a convex vertex in the merged ring, AND
        ///                 (2) no other live vertex lies inside or on the triangle (prev[v], v, next[v]).
        /// Bridge-copy vertices (isBridgeCopy[i] = true) are skipped in the point-in-triangle test:
        /// they are position-duplicates of real ring vertices introduced by hole bridging, and their
        /// spatial presence is already captured by the original vertex. Allowing bridge copies to
        /// block ear detection causes false-stalls in complex multi-hole polygons.
        /// </summary>
        private static bool IsEar(
            double[] vx, double[] vy, int[] prev, int[] next, bool[] removed, bool[] isBridgeCopy, int total, int v)
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

            // Check no other live vertex lies inside the triangle.
            // Skip bridge-copy vertices (isBridgeCopy[i]) — they are position-duplicates of real
            // ring vertices and must not block ear detection: their spatial coverage is already
            // tested through the original vertex they duplicate.
            // Also skip vertices at the exact same position as the triangle's own corners
            // (catches any remaining duplicate positions not covered by isBridgeCopy).
            for (int i = 0; i < total; i++)
            {
                if (removed[i] || i == p || i == v || i == n) continue;
                if (isBridgeCopy[i]) continue; // skip bridge-seam duplicates
                // Skip exact position duplicates of triangle corners (additional safety net).
                double vxi = vx[i], vyi = vy[i];
                if ((vxi == ax && vyi == ay) ||
                    (vxi == bx && vyi == by) ||
                    (vxi == cx && vyi == cy))
                    continue;
                if (PointInTriangle(ax, ay, bx, by, cx, cy, vxi, vyi))
                    return false;
            }
            return true;
        }

        private static bool PointInTriangle(
            double ax, double ay, double bx, double by, double cx, double cy,
            double px, double py)
        {
            double d1 = Cross(ax, ay, bx, by, px, py);
            double d2 = Cross(bx, by, cx, cy, px, py);
            double d3 = Cross(cx, cy, ax, ay, px, py);
            bool hasNeg = (d1 < 0.0) || (d2 < 0.0) || (d3 < 0.0);
            bool hasPos = (d1 > 0.0) || (d2 > 0.0) || (d3 > 0.0);
            return !(hasNeg && hasPos);
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py)
            => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
    }
}
