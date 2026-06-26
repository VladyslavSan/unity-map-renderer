using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst job: classifies decoded MVT rings into polygons (outer + holes) using signed-area
    /// classification, then stores polygon descriptors for the earcut stage.
    ///
    /// Reimplements <c>PolygonAssembler.Assemble</c> over native containers:
    ///   - Rings with |shoelace area| &lt; DegenerateThreshold are skipped.
    ///   - The first valid ring of each feature sets the "exterior sign."
    ///   - Subsequent rings with the SAME sign start a new polygon (multipolygon / island).
    ///   - Rings with the OPPOSITE sign are candidate holes — accepted only if the ring's
    ///     centroid (or first vertex) falls INSIDE the current outer ring (containment check),
    ///     preventing disjoint artefact rings from corrupting the triangulation.
    ///
    /// Does NOT reference MapRenderer.Core — Core's System.Math stays off the Burst path.
    ///
    /// Input: flat vertex array + per-ring offsets from <see cref="MvtDecodeJob"/>.
    /// Output: polygon descriptors (outer start/length + hole start/count) in flat arrays.
    ///
    /// Polygon descriptor arrays are pre-sized conservatively (worst case = one polygon per ring).
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct RingAssemblyJob : IJob
    {
        /// <summary>Rings with |2*area| (shoelace) below this threshold are skipped (degenerate).</summary>
        private const double DegenerateThreshold = 1.0;

        // ── Input ──────────────────────────────────────────────────────────────────────────────
        [ReadOnly] public NativeArray<double2> Vertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;   // length = ringCount + 1 (sentinel)
        [ReadOnly] public NativeArray<int>     RingFeatureIdx;// which feature each ring belongs to
        [ReadOnly] public int                  RingCount;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        // Each polygon: one outer ring + zero or more holes.
        // OutPolyOuterStart[p]  = index into RingOffsets of the outer ring for polygon p.
        // OutPolyOuterRingIdx[p] = which ring-index (into Vertices via RingOffsets) is the outer.
        // Holes are stored in a flat list; OutPolyHoleStart[p]/OutPolyHoleCount[p] reference it.
        //
        // Note: OutPolyOuterRingIdx and OutPolyHoleCount are NOT marked [WriteOnly] because they
        // are read internally within Execute() (OutPolyOuterRingIdx is read for hole containment;
        // OutPolyHoleCount is incremented with ++ which is a read-modify-write).
        // Marking them [WriteOnly] would cause Unity's Job Safety System to throw
        // InvalidOperationException when reading them, causing Execute() to terminate early.
        public            NativeArray<int>    OutPolyOuterRingIdx;    // ring index for outer
        [WriteOnly] public NativeArray<int>   OutPolyHoleListStart;   // start in OutHoleRingIdxs
        public            NativeArray<int>    OutPolyHoleCount;       // hole count for polygon p
        [WriteOnly] public NativeArray<int>   OutHoleRingIdxs;        // flat list of hole ring idxs
        public            NativeArray<int>    OutPolygonCount;        // [0] = total polygons
        public            NativeArray<int>    OutHoleCount;           // [0] = total holes

        public void Execute()
        {
            int polyCount     = 0;
            int holeListCount = 0;

            int prevFeature   = -1;
            double exteriorSign = 0.0;
            int currentPolyIdx  = -1;

            for (int ri = 0; ri < RingCount; ri++)
            {
                int rStart = RingOffsets[ri];
                int rEnd   = RingOffsets[ri + 1];
                int rLen   = rEnd - rStart;

                if (rLen < 3) continue; // degenerate

                int featureIdx = RingFeatureIdx[ri];

                // New feature resets exterior sign.
                if (featureIdx != prevFeature)
                {
                    exteriorSign = 0.0;
                    prevFeature  = featureIdx;
                }

                double area2 = SignedArea2(Vertices, rStart, rLen);
                if (area2 < DegenerateThreshold && area2 > -DegenerateThreshold)
                    continue; // degenerate ring

                if (exteriorSign == 0.0)
                {
                    // First valid ring in this feature: establishes exterior sign, creates polygon.
                    exteriorSign = area2 > 0.0 ? 1.0 : -1.0;

                    OutPolyOuterRingIdx[polyCount]  = ri;
                    OutPolyHoleListStart[polyCount] = holeListCount;
                    OutPolyHoleCount[polyCount]     = 0;
                    currentPolyIdx = polyCount;
                    polyCount++;
                }
                else
                {
                    double ringSign = area2 > 0.0 ? 1.0 : -1.0;
                    if (ringSign == exteriorSign)
                    {
                        // Same sign as exterior → new outer ring (multipolygon island).
                        OutPolyOuterRingIdx[polyCount]  = ri;
                        OutPolyHoleListStart[polyCount] = holeListCount;
                        OutPolyHoleCount[polyCount]     = 0;
                        currentPolyIdx = polyCount;
                        polyCount++;
                    }
                    else
                    {
                        // Opposite sign → candidate hole. Containment check: centroid (or first
                        // vertex) must fall inside the current outer ring.
                        if (currentPolyIdx >= 0)
                        {
                            int outerRi    = OutPolyOuterRingIdx[currentPolyIdx];
                            int outerStart = RingOffsets[outerRi];
                            int outerLen   = RingOffsets[outerRi + 1] - outerStart;

                            if (RingContainedIn(Vertices, rStart, rLen, outerStart, outerLen))
                            {
                                OutHoleRingIdxs[holeListCount] = ri;
                                holeListCount++;
                                OutPolyHoleCount[currentPolyIdx]++;
                            }
                            // else: disjoint artefact ring — drop it (same as managed assembler).
                        }
                    }
                }
            }

            OutPolygonCount[0] = polyCount;
            OutHoleCount[0]    = holeListCount;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Shoelace signed area × 2: Σ (x_i*y_{i+1} − x_{i+1}*y_i).
        /// Positive = CCW in Y-up = CW on screen in Y-down (MVT exterior).
        /// </summary>
        private static double SignedArea2(NativeArray<double2> verts, int start, int len)
        {
            double area = 0.0;
            for (int i = 0; i < len; i++)
            {
                double2 a = verts[start + i];
                double2 b = verts[start + (i + 1) % len];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        private static double2 Centroid(NativeArray<double2> verts, int start, int len)
        {
            double sx = 0.0, sy = 0.0;
            for (int i = 0; i < len; i++) { sx += verts[start + i].x; sy += verts[start + i].y; }
            return new double2(sx / len, sy / len);
        }

        /// <summary>Even-odd ray-cast point-in-polygon.</summary>
        private static bool PointInRing(double2 p, NativeArray<double2> ring, int rStart, int rLen)
        {
            bool inside = false;
            for (int i = 0, j = rLen - 1; i < rLen; j = i++)
            {
                double xi = ring[rStart + i].x, yi = ring[rStart + i].y;
                double xj = ring[rStart + j].x, yj = ring[rStart + j].y;
                bool straddle = (yi > p.y) != (yj > p.y);
                if (straddle && (p.x < (xj - xi) * (p.y - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        private static bool RingContainedIn(
            NativeArray<double2> verts,
            int holeStart, int holeLen,
            int outerStart, int outerLen)
        {
            double2 centroid = Centroid(verts, holeStart, holeLen);
            if (PointInRing(centroid, verts, outerStart, outerLen)) return true;
            return PointInRing(verts[holeStart], verts, outerStart, outerLen);
        }
    }
}
