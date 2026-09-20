// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// A-2: <see cref="LineAnchorPlacement.Compute"/> places along-line anchors ONCE in tile space, as stable
    /// <see cref="LineAnchor"/> topology. These teeth pin: the anchor positions (spacing walk + segment
    /// transitions), the build-time hard cap (the anti-hang guard, moved here from the old per-frame loop), the
    /// line-center + short-line degeneracies, and the decisive no-slide property — an anchor recovers the SAME
    /// world point (<c>lerp(path[seg], path[seg+1], t)</c>) regardless of any per-frame projection, which is what
    /// the old fixed-screen-px-from-start walk could not do (and the A-3 identity key builds on).
    /// </summary>
    [TestFixture]
    public class LineAnchorPlacementTests
    {
        private static List<double2> Line(params (double x, double y)[] pts)
        {
            var l = new List<double2>(pts.Length);
            foreach (var (x, y) in pts) l.Add(new double2(x, y));
            return l;
        }

        // Recover an anchor's world point from the polyline it indexes (the A-3 forward-guard helper).
        private static double2 WorldOf(List<double2> path, LineAnchor a)
            => math.lerp(path[a.Segment], path[a.Segment + 1], a.T);

        // ── Line placement: anchors at spacing·(k+0.5), each recovering the right world point. ──
        [Test]
        public void Line_StraightLine_AnchorsAtHalfSpacingMultiples()
        {
            var path = Line((0, 0), (100, 0));
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 25, SymbolPlacement.Line);

            Assert.AreEqual(4, anchors.Length, "arcs 12.5/37.5/62.5/87.5 fit in a length-100 line; 112.5 does not");
            double[] expectedX = { 12.5, 37.5, 62.5, 87.5 };
            for (int i = 0; i < anchors.Length; i++)
                Assert.AreEqual(expectedX[i], WorldOf(path, anchors[i]).x, 1e-6,
                    $"anchor {i} sits at spacing·({i}+0.5) along the line");
        }

        // ── Segment transitions: an L-bend anchor lands on the correct segment with the correct t. ──
        [Test]
        public void Line_LBend_AnchorsCrossSegmentsCorrectly()
        {
            var path = Line((0, 0), (40, 0), (40, 40)); // total arc length 80
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 20, SymbolPlacement.Line);

            Assert.AreEqual(4, anchors.Length);
            double2[] expected = { new double2(10, 0), new double2(30, 0), new double2(40, 10), new double2(40, 30) };
            for (int i = 0; i < anchors.Length; i++)
            {
                double2 w = WorldOf(path, anchors[i]);
                Assert.AreEqual(expected[i].x, w.x, 1e-6, $"anchor {i} x");
                Assert.AreEqual(expected[i].y, w.y, 1e-6, $"anchor {i} y");
            }
            Assert.AreEqual(0, anchors[1].Segment, "arc 30 is on the first segment");
            Assert.AreEqual(1, anchors[2].Segment, "arc 50 has crossed onto the second segment");
        }

        // ── THE anti-hang tooth (moved here from the old per-frame loop): a pathological huge line / tiny
        //    spacing is capped at MaxAnchors at BUILD time — never an unbounded array. ──
        [Test]
        public void Line_Pathological_CountIsHardCappedAtBuild()
        {
            var path = Line((0, 0), (1e9, 0)); // would be ~1e9 anchors at spacing 1 without the cap
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 1, SymbolPlacement.Line);
            Assert.AreEqual(LineAnchorPlacement.MaxAnchors, anchors.Length, "the count is bounded, not data-derived");
        }

        // ── line-center: exactly one anchor at the mid arc length. ──
        [Test]
        public void LineCenter_SingleMidAnchor()
        {
            var path = Line((0, 0), (100, 0));
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 25, SymbolPlacement.LineCenter);
            Assert.AreEqual(1, anchors.Length);
            Assert.AreEqual(50.0, WorldOf(path, anchors[0]).x, 1e-6, "one anchor at the line midpoint");
        }

        // ── A line shorter than one spacing still yields ONE centred anchor (a placeable line is never empty). ──
        [Test]
        public void Line_ShorterThanSpacing_YieldsOneCentredAnchor()
        {
            var path = Line((0, 0), (10, 0));
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 25, SymbolPlacement.Line);
            Assert.AreEqual(1, anchors.Length, "short line → the centred fallback, not zero anchors");
            Assert.AreEqual(5.0, WorldOf(path, anchors[0]).x, 1e-6, "at the line midpoint");
        }

        // ── Degenerate inputs (fewer than 2 points, or zero-length) produce no anchors. ──
        [Test]
        public void Degenerate_ProducesNoAnchors()
        {
            Assert.AreEqual(0, LineAnchorPlacement.Compute(Line((5, 5)), 25, SymbolPlacement.Line).Length,
                "a single point has no segment");
            Assert.AreEqual(0, LineAnchorPlacement.Compute(Line((5, 5), (5, 5)), 25, SymbolPlacement.Line).Length,
                "a zero-length (coincident) line has no arc to place along");
            Assert.AreEqual(0, LineAnchorPlacement.Compute(null, 25, SymbolPlacement.Line).Length,
                "null path is empty, not a throw");
        }
    }
}
