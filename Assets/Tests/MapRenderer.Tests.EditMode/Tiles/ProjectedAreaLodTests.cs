// Tiles/ProjectedAreaLodTests.cs — projected-area and tilt-driven LOD cover measurement, tile-cover statistics, and tile-priority ordering (fast lane: engine-free, compiled by Tools/core-tests too).
//
// The two cover-growth fixtures first (projected-area, tilt), then cover statistics and priority ordering.
//
// Contents:
//   ProjectedAreaLodTests  — ProjectedAreaLodStrategy's cover, measured on the same production selector wiring as TiltCoverGrowthTests (same fixture pose family), plus the aggressiveness knob's reach and its two measured limits.
//   TileCoverStatsTests    — Engine-free (NUnit + Core only) → runs in BOTH the Unity EditMode runner and the fast core-tests project.
//   TilePriorityTests      — TilePriority.Key/SortByPriority must be monotonic in each metric (center strictly before a far corner), deterministic (same input twice → identical order), and the sort must place the center-most tile first.
//   TiltCoverGrowthTests   — Tile-cover growth under camera tilt, measured on the production selector wiring (ScreenSpaceLodStrategy + RaySphereFarPlane), plus a self-check of the on-screen-size instrument the measurement reads.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;


namespace MapRenderer.Tests.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // ProjectedAreaLodTests — ProjectedAreaLodStrategy's cover, plus the aggressiveness knob's limits
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ProjectedAreaLodStrategy"/>'s cover, measured on the same production selector wiring as
    /// <see cref="TiltCoverGrowthTests"/> (same fixture pose family), plus the aggressiveness knob's reach
    /// and its two measured limits (it moves the curve, not its shape, and it is
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
        /// occlusion apply per tile, per level), so a coarser policy is not always a smaller cover.
        /// Measured: RAISING aggressiveness 0.5 → 0.55 RAISES the cover at tilt 30
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
        /// EXCEED the flat cover, so "partition of the flat cover" is only well-posed here. A
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileCoverStatsTests — engine-free, runs in both EditMode and the fast core-tests project
    // ───────────────────────────────────────────────────────────────────────────────────

    public class TileCoverStatsTests
    {
        private static HashSet<int> NewX() => new HashSet<int>();
        private static HashSet<int> NewY() => new HashSet<int>();

        private static TileId T(int z, int x, int y) => new TileId { Z = z, X = x, Y = y };

        // ── TileCoverStats.Compute ───────────────────────────────────────────────────────────────

        [Test]
        public void Antimeridian_WrappedXValues_ColumnsCountsDistinctX_NotMaxMinusMinPlusOne()
        {
            // THE decisive cover-stats test (globe-relevant): a single-zoom cover wrapping the antimeridian —
            // X values {0, 1, n-2, n-1} at z=5 (n=32). A max(X)-min(X)+1 implementation returns 31 (huge);
            // the correct answer is 4 distinct columns.
            const int z = 5, n = 1 << z;
            var cover = new List<TileId>
            {
                T(z, 0,     10), T(z, 1,     10),
                T(z, n - 2, 10), T(z, n - 1, 10),
            };

            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());

            Assert.AreEqual(4, columns, "antimeridian wrap: 4 distinct X values, not n-2");
            Assert.AreEqual(1, rows, "all four tiles share Y=10");
            Assert.AreEqual(z, minZ);
            Assert.AreEqual(z, maxZ);
        }

        [Test]
        public void MixedZoomCover_SpanCorrect_AndDimsCountOnlyTheFinestLevel()
        {
            const int nearZ = 6, farZ = 4;
            var cover = new List<TileId>
            {
                // A 2x3 near-field block at the finest level (nearZ).
                T(nearZ, 10, 20), T(nearZ, 11, 20), T(nearZ, 12, 20),
                T(nearZ, 10, 21), T(nearZ, 11, 21), T(nearZ, 12, 21),
                // A single coarse far tile at a coarser level — must NOT inflate the near-field grid.
                T(farZ, 0, 0),
            };

            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());

            Assert.AreEqual(farZ, minZ);
            Assert.AreEqual(nearZ, maxZ);
            Assert.AreNotEqual(minZ, maxZ, "mixed-zoom cover: IsMixedZoom would be true");
            Assert.AreEqual(3, columns, "columns count only the nearZ tiles (3 distinct X)");
            Assert.AreEqual(2, rows, "rows count only the nearZ tiles (2 distinct Y)");
        }

        [Test]
        public void ContiguousSingleZoomBlock_ColumnsAndRowsExact()
        {
            const int z = 8;
            var cover = new List<TileId>();
            for (int x = 100; x < 104; x++)      // 4 columns
                for (int y = 50; y < 53; y++)    // 3 rows
                    cover.Add(T(z, x, y));

            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());

            Assert.AreEqual(4, columns);
            Assert.AreEqual(3, rows);
            Assert.AreEqual(z, minZ);
            Assert.AreEqual(z, maxZ);
            Assert.AreEqual(cover.Count, columns * rows, "a contiguous single-zoom block is a filled rectangle");
        }

        [Test]
        public void EmptyCover_ReturnsAllZero()
        {
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(new List<TileId>(), NewX(), NewY());
            Assert.AreEqual(0, columns);
            Assert.AreEqual(0, rows);
            Assert.AreEqual(0, minZ);
            Assert.AreEqual(0, maxZ);
        }

        // ── FrustumTileSelector aspect/Flat dims tooth (planar, direct ViewContext — no MapView) ────

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        [Test]
        public void WideViewport_Planar_Flat_ColumnsExceedRows_AndCoverIsAFilledRectangle()
        {
            // A wide (3:1) viewport over a planar Web-Mercator projection, single-zoom (Flat) LOD, overhead
            // (tilt 0): the frustum footprint is a rectangle wider than it is tall, so CoverColumns must exceed
            // CoverRows, and (planar-Flat only — see docs) the cover is exactly cols*rows, no gaps.
            var view = new ViewContext
            {
                Camera     = Cam(0, 0, 6.0),
                ViewportPx = new double2(1536.0, 512.0), // 3:1
                Projection = new WebMercatorProjection(),
            };
            var selector = new FrustumTileSelector(minZoom: 0, maxZoom: 22, onScreenTilePx: 512,
                                                    lod: new FlatLodStrategy());
            var cover = new List<TileId>();
            selector.SelectVisibleTiles(in view, cover);

            Assert.IsNotEmpty(cover);
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());
            Assert.AreEqual(minZ, maxZ, "Flat LOD ⇒ single-zoom cover");
            Assert.Greater(columns, rows, "a 3:1 wide viewport must select a wider-than-tall grid");
            Assert.AreEqual(cover.Count, columns * rows,
                "planar + Flat + overhead ⇒ the cover is a filled rectangle with no gaps");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TilePriorityTests — TilePriority.Key/SortByPriority monotonicity and determinism
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TilePriorityTests
    {
        private static readonly WebMercatorProjection Proj = new WebMercatorProjection();

        private static TilePriorityContext ContextFor(GeoCoordinate3D lookAt, TilePriorityStrategy strategy,
            double zoom = 10.0, double heading = 0.0, double tilt = 0.0)
        {
            var cam = new CameraProperties(lookAt, zoom, heading, tilt);
            return TilePriorityContext.From(in cam, new double2(1024, 1024), Proj, strategy);
        }

        /// <summary>The tile whose CENTER coincides with lon/lat, so <see cref="TilePriority.Key"/> reads
        /// ~0 for it under either strategy at tilt=0 (camera directly overhead its own look-at).</summary>
        private static GeoCoordinate3D LookAtCenterOf(TileId tile)
        {
            double2 ll = tile.ToLonLat(0.5, 0.5, 1.0);
            return new GeoCoordinate3D { Longitude = ll.x, Latitude = ll.y, Altitude = 0 };
        }

        [Test]
        public void Key_CenterTile_IsSmallerThan_FarCornerTile_BothStrategies()
        {
            var center = new TileId { Z = 8, X = 128, Y = 96 };
            var corner = new TileId { Z = 8, X = center.X + 50, Y = center.Y };
            GeoCoordinate3D lookAt = LookAtCenterOf(center);

            foreach (TilePriorityStrategy strategy in new[]
                     { TilePriorityStrategy.GroundDistanceToLookAt, TilePriorityStrategy.CameraDistance })
            {
                TilePriorityContext ctx = ContextFor(lookAt, strategy);
                double centerKey = TilePriority.Key(in center, in ctx);
                double cornerKey = TilePriority.Key(in corner, in ctx);
                Assert.Less(centerKey, cornerKey,
                    $"strategy={strategy}: the tile AT lookAt must rank strictly before a distant corner tile.");
            }
        }

        [Test]
        public void Key_IsStrictlyMonotonic_AlongAStraightLineOfTiles_BothStrategies()
        {
            var center = new TileId { Z = 9, X = 200, Y = 150 };
            GeoCoordinate3D lookAt = LookAtCenterOf(center);

            foreach (TilePriorityStrategy strategy in new[]
                     { TilePriorityStrategy.GroundDistanceToLookAt, TilePriorityStrategy.CameraDistance })
            {
                TilePriorityContext ctx  = ContextFor(lookAt, strategy);
                double               prev = -1.0;
                for (int dx = 0; dx <= 10; dx++)
                {
                    var    t   = new TileId { Z = center.Z, X = center.X + dx, Y = center.Y };
                    double key = TilePriority.Key(in t, in ctx);
                    Assert.Greater(key, prev,
                        $"strategy={strategy}: key must strictly increase with distance (dx={dx}).");
                    prev = key;
                }
            }
        }

        [Test]
        public void SortByPriority_PutsCenterTileFirst_AndIsDeterministicAcrossRuns()
        {
            var center = new TileId { Z = 7, X = 64, Y = 48 };
            GeoCoordinate3D     lookAt = LookAtCenterOf(center);
            TilePriorityContext ctx    = ContextFor(lookAt, TilePriorityStrategy.GroundDistanceToLookAt);

            List<TileId> Build() => new List<TileId>
            {
                new TileId { Z = 7, X = 64 + 20, Y = 48 },     // far
                new TileId { Z = 7, X = 64,      Y = 48 },     // AT lookAt — must sort to the head
                new TileId { Z = 7, X = 64 - 5,  Y = 48 + 5 }, // mid
                new TileId { Z = 7, X = 64 + 1,  Y = 48 },     // near
            };

            List<TileId> listA = Build();
            var          keysA = new double[1]; // under-sized so SortByPriority must grow it
            TilePriority.SortByPriority(listA, ref keysA, in ctx);

            List<TileId> listB = Build();
            var          keysB = new double[listB.Count];
            TilePriority.SortByPriority(listB, ref keysB, in ctx);

            Assert.AreEqual(center, listA[0], "the tile at lookAt must sort to the head of the list.");
            CollectionAssert.AreEqual(listA, listB,
                "same input + same context, sorted twice, must produce IDENTICAL order (determinism).");
        }

        [Test]
        public void SortByPriority_TiebreaksEqualKeysByTileId_Deterministically()
        {
            // Two tiles at the SAME zoom, mirrored across lookAt (X-20 vs X+20, same Y) are equidistant
            // under GroundDistanceToLookAt at tilt=0 (a symmetric ground metric) — their keys tie exactly,
            // so the sort must fall back to a deterministic TileId (Z,X,Y) tiebreak rather than leaving
            // the outcome to happenstance input order.
            var center = new TileId { Z = 6, X = 32, Y = 32 };
            GeoCoordinate3D     lookAt = LookAtCenterOf(center);
            TilePriorityContext ctx    = ContextFor(lookAt, TilePriorityStrategy.GroundDistanceToLookAt);

            var left  = new TileId { Z = 6, X = center.X - 4, Y = center.Y };
            var right = new TileId { Z = 6, X = center.X + 4, Y = center.Y };

            var listForward = new List<TileId> { right, left };
            var keysForward = new double[2];
            TilePriority.SortByPriority(listForward, ref keysForward, in ctx);

            var listReversed = new List<TileId> { left, right };
            var keysReversed = new double[2];
            TilePriority.SortByPriority(listReversed, ref keysReversed, in ctx);

            CollectionAssert.AreEqual(listForward, listReversed,
                "an exact key tie must resolve to the SAME order regardless of input order (TileId tiebreak).");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TiltCoverGrowthTests — tile-cover growth under camera tilt on the production selector wiring
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tile-cover growth under camera tilt, measured on the production selector wiring
    /// (<see cref="ScreenSpaceLodStrategy"/> + <see cref="RaySphereFarPlane"/>), plus a self-check of the
    /// on-screen-size instrument the measurement reads. See the file header for why the counts are exact.
    /// </summary>
    public class TiltCoverGrowthTests
    {
        // The production globe wiring, mirrored from MapView.EnsureSelector + the MapViewConfig defaults.
        private const int    MinZoom        = 0;
        private const int    MaxZoom        = 14;
        private const int    OnScreenTilePx = 512;
        private const double GlobeFarCap    = 8.0;

        // Fixed pose inputs: Berlin, heading 0, 60° vertical FOV, a 1600x900 viewport.
        private const double LookAtLon = 13.405;
        private const double LookAtLat = 52.52;
        private const double Zoom      = 13.0;
        private static readonly double2 Viewport = new double2(1600.0, 900.0);

        /// <summary>The camera pose for a tilt, with every other input held at the fixture constants.</summary>
        private static CameraProperties Cam(double tiltDeg, double zoom = Zoom)
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = LookAtLon, Latitude = LookAtLat, Altitude = 0.0 },
                zoom, 0.0, tiltDeg);

        /// <summary>The globe cover size at a tilt — the same quantity TileManager publishes as VisibleTileCount.</summary>
        private static int GlobeCover(double tiltDeg)
        {
            var selector = new FrustumTileSelector(MinZoom, MaxZoom, OnScreenTilePx,
                new ScreenSpaceLodStrategy(),
                new RaySphereFarPlane(SphericalProjection.Radius, GlobeFarCap));
            var view = new ViewContext
            {
                Camera     = Cam(tiltDeg),
                ViewportPx = Viewport,
                Projection = new SphericalProjection(),
            };
            var buffer = new List<TileId>();
            selector.SelectVisibleTiles(view, buffer);
            return buffer.Count;
        }

        /// <summary>
        /// Tooth A. Exact cover sizes for the shipped globe wiring at zoom 13, 1600x900, tilt 0/30/45/60.
        /// Brittle on purpose — read the file header before you change a number.
        /// </summary>
        [Test]
        public void GlobeTiltSweep_EmitsTheseExactCoverSizes()
        {
            Assert.AreEqual(24,  GlobeCover(0.0),  "globe z13 1600x900 tilt 0");
            Assert.AreEqual(30,  GlobeCover(30.0), "globe z13 1600x900 tilt 30");
            Assert.AreEqual(54,  GlobeCover(45.0), "globe z13 1600x900 tilt 45");
            Assert.AreEqual(112, GlobeCover(60.0), "globe z13 1600x900 tilt 60");
        }

        /// <summary>
        /// Tooth B. The default mode's cover grows under tilt (billboard approximation, no foreshortening) —
        /// retained behaviour, not a defect: it is what buys the better-looking cover. See
        /// <c>ProjectedAreaLodTests</c> for the opt-in mode that trades this growth for fewer tiles.
        /// </summary>
        [Test]
        public void DistanceMode_CoverGrowthUnderTiltIsRecordedBehaviour()
        {
            int atTilt0  = GlobeCover(0.0);
            int atTilt60 = GlobeCover(60.0);

            Assert.Greater(atTilt60, 2 * atTilt0,
                "ScreenSpaceLodStrategy's cover grows well past 2x under tilt (today 112 vs 24, 4.67x) — a "
              + "recorded tradeoff for the default's better-looking cover, not a defect to fix here.");
        }

        // ── Tooth C — the instrument ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The tile's projected size on screen, in pixels — the square root of its screen-space quad area.
        /// Runs no selector: it takes ONE explicit tile, so a change to the stop rule cannot move it.
        /// Internal so <c>ProjectedAreaLodTests</c> shares this instrument rather than duplicating it.
        /// </summary>
        internal static double ProjectedTilePx(IProjection projection, in CameraProperties cam,
                                               double2 viewportPx, TileId tile)
        {
            var lookAt = new GeoCoordinate
            {
                Latitude  = projection.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, viewportPx.y, cam.VerticalFovDeg);
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 eye, out double3 fwd, out double3 up);

            // Camera basis and the pinhole scale. Both screen axes share it: the horizontal half-angle is the
            // vertical one times the aspect, and the viewport width is its height times the same aspect.
            double3 forward = math.normalize(fwd);
            double3 right   = math.normalize(math.cross(forward, up));
            double3 upward  = math.cross(right, forward);
            double  scale   = viewportPx.y / (2.0 * math.tan(Angle.FromDegrees(cam.VerticalFovDeg * 0.5).Radians));

            double3  origin = projection.Project(lookAt);
            float3x3 basis  = projection.TangentBasisAt(lookAt);

            // The four tile corners as a ring, so the shoelace sum below closes.
            double2 topLeft     = ToScreenPx(0.0, 0.0);
            double2 topRight    = ToScreenPx(1.0, 0.0);
            double2 bottomRight = ToScreenPx(1.0, 1.0);
            double2 bottomLeft  = ToScreenPx(0.0, 1.0);

            double twiceArea = Cross(topLeft, topRight) + Cross(topRight, bottomRight)
                             + Cross(bottomRight, bottomLeft) + Cross(bottomLeft, topLeft);
            return math.sqrt(math.abs(twiceArea) * 0.5);

            double2 ToScreenPx(double u, double v)
            {
                double2 lonLat = tile.ToLonLat(u, v, 1.0);
                double3 world  = projection.Project(
                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                double rx = world.x - origin.x, ry = world.y - origin.y, rz = world.z - origin.z;
                var render = new double3(
                    basis.c0.x * rx + basis.c0.y * ry + basis.c0.z * rz,
                    basis.c1.x * rx + basis.c1.y * ry + basis.c1.z * rz,
                    basis.c2.x * rx + basis.c2.y * ry + basis.c2.z * rz);

                double3 toPoint = render - eye;
                double  depth   = math.dot(toPoint, forward);
                return new double2(scale * math.dot(toPoint, right)  / depth,
                                   scale * math.dot(toPoint, upward) / depth);
            }

            double Cross(double2 a, double2 b) => a.x * b.y - b.x * a.y;
        }

        /// <summary>The tile at a zoom that holds a lon/lat.</summary>
        private static TileId TileAt(double lon, double lat, int zoom)
        {
            double2 unit = WebMercatorTiling.UnitSquareFromLonLat(
                new GeoCoordinate { Latitude = lat, Longitude = lon });
            int n = 1 << zoom;
            return new TileId { Z = zoom, X = (int)(unit.x * n), Y = (int)(unit.y * n) };
        }

        /// <summary>
        /// Tooth C. A flat Mercator tile viewed straight down has no foreshortening, so its projected size is
        /// exactly the on-screen tile size the selector targets. Proves the instrument, not the selector.
        /// </summary>
        [Test]
        public void FlatMercatorAtTiltZero_ProjectsOneTileAtTheOnScreenTileSize()
        {
            var    projection = new WebMercatorProjection();
            TileId lookAtTile = TileAt(LookAtLon, LookAtLat, 13);
            var    neighbour  = new TileId { Z = 13, X = lookAtTile.X + 2, Y = lookAtTile.Y + 1 };

            double own            = ProjectedTilePx(projection, Cam(0.0), Viewport, lookAtTile);
            double awayFromLookAt = ProjectedTilePx(projection, Cam(0.0), Viewport, neighbour);
            double coarserCamera  = ProjectedTilePx(projection, Cam(0.0, zoom: 10.0), Viewport,
                                                    TileAt(LookAtLon, LookAtLat, 10));
            double smallViewport  = ProjectedTilePx(projection, Cam(0.0), new double2(960.0, 540.0), lookAtTile);

            Assert.AreEqual(OnScreenTilePx, own,            1.0, "the look-at's own tile");
            Assert.AreEqual(OnScreenTilePx, awayFromLookAt, 1.0, "a tile away from the look-at");
            Assert.AreEqual(OnScreenTilePx, coarserCamera,  1.0, "camera and tile three zooms coarser");
            Assert.AreEqual(OnScreenTilePx, smallViewport,  1.0, "a 960x540 viewport");
        }

        /// <summary>
        /// Tooth C, second half: an instrument that always reads 512 would be useless. It must move when the
        /// projected size moves — with tilt (foreshortening) and with the tile's own zoom.
        /// </summary>
        [Test]
        public void ProjectedTileSize_MovesWithTiltAndWithTileZoom()
        {
            var    projection = new WebMercatorProjection();
            TileId own        = TileAt(LookAtLon, LookAtLat, 13);

            double tilted  = ProjectedTilePx(projection, Cam(30.0), Viewport, own);
            double coarser = ProjectedTilePx(projection, Cam(0.0), Viewport, TileAt(LookAtLon, LookAtLat, 12));

            Assert.AreEqual(463.710, tilted,  0.01, "tilt 30 foreshortens the look-at's own tile");
            Assert.AreEqual(1024.0,  coarser, 1.0,  "one zoom coarser is twice the side");
        }
    }
}
