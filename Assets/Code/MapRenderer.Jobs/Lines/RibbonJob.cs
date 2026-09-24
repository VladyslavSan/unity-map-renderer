using MapRenderer.Core.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// Burst-compiled 3D ribbon builder over an already-projected, origin-relative centerline plus per-point
    /// surface <c>up</c>, so one code path serves every projection. Joins, caps and indices extrude along
    /// <c>across = normalize(cross(along, up))</c>, which keeps winding uniform across projections. Output
    /// winding is CCW (pinned by <c>GlobeLineWindingTests</c>). Round arcs sweep in the local tangent plane.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct RibbonJob : IJob
    {
        // ── Input ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Projected centerline: origin-relative render-space points.</summary>
        [ReadOnly] public NativeArray<double3> Points;

        /// <summary>Per-point surface up (unit); parallel to <see cref="Points"/>. Constant +Y for planar Mercator.</summary>
        [ReadOnly] public NativeArray<double3> Ups;

        /// <summary>Number of valid points in <see cref="Points"/>/<see cref="Ups"/>.</summary>
        [ReadOnly] public int PointCount;

        /// <summary>Corner geometry style.</summary>
        public JoinType Join;

        /// <summary>Endpoint geometry style.</summary>
        public CapType Cap;

        /// <summary>Maximum miter ratio (1/cos(θ/2)); falls back to bevel when exceeded. Clamped to ≥ 1.</summary>
        public double MiterLimit;

        /// <summary>Arc divisions per round join/cap half. Clamped to ≥ 1.</summary>
        public int RoundSegments;

        /// <summary>Minimum miter ratio (1/cos(θ/2)) a <see cref="JoinType.Round"/> join needs before it emits
        /// a fan; at or below this the corner is shallow enough that the fan would be imperceptible, so the
        /// join collapses to the miter path instead. Clamped to ≥ 1.</summary>
        public double RoundLimit;

        // ── Output ────────────────────────────────────────────────────────────────────────────
        /// <summary>Ribbon vertices in emission order (written [0..OutVertexCount[0])).</summary>
        [WriteOnly] public NativeArray<LineRibbonVertex> OutVertices;

        /// <summary>Triangle indices, 3 per triangle (written [0..OutIndexCount[0])).</summary>
        [WriteOnly] public NativeArray<int> OutIndices;

        /// <summary>[0] = number of vertices written.</summary>
        public NativeArray<int> OutVertexCount;

        /// <summary>[0] = number of indices written.</summary>
        public NativeArray<int> OutIndexCount;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Worst-case sizing — bounded by the join/cap emission shape below.
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Upper bound on vertices for <paramref name="pointCount"/> raw input points.</summary>
        public static int MaxVertexCount(int pointCount, int roundSegments)
        {
            if (pointCount < 2) return 0;
            int rs = roundSegments < 1 ? 1 : roundSegments;
            int n = pointCount;
            // A round cap adds one co-located seed vertex but no triangle: start = pivot + rs arc + seed +
            // 2 butts; end = pivot + rs arc + seed.
            int startCap = rs + 4;
            int perJoin  = rs + 3;
            int joins    = n - 2 < 0 ? 0 : n - 2;
            int lastSeg  = 2;
            int endCap   = rs + 2;
            return startCap + joins * perJoin + lastSeg + endCap;
        }

        /// <summary>Upper bound on indices for <paramref name="pointCount"/> raw input points.</summary>
        public static int MaxIndexCount(int pointCount, int roundSegments)
        {
            if (pointCount < 2) return 0;
            int rs = roundSegments < 1 ? 1 : roundSegments;
            int n = pointCount;
            int startCap = 3 * (rs + 1);
            int perJoin  = 6 + 3 * (rs + 1);
            int joins    = n - 2 < 0 ? 0 : n - 2;
            int lastSeg  = 6;
            int endCap   = 3 * (rs + 1);
            return startCap + joins * perJoin + lastSeg + endCap;
        }

        // ── IJob ──────────────────────────────────────────────────────────────────────────────
        // The working buffers are Temp LOCALS: the safety system rejects a default container FIELD at schedule.
        public void Execute()
        {
            OutVertexCount[0] = 0;
            OutIndexCount[0]  = 0;
            if (PointCount < 2) return;

            // Collapse consecutive duplicate points (zero-length segment → NaN direction). Points are
            // origin-relative metres; 1e-9 catches truly-coincident decode repeats without merging distinct verts.
            var pts = new NativeArray<double3>(PointCount, Allocator.Temp);
            var ups = new NativeArray<double3>(PointCount, Allocator.Temp);
            int n = 0;
            pts[n] = Points[0]; ups[n] = Ups[0]; n++;
            for (int i = 1; i < PointCount; i++)
            {
                double3 prev = pts[n - 1];
                double3 cur  = Points[i];
                if (math.abs(cur.x - prev.x) > 1e-9 || math.abs(cur.y - prev.y) > 1e-9 || math.abs(cur.z - prev.z) > 1e-9)
                {
                    pts[n] = cur; ups[n] = Ups[i]; n++;
                }
            }
            if (n < 2) { pts.Dispose(); ups.Dispose(); return; }

            int roundSegments = RoundSegments < 1 ? 1 : RoundSegments;
            double miterLimit = MiterLimit < 1.0 ? 1.0 : MiterLimit;
            double roundLimit = RoundLimit < 1.0 ? 1.0 : RoundLimit;

            // Segment directions + cumulative arc lengths (3D).
            var along   = new NativeArray<double3>(n - 1, Allocator.Temp);
            var cumDist = new NativeArray<double>(n, Allocator.Temp);
            cumDist[0] = 0.0;
            for (int i = 0; i < n - 1; i++)
            {
                double3 d   = pts[i + 1] - pts[i];
                double  len = math.length(d);
                along[i]    = d / len;
                cumDist[i + 1] = cumDist[i] + len;
            }

            int v = 0, idx = 0;

            // Start cap — after this, verts[v-2]=left, verts[v-1]=right.
            EmitStartCap(pts[0], ups[0], along[0], cumDist[0], roundSegments, ref v, ref idx);
            int leftPrev  = v - 2;
            int rightPrev = v - 1;

            for (int seg = 0; seg < n - 1; seg++)
            {
                bool isLast = (seg == n - 2);
                int  jp     = seg + 1;                       // join / terminal point index
                double3 p2   = pts[jp];
                double3 upJ  = ups[jp];
                double  dist2 = cumDist[jp];

                if (!isLast)
                {
                    double3 n1 = Across(along[seg],     upJ);  // incoming segment across, at the join point's up
                    double3 n2 = Across(along[seg + 1], upJ);  // outgoing segment across, same up

                    // Non-obvious why: the test is dot < 0, because with 2D (x,y) mapped to 3D (x,0,y) the planar
                    // cross product's z equals −dot(cross(in,out), up). The emitters decide which side is convex.
                    bool leftTurn = math.dot(math.cross(along[seg], along[seg + 1]), upJ) < 0.0;

                    // A shallow Round join (f ≤ roundLimit) collapses to a miter that is still subject to
                    // miterLimit, because the style sets the two limits independently: round→miter→bevel.
                    bool roundCollapsedToMiter = Join == JoinType.Round && NeedsMiter(n1, n2, roundLimit);
                    bool bevel = (Join == JoinType.Bevel) ||
                                 (Join == JoinType.Miter && NeedsBevel(n1, n2, miterLimit)) ||
                                 (roundCollapsedToMiter && NeedsBevel(n1, n2, miterLimit));

                    if (Join == JoinType.Round && !roundCollapsedToMiter)
                    {
                        EmitRoundJoin(p2, upJ, n1, n2, dist2, roundSegments, leftTurn, leftPrev, rightPrev,
                                      miterLimit, out leftPrev, out rightPrev, ref v, ref idx);
                    }
                    else if (bevel)
                    {
                        EmitBevelJoin(p2, upJ, n1, n2, dist2, leftTurn, leftPrev, rightPrev,
                                      miterLimit, out leftPrev, out rightPrev, ref v, ref idx);
                    }
                    else
                    {
                        // Miter join — also reached by a shallow JoinType.Round join that collapsed here
                        // and did not ALSO exceed miterLimit (see roundCollapsedToMiter above).
                        ComputeMiterNormals(n1, n2, out double3 miterL, out double3 miterR);
                        int lNext = v;
                        int rNext = v + 1;
                        AddVertex(ref v, MakeVertex(p2, miterL, upJ, dist2, +1f));
                        AddVertex(ref v, MakeVertex(p2, miterR, upJ, dist2, -1f));
                        EmitQuad(ref idx, leftPrev, rightPrev, lNext, rNext);
                        leftPrev  = lNext;
                        rightPrev = rNext;
                    }
                }
                else
                {
                    double3 n1 = Across(along[seg], upJ);
                    int lNext = v;
                    int rNext = v + 1;
                    AddVertex(ref v, MakeVertex(p2,  n1, upJ, dist2, +1f));
                    AddVertex(ref v, MakeVertex(p2, -n1, upJ, dist2, -1f));
                    EmitQuad(ref idx, leftPrev, rightPrev, lNext, rNext);
                    leftPrev  = lNext;
                    rightPrev = rNext;
                }
            }

            EmitEndCap(pts[n - 1], ups[n - 1], along[n - 2], cumDist[n - 1], roundSegments,
                       leftPrev, rightPrev, ref v, ref idx);

            OutVertexCount[0] = v;
            OutIndexCount[0]  = idx;

            pts.Dispose();
            ups.Dispose();
            along.Dispose();
            cumDist.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Direction — the ONE substitution (2D (−dy,dx) → 3D cross(along, up)); everything else ports 1:1.
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Unit extrusion across-direction for a segment running in <paramref name="along"/> at a point
        /// whose surface up is <paramref name="up"/> — <c>normalize(cross(along, up))</c>. The <c>cross(along, up)</c>
        /// sign, against <c>cross(up, along)</c>, is calibrated so the flat Mercator ribbon reproduces the
        /// 2D left normal; the same formula then winds the globe identically.</summary>
        private static double3 Across(double3 along, double3 up)
            => math.normalize(math.cross(along, up));

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Miter
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the join bisector direction and half-angle cosine shared by
        /// <see cref="ComputeMiterNormals"/>, <see cref="ComputeInnerNormal"/> and
        /// <see cref="NeedsBevel"/> — the one statement of this arithmetic in the file. Returns false at
        /// a 180° hairpin (the normals cancel), in which case <paramref name="mu"/> is set to
        /// <paramref name="n1"/> and <paramref name="cosHalf"/> to 1.0 as an inert fallback.
        /// </summary>
        private static bool TryJoinBisector(double3 n1, double3 n2, out double3 mu, out double cosHalf)
        {
            double3 m    = n1 + n2;
            double  mLen = math.length(m);
            if (mLen < 1e-12) { mu = n1; cosHalf = 1.0; return false; }

            mu      = m / mLen;
            cosHalf = math.dot(mu, n1); // cos(θ/2)
            return true;
        }

        private static void ComputeMiterNormals(double3 n1, double3 n2, out double3 leftN, out double3 rightN)
        {
            if (!TryJoinBisector(n1, n2, out double3 mu, out double cosHalf))
            { leftN = n1; rightN = -n1; return; }

            if (math.abs(cosHalf) < 1e-12) { leftN = n1; rightN = -n1; return; }

            double miterFactor = 1.0 / cosHalf;
            leftN  =  mu * miterFactor;
            rightN = -mu * miterFactor;
        }

        /// <summary>
        /// Inner-vertex normal for a bevel/round join: the SAME miter-factor magnitude the miter
        /// join's <see cref="ComputeMiterNormals"/> emits, saturated at <paramref name="miterLimit"/>
        /// instead of falling back to bevel. Returns the MAGNITUDE along <c>+mu</c> only; the caller applies
        /// the turn-direction sign when placing the vertex on the concave side.
        /// </summary>
        private static double3 ComputeInnerNormal(double3 n1, double3 n2, double miterLimit)
        {
            if (!TryJoinBisector(n1, n2, out double3 mu, out double cosHalf))
                return n1; // 180° hairpin.

            if (math.abs(cosHalf) < 1e-12)
                // Saturated answer; avoids 1/0 → ±Inf through Burst. mu's DIRECTION here is
                // floating-point noise; only the saturated magnitude is relied on.
                return mu * miterLimit;

            double innerFactor = math.min(1.0 / math.abs(cosHalf), miterLimit);
            return mu * innerFactor;
        }

        internal static bool NeedsBevel(double3 n1, double3 n2, double miterLimit)
        {
            if (!TryJoinBisector(n1, n2, out double3 mu, out double cosHalf)) return true;
            if (math.abs(cosHalf) < 1e-12) return true;
            // Magnitude test: near a 180° hairpin dot can go small-NEGATIVE,
            // and a signed 1/dot > limit lets a huge negative miter factor slip past the bevel gate (the glitch).
            return math.abs(1.0 / cosHalf) > miterLimit;
        }

        /// <summary>True when a Round join's corner is shallow enough (miter ratio ≤
        /// <paramref name="roundLimit"/>) to collapse to the miter path instead of a fan.</summary>
        internal static bool NeedsMiter(double3 n1, double3 n2, double roundLimit)
        {
            if (!TryJoinBisector(n1, n2, out double3 mu, out double cosHalf)) return false;
            if (math.abs(cosHalf) < 1e-12) return false;
            return math.abs(1.0 / cosHalf) <= roundLimit;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Join geometry
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private void EmitBevelJoin(
            double3 p, double3 up, double3 n1, double3 n2, double dist,
            bool leftTurn, int leftPrev, int rightPrev, double miterLimit,
            out int leftNext, out int rightNext, ref int v, ref int idx)
        {
            double3 innerN = ComputeInnerNormal(n1, n2, miterLimit);

            // The two branches below are mirror images, not unifiable: a reflection reverses triangle
            // orientation, so the chamfer index order that is CCW in one branch is CW in the other.
            if (leftTurn)
            {
                // Left turn ⇒ concave = left, convex = right.
                int outerA = v, innerV = v + 1, outerB = v + 2;
                AddVertex(ref v, MakeVertex(p, -n1,       up, dist, -1f));
                AddVertex(ref v, MakeVertex(p,  innerN,   up, dist, +1f));
                AddVertex(ref v, MakeVertex(p, -n2,       up, dist, -1f));

                EmitQuad(ref idx, leftPrev, rightPrev, innerV, outerA);
                AddIndex(ref idx, outerA);
                AddIndex(ref idx, outerB);
                AddIndex(ref idx, innerV);

                leftNext  = innerV;
                rightNext = outerB;
            }
            else
            {
                // Right turn ⇒ concave = right, convex = left.
                int innerV = v, outerA = v + 1, outerB = v + 2;
                AddVertex(ref v, MakeVertex(p, -innerN,   up, dist, -1f));
                AddVertex(ref v, MakeVertex(p,  n1,       up, dist, +1f));
                AddVertex(ref v, MakeVertex(p,  n2,       up, dist, +1f));

                EmitQuad(ref idx, leftPrev, rightPrev, outerA, innerV);
                AddIndex(ref idx, outerA);
                AddIndex(ref idx, innerV);
                AddIndex(ref idx, outerB);

                leftNext  = outerB;
                rightNext = innerV;
            }
        }

        private void EmitRoundJoin(
            double3 p, double3 up, double3 n1, double3 n2, double dist,
            int roundSegments, bool leftTurn, int leftPrev, int rightPrev, double miterLimit,
            out int leftNext, out int rightNext, ref int v, ref int idx)
        {
            double3 innerN = ComputeInnerNormal(n1, n2, miterLimit);

            // Convex-rim arc start/end: a left turn's convex side is the right, i.e. −n1/−n2.
            double3 arcStart = leftTurn ? -n1 : n1;
            double3 arcEnd   = leftTurn ? -n2 : n2;

            // Sweep the convex arc in the tangent-plane basis (e0 = arcStart, e1 ⊥ e0); the sweep angle is
            // measured in the same basis, so the sign of e1 cancels out.
            double3 e0 = arcStart;
            double3 e1 = math.normalize(math.cross(up, e0));
            double  sweep = math.atan2(math.dot(arcEnd, e1), math.dot(arcEnd, e0));

            int innerIdx = v;
            if (leftTurn) AddVertex(ref v, MakeVertex(p,  innerN, up, dist, +1f));
            else          AddVertex(ref v, MakeVertex(p, -innerN, up, dist, -1f));

            int arcStartIdx = v;
            if (leftTurn) AddVertex(ref v, MakeVertex(p, arcStart, up, dist, -1f));
            else          AddVertex(ref v, MakeVertex(p, arcStart, up, dist, +1f));

            if (leftTurn) EmitQuad(ref idx, leftPrev, rightPrev, innerIdx, arcStartIdx);
            else          EmitQuad(ref idx, leftPrev, rightPrev, arcStartIdx, innerIdx);

            int prevFanIdx = arcStartIdx;
            for (int k = 1; k <= roundSegments; k++)
            {
                double  t   = (double)k / (roundSegments + 1);
                double  ang = t * sweep;
                double3 dir = math.cos(ang) * e0 + math.sin(ang) * e1;
                int fanIdx = v;
                if (leftTurn) AddVertex(ref v, MakeVertex(p, dir, up, dist, -1f));
                else          AddVertex(ref v, MakeVertex(p, dir, up, dist, +1f));

                if (leftTurn) { AddIndex(ref idx, prevFanIdx); AddIndex(ref idx, fanIdx); AddIndex(ref idx, innerIdx); }
                else          { AddIndex(ref idx, innerIdx); AddIndex(ref idx, fanIdx); AddIndex(ref idx, prevFanIdx); }

                prevFanIdx = fanIdx;
            }

            int arcEndIdx = v;
            if (leftTurn) AddVertex(ref v, MakeVertex(p, arcEnd, up, dist, -1f));
            else          AddVertex(ref v, MakeVertex(p, arcEnd, up, dist, +1f));

            if (leftTurn) { AddIndex(ref idx, prevFanIdx); AddIndex(ref idx, arcEndIdx); AddIndex(ref idx, innerIdx); }
            else          { AddIndex(ref idx, innerIdx); AddIndex(ref idx, arcEndIdx); AddIndex(ref idx, prevFanIdx); }

            if (leftTurn) { leftNext = innerIdx; rightNext = arcEndIdx; }
            else          { leftNext = arcEndIdx; rightNext = innerIdx; }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Caps (the round half-circle is swept in the (across, along) basis)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private void EmitStartCap(
            double3 p, double3 up, double3 along, double dist, int roundSegments, ref int v, ref int idx)
        {
            double3 across = Across(along, up);

            switch (Cap)
            {
                case CapType.Butt:
                    AddVertex(ref v, MakeVertex(p,  across, up, dist, +1f));
                    AddVertex(ref v, MakeVertex(p, -across, up, dist, -1f));
                    break;

                case CapType.Square:
                {
                    // Backward half-tangent baked into the normal (shader ½·width → ½·width backward extension).
                    AddVertex(ref v, MakeVertex(p,  across - along, up, dist, +1f));
                    AddVertex(ref v, MakeVertex(p, -across - along, up, dist, -1f));
                    break;
                }

                case CapType.Round:
                {
                    // Half-circle bulging BEHIND the start (through −along). Sweep t·π from rightButt (−across),
                    // through −along at t=½, to leftButt (+across): dir = −cos(tπ)·across − sin(tπ)·along.
                    int baseIdx      = v;
                    int centerIdx    = baseIdx;
                    int capSeedIdx   = baseIdx + roundSegments + 1;
                    int leftButtIdx  = baseIdx + roundSegments + 2;
                    int rightButtIdx = baseIdx + roundSegments + 3;

                    AddVertex(ref v, MakeVertex(p, double3.zero, up, dist, 0f)); // center pivot

                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double  t   = (double)k / (roundSegments + 1);
                        double3 dir = -math.cos(t * math.PI_DBL) * across - math.sin(t * math.PI_DBL) * along;
                        AddVertex(ref v, MakeVertex(p, dir, up, dist, +1f));
                    }

                    // Non-obvious why: the fan seed sits at rightButt but is tagged +1, so the seam edge has no
                    // |side| = 0 midpoint. It precedes the butts, so verts[v-2]/[v-1] stay left/right.
                    AddVertex(ref v, MakeVertex(p, -across, up, dist, +1f));  // capSeed

                    AddVertex(ref v, MakeVertex(p,  across, up, dist, +1f));  // leftButt  [v-2]
                    AddVertex(ref v, MakeVertex(p, -across, up, dist, -1f));  // rightButt [v-1]

                    int prevFan = capSeedIdx;
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
            double3 p, double3 up, double3 along, double dist, int roundSegments,
            int leftPrev, int rightPrev, ref int v, ref int idx)
        {
            double3 across = Across(along, up);

            switch (Cap)
            {
                case CapType.Butt:
                    break;

                case CapType.Square:
                {
                    // Forward half-tangent baked in (extends the ribbon forward).
                    int lEnd = v, rEnd = v + 1;
                    AddVertex(ref v, MakeVertex(p,  across + along, up, dist, +1f));
                    AddVertex(ref v, MakeVertex(p, -across + along, up, dist, -1f));
                    EmitQuad(ref idx, leftPrev, rightPrev, lEnd, rEnd);
                    break;
                }

                case CapType.Round:
                {
                    // Half-circle bulging PAST the end (through +along). Sweep t·π from leftPrev (+across),
                    // through +along at t=½, to rightPrev (−across): dir = cos(tπ)·across + sin(tπ)·along.
                    int centerIdx = v;
                    AddVertex(ref v, MakeVertex(p, double3.zero, up, dist, 0f)); // center pivot

                    int prevFanIdx = leftPrev;
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double  t   = (double)k / (roundSegments + 1);
                        double3 dir = math.cos(t * math.PI_DBL) * across + math.sin(t * math.PI_DBL) * along;
                        int fanIdx = v;
                        AddVertex(ref v, MakeVertex(p, dir, up, dist, +1f));

                        AddIndex(ref idx, centerIdx);
                        AddIndex(ref idx, fanIdx);
                        AddIndex(ref idx, prevFanIdx);
                        prevFanIdx = fanIdx;
                    }
                    // Closing seed: at rightPrev's position but tagged +1, so the outer edge runs +1 → +1;
                    // rightPrev stays −1 for the ribbon quad. Same reason as EmitStartCap's capSeed.
                    int capSeedIdx = v;
                    AddVertex(ref v, MakeVertex(p, -across, up, dist, +1f));
                    AddIndex(ref idx, centerIdx);
                    AddIndex(ref idx, capSeedIdx);
                    AddIndex(ref idx, prevFanIdx);
                    break;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Utilities
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private void AddVertex(ref int v, LineRibbonVertex vert) => OutVertices[v++] = vert;

        private void AddIndex(ref int idx, int value) => OutIndices[idx++] = value;

        private void EmitQuad(ref int idx, int L0, int R0, int L1, int R1)
        {
            AddIndex(ref idx, L0); AddIndex(ref idx, R0); AddIndex(ref idx, R1);
            AddIndex(ref idx, L0); AddIndex(ref idx, R1); AddIndex(ref idx, L1);
        }

        private static LineRibbonVertex MakeVertex(double3 pos, double3 across, double3 up, double dist, float side)
            => new LineRibbonVertex
            {
                Position      = pos,
                Across        = across,
                Up            = up,
                DistanceAlong = dist,
                Side          = side,
                WidthScale    = 1.0f,
            };
    }
}
