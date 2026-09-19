using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Clean-room polyline tessellator. Converts a 2D polyline into a ribbon-mesh where each
    /// vertex sits on the centerline and the vertex shader offsets it laterally by ½·widthMeters.
    ///
    /// Implemented from first principles (geometry of normals/joins/caps, not from MapLibre source).
    ///
    /// Coordinate space: space-agnostic. For S05, caller passes world-meter coordinates.
    ///
    /// Normal contract (IMPORTANT — packing note):
    ///   • Straight/terminal, cap-rim, and bevel/round OUTER vertices: |normal| = 1.
    ///   • Miter-join vertices, and the bevel/round INNER vertex: |normal| = min(1/cos(θ/2), miterLimit) —
    ///     the miter factor, saturated at miterLimit for the inner vertex instead of falling back to bevel.
    ///   • Do NOT store as SNORM (unit-only format) — miter/inner normals exceed length 1.
    ///   • Vertex shader: worldPos += normal * 0.5 * widthMeters (uniform, no side multiplier).
    ///   • Side ∈ {+1, −1} is used only by the fragment shader for AA feathering.
    ///
    /// Join bevel/round geometry principle:
    ///   At an interior point, one side is the "outer" (convex) side and one is the "inner"
    ///   (concave) side. The outer side gets the extra bevel/fan geometry; the inner side gets
    ///   a single miter-like vertex — the SAME bisector-direction, miter-factor-magnitude vertex
    ///   the miter join would emit at that corner, clamped at miterLimit (see ComputeInnerNormal).
    /// </summary>
    public static class LineTessellator
    {
        /// <summary>
        /// Result of a polyline mesh build: flat vertex array and triangle indices.
        /// OUTPUT WINDING: CCW — the pipeline's single canonical winding, reversed once to Unity-front
        /// at the mesh-write boundary (`StyledLineTileBuilder`) for stock Cull Back; the producer never
        /// bakes the render convention. See `docs/coordinates-and-projections.md` §7.1.
        /// </summary>
        public readonly struct Result
        {
            /// <summary>Flat array of all ribbon vertices in emission order.</summary>
            public readonly LineVertex[] Vertices;

            /// <summary>Triangle indices (3 per triangle) into <see cref="Vertices"/>, wound CCW (see struct summary).</summary>
            public readonly int[] Indices;

            public Result(LineVertex[] vertices, int[] indices)
            {
                Vertices = vertices;
                Indices  = indices;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build <paramref name="line"/> into a ribbon mesh.
        /// </summary>
        /// <param name="line">Polyline points in any consistent 2D space (world meters for S05).</param>
        /// <param name="joinType">Corner geometry style.</param>
        /// <param name="capType">Endpoint geometry style.</param>
        /// <param name="miterLimit">
        /// Maximum miter ratio (1/cos(θ/2)). Must be ≥ 1. Falls back to bevel when exceeded.
        /// </param>
        /// <param name="roundSegments">Number of arc divisions per round join/cap half. Min 1.</param>
        /// <param name="roundLimit">
        /// Minimum miter ratio (1/cos(θ/2)) a <see cref="JoinType.Round"/> join needs before it emits a fan.
        /// Must be ≥ 1. At or below this the corner is shallow enough that the fan would be imperceptible, so
        /// the join collapses to the miter path instead (see <see cref="NeedsMiter"/>) — and that collapsed
        /// miter is STILL subject to <paramref name="miterLimit"/>, exactly like <see cref="JoinType.Miter"/>:
        /// the full Round cascade is fan (f &gt; roundLimit), miter (f ≤ roundLimit AND f ≤ miterLimit), bevel
        /// (f ≤ roundLimit AND f &gt; miterLimit). <paramref name="roundLimit"/> and <paramref name="miterLimit"/>
        /// are independently style-settable with no cross-clamp between them, so when roundLimit &gt; miterLimit
        /// the middle (miter) tier can be empty and a collapsed join goes straight to bevel — see the
        /// <c>roundCollapsedToMiter</c> dispatch in <see cref="Triangulate"/>.
        /// </param>
        /// <returns>Ribbon mesh, or empty if the line has fewer than 2 distinct points.</returns>
        public static Result Triangulate(
            IReadOnlyList<double2> line,
            JoinType joinType      = JoinType.Miter,
            CapType  capType       = CapType.Butt,
            double   miterLimit    = 2.0,
            int      roundSegments = 4,
            double   roundLimit    = 1.05)
        {
            if (line == null || line.Count < 2)
                return new Result(Array.Empty<LineVertex>(), Array.Empty<int>());

            // Collapse consecutive duplicate points (zero-length segment → NaN normals).
            var pts = new List<double2>(line.Count);
            pts.Add(line[0]);
            for (int i = 1; i < line.Count; i++)
            {
                double2 prev = pts[pts.Count - 1];
                double2 cur  = line[i];
                if (math.abs(cur.x - prev.x) > 1e-12 || math.abs(cur.y - prev.y) > 1e-12)
                    pts.Add(cur);
            }

            if (pts.Count < 2)
                return new Result(Array.Empty<LineVertex>(), Array.Empty<int>());

            if (roundSegments < 1) roundSegments = 1;
            if (miterLimit < 1.0)  miterLimit = 1.0;
            if (roundLimit < 1.0)  roundLimit = 1.0;

            int n = pts.Count;

            // Segment tangents and left normals.
            var tangent   = new double2[n - 1];
            var segNormal = new double2[n - 1]; // unit left normal per segment
            for (int i = 0; i < n - 1; i++)
            {
                double dx  = pts[i + 1].x - pts[i].x;
                double dy  = pts[i + 1].y - pts[i].y;
                double len = Sqrt(dx * dx + dy * dy);
                tangent[i]   = new double2(dx / len, dy / len);
                segNormal[i] = new double2(-dy / len, dx / len); // CCW 90° of tangent = left normal
            }

            // Cumulative arc lengths.
            var cumDist = new double[n];
            cumDist[0] = 0.0;
            for (int i = 1; i < n; i++)
            {
                double dx = pts[i].x - pts[i - 1].x;
                double dy = pts[i].y - pts[i - 1].y;
                cumDist[i] = cumDist[i - 1] + Sqrt(dx * dx + dy * dy);
            }

            var verts   = new List<LineVertex>(2 * n + 32);
            var indices = new List<int>(6 * n + 32);

            // ── Start cap ─────────────────────────────────────────────────────────────────────
            // After this, the last 2 entries in verts are: [count-2]=left, [count-1]=right.
            EmitStartCap(verts, indices, pts[0], segNormal[0], tangent[0], cumDist[0],
                         capType, roundSegments);

            int leftPrev  = verts.Count - 2;
            int rightPrev = verts.Count - 1;

            // ── Segments + joins ──────────────────────────────────────────────────────────────
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

                    // Cross product z: t1 × t2 > 0 → left turn ⇒ the path curves LEFT ⇒ the LEFT side
                    // is CONCAVE (the two half-width bands overlap there) and the RIGHT side is CONVEX
                    // (the uncovered wedge). The chamfer/arc goes on the CONVEX side.
                    double cross = t1.x * t2.y - t1.y * t2.x;
                    bool leftTurn = cross > 0;

                    // A Round join whose corner is shallow enough (f ≤ roundLimit) collapses to the miter
                    // path instead of a fan — see NeedsMiter. That collapsed miter is STILL subject to
                    // miterLimit: roundLimit and miterLimit are independently style-settable with no
                    // cross-clamp, so a corner between the two thresholds (roundLimit < f ≤ miterLimit is
                    // fine, but f > miterLimit is not) must cascade round→miter→bevel exactly as a plain
                    // JoinType.Miter would, not jump straight to an unbounded ComputeMiterNormals spike.
                    bool roundCollapsedToMiter = joinType == JoinType.Round && NeedsMiter(n1, n2, roundLimit);
                    bool bevel = (joinType == JoinType.Bevel) ||
                                 (joinType == JoinType.Miter && NeedsBevel(n1, n2, miterLimit)) ||
                                 (roundCollapsedToMiter && NeedsBevel(n1, n2, miterLimit));

                    if (joinType == JoinType.Round && !roundCollapsedToMiter)
                    {
                        EmitRoundJoin(verts, indices, p2, n1, n2, dist2, roundSegments,
                                      leftTurn, leftPrev, rightPrev, miterLimit,
                                      out leftPrev, out rightPrev);
                    }
                    else if (bevel)
                    {
                        EmitBevelJoin(verts, indices, p2, n1, n2, dist2,
                                      leftTurn, leftPrev, rightPrev, miterLimit,
                                      out leftPrev, out rightPrev);
                    }
                    else
                    {
                        // Miter join — also reached by a shallow JoinType.Round join that collapsed here
                        // (f ≤ roundLimit) and did not ALSO exceed miterLimit (see roundCollapsedToMiter
                        // above). This branch is the sole destination once the round dispatch declines the
                        // fan and the bevel cascade declines a chamfer.
                        double2 miterL, miterR;
                        ComputeMiterNormals(n1, n2, out miterL, out miterR);

                        int lNext = verts.Count;
                        int rNext = verts.Count + 1;
                        verts.Add(MakeVertex(p2, miterL, dist2, +1f));
                        verts.Add(MakeVertex(p2, miterR, dist2, -1f));
                        EmitQuad(indices, leftPrev, rightPrev, lNext, rNext);
                        leftPrev  = lNext;
                        rightPrev = rNext;
                    }
                }
                else
                {
                    // Last segment: emit terminal butt vertices (cap will handle geometry extension).
                    int lNext = verts.Count;
                    int rNext = verts.Count + 1;
                    verts.Add(MakeVertex(p2,  n1,    dist2, +1f));
                    verts.Add(MakeVertex(p2, Neg(n1), dist2, -1f));
                    EmitQuad(indices, leftPrev, rightPrev, lNext, rNext);
                    leftPrev  = lNext;
                    rightPrev = rNext;
                }
            }

            // ── End cap ───────────────────────────────────────────────────────────────────────
            EmitEndCap(verts, indices, pts[n - 1], segNormal[n - 2], tangent[n - 2],
                       cumDist[n - 1], capType, roundSegments, leftPrev, rightPrev);

            return new Result(verts.ToArray(), indices.ToArray());
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Normal computation
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the join bisector direction and half-angle cosine shared by
        /// <see cref="ComputeMiterNormals"/>, <see cref="ComputeInnerNormal"/> and
        /// <see cref="NeedsBevel"/> — the one statement of this arithmetic in the file.
        /// <paramref name="mu"/> is the normalised average of the two left normals
        /// (<paramref name="n1"/> incoming, <paramref name="n2"/> outgoing); <paramref name="cosHalf"/>
        /// is <c>dot(mu, n1) == cos(θ/2)</c> where θ is the turn angle. Returns false at a 180°
        /// hairpin (the normals cancel), in which case <paramref name="mu"/> is set to
        /// <paramref name="n1"/> and <paramref name="cosHalf"/> to 1.0 as an inert fallback.
        /// </summary>
        private static bool TryJoinBisector(double2 n1, double2 n2, out double2 mu, out double cosHalf)
        {
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = Sqrt(mx * mx + my * my);

            if (mLen < 1e-12)
            {
                // 180° hairpin — normals cancel; fall back to incoming normal.
                mu      = n1;
                cosHalf = 1.0;
                return false;
            }

            double mux = mx / mLen;
            double muy = my / mLen;
            mu      = new double2(mux, muy);
            cosHalf = mux * n1.x + muy * n1.y; // dot(mu, n1) = cos(θ/2)
            return true;
        }

        /// <summary>
        /// Compute miter left/right normals at the join between two segments whose left normals
        /// are <paramref name="n1"/> (incoming) and <paramref name="n2"/> (outgoing).
        /// The miter normal's LENGTH encodes the miter factor (1/cos(θ/2)).
        /// </summary>
        private static void ComputeMiterNormals(double2 n1, double2 n2,
                                                out double2 leftN, out double2 rightN)
        {
            if (!TryJoinBisector(n1, n2, out double2 mu, out double cosHalf))
            {
                leftN  = n1;
                rightN = Neg(n1);
                return;
            }

            // Miter factor = 1 / (miter_unit · n1).  dot = cos(θ/2).
            if (math.abs(cosHalf) < 1e-12)
            {
                leftN  = n1;
                rightN = Neg(n1);
                return;
            }

            double miterFactor = 1.0 / cosHalf;
            leftN  = new double2(mu.x * miterFactor,  mu.y * miterFactor);
            rightN = new double2(-mu.x * miterFactor, -mu.y * miterFactor);
        }

        /// <summary>
        /// Inner-vertex normal for a bevel/round join: the SAME miter-factor magnitude the miter
        /// join's <see cref="ComputeMiterNormals"/> emits, saturated at <paramref name="miterLimit"/>
        /// instead of falling back to bevel. Derivation: the two CONCAVE inner offset lines (perpendicular
        /// distance h from the centerline along <paramref name="n1"/>/<paramref name="n2"/>) meet at
        /// <c>P + h·mu/cos(θ/2)</c> for a LEFT turn (bisector direction <c>mu</c>, half-angle cosine
        /// <c>cos(θ/2)</c>); for a RIGHT turn the concave offset lines are the −n1/−n2 pair and meet at
        /// <c>P − h·mu/cos(θ/2)</c>. Clamping the factor is the same bound <see cref="NeedsBevel"/> gates
        /// the miter path with. This helper returns the MAGNITUDE along <c>+mu</c> only (not signed) —
        /// the caller applies the turn-direction sign (<c>leftTurn ? +innerN : -innerN</c>) when placing
        /// the vertex on the concave side (see <see cref="EmitBevelJoin"/>/<see cref="EmitRoundJoin"/>).
        /// </summary>
        private static double2 ComputeInnerNormal(double2 n1, double2 n2, double miterLimit)
        {
            if (!TryJoinBisector(n1, n2, out double2 mu, out double cosHalf))
                return n1; // 180° hairpin — exactly today's fallback.

            if (math.abs(cosHalf) < 1e-12)
                // Saturated answer; avoids 1/0 → ±Inf through Burst. mLen = 2·|cosHalf| for unit n1/n2,
                // so this branch is only reached in the narrow band where mLen cleared the 1e-12 hairpin
                // guard but cosHalf still landed near zero — mu's DIRECTION here is floating-point noise,
                // not a meaningful bisector; only the saturated magnitude is relied on.
                return mu * miterLimit;

            double innerFactor = math.min(1.0 / math.abs(cosHalf), miterLimit);
            return mu * innerFactor;
        }

        /// <summary>
        /// Returns true when the miter ratio between segments with left normals <paramref name="n1"/>
        /// and <paramref name="n2"/> exceeds <paramref name="miterLimit"/>.
        /// </summary>
        public static bool NeedsBevel(double2 n1, double2 n2, double miterLimit)
        {
            if (!TryJoinBisector(n1, n2, out double2 mu, out double cosHalf)) return true;
            if (math.abs(cosHalf) < 1e-12) return true;
            // The miter factor is a MAGNITUDE (1/|cos(θ/2)|). Near a 180° hairpin normalize(n1+n2)
            // is dominated by numerical residual and can point opposite n1, making dot a small NEGATIVE;
            // a signed `1/dot > limit` then lets a huge negative factor slip past the bevel gate and the
            // miter normal blows up (the "line across the whole screen" glitch). Compare the magnitude.
            return math.abs(1.0 / cosHalf) > miterLimit;
        }

        /// <summary>
        /// Returns true when a <see cref="JoinType.Round"/> join's corner is shallow enough that its fan
        /// would be imperceptible — the miter ratio between segments with left normals <paramref name="n1"/>
        /// and <paramref name="n2"/> is at or below <paramref name="roundLimit"/> — and should collapse to
        /// the miter path instead. Mirrors <see cref="NeedsBevel"/>'s hairpin/near-zero-cosine handling: a
        /// hairpin or degenerate half-angle is never "shallow", so both return false there (round stays round;
        /// it is <see cref="EmitRoundJoin"/>'s own <see cref="TryJoinBisector"/> fallback that keeps that case
        /// well-defined, same as today).
        /// </summary>
        public static bool NeedsMiter(double2 n1, double2 n2, double roundLimit)
        {
            if (!TryJoinBisector(n1, n2, out double2 mu, out double cosHalf)) return false;
            if (math.abs(cosHalf) < 1e-12) return false;
            return math.abs(1.0 / cosHalf) <= roundLimit;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Join geometry
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bevel join: outer side gets two unit-normal vertices (one per adjacent segment);
        /// inner side gets the bisector-direction, miter-factor-magnitude vertex (see
        /// <see cref="ComputeInnerNormal"/>), saturated at <c>miterLimit</c>. Emits: quad from
        /// previous seg, bevel triangle, and updates out-params for next segment start.
        ///
        /// After call: leftPrev/rightPrev are updated to the join's "outgoing" vertices.
        /// </summary>
        private static void EmitBevelJoin(
            List<LineVertex> verts, List<int> indices,
            double2 p, double2 n1, double2 n2, double dist,
            bool leftTurn, int leftPrev, int rightPrev,
            double miterLimit,
            out int leftNext, out int rightNext)
        {
            double2 innerN = ComputeInnerNormal(n1, n2, miterLimit);

            // The two branches below are mirror images, NOT the same code with signs swapped, and they
            // are NOT unifiable: a reflection reverses triangle orientation, so the chamfer index order
            // that is CCW in one branch is CW in the other. Concretely — flip only the outer/inner
            // predicate and reuse the leftTurn branch's index order on a right turn and the chamfer comes
            // out as (outerA, outerB, innerV) = ((10,2),(12,0),(8,−2)) on Fixture B: signed area
            // 2A = −12, CW — back-faced. Keep both branches written out in full.
            if (leftTurn)
            {
                // Left turn ⇒ concave = left, convex = right (see the `cross` comment above).
                // Convex (outer) side gets two unit-normal vertices; concave (inner) side gets the
                // single bisector/miter-factor vertex.
                int outerA  = verts.Count;
                int innerV  = verts.Count + 1;
                int outerB  = verts.Count + 2;
                verts.Add(MakeVertex(p, Neg(n1),   dist, -1f));  // outerA: right of incoming seg, convex
                verts.Add(MakeVertex(p, innerN,    dist, +1f));  // innerV: left, concave
                verts.Add(MakeVertex(p, Neg(n2),   dist, -1f));  // outerB: right of outgoing seg, convex

                // Quad from prev seg ending at innerV (left) and outerA (right).
                EmitQuad(indices, leftPrev, rightPrev, innerV, outerA);
                // Chamfer triangle fills the convex gap: CCW = outerA, outerB, innerV when looking down −Y.
                indices.Add(outerA);
                indices.Add(outerB);
                indices.Add(innerV);

                leftNext  = innerV;
                rightNext = outerB;
            }
            else
            {
                // Right turn ⇒ concave = right, convex = left.
                int innerV  = verts.Count;
                int outerA  = verts.Count + 1;
                int outerB  = verts.Count + 2;
                verts.Add(MakeVertex(p, Neg(innerN), dist, -1f)); // innerV: right, concave
                verts.Add(MakeVertex(p,  n1,         dist, +1f)); // outerA: left of incoming seg, convex
                verts.Add(MakeVertex(p,  n2,         dist, +1f)); // outerB: left of outgoing seg, convex

                EmitQuad(indices, leftPrev, rightPrev, outerA, innerV);
                // Chamfer triangle on the convex (left) side: CCW = outerA, innerV, outerB.
                indices.Add(outerA);
                indices.Add(innerV);
                indices.Add(outerB);

                leftNext  = outerB;
                rightNext = innerV;
            }
        }

        /// <summary>
        /// Round join: outer side gets a fan of arc vertices; inner side gets the bisector-direction,
        /// miter-factor-magnitude vertex (see <see cref="ComputeInnerNormal"/>), saturated at
        /// <c>miterLimit</c>. Emits quad, fan triangles, and updates out-params.
        /// </summary>
        private static void EmitRoundJoin(
            List<LineVertex> verts, List<int> indices,
            double2 p, double2 n1, double2 n2, double dist,
            int roundSegments, bool leftTurn,
            int leftPrev, int rightPrev,
            double miterLimit,
            out int leftNext, out int rightNext)
        {
            double2 innerN = ComputeInnerNormal(n1, n2, miterLimit);

            // Arc start/end normals — the CONVEX rim. A left turn rotates the tangent CCW by θ, so both
            // n1→n2 and −n1→−n2 rotate CCW by θ; the convex (outer) rim for a left turn is the RIGHT side,
            // i.e. −n1/−n2 (concave is left, per the `cross` comment above). Mirrored for a right turn.
            double2 arcStart = leftTurn ? Neg(n1) : n1;
            double2 arcEnd   = leftTurn ? Neg(n2) : n2;

            // Angles for arc interpolation (unit circle).
            double a0 = math.atan2(arcStart.y, arcStart.x);
            double a1 = math.atan2(arcEnd.y,   arcEnd.x);
            if (leftTurn)
            {
                // Convex (right) rim sweeps CCW as the tangent turns left (a1 ≥ a0 after wrapping).
                while (a1 < a0) a1 += 2.0 * math.PI_DBL;
            }
            else
            {
                // Convex (left) rim sweeps CW as the tangent turns right (a1 ≤ a0 after wrapping).
                while (a1 > a0) a1 -= 2.0 * math.PI_DBL;
            }

            // Emit inner (concave) vertex.
            int innerIdx = verts.Count;
            if (leftTurn)
                verts.Add(MakeVertex(p,  innerN,     dist, +1f));
            else
                verts.Add(MakeVertex(p, Neg(innerN), dist, -1f));

            // Emit arcStart vertex (convex rim).
            int arcStartIdx = verts.Count;
            if (leftTurn)
                verts.Add(MakeVertex(p, arcStart, dist, -1f));
            else
                verts.Add(MakeVertex(p, arcStart, dist, +1f));

            // Connect previous quad to this join's arcStart and innerV.
            if (leftTurn)
                EmitQuad(indices, leftPrev, rightPrev, innerIdx, arcStartIdx);
            else
                EmitQuad(indices, leftPrev, rightPrev, arcStartIdx, innerIdx);

            // Fan intermediate vertices.
            int prevFanIdx = arcStartIdx;
            for (int k = 1; k <= roundSegments; k++)
            {
                double t   = (double)k / (roundSegments + 1);
                double ang = a0 + t * (a1 - a0);
                int fanIdx = verts.Count;
                if (leftTurn)
                    verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, -1f));
                else
                    verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));

                if (leftTurn)
                { indices.Add(prevFanIdx); indices.Add(fanIdx); indices.Add(innerIdx); }
                else
                { indices.Add(innerIdx); indices.Add(fanIdx); indices.Add(prevFanIdx); }

                prevFanIdx = fanIdx;
            }

            // Emit arcEnd vertex (convex rim).
            int arcEndIdx = verts.Count;
            if (leftTurn)
                verts.Add(MakeVertex(p, arcEnd, dist, -1f));
            else
                verts.Add(MakeVertex(p, arcEnd, dist, +1f));

            // Last fan triangle.
            if (leftTurn)
            { indices.Add(prevFanIdx); indices.Add(arcEndIdx); indices.Add(innerIdx); }
            else
            { indices.Add(innerIdx); indices.Add(arcEndIdx); indices.Add(prevFanIdx); }

            // Outgoing edge — the quad's L1/R1 roles swap relative to the (unreflected) leftPrev/rightPrev,
            // so the next-pointers swap too (see the join-point-reflection argument in the plan).
            if (leftTurn)
            { leftNext = innerIdx; rightNext = arcEndIdx; }
            else
            { leftNext = arcEndIdx; rightNext = innerIdx; }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Caps
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emit a start cap at <paramref name="p"/> with the first segment's normal.
        ///
        /// Guarantee: after this call, verts[count-2] = left (+1 side) and verts[count-1] = right
        /// (−1 side) at position p, ready for the first quad.
        /// </summary>
        private static void EmitStartCap(
            List<LineVertex> verts, List<int> indices,
            double2 p, double2 segNormal, double2 segTangent, double dist,
            CapType capType, int roundSegments)
        {
            switch (capType)
            {
                case CapType.Butt:
                    verts.Add(MakeVertex(p,  segNormal,    dist, +1f));
                    verts.Add(MakeVertex(p, Neg(segNormal), dist, -1f));
                    break;

                case CapType.Square:
                {
                    // Square cap: the normal bakes in a backward half-tangent offset so the shader
                    // expansion includes both lateral and backward extension.
                    // Normal = segNormal − tangent (for left); −segNormal − tangent (for right).
                    // The vertex shader applies ½·width → net backward extension = ½·width.
                    double2 lN = new double2(segNormal.x - segTangent.x, segNormal.y - segTangent.y);
                    double2 rN = new double2(-segNormal.x - segTangent.x, -segNormal.y - segTangent.y);
                    verts.Add(MakeVertex(p, lN, dist, +1f));
                    verts.Add(MakeVertex(p, rN, dist, -1f));
                    break;
                }

                case CapType.Round:
                {
                    // Round start cap: a half-circle fan bulging BEHIND the start point (toward −tangent).
                    //
                    // Fan pivot: centerline point itself (normal=(0,0), side=0 → stays on line after
                    // shader extrusion; side=0 → interior alpha in fragment shader, not an edge pixel).
                    //
                    // Arc sweeps from rightButt (−segNormal direction) CLOCKWISE through −tangent to
                    // leftButt (+segNormal direction), i.e., going through the backward hemisphere.
                    //
                    // Arc angles: a0 = angle of −segNormal (right rim), a1 = angle of +segNormal (left rim).
                    // CW sweep: a1 < a0 (after wrapping), so the parametric midpoint passes through
                    // angle a0 − π/2 → −tangent direction → the outward backward hemisphere. ✓
                    //
                    // Emission order (so verts[count-2]=leftButt, verts[count-1]=rightButt):
                    //   [baseIdx + 0]                = center pivot
                    //   [baseIdx + 1 .. roundSegs]   = arc intermediates
                    //   [baseIdx + roundSegs + 1]    = capSeed (side=+1)    ← fan seed, co-located w/ rightButt
                    //   [baseIdx + roundSegs + 2]    = leftButt (side=+1)   ← count-2
                    //   [baseIdx + roundSegs + 3]    = rightButt (side=−1)  ← count-1
                    //
                    // Fan triangles (CCW, verified by signed-area): center, intermediate[k], intermediate[k-1]
                    //   where intermediate[0] = capSeed.

                    double a0 = math.atan2(-segNormal.y, -segNormal.x); // rightButt direction
                    double a1 = math.atan2( segNormal.y,  segNormal.x); // leftButt direction
                    // CW sweep: ensure a1 < a0 (so intermediate angles decrease through −tangent).
                    while (a1 > a0) a1 -= 2.0 * math.PI_DBL;

                    int baseIdx      = verts.Count;
                    int centerIdx    = baseIdx;
                    int capSeedIdx   = baseIdx + roundSegments + 1;
                    int leftButtIdx  = baseIdx + roundSegments + 2;
                    int rightButtIdx = baseIdx + roundSegments + 3;

                    // Emit center pivot.
                    verts.Add(MakeVertex(p, new double2(0.0, 0.0), dist, 0f));

                    // Emit arc intermediates [1 .. roundSegments].
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double t   = (double)k / (roundSegments + 1);
                        double ang = a0 + t * (a1 - a0);
                        verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));
                    }

                    // Fan seed: geometrically identical to rightButt (same p, same −segNormal) but tagged +1.
                    // Seeding the fan from rightButt itself gave the seam triangle an OUTER edge — a true
                    // silhouette — interpolating side +1 → −1 and passing through 0 at its midpoint, so anything
                    // keyed on |side| read that one arc segment as deep interior. Emitted BEFORE the two butts
                    // so the [count-2]=left / [count-1]=right contract still holds.
                    verts.Add(MakeVertex(p, Neg(segNormal), dist, +1f));  // capSeed

                    // Emit leftButt and rightButt LAST to satisfy the [count-2]=left,[count-1]=right contract.
                    verts.Add(MakeVertex(p,  segNormal,    dist, +1f));   // leftButt  [count-2]
                    verts.Add(MakeVertex(p, Neg(segNormal), dist, -1f));  // rightButt [count-1]

                    // Fan triangles: center + (capSeed→intermediates→leftButt) arc, CCW.
                    // "prevFanIdx" starts at capSeedIdx; each new intermediate continues the arc.
                    int prevFan = capSeedIdx;
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        int fanIdx = baseIdx + k; // intermediate k
                        indices.Add(centerIdx);
                        indices.Add(fanIdx);
                        indices.Add(prevFan);
                        prevFan = fanIdx;
                    }
                    // Final triangle: center → leftButt → lastIntermediate.
                    indices.Add(centerIdx);
                    indices.Add(leftButtIdx);
                    indices.Add(prevFan);
                    break;
                }
            }
        }

        /// <summary>
        /// Emit an end cap at <paramref name="p"/> using the last segment's normal.
        /// <paramref name="leftPrev"/> and <paramref name="rightPrev"/> are the last ribbon vertices.
        /// </summary>
        private static void EmitEndCap(
            List<LineVertex> verts, List<int> indices,
            double2 p, double2 segNormal, double2 segTangent, double dist,
            CapType capType, int roundSegments,
            int leftPrev, int rightPrev)
        {
            switch (capType)
            {
                case CapType.Butt:
                    // Nothing to add — ribbon already ends at the terminal vertices.
                    break;

                case CapType.Square:
                {
                    // Extend forward: normal bakes in +tangent so shader extends the ribbon forward.
                    double2 lN = new double2(segNormal.x + segTangent.x,  segNormal.y + segTangent.y);
                    double2 rN = new double2(-segNormal.x + segTangent.x, -segNormal.y + segTangent.y);
                    int lEnd = verts.Count;
                    int rEnd = verts.Count + 1;
                    verts.Add(MakeVertex(p, lN, dist, +1f));
                    verts.Add(MakeVertex(p, rN, dist, -1f));
                    EmitQuad(indices, leftPrev, rightPrev, lEnd, rEnd);
                    break;
                }

                case CapType.Round:
                {
                    // Round end cap: a half-circle fan bulging PAST the end point (toward +tangent).
                    //
                    // Fan pivot: centerline end point (normal=(0,0), side=0 — same reasoning as start cap).
                    //
                    // Arc sweeps from leftPrev (+segNormal direction) CLOCKWISE through +tangent to
                    // rightPrev (−segNormal direction), i.e., through the forward hemisphere.
                    //
                    // Arc angles: a0 = angle of +segNormal (leftPrev), a1 = angle of −segNormal (rightPrev).
                    // CW sweep: a1 < a0 naturally (−π/2 < π/2), so no wrapping needed for horizontal.
                    // General: ensure a1 < a0.
                    //
                    // Fan triangles (CCW, verified by signed-area): center, fanIdx, prevFanIdx
                    //   where prevFanIdx starts as leftPrev.
                    // Final triangle: center, rightPrev, prevFanIdx (last intermediate).

                    double a0 = math.atan2( segNormal.y,  segNormal.x);  // leftPrev direction
                    double a1 = math.atan2(-segNormal.y, -segNormal.x);  // rightPrev direction
                    // CW sweep (through +tangent = forward): ensure a1 < a0.
                    while (a1 > a0) a1 -= 2.0 * math.PI_DBL;

                    // Center pivot vertex.
                    int centerIdx = verts.Count;
                    verts.Add(MakeVertex(p, new double2(0.0, 0.0), dist, 0f));

                    // Fan from leftPrev through arc intermediates toward rightPrev.
                    int prevFanIdx = leftPrev;
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double t   = (double)k / (roundSegments + 1);
                        double ang = a0 + t * (a1 - a0);
                        int fanIdx = verts.Count;
                        verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));

                        indices.Add(centerIdx);
                        indices.Add(fanIdx);
                        indices.Add(prevFanIdx);
                        prevFanIdx = fanIdx;
                    }
                    // Closing-triangle seed: geometrically identical to rightPrev (this cap is called with the
                    // same segNormal the last segment extruded rightPrev with) but tagged +1, so the closing
                    // triangle's outer edge runs +1 → +1 instead of +1 → −1. rightPrev itself stays −1 for the
                    // ribbon quad. Same fix as EmitStartCap's capSeed.
                    int capSeedIdx = verts.Count;
                    verts.Add(MakeVertex(p, Neg(segNormal), dist, +1f));

                    // Final triangle: center → capSeed → lastIntermediate.
                    indices.Add(centerIdx);
                    indices.Add(capSeedIdx);
                    indices.Add(prevFanIdx);
                    break;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Utilities
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emit two CCW triangles forming a quad:
        ///   L0──L1     (L=left/+side, R=right/−side, 0=prev, 1=next)
        ///   │ ╲ │
        ///   R0──R1
        /// CCW winding when east=+X, north=+Z, viewed from above (−Y).
        /// </summary>
        private static void EmitQuad(List<int> indices, int L0, int R0, int L1, int R1)
        {
            indices.Add(L0); indices.Add(R0); indices.Add(R1);
            indices.Add(L0); indices.Add(R1); indices.Add(L1);
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

        private static double Sqrt(double x) => math.sqrt(x);
        private static double2 Neg(double2 v) => new double2(-v.x, -v.y);
    }
}
