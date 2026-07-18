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
    ///   • Straight/bevel/round vertices: |normal| = 1.
    ///   • Miter join vertices: |normal| = 1/cos(θ/2) (the miter factor is baked into the length).
    ///   • Do NOT store as SNORM (unit-only format) — miter normals exceed length 1.
    ///   • Vertex shader: worldPos += normal * 0.5 * widthMeters (uniform, no side multiplier).
    ///   • Side ∈ {+1, −1} is used only by the fragment shader for AA feathering.
    ///
    /// Join bevel/round geometry principle:
    ///   At an interior point, one side is the "outer" (convex) side and one is the "inner"
    ///   (concave) side. The outer side gets the extra bevel/fan geometry; the inner side gets
    ///   a single miter-like vertex (using the normalised average normal, length=1).
    /// </summary>
    public static class LineTessellator
    {
        /// <summary>
        /// Result of a polyline mesh build: flat vertex array and triangle indices,
        /// mirroring <see cref="Earcut.Result"/> shape.
        /// OUTPUT WINDING: CCW — the pipeline's single canonical winding (same as <see cref="Earcut"/>),
        /// reversed once to Unity-front at the mesh-write boundary (`StyledLineTileBuilder`) for stock Cull
        /// Back; the producer never bakes the render convention. See `docs/coordinates-and-projections.md` §7.1.
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
        /// <returns>Ribbon mesh, or empty if the line has fewer than 2 distinct points.</returns>
        public static Result Triangulate(
            IReadOnlyList<double2> line,
            JoinType joinType      = JoinType.Miter,
            CapType  capType       = CapType.Butt,
            double   miterLimit    = 2.0,
            int      roundSegments = 4)
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

                    // Cross product z: t1 × t2 > 0 → left turn, outer side = left (+).
                    double cross = t1.x * t2.y - t1.y * t2.x;
                    bool leftTurn = cross > 0;

                    bool bevel = (joinType == JoinType.Bevel) ||
                                 (joinType == JoinType.Miter && NeedsBevel(n1, n2, miterLimit));

                    if (joinType == JoinType.Round)
                    {
                        EmitRoundJoin(verts, indices, p2, n1, n2, dist2, roundSegments,
                                      leftTurn, leftPrev, rightPrev,
                                      out leftPrev, out rightPrev);
                    }
                    else if (bevel)
                    {
                        EmitBevelJoin(verts, indices, p2, n1, n2, dist2,
                                      leftTurn, leftPrev, rightPrev,
                                      out leftPrev, out rightPrev);
                    }
                    else
                    {
                        // Miter join.
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
        /// Compute miter left/right normals at the join between two segments whose left normals
        /// are <paramref name="n1"/> (incoming) and <paramref name="n2"/> (outgoing).
        /// The miter normal's LENGTH encodes the miter factor (1/cos(θ/2)).
        /// </summary>
        private static void ComputeMiterNormals(double2 n1, double2 n2,
                                                out double2 leftN, out double2 rightN)
        {
            // Miter direction = normalised average of the two left normals.
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = Sqrt(mx * mx + my * my);

            if (mLen < 1e-12)
            {
                // 180° hairpin — normals cancel; fall back to incoming normal.
                leftN  = n1;
                rightN = Neg(n1);
                return;
            }

            double mux = mx / mLen;
            double muy = my / mLen;

            // Miter factor = 1 / (miter_unit · n1).  dot = cos(θ/2).
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12)
            {
                leftN  = n1;
                rightN = Neg(n1);
                return;
            }

            double miterFactor = 1.0 / dot;
            leftN  = new double2(mux * miterFactor,  muy * miterFactor);
            rightN = new double2(-mux * miterFactor, -muy * miterFactor);
        }

        /// <summary>
        /// Returns true when the miter ratio between segments with left normals <paramref name="n1"/>
        /// and <paramref name="n2"/> exceeds <paramref name="miterLimit"/>.
        /// </summary>
        public static bool NeedsBevel(double2 n1, double2 n2, double miterLimit)
        {
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = Sqrt(mx * mx + my * my);
            if (mLen < 1e-12) return true;
            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12) return true;
            // The miter factor is a MAGNITUDE (1/|cos(θ/2)|). Near a 180° hairpin normalize(n1+n2)
            // is dominated by numerical residual and can point opposite n1, making dot a small NEGATIVE;
            // a signed `1/dot > limit` then lets a huge negative factor slip past the bevel gate and the
            // miter normal blows up (the "line across the whole screen" glitch). Compare the magnitude.
            return math.abs(1.0 / dot) > miterLimit;
        }

        /// <summary>
        /// Compute the miter factor (1/cos(θ/2)) for the join between two segment left normals.
        /// Returns 1.0 if the normals are parallel (no turn).
        /// </summary>
        public static double MiterFactor(double2 n1, double2 n2)
        {
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = Sqrt(mx * mx + my * my);
            if (mLen < 1e-12) return double.MaxValue;
            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12) return double.MaxValue;
            return 1.0 / dot;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Join geometry
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bevel join: outer side gets two unit-normal vertices (one per adjacent segment);
        /// inner side gets the normalised-average unit normal. Emits: quad from previous seg,
        /// bevel triangle, and updates out-params for next segment start.
        ///
        /// After call: leftPrev/rightPrev are updated to the join's "outgoing" vertices.
        /// </summary>
        private static void EmitBevelJoin(
            List<LineVertex> verts, List<int> indices,
            double2 p, double2 n1, double2 n2, double dist,
            bool leftTurn, int leftPrev, int rightPrev,
            out int leftNext, out int rightNext)
        {
            // Inner normal: normalised average.
            double ix   = n1.x + n2.x;
            double iy   = n1.y + n2.y;
            double iLen = Sqrt(ix * ix + iy * iy);
            double2 innerN = (iLen > 1e-12)
                ? new double2(ix / iLen, iy / iLen) : n1;

            if (leftTurn)
            {
                // Outer = left (+), inner = right (−).
                int outerA  = verts.Count;
                int innerV  = verts.Count + 1;
                int outerB  = verts.Count + 2;
                verts.Add(MakeVertex(p,  n1,        dist, +1f));  // outerA: left of incoming seg
                verts.Add(MakeVertex(p, Neg(innerN), dist, -1f)); // innerV: right inner
                verts.Add(MakeVertex(p,  n2,        dist, +1f));  // outerB: left of outgoing seg

                // Quad from prev seg ending at outerA (left) and innerV (right).
                EmitQuad(indices, leftPrev, rightPrev, outerA, innerV);
                // Bevel triangle fills outer gap: CCW = outerA, outerB, innerV when looking down −Y.
                indices.Add(outerA);
                indices.Add(outerB);
                indices.Add(innerV);

                leftNext  = outerB;
                rightNext = innerV;
            }
            else
            {
                // Outer = right (−), inner = left (+).
                int innerV  = verts.Count;
                int outerA  = verts.Count + 1;
                int outerB  = verts.Count + 2;
                verts.Add(MakeVertex(p,  innerN,  dist, +1f));  // innerV: left inner
                verts.Add(MakeVertex(p, Neg(n1),  dist, -1f));  // outerA: right of incoming seg
                verts.Add(MakeVertex(p, Neg(n2),  dist, -1f));  // outerB: right of outgoing seg

                EmitQuad(indices, leftPrev, rightPrev, innerV, outerA);
                // Bevel triangle on right side: CCW = outerA, innerV, outerB.
                indices.Add(outerA);
                indices.Add(innerV);
                indices.Add(outerB);

                leftNext  = innerV;
                rightNext = outerB;
            }
        }

        /// <summary>
        /// Round join: outer side gets a fan of arc vertices; inner side gets the unit average
        /// normal. Emits quad, fan triangles, and updates out-params.
        /// </summary>
        private static void EmitRoundJoin(
            List<LineVertex> verts, List<int> indices,
            double2 p, double2 n1, double2 n2, double dist,
            int roundSegments, bool leftTurn,
            int leftPrev, int rightPrev,
            out int leftNext, out int rightNext)
        {
            // Inner normal: normalised average.
            double ix   = n1.x + n2.x;
            double iy   = n1.y + n2.y;
            double iLen = Sqrt(ix * ix + iy * iy);
            double2 innerN = (iLen > 1e-12)
                ? new double2(ix / iLen, iy / iLen) : n1;

            // Arc start/end normals (outer side).
            double2 arcStart = leftTurn ?  n1 : Neg(n1);
            double2 arcEnd   = leftTurn ?  n2 : Neg(n2);

            // Angles for arc interpolation (unit circle).
            double a0 = math.atan2(arcStart.y, arcStart.x);
            double a1 = math.atan2(arcEnd.y,   arcEnd.x);
            if (leftTurn)
            {
                // Outer arc goes CCW (a1 ≥ a0 after wrapping).
                while (a1 < a0) a1 += 2.0 * math.PI_DBL;
            }
            else
            {
                // Outer arc goes CW (a1 ≤ a0 after wrapping).
                while (a1 > a0) a1 -= 2.0 * math.PI_DBL;
            }

            // Emit inner vertex.
            int innerIdx = verts.Count;
            if (leftTurn)
                verts.Add(MakeVertex(p, Neg(innerN), dist, -1f));
            else
                verts.Add(MakeVertex(p,  innerN,    dist, +1f));

            // Emit arcStart vertex.
            int arcStartIdx = verts.Count;
            if (leftTurn)
                verts.Add(MakeVertex(p, arcStart, dist, +1f));
            else
                verts.Add(MakeVertex(p, arcStart, dist, -1f));

            // Connect previous quad to this join's arcStart and innerV.
            if (leftTurn)
                EmitQuad(indices, leftPrev, rightPrev, arcStartIdx, innerIdx);
            else
                EmitQuad(indices, leftPrev, rightPrev, innerIdx, arcStartIdx);

            // Fan intermediate vertices.
            int prevFanIdx = arcStartIdx;
            for (int k = 1; k <= roundSegments; k++)
            {
                double t   = (double)k / (roundSegments + 1);
                double ang = a0 + t * (a1 - a0);
                int fanIdx = verts.Count;
                if (leftTurn)
                    verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));
                else
                    verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, -1f));

                if (leftTurn)
                { indices.Add(prevFanIdx); indices.Add(fanIdx); indices.Add(innerIdx); }
                else
                { indices.Add(innerIdx); indices.Add(fanIdx); indices.Add(prevFanIdx); }

                prevFanIdx = fanIdx;
            }

            // Emit arcEnd vertex.
            int arcEndIdx = verts.Count;
            if (leftTurn)
                verts.Add(MakeVertex(p, arcEnd, dist, +1f));
            else
                verts.Add(MakeVertex(p, arcEnd, dist, -1f));

            // Last fan triangle.
            if (leftTurn)
            { indices.Add(prevFanIdx); indices.Add(arcEndIdx); indices.Add(innerIdx); }
            else
            { indices.Add(innerIdx); indices.Add(arcEndIdx); indices.Add(prevFanIdx); }

            // Outgoing edge.
            if (leftTurn)
            { leftNext = arcEndIdx; rightNext = innerIdx; }
            else
            { leftNext = innerIdx; rightNext = arcEndIdx; }
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
                    //   [baseIdx + roundSegs + 1]    = leftButt (side=+1)   ← count-2
                    //   [baseIdx + roundSegs + 2]    = rightButt (side=−1)  ← count-1
                    //
                    // Fan triangles (CCW, verified by signed-area): center, intermediate[k], intermediate[k-1]
                    //   where intermediate[0] = rightButt.

                    double a0 = math.atan2(-segNormal.y, -segNormal.x); // rightButt direction
                    double a1 = math.atan2( segNormal.y,  segNormal.x); // leftButt direction
                    // CW sweep: ensure a1 < a0 (so intermediate angles decrease through −tangent).
                    while (a1 > a0) a1 -= 2.0 * math.PI_DBL;

                    int baseIdx      = verts.Count;
                    int centerIdx    = baseIdx;
                    int leftButtIdx  = baseIdx + roundSegments + 1;
                    int rightButtIdx = baseIdx + roundSegments + 2;

                    // Emit center pivot.
                    verts.Add(MakeVertex(p, new double2(0.0, 0.0), dist, 0f));

                    // Emit arc intermediates [1 .. roundSegments].
                    for (int k = 1; k <= roundSegments; k++)
                    {
                        double t   = (double)k / (roundSegments + 1);
                        double ang = a0 + t * (a1 - a0);
                        verts.Add(MakeVertex(p, new double2(math.cos(ang), math.sin(ang)), dist, +1f));
                    }

                    // Emit leftButt and rightButt LAST to satisfy the [count-2]=left,[count-1]=right contract.
                    verts.Add(MakeVertex(p,  segNormal,    dist, +1f));   // leftButt  [count-2]
                    verts.Add(MakeVertex(p, Neg(segNormal), dist, -1f));  // rightButt [count-1]

                    // Fan triangles: center + (rightButt→intermediates→leftButt) arc, CCW.
                    // "prevFanIdx" starts at rightButtIdx; each new intermediate continues the arc.
                    int prevFan = rightButtIdx;
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
                    // Final triangle: center → rightPrev → lastIntermediate.
                    indices.Add(centerIdx);
                    indices.Add(rightPrev);
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
