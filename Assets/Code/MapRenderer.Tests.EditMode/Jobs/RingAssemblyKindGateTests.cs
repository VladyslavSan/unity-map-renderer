// Unity EditMode only — NativeArray + the Burst RingAssemblyJob. NOT registered in Tools/core-tests.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR B7a T5 — <see cref="RingAssemblyJob"/>'s <b>kind gate</b>: a ring whose feature is not a Polygon is
    /// never classified, whatever its area.
    ///
    /// <para><b>Why the job needs its own gate.</b> The assembler classifies purely by signed area, and a
    /// LineString ring is the same shape of data as a polygon ring — so an ungated job reads a road as a
    /// spurious exterior (or, worse, as a hole of the polygon before it) and corrupts the triangulation
    /// silently. Until B7 the only guard was the caller choosing what to hand the materializer. Since the
    /// geometry buffer is shared across consumers, that caller-side filter now decides which ring INDICES go
    /// into an int array — bookkeeping-shaped code in a loop whose obvious purpose is "compute a draw order",
    /// and therefore much easier to lose than a filter that reads "select my features". Two independent
    /// guards; this file observes the Burst one in isolation.</para>
    /// </summary>
    [TestFixture]
    public class RingAssemblyKindGateTests
    {
        /// <summary>
        /// The gate, with its <b>anti-vacuity twin in the same test</b>: the SAME 4-vertex ring with a large
        /// signed area assembles to <b>1</b> polygon when its feature is a Polygon and <b>0</b> when it is a
        /// LineString. Without the positive arm this could not tell "the gate works" from "this fixture never
        /// produces a polygon at all" — the inert-injection shape this epic has already produced once.
        /// </summary>
        [Test]
        public void KindGate_ALineStringRingIsNeverClassified_ButTheSameRingAsAPolygonIs()
        {
            Assert.AreEqual(0, AssembleOneRing(TileGeometryType.LineString).polygons,
                "a LineString feature's ring must NOT become a polygon — the assembler classifies by signed " +
                "area alone, so without the kind gate this road-shaped ring is read as an exterior");
            Assert.AreEqual(0, AssembleOneRing(TileGeometryType.Unknown).polygons,
                "…and an unfilled kind column reads Unknown, which must also be rejected: a producer that " +
                "forgot to fill the column renders NOTHING (loud) rather than something WRONG (silent)");

            // Anti-vacuity: the identical geometry DOES assemble when its kind says Polygon, so the zeros
            // above are the gate's doing and not the fixture's.
            Assert.AreEqual(1, AssembleOneRing(TileGeometryType.Polygon).polygons,
                "anti-vacuity: the same ring, declared a Polygon, must assemble to exactly one polygon");
        }

        /// <summary>
        /// Placement, not merely presence: a LineString feature's ring appearing BEFORE a polygon feature's
        /// must leave the classifier state untouched, so the polygon still becomes an outer and its hole is
        /// still attached to it.
        ///
        /// <para>A gate placed after the sign/area work would let the LineString establish an exterior sign
        /// first; the polygon's outer would then be read as an opposite-sign candidate hole of a polygon that
        /// does not exist, and the real hole would flip to an outer. That is a 2-polygon / 0-hole answer here
        /// versus the correct 1 / 1.</para>
        /// </summary>
        [Test]
        public void KindGate_ALineStringBeforeAPolygon_DoesNotDisturbTheClassifierState()
        {
            // Feature 0: a LineString ring wound the SAME way as the polygon's outer (so, if it were let
            // through, it would establish the same exterior sign and steal `currentPolyIdx`).
            // Feature 1: a polygon outer + its hole, wound oppositely as the format requires.
            double2[][] rings =
            {
                Ring(3000, 3000, 3600),          // feature 0 — LineString, CCW
                Ring(0, 0, 1000),                // feature 1 — outer, CCW
                RingReversed(300, 300, 400),     // feature 1 — hole, CW
            };
            int[] ringFeature = { 0, 1, 1 };
            var kinds = new[] { TileGeometryType.LineString, TileGeometryType.Polygon };

            (int polygons, int holes, int outerRing) gated = Assemble(rings, ringFeature, kinds);

            Assert.AreEqual(1, gated.polygons,
                "exactly one polygon — the LineString must contribute none, and must not turn the real " +
                "outer into a candidate hole of itself");
            Assert.AreEqual(1, gated.holes, "…and the polygon must keep its hole");
            Assert.AreEqual(1, gated.outerRing,
                "the OUTER must be ring 1 (the polygon feature's exterior), not ring 0 — this is what a " +
                "gate placed after the sign/area work gets wrong while still reporting one polygon");

            // Anti-vacuity: declare feature 0 a Polygon and the answer really does change, so the assertions
            // above discriminate rather than restating a fixture that could only ever produce one polygon.
            (int polygons, int holes, int outerRing) ungated =
                Assemble(rings, ringFeature, new[] { TileGeometryType.Polygon, TileGeometryType.Polygon });
            Assert.AreEqual(2, ungated.polygons,
                "anti-vacuity: with feature 0 declared a Polygon the same rings assemble to TWO polygons, " +
                "so the gated result above is the gate's doing");
        }

        // ── Fixture ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A CCW square of side <paramref name="size"/> at (<paramref name="x"/>,
        /// <paramref name="y"/>) — |shoelace| far above the assembler's 1.0 degenerate threshold.</summary>
        private static double2[] Ring(double x, double y, double size) => new[]
        {
            new double2(x, y),
            new double2(x + size, y),
            new double2(x + size, y + size),
            new double2(x, y + size),
        };

        private static double2[] RingReversed(double x, double y, double size)
        {
            double2[] ring = Ring(x, y, size);
            return new[] { ring[0], ring[3], ring[2], ring[1] };
        }

        private static (int polygons, int holes, int outerRing) AssembleOneRing(TileGeometryType kind)
            => Assemble(new[] { Ring(0, 0, 1000) }, new[] { 0 }, new[] { kind });

        private static (int polygons, int holes, int outerRing) Assemble(
            double2[][] rings, int[] ringFeature, TileGeometryType[] featureKinds)
        {
            int totalVerts = 0;
            foreach (double2[] r in rings) totalVerts += r.Length;

            var verts       = new NativeArray<double2>(totalVerts, Allocator.Persistent);
            var ringOffsets = new NativeArray<int>(rings.Length + 1, Allocator.Persistent);
            var ringFeatIdx = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var kinds       = new NativeArray<TileGeometryType>(featureKinds.Length, Allocator.Persistent);

            var polyOuterIdx  = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
                int pos = 0;
                for (int ri = 0; ri < rings.Length; ri++)
                {
                    ringOffsets[ri] = pos;
                    ringFeatIdx[ri] = ringFeature[ri];
                    foreach (double2 v in rings[ri]) verts[pos++] = v;
                }
                ringOffsets[rings.Length] = pos;
                for (int fi = 0; fi < featureKinds.Length; fi++) kinds[fi] = featureKinds[fi];

                new RingAssemblyJob
                {
                    Vertices             = verts,
                    RingOffsets          = ringOffsets,
                    RingFeatureIdx       = ringFeatIdx,
                    RingCount            = rings.Length,
                    FeatureGeometryType  = kinds,
                    OutPolyOuterRingIdx  = polyOuterIdx,
                    OutPolyHoleListStart = polyHoleStart,
                    OutPolyHoleCount     = polyHoleCount,
                    OutHoleRingIdxs      = holeRingIdxs,
                    OutPolygonCount      = polyCountArr,
                    OutHoleCount         = holeCountArr,
                }.Run();

                return (polyCountArr[0], holeCountArr[0],
                        polyCountArr[0] > 0 ? polyOuterIdx[0] : -1);
            }
            finally
            {
                verts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose(); kinds.Dispose();
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            }
        }
    }
}
