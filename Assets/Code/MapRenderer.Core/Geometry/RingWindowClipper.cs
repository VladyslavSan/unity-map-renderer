using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Sutherland–Hodgman clip of ONE ring against an axis-aligned window — the managed, engine-free twin of
    /// <c>RingClipJob</c>, for the client-side GeoJSON slicer (which must stay engine-free
    /// in <c>MapRenderer.Core</c>, and so cannot reach for a Burst job over native containers).
    ///
    /// <para><b>Coordinate space and winding (producer declaration).</b> Input and output are both tile-local
    /// <c>double2</c>, origin top-left, Y down. Rings are <b>implicitly closed</b> (the first vertex is not
    /// repeated), matching production's MVT decoder <c>MvtDecodeJob</c>. Sutherland–Hodgman
    /// is orientation-preserving, so
    /// this type <b>chooses no winding at all</b>: <b>output winding equals input winding</b>, and the
    /// relative sign between an exterior ring and its holes survives unchanged.</para>
    ///
    /// <para>Which absolute sign an exterior ring carries is decided upstream, at the place it is produced —
    /// <c>GeoJson.GeoJsonParser</c> normalises it and <c>GeoJson.GeoJsonTileSlicer</c> declares it for the
    /// sliced output; both state the MVT convention there. Stating it here as well would claim a guarantee
    /// this type does not make, and would contradict <c>RingClipJob</c> — the bit-identical
    /// twin, which names the other canonical because it clips a different producer's rings. The two agree on
    /// the only thing either one actually guarantees: winding is preserved.</para>
    ///
    /// <para>The Unity-front reversal for stock Cull Back stays where it is, at the mesh-write boundary in
    /// <c>StyledFillTileBuilder</c> (<c>docs/coordinates-and-projections.md</c>).</para>
    ///
    /// <para><b>Why it is exact at the corners.</b> S–H is exact for a CONVEX clip region against an
    /// arbitrary (possibly concave) subject; the window is an axis-aligned rectangle, hence convex, so the
    /// four sequential half-plane passes compose to the exact intersection region. A polygon that fully
    /// CONTAINS the window reduces to the window rectangle itself with no special case — each pass replaces
    /// the crossing edges with the plane segment.</para>
    ///
    /// <para><b>Degenerate output is expected and fine.</b> A subject passing AROUND a corner emits one ring
    /// with zero-width channels along the boundary rather than two disjoint rings. That is a representation
    /// artefact, not an area error — the enclosed area is still exactly the intersection — and it is cleared
    /// downstream by <c>RingAssemblyJob</c>'s <c>rLen &lt; 3</c> / <c>|area2| &lt; 1</c> filters and
    /// <c>EarcutJob</c>'s cure → split → drop cascade.</para>
    ///
    /// <para><b>Deliberate duplication, pinned rather than assumed.</b> This is a structural mirror of
    /// <c>RingClipJob</c>: same plane order, same inclusive boundary test, same exact-boundary
    /// <c>Intersect</c>, same zero-length-edge dedup, same trailing first==last collapse, same bbox fast
    /// path. <c>RingWindowClipperParityTests</c> pins the two BIT-IDENTICAL over a randomised corpus, which
    /// is what converts the copy from an unverified smell into a checked equivalence — and leaves a clean
    /// retirement path: <c>RingClipJob</c> can delegate to it.</para>
    /// </summary>
    public static class RingWindowClipper
    {
        /// <summary>
        /// Clips <paramref name="ring"/> to the inclusive window <c>[min, max]</c>. Returns null when
        /// nothing survives (wholly outside, empty, or a sub-triangle ring that is not already inside) —
        /// the managed spelling of <c>RingClipJob</c> dropping the ring.
        /// </summary>
        public static List<double2> Clip(IReadOnlyList<double2> ring, double2 min, double2 max)
        {
            if (ring == null || ring.Count == 0) return null;

            // Fast path (structural, not an optimisation): geometry already inside is copied verbatim, so
            // "already inside ⇒ bit-identical" is a property of the control flow, not of the arithmetic.
            if (BboxInsideWindow(ring, min, max))
                return new List<double2>(ring);

            // A sub-triangle ring that is not wholly inside cannot survive assembly's rLen < 3 filter.
            if (ring.Count < 3) return null;

            var current = new List<double2>(ring);
            var next = new List<double2>(ring.Count);

            // axis 0 = x, axis 1 = y; keepAbove = the >= min half-plane, else the <= max one.
            if (!ClipAgainstPlane(ref current, ref next, axis: 0, boundary: min.x, keepAbove: true))  return null;
            if (!ClipAgainstPlane(ref current, ref next, axis: 0, boundary: max.x, keepAbove: false)) return null;
            if (!ClipAgainstPlane(ref current, ref next, axis: 1, boundary: min.y, keepAbove: true))  return null;
            if (!ClipAgainstPlane(ref current, ref next, axis: 1, boundary: max.y, keepAbove: false)) return null;

            return current;
        }

        private static bool BboxInsideWindow(IReadOnlyList<double2> ring, double2 min, double2 max)
        {
            double2 lo = ring[0];
            double2 hi = lo;
            for (int i = 1; i < ring.Count; i++)
            {
                double2 v = ring[i];
                lo = math.min(lo, v);
                hi = math.max(hi, v);
            }
            return lo.x >= min.x && lo.y >= min.y && hi.x <= max.x && hi.y <= max.y;
        }

        /// <summary>
        /// One Sutherland–Hodgman pass: reads <paramref name="current"/>, writes the survivors into
        /// <paramref name="next"/>, then swaps so the result is back in <paramref name="current"/>.
        /// Returns false when the ring clipped away entirely.
        /// </summary>
        private static bool ClipAgainstPlane(
            ref List<double2> current, ref List<double2> next, int axis, double boundary, bool keepAbove)
        {
            next.Clear();

            double2 prev       = current[current.Count - 1];
            bool    prevInside = Inside(prev, axis, boundary, keepAbove);

            for (int i = 0; i < current.Count; i++)
            {
                double2 cur       = current[i];
                bool    curInside = Inside(cur, axis, boundary, keepAbove);

                if (curInside)
                {
                    if (!prevInside)
                        Emit(next, Intersect(prev, cur, axis, boundary));
                    Emit(next, cur);
                }
                else if (prevInside)
                {
                    Emit(next, Intersect(prev, cur, axis, boundary));
                }

                prev       = cur;
                prevInside = curInside;
            }

            // Close the ring: an exit crossing whose entry vertex sat exactly ON the plane emits that vertex
            // a second time, and the wrap-around can leave the first and last equal. Both are zero-length
            // edges, and both are what turn an on-boundary quad into a 5-vertex ring.
            if (next.Count > 1 && SameVertex(next[next.Count - 1], next[0]))
                next.RemoveAt(next.Count - 1);

            List<double2> consumed = current;
            current = next;
            next = consumed;
            return current.Count != 0;
        }

        /// <summary>Appends unless it would repeat the previous vertex (a zero-length edge).</summary>
        private static void Emit(List<double2> output, double2 v)
        {
            if (output.Count > 0 && SameVertex(output[output.Count - 1], v)) return;
            output.Add(v);
        }

        // Component-wise equality, spelled out rather than double2.Equals: identical semantics, and it does
        // not depend on a struct method the engine-free math shim would have to reproduce bit-for-bit.
        private static bool SameVertex(double2 a, double2 b) => a.x == b.x && a.y == b.y;

        private static bool Inside(double2 p, int axis, double boundary, bool keepAbove)
        {
            double c = axis == 0 ? p.x : p.y;
            return keepAbove ? c >= boundary : c <= boundary;
        }

        /// <summary>
        /// Where segment <paramref name="a"/>→<paramref name="b"/> meets the plane. The clipped axis is
        /// assigned the boundary EXACTLY (not the interpolated value) so a later inclusive-boundary test on
        /// this vertex — the next plane's, or the neighbouring tile's — cannot disagree by a rounding step.
        /// </summary>
        private static double2 Intersect(double2 a, double2 b, int axis, double boundary)
        {
            double ca = axis == 0 ? a.x : a.y;
            double cb = axis == 0 ? b.x : b.y;
            double d  = cb - ca;
            double t  = d != 0.0 ? (boundary - ca) / d : 0.0;
            double2 p = a + t * (b - a);
            if (axis == 0) p.x = boundary; else p.y = boundary;
            return p;
        }
    }
}
