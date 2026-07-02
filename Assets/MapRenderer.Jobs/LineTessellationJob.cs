using MapRenderer.Core.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst-compiled polyline tessellator: a faithful port of
    /// <see cref="LineTessellator.Triangulate"/> (same algorithm, same emission order, same
    /// tie-breaking) over <see cref="Unity.Collections"/> data. Emits <see cref="LineVertex"/>
    /// (double-precision) so its output is byte-for-byte comparable to the managed reference.
    ///
    /// The managed <see cref="LineTessellator"/> is the differential ORACLE for this job
    /// (<c>LineTessellationJobTests</c>): the two implementations must agree over the fixture and the
    /// constructed branch-coverage polylines. Edit both together, or the oracle fails.
    ///
    /// Output capacity is data-dependent (join/cap type + roundSegments change the vertex count), so the
    /// caller must size <see cref="OutVertices"/>/<see cref="OutIndices"/> to the worst case
    /// (round-everywhere — see <see cref="MaxVertexCount"/>/<see cref="MaxIndexCount"/>); the actual
    /// counts written are reported in <see cref="OutVertexCount"/>/<see cref="OutIndexCount"/>.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct LineTessellationJob : IJob
    {
        // ── Input ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Input polyline: flat 2D points in the tessellation space (world meters for S05).</summary>
        [ReadOnly] public NativeArray<double2> InputPoints;

        /// <summary>Number of valid points in <see cref="InputPoints"/>.</summary>
        [ReadOnly] public int PointCount;

        /// <summary>Corner geometry style.</summary>
        public JoinType Join;

        /// <summary>Endpoint geometry style.</summary>
        public CapType Cap;

        /// <summary>Maximum miter ratio (1/cos(θ/2)); falls back to bevel when exceeded. Clamped to ≥ 1.</summary>
        public double MiterLimit;

        /// <summary>Arc divisions per round join/cap half. Clamped to ≥ 1.</summary>
        public int RoundSegments;

        // ── Output ────────────────────────────────────────────────────────────────────────────
        /// <summary>Ribbon vertices in emission order (written [0..OutVertexCount[0])).</summary>
        [WriteOnly] public NativeArray<LineVertex> OutVertices;

        /// <summary>Triangle indices, 3 per triangle (written [0..OutIndexCount[0])).</summary>
        [WriteOnly] public NativeArray<int> OutIndices;

        /// <summary>[0] = number of vertices written.</summary>
        public NativeArray<int> OutVertexCount;

        /// <summary>[0] = number of indices written.</summary>
        public NativeArray<int> OutIndexCount;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Worst-case sizing (round-everywhere is an upper bound for miter/bevel/butt/square too).
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Upper bound on vertices for <paramref name="pointCount"/> raw input points.</summary>
        public static int MaxVertexCount(int pointCount, int roundSegments)
        {
            if (pointCount < 2) return 0;
            int rs = roundSegments < 1 ? 1 : roundSegments;
            int n = pointCount;                          // ≥ distinct-point count after dedup
            int startCap = rs + 3;                       // round start cap is the largest
            int perJoin  = rs + 3;                       // round join is the largest per interior vertex
            int joins    = n - 2 < 0 ? 0 : n - 2;
            int lastSeg  = 2;
            int endCap   = rs + 1;                       // round end cap
            return startCap + joins * perJoin + lastSeg + endCap;
        }

        /// <summary>Upper bound on indices for <paramref name="pointCount"/> raw input points.</summary>
        public static int MaxIndexCount(int pointCount, int roundSegments)
        {
            if (pointCount < 2) return 0;
            int rs = roundSegments < 1 ? 1 : roundSegments;
            int n = pointCount;
            int startCap = 3 * (rs + 1);                 // round start cap: rs+1 triangles
            int perJoin  = 6 + 3 * (rs + 1);             // round join: quad (2 tris) + rs+1 fan tris
            int joins    = n - 2 < 0 ? 0 : n - 2;
            int lastSeg  = 6;                            // terminal quad
            int endCap   = 3 * (rs + 1);                 // round end cap
            return startCap + joins * perJoin + lastSeg + endCap;
        }

        // ── IJob ──────────────────────────────────────────────────────────────────────────────
        public void Execute()
        {
            OutVertexCount[0] = 0;
            OutIndexCount[0]  = 0;
            if (PointCount < 2) return;

            // Collapse consecutive duplicate points (zero-length segment → NaN normals).
            var pts = new NativeArray<double2>(PointCount, Allocator.Temp);
            int n = 0;
            pts[n++] = InputPoints[0];
            for (int i = 1; i < PointCount; i++)
            {
                double2 prev = pts[n - 1];
                double2 cur  = InputPoints[i];
                if (math.abs(cur.x - prev.x) > 1e-12 || math.abs(cur.y - prev.y) > 1e-12)
                    pts[n++] = cur;
            }
            if (n < 2) { pts.Dispose(); return; }

            int roundSegments = RoundSegments < 1 ? 1 : RoundSegments;
            double miterLimit = MiterLimit < 1.0 ? 1.0 : MiterLimit;

            // Segment tangents and left normals.
            var tangent   = new NativeArray<double2>(n - 1, Allocator.Temp);
            var segNormal = new NativeArray<double2>(n - 1, Allocator.Temp);
            for (int i = 0; i < n - 1; i++)
            {
                double dx  = pts[i + 1].x - pts[i].x;
                double dy  = pts[i + 1].y - pts[i].y;
                double len = math.sqrt(dx * dx + dy * dy);
                tangent[i]   = new double2(dx / len, dy / len);
                segNormal[i] = new double2(-dy / len, dx / len); // CCW 90° of tangent = left normal
            }

            // Cumulative arc lengths.
            var cumDist = new NativeArray<double>(n, Allocator.Temp);
            cumDist[0] = 0.0;
            for (int i = 1; i < n; i++)
            {
                double dx = pts[i].x - pts[i - 1].x;
                double dy = pts[i].y - pts[i - 1].y;
                cumDist[i] = cumDist[i - 1] + math.sqrt(dx * dx + dy * dy);
            }

            int v = 0, idx = 0;

            // Start cap: after this, verts[v-2]=left, verts[v-1]=right.
            EmitStartCap(pts[0], segNormal[0], tangent[0], cumDist[0], roundSegments, ref v, ref idx);
            int leftPrev  = v - 2;
            int rightPrev = v - 1;

            for (int seg = 0; seg < n - 1; seg++)
            {
                bool isLast  = (seg == n - 2);
                double2 p2   = pts[seg + 1];
                double2 n1   = segNormal[seg];
                double dist2 = cumDist[seg + 1];

                if (!isLast)
                {
                    double2 n2 = segNormal[seg + 1];
                    double2 t1 = tangent[seg];
                    double2 t2 = tangent[seg + 1];

                    double cross = t1.x * t2.y - t1.y * t2.x;
                    bool leftTurn = cross > 0;

                    bool bevel = (Join == JoinType.Bevel) ||
                                 (Join == JoinType.Miter && NeedsBevel(n1, n2, miterLimit));

                    if (Join == JoinType.Round)
                    {
                        EmitRoundJoin(p2, n1, n2, dist2, roundSegments, leftTurn, leftPrev, rightPrev,
                                      out leftPrev, out rightPrev, ref v, ref idx);
                    }
                    else if (bevel)
                    {
                        EmitBevelJoin(p2, n1, n2, dist2, leftTurn, leftPrev, rightPrev,
                                      out leftPrev, out rightPrev, ref v, ref idx);
                    }
                    else
                    {
                        ComputeMiterNormals(n1, n2, out double2 miterL, out double2 miterR);
                        int lNext = v;
                        int rNext = v + 1;
                        AddVertex(ref v, MakeVertex(p2, miterL, dist2, +1f));
                        AddVertex(ref v, MakeVertex(p2, miterR, dist2, -1f));
                        EmitQuad(ref idx, leftPrev, rightPrev, lNext, rNext);
                        leftPrev  = lNext;
                        rightPrev = rNext;
                    }
                }
                else
                {
                    int lNext = v;
                    int rNext = v + 1;
                    AddVertex(ref v, MakeVertex(p2,  n1,    dist2, +1f));
                    AddVertex(ref v, MakeVertex(p2, Neg(n1), dist2, -1f));
                    EmitQuad(ref idx, leftPrev, rightPrev, lNext, rNext);
                    leftPrev  = lNext;
                    rightPrev = rNext;
                }
            }

            EmitEndCap(pts[n - 1], segNormal[n - 2], tangent[n - 2], cumDist[n - 1], roundSegments,
                       leftPrev, rightPrev, ref v, ref idx);

            OutVertexCount[0] = v;
            OutIndexCount[0]  = idx;

            pts.Dispose();
            tangent.Dispose();
            segNormal.Dispose();
            cumDist.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Normal computation (mirrors LineTessellator)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private static void ComputeMiterNormals(double2 n1, double2 n2, out double2 leftN, out double2 rightN)
        {
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = math.sqrt(mx * mx + my * my);

            if (mLen < 1e-12) { leftN = n1; rightN = Neg(n1); return; }

            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y; // cos(θ/2)
            if (math.abs(dot) < 1e-12) { leftN = n1; rightN = Neg(n1); return; }

            double miterFactor = 1.0 / dot;
            leftN  = new double2(mux * miterFactor,  muy * miterFactor);
            rightN = new double2(-mux * miterFactor, -muy * miterFactor);
        }

        private static bool NeedsBevel(double2 n1, double2 n2, double miterLimit)
        {
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = math.sqrt(mx * mx + my * my);
            if (mLen < 1e-12) return true;
            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12) return true;
            return (1.0 / dot) > miterLimit;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Join geometry (mirrors LineTessellator)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private void EmitBevelJoin(
            double2 p, double2 n1, double2 n2, double dist,
            bool leftTurn, int leftPrev, int rightPrev,
            out int leftNext, out int rightNext, ref int v, ref int idx)
        {
            double ix   = n1.x + n2.x;
            double iy   = n1.y + n2.y;
            double iLen = math.sqrt(ix * ix + iy * iy);
            double2 innerN = (iLen > 1e-12) ? new double2(ix / iLen, iy / iLen) : n1;

            if (leftTurn)
            {
                int outerA = v, innerV = v + 1, outerB = v + 2;
                AddVertex(ref v, MakeVertex(p,  n1,        dist, +1f));
                AddVertex(ref v, MakeVertex(p, Neg(innerN), dist, -1f));
                AddVertex(ref v, MakeVertex(p,  n2,        dist, +1f));

                EmitQuad(ref idx, leftPrev, rightPrev, outerA, innerV);
                AddIndex(ref idx, outerA);
                AddIndex(ref idx, outerB);
                AddIndex(ref idx, innerV);

                leftNext  = outerB;
                rightNext = innerV;
            }
            else
            {
                int innerV = v, outerA = v + 1, outerB = v + 2;
                AddVertex(ref v, MakeVertex(p,  innerN,  dist, +1f));
                AddVertex(ref v, MakeVertex(p, Neg(n1),  dist, -1f));
                AddVertex(ref v, MakeVertex(p, Neg(n2),  dist, -1f));

                EmitQuad(ref idx, leftPrev, rightPrev, innerV, outerA);
                AddIndex(ref idx, outerA);
                AddIndex(ref idx, innerV);
                AddIndex(ref idx, outerB);

                leftNext  = innerV;
                rightNext = outerB;
            }
        }

        private void EmitRoundJoin(
            double2 p, double2 n1, double2 n2, double dist,
            int roundSegments, bool leftTurn, int leftPrev, int rightPrev,
            out int leftNext, out int rightNext, ref int v, ref int idx)
        {
            double ix   = n1.x + n2.x;
            double iy   = n1.y + n2.y;
            double iLen = math.sqrt(ix * ix + iy * iy);
            double2 innerN = (iLen > 1e-12) ? new double2(ix / iLen, iy / iLen) : n1;

            double2 arcStart = leftTurn ?  n1 : Neg(n1);
            double2 arcEnd   = leftTurn ?  n2 : Neg(n2);

            double a0 = math.atan2(arcStart.y, arcStart.x);
            double a1 = math.atan2(arcEnd.y,   arcEnd.x);
            if (leftTurn) { while (a1 < a0) a1 += 2.0 * math.PI_DBL; }
            else          { while (a1 > a0) a1 -= 2.0 * math.PI_DBL; }

            int innerIdx = v;
            if (leftTurn) AddVertex(ref v, MakeVertex(p, Neg(innerN), dist, -1f));
            else          AddVertex(ref v, MakeVertex(p,  innerN,    dist, +1f));

            int arcStartIdx = v;
            if (leftTurn) AddVertex(ref v, MakeVertex(p, arcStart, dist, +1f));
            else          AddVertex(ref v, MakeVertex(p, arcStart, dist, -1f));

            if (leftTurn) EmitQuad(ref idx, leftPrev, rightPrev, arcStartIdx, innerIdx);
            else          EmitQuad(ref idx, leftPrev, rightPrev, innerIdx, arcStartIdx);

            int prevFanIdx = arcStartIdx;
            for (int k = 1; k <= roundSegments; k++)
            {
                double t   = (double)k / (roundSegments + 1);
                double ang = a0 + t * (a1 - a0);
                int fanIdx = v;
                if (leftTurn) AddVertex(ref v, MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));
                else          AddVertex(ref v, MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, -1f));

                if (leftTurn) { AddIndex(ref idx, prevFanIdx); AddIndex(ref idx, fanIdx); AddIndex(ref idx, innerIdx); }
                else          { AddIndex(ref idx, innerIdx); AddIndex(ref idx, fanIdx); AddIndex(ref idx, prevFanIdx); }

                prevFanIdx = fanIdx;
            }

            int arcEndIdx = v;
            if (leftTurn) AddVertex(ref v, MakeVertex(p, arcEnd, dist, +1f));
            else          AddVertex(ref v, MakeVertex(p, arcEnd, dist, -1f));

            if (leftTurn) { AddIndex(ref idx, prevFanIdx); AddIndex(ref idx, arcEndIdx); AddIndex(ref idx, innerIdx); }
            else          { AddIndex(ref idx, innerIdx); AddIndex(ref idx, arcEndIdx); AddIndex(ref idx, prevFanIdx); }

            if (leftTurn) { leftNext = arcEndIdx; rightNext = innerIdx; }
            else          { leftNext = innerIdx; rightNext = arcEndIdx; }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Caps (mirrors LineTessellator)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private void EmitStartCap(
            double2 p, double2 segNormal, double2 segTangent, double dist,
            int roundSegments, ref int v, ref int idx)
        {
            switch (Cap)
            {
                case CapType.Butt:
                    AddVertex(ref v, MakeVertex(p,  segNormal,    dist, +1f));
                    AddVertex(ref v, MakeVertex(p, Neg(segNormal), dist, -1f));
                    break;

                case CapType.Square:
                {
                    double2 lN = new double2(segNormal.x - segTangent.x, segNormal.y - segTangent.y);
                    double2 rN = new double2(-segNormal.x - segTangent.x, -segNormal.y - segTangent.y);
                    AddVertex(ref v, MakeVertex(p, lN, dist, +1f));
                    AddVertex(ref v, MakeVertex(p, rN, dist, -1f));
                    break;
                }

                case CapType.Round:
                {
                    double a0 = math.atan2(-segNormal.y, -segNormal.x); // rightButt direction
                    double a1 = math.atan2( segNormal.y,  segNormal.x); // leftButt direction
                    while (a1 > a0) a1 -= 2.0 * math.PI_DBL;

                    int baseIdx      = v;
                    int centerIdx    = baseIdx;
                    int leftButtIdx  = baseIdx + roundSegments + 1;
                    int rightButtIdx = baseIdx + roundSegments + 2;

                    AddVertex(ref v, MakeVertex(p, new double2(0.0, 0.0), dist, 0f)); // center pivot

                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double t   = (double)k / (roundSegments + 1);
                        double ang = a0 + t * (a1 - a0);
                        AddVertex(ref v, MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));
                    }

                    AddVertex(ref v, MakeVertex(p,  segNormal,    dist, +1f));  // leftButt  [v-2]
                    AddVertex(ref v, MakeVertex(p, Neg(segNormal), dist, -1f)); // rightButt [v-1]

                    int prevFan = rightButtIdx;
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        int fanIdx = baseIdx + k;
                        AddIndex(ref idx, centerIdx);
                        AddIndex(ref idx, fanIdx);
                        AddIndex(ref idx, prevFan);
                        prevFan = fanIdx;
                    }
                    AddIndex(ref idx, centerIdx);
                    AddIndex(ref idx, leftButtIdx);
                    AddIndex(ref idx, prevFan);
                    break;
                }
            }
        }

        private void EmitEndCap(
            double2 p, double2 segNormal, double2 segTangent, double dist,
            int roundSegments, int leftPrev, int rightPrev, ref int v, ref int idx)
        {
            switch (Cap)
            {
                case CapType.Butt:
                    break;

                case CapType.Square:
                {
                    double2 lN = new double2(segNormal.x + segTangent.x,  segNormal.y + segTangent.y);
                    double2 rN = new double2(-segNormal.x + segTangent.x, -segNormal.y + segTangent.y);
                    int lEnd = v, rEnd = v + 1;
                    AddVertex(ref v, MakeVertex(p, lN, dist, +1f));
                    AddVertex(ref v, MakeVertex(p, rN, dist, -1f));
                    EmitQuad(ref idx, leftPrev, rightPrev, lEnd, rEnd);
                    break;
                }

                case CapType.Round:
                {
                    double a0 = math.atan2( segNormal.y,  segNormal.x);  // leftPrev direction
                    double a1 = math.atan2(-segNormal.y, -segNormal.x);  // rightPrev direction
                    while (a1 > a0) a1 -= 2.0 * math.PI_DBL;

                    int centerIdx = v;
                    AddVertex(ref v, MakeVertex(p, new double2(0.0, 0.0), dist, 0f)); // center pivot

                    int prevFanIdx = leftPrev;
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double t   = (double)k / (roundSegments + 1);
                        double ang = a0 + t * (a1 - a0);
                        int fanIdx = v;
                        AddVertex(ref v, MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));

                        AddIndex(ref idx, centerIdx);
                        AddIndex(ref idx, fanIdx);
                        AddIndex(ref idx, prevFanIdx);
                        prevFanIdx = fanIdx;
                    }
                    AddIndex(ref idx, centerIdx);
                    AddIndex(ref idx, rightPrev);
                    AddIndex(ref idx, prevFanIdx);
                    break;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Utilities
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private void AddVertex(ref int v, LineVertex vert) => OutVertices[v++] = vert;

        private void AddIndex(ref int idx, int value) => OutIndices[idx++] = value;

        private void EmitQuad(ref int idx, int L0, int R0, int L1, int R1)
        {
            AddIndex(ref idx, L0); AddIndex(ref idx, R0); AddIndex(ref idx, R1);
            AddIndex(ref idx, L0); AddIndex(ref idx, R1); AddIndex(ref idx, L1);
        }

        private static LineVertex MakeVertex(double2 pos, double2 normal, double dist, float side)
            => new LineVertex
            {
                Position      = pos,
                Normal        = normal,
                DistanceAlong = dist,
                Side          = side,
                WidthScale    = 1.0f,
            };

        private static double2 Neg(double2 v) => new double2(-v.x, -v.y);
    }
}
