// Engine-free (NUnit + Core only) → runs in BOTH the Unity EditMode runner and the fast core-tests project.
// UMR-125: the opt-in projected-area LOD strategy, as a THIRD mode alongside Flat and ScreenSpaceLod (the
// unchanged default). This is a quality-vs-frame-time tradeoff, not a fix — see ScreenSpaceLodStrategy and
// ProjectedAreaLodStrategy's own summaries. The distance rule's numbers (TiltCoverGrowthTests) are untouched
// by this file; every count here is the AREA rule's own, independently pinned.
//
// These are CHARACTERISATION tests. The exact counts below are deliberately brittle — see the header of
// TiltCoverGrowthTests.cs for why. A stage that changes tile selection or this strategy is REQUIRED to edit
// these numbers and say so.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// <see cref="ProjectedAreaLodStrategy"/>'s cover, measured on the same production selector wiring as
    /// <see cref="TiltCoverGrowthTests"/> (same fixture pose family), plus the aggressiveness knob's reach
    /// and its two measured limits (§1.7 of the stage plan: it moves the curve, not its shape, and it is
    /// NOT monotone on the globe).
    /// </summary>
    public class ProjectedAreaLodTests
    {
        // The production wiring, mirrored from MapView.EnsureSelector + the MapViewConfig defaults.
        private const int    MinZoom        = 0;
        private const int    MaxZoom        = 14;
        private const int    OnScreenTilePx = 512;
        private const double GlobeFarCap    = 8.0;
        private const double MercatorFarCap = 4.0;

        // Fixed pose inputs: Berlin, heading 0, 60° vertical FOV, a 1600x900 viewport — same as TiltCoverGrowthTests.
        private const double LookAtLon = 13.405;
        private const double LookAtLat = 52.52;
        private const double Zoom      = 13.0;
        private static readonly double2 Viewport = new double2(1600.0, 900.0);

        /// <summary>The camera pose for a tilt, with every other input held at the fixture constants.</summary>
        private static CameraProperties Cam(double tiltDeg, double zoom = Zoom)
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = LookAtLon, Latitude = LookAtLat, Altitude = 0.0 },
                zoom, 0.0, tiltDeg);

        /// <summary>The globe cover at a tilt, defaulting to <see cref="ProjectedAreaLodStrategy"/> — an
        /// explicit <paramref name="lod"/> selects the comparison strategy (distance rule or flat).</summary>
        private static List<TileId> SelectGlobe(double tiltDeg, double zoom = Zoom, ITileLodStrategy lod = null)
        {
            var selector = new FrustumTileSelector(MinZoom, MaxZoom, OnScreenTilePx,
                lod ?? new ProjectedAreaLodStrategy(),
                new RaySphereFarPlane(SphericalProjection.Radius, GlobeFarCap));
            var view = new ViewContext
            {
                Camera     = Cam(tiltDeg, zoom),
                ViewportPx = Viewport,
                Projection = new SphericalProjection(),
            };
            var buffer = new List<TileId>();
            selector.SelectVisibleTiles(view, buffer);
            return buffer;
        }

        /// <summary>The Mercator cover at a tilt, defaulting to <see cref="ProjectedAreaLodStrategy"/>.</summary>
        private static List<TileId> SelectMercator(double tiltDeg, double zoom = Zoom, ITileLodStrategy lod = null)
        {
            var selector = new FrustumTileSelector(MinZoom, MaxZoom, OnScreenTilePx,
                lod ?? new ProjectedAreaLodStrategy(),
                new GeometryAwareFarPlane(MercatorFarCap));
            var view = new ViewContext
            {
                Camera     = Cam(tiltDeg, zoom),
                ViewportPx = Viewport,
                Projection = new WebMercatorProjection(),
            };
            var buffer = new List<TileId>();
            selector.SelectVisibleTiles(view, buffer);
            return buffer;
        }

        private static int[] GlobeRow(double aggressiveness)
        {
            var lod = new ProjectedAreaLodStrategy(aggressiveness);
            return new[]
            {
                SelectGlobe(0.0,  lod: lod).Count,
                SelectGlobe(30.0, lod: lod).Count,
                SelectGlobe(45.0, lod: lod).Count,
                SelectGlobe(60.0, lod: lod).Count,
            };
        }

        private static int[] MercatorRow(double aggressiveness)
        {
            var lod = new ProjectedAreaLodStrategy(aggressiveness);
            return new[]
            {
                SelectMercator(0.0,  lod: lod).Count,
                SelectMercator(30.0, lod: lod).Count,
                SelectMercator(45.0, lod: lod).Count,
                SelectMercator(60.0, lod: lod).Count,
            };
        }

        /// <summary>True iff tile <paramref name="t"/> is <paramref name="ancestor"/> itself, or one of its
        /// quadtree descendants.</summary>
        private static bool IsAncestorOrSelf(TileId ancestor, TileId t)
        {
            if (t.Z < ancestor.Z) return false;
            int shift = t.Z - ancestor.Z;
            return (t.X >> shift) == ancestor.X && (t.Y >> shift) == ancestor.Y;
        }

        private static void AssertUniformZoom(List<TileId> cover, int target, string label)
        {
            int minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (TileId t in cover) { if (t.Z < minZ) minZ = t.Z; if (t.Z > maxZ) maxZ = t.Z; }
            Assert.AreEqual(target, minZ, $"{label}: a tile stopped below the target zoom at tilt 0");
            Assert.AreEqual(target, maxZ, $"{label}: a tile went finer than the target zoom at tilt 0");
        }

        // ── T-ENUM — the enum's numbering is a persisted contract ─────────────────────────────────

        /// <summary>
        /// T-ENUM. The scene assets serialize the raw INTEGER, not the member name — MapDemo.unity:440 and
        /// OpenStreetMapLiberty.unity:439 both hold <c>LodMode: 1</c>. Inserting <see cref="TileLodMode.ProjectedArea"/>
        /// above <see cref="TileLodMode.ScreenSpaceLod"/> would renumber it to 2 and silently repoint both
        /// scenes at the aggressive mode, with nothing visible in the C# diff.
        /// </summary>
        [Test]
        public void TileLodMode_IntegerValues_ArePersistedContract()
        {
            Assert.AreEqual(0, (int)TileLodMode.Flat);
            Assert.AreEqual(1, (int)TileLodMode.ScreenSpaceLod);
            Assert.AreEqual(2, (int)TileLodMode.ProjectedArea);
        }

        // ── T-DISAGREE — the two strategies must not agree ────────────────────────────────────────

        /// <summary>
        /// T-DISAGREE. Both strategies over the SAME view (globe z13 1600x900 tilt 60): the distance rule's
        /// 112 and the area rule's 39, as two exact integers. An alias, a copy-paste of one formula into the
        /// other, or an area strategy that forwards to the distance one fails instantly.
        /// </summary>
        [Test]
        public void GlobeTilt60_DistanceAndAreaStrategies_DisagreeOverTheSameView()
        {
            int distance = SelectGlobe(60.0, lod: new ScreenSpaceLodStrategy()).Count;
            int area     = SelectGlobe(60.0, lod: new ProjectedAreaLodStrategy()).Count;
            Assert.AreEqual(112, distance, "distance rule, globe z13 1600x900 tilt 60");
            Assert.AreEqual(39,  area,     "area rule, globe z13 1600x900 tilt 60");
        }

        // ── T-GROWTH / T-MERC — the area rule's own exact covers ──────────────────────────────────

        /// <summary>
        /// T-GROWTH. Exact globe cover sizes under the area rule, same pose family as
        /// <see cref="TiltCoverGrowthTests"/>'s T1. Characterises the tradeoff — do not read a smaller number
        /// as "better"; the distance rule's 24/30/54/112 stays pinned there and both modes ship.
        /// </summary>
        [Test]
        public void GlobeTiltSweep_ProjectedArea_EmitsTheseExactCoverSizes()
        {
            Assert.AreEqual(24, SelectGlobe(0.0).Count,  "globe z13 1600x900 tilt 0");
            Assert.AreEqual(20, SelectGlobe(30.0).Count, "globe z13 1600x900 tilt 30");
            Assert.AreEqual(26, SelectGlobe(45.0).Count, "globe z13 1600x900 tilt 45");
            Assert.AreEqual(39, SelectGlobe(60.0).Count, "globe z13 1600x900 tilt 60");
        }

        /// <summary>
        /// T-MERC. Exact Mercator cover sizes under the area rule — the only tooth that observes the
        /// near-plane depth clamp in <c>PixelBasis.ToPixels</c> at the default aggressiveness (RED: removing
        /// it collapses this to 12/1/1/1; every globe count at 1.0, including T-FOLD, is unaffected). Not
        /// universal: the clamp also moves the globe row at aggressiveness 2.0 — see the characterisation
        /// test below.
        /// </summary>
        [Test]
        public void MercatorTiltSweep_ProjectedArea_EmitsTheseExactCoverSizes()
        {
            Assert.AreEqual(12, SelectMercator(0.0).Count,  "mercator z13 1600x900 tilt 0");
            Assert.AreEqual(14, SelectMercator(30.0).Count, "mercator z13 1600x900 tilt 30");
            Assert.AreEqual(18, SelectMercator(45.0).Count, "mercator z13 1600x900 tilt 45");
            Assert.AreEqual(19, SelectMercator(60.0).Count, "mercator z13 1600x900 tilt 60");
        }

        /// <summary>
        /// T-FOLD. The only tooth that sees the abs-per-triangle-vs-signed-fan fork: every z13 pose above
        /// projects a convex fan, where the two agree. At this low-zoom pose the fan folds near the camera, so
        /// they diverge. RED-verified: summing the four triangles signed, then taking one abs around the
        /// total, collapses this to 16.
        /// </summary>
        [Test]
        public void GlobeLowZoom_FoldedFanCover_IsNineteenTiles()
        {
            Assert.AreEqual(19, SelectGlobe(60.0, zoom: 4.0).Count, "globe camera zoom 4 tilt 60");
        }

        // ── T-AGGR-1 — the default value must reproduce the pinned numbers ────────────────────────

        /// <summary>
        /// T-AGGR-1. <c>new ProjectedAreaLodStrategy()</c> and <c>new ProjectedAreaLodStrategy(1.0)</c> both
        /// reproduce the exact covers above. Ties the parametric form to numbers verified before the knob
        /// existed — if the parameter perturbs the default path at all, this names why.
        /// </summary>
        [Test]
        public void DefaultAggressiveness_ReproducesTheMeasuredCovers()
        {
            CollectionAssert.AreEqual(new[] { 24, 20, 26, 39 }, GlobeRow(1.0),    "globe, explicit 1.0");
            CollectionAssert.AreEqual(new[] { 12, 14, 18, 19 }, MercatorRow(1.0), "mercator, explicit 1.0");

            // The FULL sweep, not just one tilt — a single-tilt check can miss a perturbed default that
            // happens to land on the same side of that one tilt's threshold (RED-verified: a 1.0 → 1.1
            // default shift left tilt 60 unchanged on both projections but moved mercator tilt 0/30/45).
            var implicitDefault = new ProjectedAreaLodStrategy();
            CollectionAssert.AreEqual(new[] { 24, 20, 26, 39 },
                new[] { SelectGlobe(0.0, lod: implicitDefault).Count, SelectGlobe(30.0, lod: implicitDefault).Count,
                        SelectGlobe(45.0, lod: implicitDefault).Count, SelectGlobe(60.0, lod: implicitDefault).Count },
                "globe, implicit default");
            CollectionAssert.AreEqual(new[] { 12, 14, 18, 19 },
                new[] { SelectMercator(0.0, lod: implicitDefault).Count, SelectMercator(30.0, lod: implicitDefault).Count,
                        SelectMercator(45.0, lod: implicitDefault).Count, SelectMercator(60.0, lod: implicitDefault).Count },
                "mercator, implicit default");
        }

        // ── T-AGGR-MONO — monotone at the strategy, and on mercator covers; NOT on globe covers ───

        /// <summary>
        /// T-AGGR-MONO, strategy level. <c>StopAt</c> is non-decreasing in the aggressiveness parameter for a
        /// fixed context: below the threshold it must not stop, at the threshold it must, and raising it
        /// further must not un-stop it. Falsifiable by a stub that ignores the parameter.
        /// </summary>
        [Test]
        public void StopAt_IsNonDecreasing_InAggressiveness()
        {
            var ctx = new TileLodContext { OnScreenPx = 100.0, TargetOnScreenPx = 50.0 }; // ratio 2.0
            Assert.IsFalse(new ProjectedAreaLodStrategy(1.0).StopAt(in ctx), "below the ratio: must not stop");
            Assert.IsTrue(new ProjectedAreaLodStrategy(2.0).StopAt(in ctx), "at the ratio: must stop");
            Assert.IsTrue(new ProjectedAreaLodStrategy(5.0).StopAt(in ctx), "past the ratio: must stay stopped");
        }

        /// <summary>
        /// T-AGGR-MONO, Mercator cover level. Non-increasing across aggressiveness 0.5 → 1.0 → 2.0 at every
        /// tilt, and strictly smaller somewhere (proves the knob actually moves the cover, not just that it
        /// never grows it). Mercator only — see the globe test below for why.
        /// </summary>
        [Test]
        public void MercatorAggressivenessSweep_CoverIsMonotoneNonIncreasing()
        {
            // Non-increasing 12→14→28→29, 12→14→18→19, 6→11→18→19 down each column (0.5→1.0→2.0) and
            // strictly smaller somewhere (tilt 0: 12→6; tilt 45: 28→18) — read directly off the three
            // pinned rows; no further assertion needed.
            CollectionAssert.AreEqual(new[] { 12, 14, 28, 29 }, MercatorRow(0.5), "mercator aggressiveness 0.5");
            CollectionAssert.AreEqual(new[] { 12, 14, 18, 19 }, MercatorRow(1.0), "mercator aggressiveness 1.0");
            CollectionAssert.AreEqual(new[] { 6, 11, 18, 19 },  MercatorRow(2.0), "mercator aggressiveness 2.0");
        }

        /// <summary>
        /// T-AGGR-MONO, globe — CHARACTERISATION, not a monotonicity claim. On a globe, stopping early can
        /// EMIT a coarse tile where descending would have had all four children culled (frustum + horizon
        /// occlusion apply per tile, per level), so a coarser policy is not always a smaller cover (§1.7 Limit
        /// 2 of the stage plan). Measured: RAISING aggressiveness 0.5 → 0.55 RAISES the cover at tilt 30
        /// (28 → 33) and tilt 45 (45 → 48) — a genuine rise as the knob gets coarser, not the same data read
        /// backwards. Pinned exactly so the next reader does not "fix" this as a bug; do NOT add a
        /// non-increasing assertion here.
        /// </summary>
        [Test]
        public void GlobeAggressivenessSweep_CoverIsCharacterisedNotMonotone()
        {
            int[] at050 = GlobeRow(0.5);
            int[] at055 = GlobeRow(0.55);

            CollectionAssert.AreEqual(new[] { 24, 28, 45, 39 }, at050, "globe aggressiveness 0.5");
            CollectionAssert.AreEqual(new[] { 24, 33, 48, 39 }, at055, "globe aggressiveness 0.55");
            CollectionAssert.AreEqual(new[] { 24, 20, 26, 39 }, GlobeRow(1.0), "globe aggressiveness 1.0");
            CollectionAssert.AreEqual(new[] { 15, 13, 18, 22 }, GlobeRow(2.0), "globe aggressiveness 2.0");

            Assert.Greater(at055[1], at050[1],
                "tilt 30: cover must RISE 28→33 as aggressiveness rises 0.5→0.55 — a correct implementation " +
                "is non-monotone here; an implementation that clamps this to non-increasing is the bug.");
            Assert.Greater(at055[2], at050[2],
                "tilt 45: cover must RISE 45→48 as aggressiveness rises 0.5→0.55, for the same reason.");
        }

        // ── T-TILT0 / T-PARTITION / T-NOUNDER — anti-shallow-fix teeth ────────────────────────────

        /// <summary>
        /// T-TILT0. At tilt 0 nothing is foreshortened, so the stop rule must never fire under the area rule
        /// either: every emitted tile sits at the target zoom, on both projections. Scoped to this fixture's
        /// pose — a wide-enough overhead globe view legitimately grazes incidence at its limb even at tilt 0.
        /// </summary>
        [Test]
        public void TiltZero_ProjectedArea_StopRuleNeverFiresAtTheFixturePose()
        {
            int target = Cam(0.0).IntegerZoom;
            AssertUniformZoom(SelectGlobe(0.0),    target, "globe");
            AssertUniformZoom(SelectMercator(0.0), target, "mercator");
        }

        /// <summary>
        /// T-PARTITION. At the default aggressiveness (1.0) the area cover is an exact quadtree partition of
        /// the flat (uniform-zoom) cover: no area tile is an ancestor of another, and every flat leaf has
        /// exactly one ancestor-or-self in the area cover. Scoped to 1.0 — at other values the area cover can
        /// EXCEED the flat cover (§1.7 Limit 2), so "partition of the flat cover" is only well-posed here. A
        /// count clamp or cover cap drops tiles, which surfaces here as an uncovered leaf.
        /// </summary>
        [Test]
        public void GlobeTiltSweep_ProjectedAreaCoverIsAnExactQuadtreePartitionOfTheFlatCover()
        {
            foreach (double tilt in new[] { 0.0, 30.0, 45.0, 60.0 })
            {
                List<TileId> area = SelectGlobe(tilt);
                List<TileId> flat = SelectGlobe(tilt, lod: new FlatLodStrategy());

                int ancestorPairs = 0;
                for (int i = 0; i < area.Count; i++)
                    for (int j = 0; j < area.Count; j++)
                        if (i != j && IsAncestorOrSelf(area[i], area[j])) ancestorPairs++;
                Assert.AreEqual(0, ancestorPairs, $"tilt {tilt}: an area tile is an ancestor of another area tile");

                // A leaf covered by two area tiles would make one of them the other's ancestor (both lie
                // on the same root-to-leaf path), which the ancestorPairs check above already rules out —
                // so only "uncovered" needs its own count here.
                int uncoveredLeaves = 0;
                foreach (TileId leaf in flat)
                {
                    int matches = 0;
                    foreach (TileId t in area)
                        if (IsAncestorOrSelf(t, leaf)) matches++;
                    if (matches == 0) uncoveredLeaves++;
                }
                Assert.AreEqual(0, uncoveredLeaves, $"tilt {tilt}: a flat leaf has no area ancestor-or-self");
            }
        }

        /// <summary>
        /// T-NOUNDER. Every stop-rule-emitted tile (<c>t.Z &lt; cam.IntegerZoom</c>) measures ≤ 512px by
        /// <see cref="TiltCoverGrowthTests"/>'s independent <c>ProjectedTilePx</c> instrument, which runs no
        /// selector. 512 is the target itself, not a recorded measurement. Observed maxima 453 / 318 / 238 px
        /// at tilt 30/45/60 — binding at tilt 30 with 59px of room, so a regression here is real, not noise.
        /// </summary>
        [Test]
        public void GlobeTiltSweep_ProjectedAreaEmittedTiles_NeverExceedTheTargetSize()
        {
            var projection = new SphericalProjection();
            foreach (double tilt in new[] { 30.0, 45.0, 60.0 })
            {
                CameraProperties cam = Cam(tilt);
                double max = 0.0;
                int    stopEmittedCount = 0;
                foreach (TileId t in SelectGlobe(tilt))
                {
                    if (t.Z >= cam.IntegerZoom) continue; // near-field cap tile, not stop-rule-emitted
                    stopEmittedCount++;
                    double px = TiltCoverGrowthTests.ProjectedTilePx(projection, cam, Viewport, t);
                    if (px > max) max = px;
                }
                Assert.Greater(stopEmittedCount, 0,
                    $"tilt {tilt}: no stop-rule-emitted tile — the max-px check below would pass vacuously");
                Assert.LessOrEqual(max, 512.0, $"tilt {tilt}: a stop-rule-emitted tile exceeds the 512px target");
            }
        }
    }
}
