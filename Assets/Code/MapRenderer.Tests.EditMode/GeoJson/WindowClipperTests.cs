// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The two window clippers, in tile-local coordinates: rings (Sutherland–Hodgman) and open polylines
    /// (Liang–Barsky, T3 / T3b). Lines are NOT the polygon case with the closure removed, and these teeth
    /// are what make substituting one for the other visible.
    /// </summary>
    [TestFixture]
    public class WindowClipperTests
    {
        private static readonly double2 Min = new double2(0.0, 0.0);
        private static readonly double2 Max = new double2(4096.0, 4096.0);

        private static List<double2> Path(params double[] xy)
        {
            var path = new List<double2>(xy.Length / 2);
            for (int i = 0; i < xy.Length; i += 2) path.Add(new double2(xy[i], xy[i + 1]));
            return path;
        }

        private static bool Closed(IReadOnlyList<double2> path)
            => path.Count > 1 &&
               path[0].x == path[path.Count - 1].x && path[0].y == path[path.Count - 1].y;

        // ── Rings ───────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Ring_AlreadyInsideTheWindow_IsCopiedVerbatim()
        {
            List<double2> ring = Path(100.0, 100.0, 3000.0, 100.0, 3000.0, 2000.0, 100.0, 2000.0);
            List<double2> clipped = RingWindowClipper.Clip(ring, Min, Max);

            Assert.That(clipped, Is.EqualTo(ring), "the bbox fast path must not perturb inside geometry");
        }

        [Test]
        public void Ring_WhollyOutsideTheWindow_IsDropped()
        {
            List<double2> ring = Path(5000.0, 5000.0, 6000.0, 5000.0, 6000.0, 6000.0, 5000.0, 6000.0);

            Assert.That(RingWindowClipper.Clip(ring, Min, Max), Is.Null);
        }

        /// <summary>
        /// A ring straddling one edge gains SYNTHESISED vertices exactly on that edge — the property a
        /// "drop the out-of-bounds vertices" implementation cannot produce. The clipped axis is assigned the
        /// boundary exactly, so the equality below is exact rather than approximate.
        /// </summary>
        [Test]
        public void Ring_StraddlingAnEdge_GainsExactBoundaryVertices()
        {
            List<double2> ring = Path(3000.0, 1000.0, 5000.0, 1000.0, 5000.0, 2000.0, 3000.0, 2000.0);
            List<double2> clipped = RingWindowClipper.Clip(ring, Min, Max);

            Assert.That(clipped, Is.Not.Null);
            Assert.That(clipped.Count, Is.EqualTo(4));

            int onBoundary = 0;
            foreach (double2 v in clipped)
            {
                Assert.That(v.x, Is.LessThanOrEqualTo(Max.x));
                if (v.x == Max.x) onBoundary++;
            }
            Assert.That(onBoundary, Is.EqualTo(2), "both crossings must land exactly on x = 4096");
        }

        /// <summary>Orientation-preserving: the shoelace SIGN survives clipping, which is what keeps a hole a
        /// hole once the polygon is cut by a tile edge.</summary>
        [Test]
        public void Ring_Clipping_PreservesWinding()
        {
            List<double2> clockwise        = Path(3000.0, 1000.0, 5000.0, 1000.0, 5000.0, 2000.0, 3000.0, 2000.0);
            var counterClockwise           = new List<double2>(clockwise);
            counterClockwise.Reverse();

            double before = GeoJsonTestFixtures.Shoelace(clockwise);
            double after  = GeoJsonTestFixtures.Shoelace(RingWindowClipper.Clip(clockwise, Min, Max));
            Assert.That(math.sign(after), Is.EqualTo(math.sign(before)));

            double beforeReversed = GeoJsonTestFixtures.Shoelace(counterClockwise);
            double afterReversed  = GeoJsonTestFixtures.Shoelace(
                RingWindowClipper.Clip(counterClockwise, Min, Max));
            Assert.That(math.sign(afterReversed), Is.EqualTo(math.sign(beforeReversed)));
            Assert.That(math.sign(afterReversed), Is.Not.EqualTo(math.sign(after)));
        }

        // ── Polylines: T3 and T3b ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// T3. A polyline that traverses the window, leaves, and traverses it again must emit TWO open
        /// paths. The count is asserted exactly (not <c>&gt;= 1</c>), and neither path may be closed —
        /// substituting the ring clipper here yields one closed ring, joining the two traverses across a gap
        /// the input never had.
        /// </summary>
        [Test]
        public void T3_LineCrossingTheWindowTwice_EmitsTwoOpenPaths()
        {
            List<double2> line = Path(-500.0, 1000.0, 4500.0, 1000.0, 4500.0, 3000.0, -500.0, 3000.0);
            List<List<double2>> pieces = PolylineWindowClipper.Clip(line, Min, Max);

            Assert.That(pieces.Count, Is.EqualTo(2), "two traverses ⇒ exactly two pieces");
            Assert.That(pieces[0], Is.EqualTo(Path(0.0, 1000.0, 4096.0, 1000.0)));
            Assert.That(pieces[1], Is.EqualTo(Path(4096.0, 3000.0, 0.0, 3000.0)));

            foreach (List<double2> piece in pieces)
            {
                Assert.That(piece.Count, Is.GreaterThanOrEqualTo(2));
                Assert.That(Closed(piece), Is.False, "output paths are OPEN");
            }

            // The two boundary vertices are at different parameters along the input, so a clipper that
            // merged the runs would be joining points that are genuinely apart.
            Assert.That(pieces[0][1].y, Is.Not.EqualTo(pieces[1][0].y));
        }

        /// <summary>
        /// T3b. Leaving and re-entering through the SAME edge also yields two paths. Distinct from T3, whose
        /// runs enter through different edges: this catches an implementation that only breaks a run when
        /// the crossing changes axis.
        /// </summary>
        [Test]
        public void T3b_LineLeavingAndReenteringThroughTheSameEdge_EmitsTwoPaths()
        {
            List<double2> line = Path(2000.0, 1000.0, 4500.0, 1000.0, 4500.0, 3000.0, 2000.0, 3000.0);
            List<List<double2>> pieces = PolylineWindowClipper.Clip(line, Min, Max);

            Assert.That(pieces.Count, Is.EqualTo(2));
            Assert.That(pieces[0], Is.EqualTo(Path(2000.0, 1000.0, 4096.0, 1000.0)));
            Assert.That(pieces[1], Is.EqualTo(Path(4096.0, 3000.0, 2000.0, 3000.0)));
        }

        [Test]
        public void Polyline_AlreadyInsideTheWindow_IsPassedThroughVerbatim()
        {
            List<double2> line = Path(100.0, 100.0, 2000.0, 500.0, 3000.0, 4000.0);
            List<List<double2>> pieces = PolylineWindowClipper.Clip(line, Min, Max);

            Assert.That(pieces.Count, Is.EqualTo(1));
            Assert.That(pieces[0], Is.EqualTo(line), "un-clipped endpoints must not be recomputed");
        }

        [Test]
        public void Polyline_WhollyOutsideTheWindow_EmitsNothing()
        {
            Assert.That(PolylineWindowClipper.Clip(Path(5000.0, 100.0, 6000.0, 200.0), Min, Max),
                Is.Empty);
        }

        [Test]
        public void Polyline_CrossingSeveralInsideSegments_StaysOnePath()
        {
            List<double2> line = Path(-100.0, 1000.0, 1000.0, 1000.0, 2000.0, 2000.0, 5000.0, 2000.0);
            List<List<double2>> pieces = PolylineWindowClipper.Clip(line, Min, Max);

            Assert.That(pieces.Count, Is.EqualTo(1), "a continuous run must not be split at inside vertices");
            Assert.That(pieces[0], Is.EqualTo(Path(0.0, 1000.0, 1000.0, 1000.0, 2000.0, 2000.0, 4096.0, 2000.0)));
        }

        // ── T13/T14: the boundary ASSIGNMENT — the contract nothing above observes ───────────────────
        //
        // Every tooth above survives an implementation that writes the interpolated `a + t·d` instead of
        // assigning the boundary literal, because their crossings all fall on coordinates where the two
        // happen to agree. These two do not: their fixtures are chosen so `a + t·d` demonstrably misses.

        /// <summary>
        /// T13. One world-space line, clipped from the frames of two horizontally adjacent tiles, must place
        /// the shared seam vertex EXACTLY on the seam in each frame — <c>x = Extent</c> for the western tile,
        /// <c>x = 0</c> for its eastern neighbour. That is what makes the two tiles agree about where their
        /// common edge is, and it is the property seam-matched stroke and label geometry will rest on.
        ///
        /// <para><b>Discriminating by construction:</b> the test computes the interpolation the clipper
        /// would otherwise emit and asserts it MISSES the boundary — here by one ulp, leaving the vertex just
        /// inside the western tile and (in the eastern frame, where the same absolute error is enormous in
        /// ulps) just outside the eastern one. A fixture where interpolation and assignment agree would make
        /// the tooth vacuous, so that is asserted rather than assumed.</para>
        ///
        /// <para>The frames of two adjacent tiles differ by exactly one extent in x, and the fixture's
        /// coordinates are integers, so the re-expression is exact and the FULL seam vertex — not just its
        /// clipped axis — must match bit-for-bit. That clause is a corollary of the boundary assignment
        /// here, not an independent check: with the assignment removed both frames drift by the same
        /// amount, so it is the two boundary-literal assertions above it that carry the discrimination.</para>
        /// </summary>
        [Test]
        public void T13_OneLineClippedFromTwoAdjacentTiles_PutsTheSeamVertexExactlyOnTheSeam()
        {
            const double extent = 4096.0;                                  // == Max.x: buffer off, so the
            const double startX = 100.0,  startY = 1000.0;                 // tile edge IS the clip boundary
            const double endX   = 5600.0, endY   = 3000.0;                 // and the two windows partition

            double t     = (extent - startX) / (endX - startX);
            double naive = startX + t * (endX - startX);
            Assert.That(naive, Is.Not.EqualTo(extent),
                "fixture must discriminate: pick coordinates where a + t·d misses the seam");

            // The same line, seen from each tile: the eastern tile's origin sits one extent further east.
            List<List<double2>> fromWest = PolylineWindowClipper.Clip(
                Path(startX, startY, endX, endY), Min, Max);
            List<List<double2>> fromEast = PolylineWindowClipper.Clip(
                Path(startX - extent, startY, endX - extent, endY), Min, Max);

            Assert.That(fromWest.Count, Is.EqualTo(1), "the line leaves the western tile once");
            Assert.That(fromEast.Count, Is.EqualTo(1), "and enters the eastern tile once");

            double2 seamFromWest = fromWest[0][fromWest[0].Count - 1];
            double2 seamFromEast = fromEast[0][0];

            Assert.That(seamFromWest.x, Is.EqualTo(extent),
                "the western tile must put its exit exactly ON the seam, not an ulp short of it");
            Assert.That(seamFromEast.x, Is.EqualTo(0.0),
                "and the eastern tile must put its entry exactly on the same seam in its own frame");

            Assert.That(seamFromWest.x - extent, Is.EqualTo(seamFromEast.x));
            Assert.That(seamFromWest.y, Is.EqualTo(seamFromEast.y),
                "one point, two frames: the tiles must not disagree about where along the seam it sits");

            Assert.That(seamFromWest.y, Is.GreaterThan(startY).And.LessThan(endY),
                "and it must be a real crossing, not an endpoint passed through");
        }

        /// <summary>
        /// T14. A segment meeting the window exactly at a CORNER is put there by two half-planes at the same
        /// parameter, so BOTH of its coordinates must be assigned. Recording only the first plane tried
        /// leaves the other interpolated: on these fixtures that lands the vertex off the corner — outside
        /// the window on entry, inside it on exit — which is precisely the disagreement the assignment
        /// exists to remove.
        /// </summary>
        [Test]
        public void T14_SegmentCrossingExactlyAtAWindowCorner_LandsOnBothBoundariesExactly()
        {
            AssertCornerCrossing(
                Path(-4000.0, -2000.0, 432.0, 216.0), Min, first: true,  corner: Min);
            AssertCornerCrossing(
                Path(100.0, 1000.0, 7426.0, 6676.0),  Max, first: false, corner: Max);
        }

        /// <summary>Clips <paramref name="line"/>, checks the fixture really does tie both half-planes at one
        /// parameter and really does interpolate off the corner, then requires the emitted vertex to sit on
        /// <paramref name="corner"/> exactly. <paramref name="first"/> selects the entry vertex over the exit
        /// one.</summary>
        private static void AssertCornerCrossing(
            List<double2> line, double2 boundary, bool first, double2 corner)
        {
            double2 a = line[0], b = line[1];
            double tx = (boundary.x - a.x) / (b.x - a.x);
            double ty = (boundary.y - a.y) / (b.y - a.y);

            Assert.That(tx, Is.EqualTo(ty), "fixture must be a true corner: both planes at one parameter");
            Assert.That(a.x + tx * (b.x - a.x), Is.Not.EqualTo(boundary.x),
                "fixture must discriminate on x");
            Assert.That(a.y + tx * (b.y - a.y), Is.Not.EqualTo(boundary.y),
                "fixture must discriminate on y — the axis a first-plane-wins clipper leaves interpolated");

            List<List<double2>> pieces = PolylineWindowClipper.Clip(line, Min, Max);
            Assert.That(pieces.Count, Is.EqualTo(1));

            List<double2> piece = pieces[0];
            double2 crossing = first ? piece[0] : piece[piece.Count - 1];

            Assert.That(crossing.x, Is.EqualTo(corner.x), "x must be the boundary literal");
            Assert.That(crossing.y, Is.EqualTo(corner.y), "y must be the boundary literal too, not a + t·d");
        }
    }
}
