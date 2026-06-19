using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst job: ear-clipping polygon triangulator over NativeArrays. Faithful port of
    /// <c>Earcut.Triangulate</c> from MapRenderer.Core — same algorithm, same tie-breaking,
    /// same bridge/hole logic, same stall-guard force-clip behaviour. Produces bit-identical
    /// output to the managed reference (integer tile-space coords are exact).
    ///
    /// Input: vertices for ONE polygon (outer + bridged holes packed contiguously), ring metadata
    /// from <see cref="RingAssemblyJob"/> output.
    ///
    /// Output: flat index array written to <see cref="OutIndices"/> starting at
    /// <see cref="OutIndexOffset"/> (pre-allocated by the pipeline coordinator).
    ///
    /// Does NOT reference MapRenderer.Core — keeps System.Math off the Burst path.
    ///
    /// Parity tie-break: hole sort (leftmost-x, then min-y, then ring index) must match the
    /// managed <c>validHoles.Sort</c> tie-break. The managed sort uses only leftmost-x (and
    /// List.Sort is unstable — the managed code re-sorts using the same comparator used here,
    /// extended with y + index for total order, to guarantee determinism on both sides).
    ///
    /// IMPORTANT: this job operates on a single polygon's data (outer + holes). The pipeline
    /// coordinator schedules one job per polygon. For simple tiles the number of polygons is
    /// small (~tens to ~hundreds); Burst parallelism is across tiles.
    /// </summary>
    [BurstCompile]
    public struct EarcutJob : IJob
    {
        // ── Per-polygon input ─────────────────────────────────────────────────────────────────
        /// <summary>Flat vertex array for this polygon's outer ring + holes (Earcut internal order).</summary>
        [ReadOnly] public NativeArray<double2> PolyVertices;

        /// <summary>Number of vertices in the outer ring.</summary>
        [ReadOnly] public int OuterCount;

        /// <summary>
        /// Hole vertex counts, sorted by (leftmost-x, min-y, ring-index) — same order as the
        /// managed Earcut.validHoles after the total-order sort. Length = HoleCount.
        /// </summary>
        [ReadOnly] public NativeArray<int> SortedHoleCounts;

        [ReadOnly] public int HoleCount;

        // ── Output ───────────────────────────────────────────────────────────────────────────
        /// <summary>Triangle index output. Pre-allocated by the coordinator.</summary>
        [WriteOnly] public NativeArray<int> OutIndices;

        /// <summary>Offset into <see cref="OutIndices"/> at which to start writing.</summary>
        [ReadOnly] public int OutIndexOffset;

        /// <summary>[0] = number of indices written.</summary>
        public NativeArray<int> OutIndexCount;

        /// <summary>[0] = force-clip count (stall-guard firings).</summary>
        public NativeArray<int> OutForceClipCount;

        // ── Scratch space (pre-allocated by coordinator, size = capacity) ─────────────────────
        // Using NativeArrays for all internal buffers so the job has no GC allocations.
        public NativeArray<double> Vx;
        public NativeArray<double> Vy;
        public NativeArray<int>    Prev;
        public NativeArray<int>    Next;
        public NativeArray<bool>   IsBridgeCopy;
        public NativeArray<bool>   Removed;
        public NativeArray<bool>   IsEar;

        public void Execute()
        {
            int outerCount = OuterCount;
            if (outerCount < 3) { OutIndexCount[0] = 0; OutForceClipCount[0] = 0; return; }

            // ── Sort holes by (leftmost-x, min-y, original-index) for deterministic bridging.
            // ── This MUST match the managed Earcut.validHoles.Sort tie-break.
            // We sort the SortedHoleCounts array indices. Since caller already provides them sorted,
            // we just use them in order. The coordinator sorts before scheduling this job.

            // Capacity: outer + each hole + 2 bridge verts per hole.
            int capacity = outerCount;
            for (int h = 0; h < HoleCount; h++) capacity += SortedHoleCounts[h] + 2;
            // (NativeArrays pre-allocated to capacity by the coordinator)

            int total = 0;

            // ── Insert outer ring, normalised to CCW-on-screen (area2 < 0 in Y-down). ────────
            double outerArea2 = ComputeArea2(PolyVertices, 0, outerCount);
            bool reverseOuter = outerArea2 > 0.0; // positive = CW on screen → reverse to CCW
            for (int i = 0; i < outerCount; i++)
            {
                int src = reverseOuter ? (outerCount - 1 - i) : i;
                Vx[total] = PolyVertices[src].x;
                Vy[total] = PolyVertices[src].y;
                Prev[total] = total - 1;
                Next[total] = total + 1;
                IsBridgeCopy[total] = false;
                Removed[total]      = false;
                total++;
            }
            Prev[0]              = outerCount - 1;
            Next[outerCount - 1] = 0;

            int mergedRingStart = 0;
            int mergedRingCount = outerCount;

            // ── Bridge each hole into the outer (merged) ring. ────────────────────────────────
            int holeVertexStart = outerCount; // where in PolyVertices the holes start
            for (int h = 0; h < HoleCount; h++)
            {
                int holeStart  = total;
                int holeCount  = SortedHoleCounts[h];

                double holeArea2 = ComputeArea2(PolyVertices, holeVertexStart, holeCount);
                bool reverseHole = holeArea2 < 0.0; // negative = CCW on screen → reverse to CW
                for (int i = 0; i < holeCount; i++)
                {
                    int src = reverseHole ? (holeCount - 1 - i) : i;
                    Vx[total] = PolyVertices[holeVertexStart + src].x;
                    Vy[total] = PolyVertices[holeVertexStart + src].y;
                    Prev[total] = total - 1;
                    Next[total] = total + 1;
                    IsBridgeCopy[total] = false;
                    Removed[total]      = false;
                    total++;
                }
                Prev[holeStart] = total - 1;
                Next[total - 1] = holeStart;

                holeVertexStart += holeCount;

                int holeLM     = HoleLeftmostIndex(holeStart, holeCount);
                int outerBridge = FindBridgeVertex(holeLM, mergedRingStart, mergedRingCount);

                int copyHoleLM = total;
                int copyOuter  = total + 1;
                total += 2;

                Vx[copyHoleLM] = Vx[holeLM];      Vy[copyHoleLM] = Vy[holeLM];
                Vx[copyOuter]  = Vx[outerBridge];  Vy[copyOuter]  = Vy[outerBridge];
                IsBridgeCopy[copyHoleLM] = true;
                IsBridgeCopy[copyOuter]  = true;
                Removed[copyHoleLM]      = false;
                Removed[copyOuter]       = false;

                int outerNext  = Next[outerBridge];
                int holePrevLM = Prev[holeLM];

                Next[outerBridge] = holeLM;      Prev[holeLM]      = outerBridge;
                Next[holePrevLM]  = copyHoleLM;  Prev[copyHoleLM]  = holePrevLM;
                Next[copyHoleLM]  = copyOuter;   Prev[copyOuter]   = copyHoleLM;
                Next[copyOuter]   = outerNext;   Prev[outerNext]   = copyOuter;

                mergedRingCount = total;
            }

            // ── Ear-clipping loop. ────────────────────────────────────────────────────────────
            for (int i = 0; i < total; i++)
                IsEar[i] = ComputeIsEar(total, i);

            int remaining      = total;
            int forceClipCount = 0;
            int outCount       = 0;
            int outBase        = OutIndexOffset;

            while (remaining > 3)
            {
                int start = -1;
                for (int i = 0; i < total; i++)
                    if (!Removed[i]) { start = i; break; }
                if (start < 0) break;

                int stallLimit     = remaining;
                int stallIter      = 0;
                bool clippedAny    = false;
                bool didFullRefresh = false;

                int v = start;
                for (int iter = 0; iter < remaining * 4 && remaining > 3; iter++)
                {
                    if (Removed[v]) { v = Next[v]; continue; }

                    if (IsEar[v])
                    {
                        int p = Prev[v], n = Next[v];
                        OutIndices[outBase + outCount]     = p;
                        OutIndices[outBase + outCount + 1] = v;
                        OutIndices[outBase + outCount + 2] = n;
                        outCount += 3;

                        Next[p] = n; Prev[n] = p;
                        Removed[v] = true;
                        remaining--;
                        clippedAny    = true;
                        stallIter     = 0;
                        didFullRefresh = false;

                        IsEar[p] = ComputeIsEar(total, p);
                        IsEar[n] = ComputeIsEar(total, n);
                        v = n;
                    }
                    else
                    {
                        v = Next[v];
                        stallIter++;
                        if (stallIter > stallLimit)
                        {
                            if (!didFullRefresh)
                            {
                                for (int i = 0; i < total; i++)
                                    if (!Removed[i])
                                        IsEar[i] = ComputeIsEar(total, i);
                                stallIter = 0; didFullRefresh = true; stallLimit = remaining;
                            }
                            else
                            {
                                int p = Prev[v], n = Next[v];
                                OutIndices[outBase + outCount]     = p;
                                OutIndices[outBase + outCount + 1] = v;
                                OutIndices[outBase + outCount + 2] = n;
                                outCount += 3;
                                Next[p] = n; Prev[n] = p;
                                Removed[v] = true;
                                remaining--;
                                forceClipCount++;
                                IsEar[p] = ComputeIsEar(total, p);
                                IsEar[n] = ComputeIsEar(total, n);
                                v = n; stallIter = 0; didFullRefresh = false; clippedAny = true;
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
                    if (Removed[i]) continue;
                    if (v0 < 0)      v0 = i;
                    else if (v1 < 0) v1 = i;
                    else             { v2 = i; break; }
                }
                if (v0 >= 0 && v1 >= 0 && v2 >= 0)
                {
                    OutIndices[outBase + outCount]     = v0;
                    OutIndices[outBase + outCount + 1] = v1;
                    OutIndices[outBase + outCount + 2] = v2;
                    outCount += 3;
                }
            }

            OutIndexCount[0]    = outCount;
            OutForceClipCount[0] = forceClipCount;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private double ComputeArea2(NativeArray<double2> verts, int start, int len)
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

        private int HoleLeftmostIndex(int holeStart, int holeCount)
        {
            int    idx  = holeStart;
            double minX = Vx[holeStart];
            for (int i = 1; i < holeCount; i++)
            {
                int j = holeStart + i;
                if (Vx[j] < minX || (Vx[j] == minX && Vy[j] < Vy[idx]))
                { minX = Vx[j]; idx = j; }
            }
            return idx;
        }

        private int FindBridgeVertex(int holeLM, int mergedRingStart, int mergedRingCount)
        {
            double hx = Vx[holeLM];
            double hy = Vy[holeLM];

            int    bestVert       = -1;
            double bestIntersectX = double.NegativeInfinity;

            int cur = mergedRingStart;
            for (int iter = 0; iter < mergedRingCount * 2; iter++)
            {
                int nc = Next[cur];
                double ax = Vx[cur], ay = Vy[cur];
                double bx = Vx[nc],  by = Vy[nc];

                bool straddles = (ay > hy) != (by > hy);
                if (straddles)
                {
                    double t  = (hy - ay) / (by - ay);
                    double ix = ax + t * (bx - ax);
                    if (ix <= hx && ix > bestIntersectX)
                    {
                        bestIntersectX = ix;
                        bestVert = (ax >= bx) ? cur : nc;
                    }
                }
                cur = Next[cur];
                if (cur == mergedRingStart && iter > 0) break;
            }

            // Reflex-vertex refinement.
            if (bestVert >= 0)
            {
                double mx    = bestIntersectX;
                double candX = Vx[bestVert];
                double candY = Vy[bestVert];
                double sHMP  = (mx - hx) * (candY - hy);

                if (math.abs(sHMP) < 1e-10)
                {
                    cur = mergedRingStart;
                    for (int iter = 0; iter < mergedRingCount * 2; iter++)
                    {
                        double qx = Vx[cur], qy = Vy[cur];
                        if (cur != holeLM && math.abs(qy - hy) < 1e-10 && qx > candX && qx < hx)
                        { bestVert = cur; candX = qx; }
                        cur = Next[cur];
                        if (cur == mergedRingStart && iter > 0) break;
                    }
                }
                else
                {
                    double bestTan = (hx - candX) > 1e-12
                        ? math.abs(candY - hy) / (hx - candX) : double.MaxValue;

                    cur = mergedRingStart;
                    for (int iter = 0; iter < mergedRingCount * 2; iter++)
                    {
                        double qx = Vx[cur], qy = Vy[cur];
                        if (qx > mx && qx < hx && cur != holeLM)
                        {
                            double sHMQ = (mx - hx) * (qy - hy);
                            double sMPQ = (candX - mx) * (qy - hy) - (candY - hy) * (qx - mx);
                            double sMPH = -(candY - hy) * (hx - mx);
                            double sPHQ = (hx - candX) * (qy - candY) - (hy - candY) * (qx - candX);
                            double sPHM = (hy - candY) * (hx - mx);

                            bool inside =
                                sHMP * sHMQ >= 0.0 &&
                                sMPH * sMPQ >= 0.0 &&
                                sPHM * sPHQ >= 0.0;

                            if (inside)
                            {
                                double dx  = hx - qx;
                                double tan = dx > 1e-12 ? math.abs(qy - hy) / dx : double.MaxValue;
                                if (tan < bestTan || (math.abs(tan - bestTan) < 1e-14 && qx > candX))
                                { bestTan = tan; bestVert = cur; candX = qx; candY = qy; }
                            }
                        }
                        cur = Next[cur];
                        if (cur == mergedRingStart && iter > 0) break;
                    }
                }
            }

            // Fallback: nearest by distance.
            if (bestVert < 0)
            {
                double bestDist = double.MaxValue;
                cur = mergedRingStart;
                for (int iter = 0; iter < mergedRingCount; iter++)
                {
                    double dx = Vx[cur] - hx, dy = Vy[cur] - hy;
                    double d  = dx * dx + dy * dy;
                    if (d < bestDist) { bestDist = d; bestVert = cur; }
                    cur = Next[cur];
                    if (cur == mergedRingStart && iter > 0) break;
                }
            }

            return bestVert < 0 ? mergedRingStart : bestVert;
        }

        private bool ComputeIsEar(int total, int v)
        {
            if (Removed[v]) return false;
            int p = Prev[v], n = Next[v];
            if (Removed[p] || Removed[n]) return false;

            double ax = Vx[p], ay = Vy[p];
            double bx = Vx[v], by = Vy[v];
            double cx = Vx[n], cy = Vy[n];

            double triArea2 = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (triArea2 > 1e-10) return false; // reflex vertex

            for (int i = 0; i < total; i++)
            {
                if (Removed[i] || i == p || i == v || i == n) continue;
                if (IsBridgeCopy[i]) continue;
                double vxi = Vx[i], vyi = Vy[i];
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
