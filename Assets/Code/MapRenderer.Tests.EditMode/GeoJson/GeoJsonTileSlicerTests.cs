// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Client-side slicing end to end — parse → project → cut one tile: the straddle partition (T1), the
    /// containing polygon (T2), line pieces, ring role and winding (T7a/T7b/T7c), the simplification
    /// deferral (T9), and the buffer window (T10).
    /// </summary>
    [TestFixture]
    public class GeoJsonTileSlicerTests
    {
        private static double TileLocalX(double longitude, TileId tile, double extent)
            => WebMercatorTiling.TileLocal(
                   WebMercatorTiling.UnitSquareFromLonLat(
                       new GeoCoordinate { Latitude = 0.0, Longitude = longitude }), tile, extent).x;

        // ── T1: a polygon straddling a seam is PARTITIONED, not truncated ───────────────────────────

        /// <summary>
        /// T1. A lon/lat rectangle spanning the z1 seam at longitude 0, sliced into both neighbouring tiles
        /// with the buffer OFF (so the two windows partition the plane rather than overlap it). The pieces'
        /// areas must sum to the input's, both must keep the input's shoelace sign, and both must be
        /// implicitly closed.
        ///
        /// <para><b>Non-vacuity:</b> each piece must contain a vertex lying EXACTLY on the seam that was not
        /// in the input — a genuine synthesised intersection. A "drop the out-of-bounds vertices" clipper
        /// produces none of those and a strictly smaller total area.</para>
        /// </summary>
        [Test]
        public void T1_PolygonStraddlingATileSeam_PartitionsAcrossBothTiles()
        {
            const double extent = GeoJsonSliceOptions.DefaultExtent;
            string json = GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(-10.0, 10.0, 10.0, 30.0)}]");

            GeoJsonSliceOptions options = GeoJsonTestFixtures.Unbuffered;
            TileId west = GeoJsonTestFixtures.Tile(1, 0, 0);
            TileId east = GeoJsonTestFixtures.Tile(1, 1, 0);

            IReadOnlyList<double2> pieceWest = SingleRing(GeoJsonTestFixtures.Slice(json, west, options));
            IReadOnlyList<double2> pieceEast = SingleRing(GeoJsonTestFixtures.Slice(json, east, options));

            // The unclipped input, in the western tile's coordinates: its own x extremes are the vertices a
            // vertex-dropping clipper would keep, and neither of them is the seam.
            double inputWestX = math.round(TileLocalX(-10.0, west, extent));
            double inputEastX = math.round(TileLocalX( 10.0, west, extent));
            Assert.That(inputWestX, Is.Not.EqualTo(extent));
            Assert.That(inputEastX, Is.Not.EqualTo(extent));

            Assert.That(CountAtX(pieceWest, extent), Is.EqualTo(2),
                "the western piece must gain two SYNTHESISED vertices on the seam (x = extent)");
            Assert.That(CountAtX(pieceEast, 0.0), Is.EqualTo(2),
                "the eastern piece must gain two SYNTHESISED vertices on the seam (x = 0)");

            double inputArea = (inputEastX - inputWestX) * InputHeight(west, extent);
            double sliced    = GeoJsonTestFixtures.Area(pieceWest) + GeoJsonTestFixtures.Area(pieceEast);

            // Quantization moves every vertex by at most half a tile unit, so each piece's width and height
            // move by at most one; the sum of the two pieces is bounded accordingly.
            double bound = 2.0 * ((inputEastX - inputWestX) + InputHeight(west, extent)) + 4.0;
            TestContext.WriteLine(
                $"T1 input area = {inputArea:F3}, sliced = {sliced:F3}, |Δ| = {math.abs(sliced - inputArea):F3}, " +
                $"bound = {bound:F3}");
            Assert.That(math.abs(sliced - inputArea), Is.LessThanOrEqualTo(bound));

            foreach (IReadOnlyList<double2> piece in new[] { pieceWest, pieceEast })
            {
                Assert.That(GeoJsonTestFixtures.Shoelace(piece), Is.GreaterThan(0.0),
                    "the exterior's winding must survive clipping");
                Assert.That(piece[0].x == piece[piece.Count - 1].x && piece[0].y == piece[piece.Count - 1].y,
                    Is.False, "rings stay implicitly closed");
            }
        }

        private static double InputHeight(TileId tile, double extent)
        {
            double2 north = WebMercatorTiling.TileLocal(
                WebMercatorTiling.UnitSquareFromLonLat(new GeoCoordinate { Latitude = 30.0, Longitude = 0.0 }),
                tile, extent);
            double2 south = WebMercatorTiling.TileLocal(
                WebMercatorTiling.UnitSquareFromLonLat(new GeoCoordinate { Latitude = 10.0, Longitude = 0.0 }),
                tile, extent);
            return math.round(south.y) - math.round(north.y);
        }

        private static int CountAtX(IReadOnlyList<double2> ring, double x)
        {
            int n = 0;
            foreach (double2 v in ring) if (v.x == x) n++;
            return n;
        }

        // ── T2: a polygon containing the tile becomes the window itself ─────────────────────────────

        /// <summary>
        /// T2. Sutherland–Hodgman reduces a subject enclosing the whole window to the window rectangle with
        /// no special case. Non-vacuity: NONE of the input's vertices is inside the window, so a
        /// "keep the vertices that are inside" clipper emits nothing here and this reds.
        /// </summary>
        [Test]
        public void T2_PolygonContainingTheTile_BecomesTheWindowRectangle()
        {
            string json = GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(-80.0, -80.0, 80.0, 80.0)}]");

            GeoJsonSliceOptions options = GeoJsonSliceOptions.Default;
            TileId tile = GeoJsonTestFixtures.Tile(4, 8, 8);
            options.Window(out double2 windowMin, out double2 windowMax);

            // Non-vacuity: every input corner is outside the window, in tile-local terms.
            foreach ((double lon, double lat) corner in
                     new[] { (-80.0, -80.0), (80.0, -80.0), (80.0, 80.0), (-80.0, 80.0) })
            {
                double2 p = WebMercatorTiling.TileLocal(
                    WebMercatorTiling.UnitSquareFromLonLat(
                        new GeoCoordinate { Latitude = corner.lat, Longitude = corner.lon }),
                    tile, options.Extent);
                Assert.That(p.x < windowMin.x || p.x > windowMax.x || p.y < windowMin.y || p.y > windowMax.y,
                    Is.True, $"input corner ({corner.lon}, {corner.lat}) must lie OUTSIDE the window");
            }

            IReadOnlyList<double2> ring = SingleRing(GeoJsonTestFixtures.Slice(json, tile, options));

            Assert.That(ring.Count, Is.EqualTo(4));
            double side = options.Extent + 2.0 * GeoJsonSliceOptions.DefaultBufferAtReferenceExtent;
            Assert.That(GeoJsonTestFixtures.Area(ring), Is.EqualTo(side * side).Within(1e-6));
            Assert.That(GeoJsonTestFixtures.Shoelace(ring), Is.GreaterThan(0.0), "winding preserved");

            foreach (double2 v in ring)
            {
                Assert.That(v.x == windowMin.x || v.x == windowMax.x, Is.True);
                Assert.That(v.y == windowMin.y || v.y == windowMax.y, Is.True);
            }
        }

        // ── Lines go through the polyline clipper, not the ring clipper ─────────────────────────────

        /// <summary>The slicer's DISPATCH, not just the clipper: a LineString that traverses a tile twice
        /// must reach <c>PolylineWindowClipper</c> and come back as two OPEN paths.</summary>
        [Test]
        public void LineStringCrossingTheTileTwice_SlicesIntoTwoOpenPaths()
        {
            string json = GeoJsonTestFixtures.Feature(
                "LineString", GeoJsonTestFixtures.Positions(-10.0, 40.0, 10.0, 40.0, 10.0, 60.0, -10.0, 60.0));

            TileSlice slice = GeoJsonTestFixtures.Slice(
                json, GeoJsonTestFixtures.Tile(1, 0, 0), GeoJsonTestFixtures.Unbuffered);

            Assert.That(slice.Features.Count, Is.EqualTo(1));
            IReadOnlyList<IReadOnlyList<double2>> paths = slice.Features[0].Paths;

            Assert.That(paths.Count, Is.EqualTo(2), "two traverses ⇒ two pieces, not one joined across the gap");
            foreach (IReadOnlyList<double2> path in paths)
            {
                Assert.That(path.Count, Is.EqualTo(2));
                Assert.That(path[0].x == path[path.Count - 1].x && path[0].y == path[path.Count - 1].y,
                    Is.False, "line pieces are open");
            }
        }

        // ── T7a / T7b: winding comes from ROLE, and rings stay implicitly closed ────────────────────

        /// <summary>
        /// T7a. RFC 7946 §3.1.6 gives ring role POSITIONALLY and tells parsers not to reject non-conforming
        /// winding, so authored winding carries no information — role must be re-encoded as winding. A
        /// polygon authored AGAINST the right-hand rule must therefore slice identically to its conforming
        /// twin, with exterior positive / hole negative in tile space (the MVT convention the downstream
        /// assembler classifies on).
        ///
        /// <para><b>Non-vacuity:</b> asserting the two twins are IDENTICAL is what proves normalisation ran,
        /// rather than the input happening to be right already.</para>
        /// </summary>
        [Test]
        public void T7a_RingWindingIsNormalisedFromRole_NotFromAuthoring()
        {
            string conforming = Polygon(rfcWound: true);
            string reversed   = Polygon(rfcWound: false);

            TileId world = GeoJsonTestFixtures.Tile(0, 0, 0);
            IReadOnlyList<IReadOnlyList<double2>> fromConforming =
                GeoJsonTestFixtures.Slice(conforming, world, GeoJsonSliceOptions.Default).Features[0].Paths;
            IReadOnlyList<IReadOnlyList<double2>> fromReversed =
                GeoJsonTestFixtures.Slice(reversed, world, GeoJsonSliceOptions.Default).Features[0].Paths;

            Assert.That(fromConforming.Count, Is.EqualTo(2));
            Assert.That(fromReversed.Count, Is.EqualTo(2));

            for (int r = 0; r < fromConforming.Count; r++)
                Assert.That(fromReversed[r], Is.EqualTo(fromConforming[r]),
                    $"ring {r} must be identical whichever way the input was wound");

            Assert.That(GeoJsonTestFixtures.Shoelace(fromConforming[0]), Is.GreaterThan(0.0),
                "exterior must be CW on screen (positive shoelace) — the MVT convention");
            Assert.That(GeoJsonTestFixtures.Shoelace(fromConforming[1]), Is.LessThan(0.0),
                "the hole must carry the OPPOSITE sign, which is what makes it a hole downstream");
        }

        /// <summary>T7b, at the slicer's output: a 5-position RFC ring emits exactly 4 tile-space vertices,
        /// first ≠ last. The count relation is the tooth — inequality alone would also hold if a vertex had
        /// been lost elsewhere.</summary>
        [Test]
        public void T7b_SlicedRingsAreImplicitlyClosed()
        {
            IReadOnlyList<double2> ring = SingleRing(GeoJsonTestFixtures.Slice(
                GeoJsonTestFixtures.Feature("Polygon", $"[{GeoJsonTestFixtures.RectangleRing(-20.0, -20.0, 20.0, 20.0)}]"),
                GeoJsonTestFixtures.Tile(0, 0, 0), GeoJsonSliceOptions.Default));

            Assert.That(ring.Count, Is.EqualTo(4), "5 RFC positions ⇒ 4 implicitly-closed vertices");
            Assert.That(ring[0].x == ring[3].x && ring[0].y == ring[3].y, Is.False);
        }

        private static string Polygon(bool rfcWound)
        {
            string exterior = GeoJsonTestFixtures.RectangleRing(-20.0, -20.0, 20.0, 20.0, rfcWound);
            string hole     = GeoJsonTestFixtures.RectangleRing(-10.0, -10.0, 10.0, 10.0, !rfcWound);
            return GeoJsonTestFixtures.Feature("Polygon", $"[{exterior},{hole}]");
        }

        // ── T7c: a polygon's holes leave with its exterior ──────────────────────────────────────────

        /// <summary>
        /// T7c. If a polygon's exterior clips away, its holes must go too: a surviving orphan would become
        /// the feature's FIRST ring downstream, establish the exterior sign itself, and render as an
        /// inverted patch. The fixture is deliberately MALFORMED — polygon 2's hole does not lie inside its
        /// exterior — because that is the only way the case can arise, and the failure is silent-wrong.
        ///
        /// <para><b>Non-vacuity:</b> asserting the ring COUNT alone would pass an implementation that
        /// dropped the exterior only if the hole also happened to clip away; here the orphan hole lies
        /// squarely inside the tile, so dropping only the exterior leaves a visible sign-inverted ring.</para>
        /// </summary>
        [Test]
        public void T7c_ExteriorClippedAway_TakesItsHolesWithIt()
        {
            string insidePolygon  = GeoJsonTestFixtures.RectangleRing(10.0, -30.0, 30.0, -10.0);
            string outsideOuter   = GeoJsonTestFixtures.RectangleRing(-170.0, 10.0, -150.0, 30.0);
            string orphanCandidate = GeoJsonTestFixtures.RectangleRing(40.0, -40.0, 60.0, -20.0);

            string json = GeoJsonTestFixtures.Feature(
                "MultiPolygon", $"[[{insidePolygon}],[{outsideOuter},{orphanCandidate}]]");

            TileSlice slice = GeoJsonTestFixtures.Slice(
                json, GeoJsonTestFixtures.Tile(2, 2, 2), GeoJsonSliceOptions.Default);

            Assert.That(slice.Features.Count, Is.EqualTo(1));
            IReadOnlyList<IReadOnlyList<double2>> rings = slice.Features[0].Paths;

            Assert.That(rings.Count, Is.EqualTo(1),
                "only the first polygon's exterior may survive — the orphaned hole must not appear");
            Assert.That(GeoJsonTestFixtures.Shoelace(rings[0]), Is.GreaterThan(0.0),
                "no surviving ring may carry the hole's (negative) sign");
        }

        // ── T9: the simplification deferral is observable ───────────────────────────────────────────

        /// <summary>
        /// T9. Simplification is deferred, and a silently-ignored parameter is indistinguishable from an
        /// implemented one — so a non-zero tolerance THROWS. The default's numeric value is asserted
        /// explicitly, so a later silent change of default reds here.
        /// </summary>
        [Test]
        public void T9_NonZeroSimplifyTolerance_ThrowsNotSupported()
        {
            Assert.That(GeoJsonSliceOptions.Default.SimplifyTolerance, Is.EqualTo(0.0),
                "v1 slices at the authored resolution; a fixture wants the EXACT authored position");

            GeoJsonProjectedDataset dataset = GeoJsonProjectedDataset.Project(
                GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                    "Point", GeoJsonTestFixtures.Position(0.0, 0.0))));

            var simplifying = new GeoJsonSliceOptions
            {
                Extent                  = GeoJsonSliceOptions.DefaultExtent,
                BufferAtReferenceExtent = GeoJsonSliceOptions.DefaultBufferAtReferenceExtent,
                SimplifyTolerance       = 1.0
            };

            Assert.Throws<NotSupportedException>(
                () => GeoJsonTileSlicer.Slice(dataset, GeoJsonTestFixtures.Tile(0, 0, 0), simplifying));

            Assert.DoesNotThrow(
                () => GeoJsonTileSlicer.Slice(dataset, GeoJsonTestFixtures.Tile(0, 0, 0),
                                              GeoJsonSliceOptions.Default));
        }

        /// <summary>
        /// T9b. The deferral is stated by the <b>validator</b>, so every source that retains options — a source that
        /// keeps them for its whole life, not just a caller of <c>Slice</c> — rejects a non-zero tolerance
        /// at the moment it accepts them. Calling <c>Validate</c> directly is the point: <c>Slice</c>'s own
        /// guard runs first (T9 pins that), so a check reachable only through <c>Slice</c> would leave the
        /// construction boundary admitting a tolerance it cannot honour, and this test is what tells the two
        /// front lines apart.
        ///
        /// <para>NaN is an arm because <c>!= 0</c> catches it while the sign- or range-shaped rewrites of
        /// this predicate (<c>&lt; 0</c>, <c>&gt; 0</c>) do not; the valid arm is what stops "reject
        /// everything" from satisfying the rest.</para>
        /// </summary>
        [Test]
        public void T9b_ANonZeroSimplifyTolerance_IsRejectedByTheValidator()
        {
            Assert.DoesNotThrow(() => GeoJsonSliceOptions.Default.Validate(),
                "precondition: the default option set must validate, or every arm below is satisfied by a " +
                "validator that refuses everything");

            foreach (double tolerance in new[] { 1.0, 1e-12, -1.0, double.NaN })
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => new GeoJsonSliceOptions
                    {
                        Extent                  = GeoJsonSliceOptions.DefaultExtent,
                        BufferAtReferenceExtent = GeoJsonSliceOptions.DefaultBufferAtReferenceExtent,
                        SimplifyTolerance       = tolerance
                    }.Validate(),
                    $"a SimplifyTolerance of {tolerance} is unimplemented and must be rejected where the " +
                    "options are accepted — the extent here is perfectly usable, so nothing else can " +
                    "reject it");
        }

        // ── T10: the slicer's window IS the fill pipeline's window ──────────────────────────────────

        /// <summary>
        /// T10. At the default buffer the slicer's window and <c>TileBufferClip</c>'s are EQUAL, not merely
        /// nested — which makes the pipeline's clip a provable no-op on GeoJSON tiles. Several extents are
        /// the non-vacuity clause: a single one would also pass on a formula that ignored the rescale.
        ///
        /// <para><b>Why the NON-power-of-two extents are in the list.</b> At 512/4096/8192 the rescaled
        /// margin is a whole number (8/64/128), so those rows pass equally against a formula that rounds or
        /// truncates it — the "close, not bit-equal" failure the window's own XML rules out. At 1000 and
        /// 4095 it is 15.625 and 63.984375, and any such formula reds.</para>
        ///
        /// <para><i>What this tooth deliberately does NOT claim:</i> it cannot discriminate the ASSOCIATION
        /// of the three factors, and no choice of extent would let it. <c>ReferenceExtent</c> is 4096, so
        /// dividing by it is an exact binary scaling and <c>b·e/R</c>, <c>b·(e/R)</c> and <c>(b/R)·e</c> are
        /// bit-identical for every finite input. That half of the bit-equality is structural, not
        /// tooth-enforced — and it is what a future change to <c>ReferenceExtent</c> would spend.</para>
        /// </summary>
        [Test]
        public void T10_DefaultBufferWindow_EqualsTheFillPipelineWindow()
        {
            Assert.That(GeoJsonSliceOptions.Default.BufferAtReferenceExtent, Is.EqualTo(64.0));
            Assert.That(GeoJsonSliceOptions.Default.Extent, Is.EqualTo(4096.0));

            foreach (double extent in new[] { 512.0, 1000.0, 4095.0, 4096.0, 8192.0 })
            {
                GeoJsonSliceOptions options = GeoJsonTestFixtures.Options(
                    extent, GeoJsonSliceOptions.DefaultBufferAtReferenceExtent);
                options.Window(out double2 sliceMin, out double2 sliceMax);

                bool clipped = TileBufferClip
                    .KeepTileUnits(GeoJsonSliceOptions.DefaultBufferAtReferenceExtent)
                    .TryWindow(extent, out double2 pipelineMin, out double2 pipelineMax);

                Assert.That(clipped, Is.True);
                TestContext.WriteLine(
                    $"T10 extent={extent}: slicer [{sliceMin.x}, {sliceMax.x}] vs " +
                    $"pipeline [{pipelineMin.x}, {pipelineMax.x}]");

                Assert.That(sliceMin.x, Is.EqualTo(pipelineMin.x));
                Assert.That(sliceMin.y, Is.EqualTo(pipelineMin.y));
                Assert.That(sliceMax.x, Is.EqualTo(pipelineMax.x));
                Assert.That(sliceMax.y, Is.EqualTo(pipelineMax.y));
            }
        }

        /// <summary>
        /// T10b. The window carries <c>TileBufferClip</c>'s input GUARDS, not merely its arithmetic. A
        /// NEGATIVE margin would erode INTO the tile (min &gt; 0, max &lt; extent), silently dropping
        /// geometry the tile owns; a NaN one would make an all-NaN window against which every vertex tests
        /// outside. Both must degrade to "cut exactly at the tile boundary" — and, since the two types claim
        /// to be the same window, must still agree with the pipeline's on the same input.
        /// </summary>
        [Test]
        public void T10b_NegativeAndNaNBuffers_DegradeToTheTileBoundaryLikeTheFillPipeline()
        {
            const double extent = GeoJsonSliceOptions.DefaultExtent;

            foreach (double authored in new[] { -64.0, double.NaN })
            {
                GeoJsonSliceOptions options = GeoJsonTestFixtures.Options(extent, authored);
                options.Window(out double2 min, out double2 max);

                Assert.That(min.x, Is.EqualTo(0.0), $"buffer {authored} must not erode into the tile");
                Assert.That(min.y, Is.EqualTo(0.0), $"buffer {authored} must not erode into the tile");
                Assert.That(max.x, Is.EqualTo(extent), $"buffer {authored} must cut at the tile boundary");
                Assert.That(max.y, Is.EqualTo(extent), $"buffer {authored} must cut at the tile boundary");

                bool clipped = TileBufferClip.KeepTileUnits(authored)
                    .TryWindow(extent, out double2 pipelineMin, out double2 pipelineMax);

                Assert.That(clipped, Is.True);
                Assert.That(min.x, Is.EqualTo(pipelineMin.x), $"buffer {authored}: windows must still agree");
                Assert.That(min.y, Is.EqualTo(pipelineMin.y));
                Assert.That(max.x, Is.EqualTo(pipelineMax.x));
                Assert.That(max.y, Is.EqualTo(pipelineMax.y));
            }
        }

        /// <summary>
        /// T10c. The consequence, live: a NaN margin must not clip the map away to nothing. Every comparison
        /// against NaN is false, so an unguarded window makes <see cref="RingWindowClipper"/>'s inclusive
        /// test reject EVERY vertex — the whole source renders blank with no error anywhere. This is the
        /// failure <c>TileBufferClip</c> calls "the worst failure this type can produce"; T10b pins the
        /// window, this pins what the window does.
        /// </summary>
        [Test]
        public void T10c_ANaNBufferDoesNotClipTheMapAway()
        {
            string json = GeoJsonTestFixtures.Feature(
                "Polygon", "[" + GeoJsonTestFixtures.RectangleRing(-40.0, -40.0, 40.0, 40.0) + "]");

            TileSlice sliced = GeoJsonTestFixtures.Slice(
                json, GeoJsonTestFixtures.Tile(0, 0, 0),
                GeoJsonTestFixtures.Options(GeoJsonSliceOptions.DefaultExtent, double.NaN));

            Assert.That(sliced.Features.Count, Is.EqualTo(1),
                "a NaN margin must degrade to the tile boundary, not delete the geometry");
            Assert.That(sliced.Features[0].Paths.Count, Is.EqualTo(1));
            Assert.That(GeoJsonTestFixtures.Area(sliced.Features[0].Paths[0]), Is.GreaterThan(0.0));
        }

        // ── T13b: the two tiles either side of a seam agree where the seam is ───────────────────────

        /// <summary>
        /// T13b. <c>WindowClipperTests.T13</c> proves the boundary assignment in the clipper's own idealised
        /// frame; this proves it survives the frames the slicer actually builds. The two tiles either side of
        /// a seam derive their local coordinates independently — <c>(u·2^z − x)·extent</c> with a different
        /// <c>x</c> — so their arithmetic does NOT round alike, and the only thing that can make them agree
        /// about where their common edge is, is that each writes its own boundary LITERAL there.
        ///
        /// <para><b>The eastern tile is where this bites</b>, and the tooth says so rather than asserting
        /// both sides and hoping. Its frame puts the crossing near ZERO, where the interpolation's absolute
        /// error is enormous in ulps: <c>a + t·d</c> lands at <c>1.14e−13</c>, not on the seam. The western
        /// tile's frame puts the same crossing near 4096, where that same absolute error is under half an
        /// ulp and rounds away — the interpolation looks exact there purely by luck of magnitude, which is
        /// the reason the seam coordinate cannot be left to it.</para>
        ///
        /// <para>Clipped, not sliced, on purpose: <see cref="GeoJsonTileSlicer"/> rounds to integers after
        /// clipping, and that rounding currently masks a disagreement this size. The guarantee belongs to the
        /// clipper, and it is what a consumer working at unquantized precision gets to rely on.</para>
        /// </summary>
        [Test]
        public void T13b_AdjacentTilesPutTheirSharedSeamVertexOnTheirOwnBoundary()
        {
            const double extent = GeoJsonSliceOptions.DefaultExtent;
            const int    zoom   = 4;

            // A line crossing the seam between two horizontally adjacent z4 tiles, well inside them in y.
            var from = new GeoCoordinate { Latitude = 1.0, Longitude = 17.0 };
            var to   = new GeoCoordinate { Latitude = 3.0, Longitude = 37.0 };

            TileId westTile = GeoJsonTestFixtures.TileOf(from.Longitude, from.Latitude, zoom);
            TileId eastTile = GeoJsonTestFixtures.TileOf(to.Longitude,   to.Latitude,   zoom);
            Assert.That(eastTile.X, Is.EqualTo(westTile.X + 1), "the fixture must span exactly one seam");
            Assert.That(eastTile.Y, Is.EqualTo(westTile.Y));

            // Buffer off, so the shared tile edge IS the clip boundary on both sides.
            double2 windowMin = new double2(0.0, 0.0);
            double2 windowMax = new double2(extent, extent);

            List<double2> inWest = SegmentInTile(from, to, westTile, extent);
            List<double2> inEast = SegmentInTile(from, to, eastTile, extent);

            // The fixture must discriminate where it claims to: the eastern tile's interpolated crossing has
            // to miss its western edge, or this tooth would pass against an implementation that never
            // assigned the boundary at all.
            double t = (0.0 - inEast[0].x) / (inEast[1].x - inEast[0].x);
            Assert.That(inEast[0].x + t * (inEast[1].x - inEast[0].x), Is.Not.EqualTo(0.0),
                "fixture must discriminate: pick a seam where a + t·d misses in the eastern frame");

            List<double2> fromWest = ClipOnce(inWest, windowMin, windowMax, westTile);
            List<double2> fromEast = ClipOnce(inEast, windowMin, windowMax, eastTile);

            Assert.That(fromEast[0].x, Is.EqualTo(0.0),
                "the eastern tile must place the crossing exactly on its western edge");
            Assert.That(fromWest[fromWest.Count - 1].x, Is.EqualTo(extent),
                "and the western tile exactly on its eastern edge — the same seam, from the other side");

            // Non-vacuity: both really did clip, rather than passing an endpoint through.
            Assert.That(fromWest[fromWest.Count - 1].y, Is.Not.EqualTo(fromWest[0].y));
            Assert.That(fromEast[0].y, Is.Not.EqualTo(fromEast[fromEast.Count - 1].y));
        }

        /// <summary>The geodetic segment expressed in <paramref name="tile"/>'s own local frame.</summary>
        private static List<double2> SegmentInTile(
            GeoCoordinate from, GeoCoordinate to, TileId tile, double extent)
            => new List<double2>
            {
                WebMercatorTiling.TileLocal(WebMercatorTiling.UnitSquareFromLonLat(from), tile, extent),
                WebMercatorTiling.TileLocal(WebMercatorTiling.UnitSquareFromLonLat(to),   tile, extent)
            };

        private static List<double2> ClipOnce(List<double2> segment, double2 min, double2 max, TileId tile)
        {
            List<List<double2>> pieces = PolylineWindowClipper.Clip(segment, min, max);
            Assert.That(pieces.Count, Is.EqualTo(1), $"tile {tile.X}/{tile.Y}: one crossing, one piece");
            return pieces[0];
        }

        // ── Points, the buffer, and empty tiles ─────────────────────────────────────────────────────

        /// <summary>A point near a seam appears in BOTH neighbouring tiles once the buffer is on — the
        /// duplication is the point of the margin, and it is what lets a label be placed from either tile.
        /// With the buffer off it belongs to exactly one.</summary>
        [Test]
        public void PointNearASeam_AppearsInBothTilesOnlyWhenBuffered()
        {
            // ~0.2 tile units west of the z1 seam at longitude 0, well inside a 64-unit margin.
            const double nearSeamLon = -0.01;
            string json = GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(nearSeamLon, 20.0));

            TileId west = GeoJsonTestFixtures.Tile(1, 0, 0);
            TileId east = GeoJsonTestFixtures.Tile(1, 1, 0);

            Assert.That(GeoJsonTestFixtures.Slice(json, west, GeoJsonSliceOptions.Default).Features.Count,
                Is.EqualTo(1));
            Assert.That(GeoJsonTestFixtures.Slice(json, east, GeoJsonSliceOptions.Default).Features.Count,
                Is.EqualTo(1), "the buffer must carry a near-seam point into the neighbour too");

            Assert.That(GeoJsonTestFixtures.Slice(json, east, GeoJsonTestFixtures.Unbuffered).Features.Count,
                Is.EqualTo(0), "unbuffered, the point belongs to exactly one tile");
        }

        [Test]
        public void TileWithNothingInIt_YieldsAnEmptyFeatureList()
        {
            TileSlice slice = GeoJsonTestFixtures.Slice(
                GeoJsonTestFixtures.Feature("Point", GeoJsonTestFixtures.Position(0.0, 0.0)),
                GeoJsonTestFixtures.Tile(4, 0, 0), GeoJsonSliceOptions.Default);

            Assert.That(slice.Features, Is.Not.Null);
            Assert.That(slice.Features.Count, Is.EqualTo(0));
            Assert.That(slice.Tile, Is.EqualTo(GeoJsonTestFixtures.Tile(4, 0, 0)));
            Assert.That(slice.Extent, Is.EqualTo(GeoJsonSliceOptions.DefaultExtent));
        }

        [Test]
        public void SlicedFeatureCarriesIdentityByReference()
        {
            string json = GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0),
                properties: "{\"name\":\"origin\"}", idMember: "\"id\":7,");

            GeoJsonDataset parsed = GeoJsonParser.Parse(json);
            TileSlice slice = GeoJsonTileSlicer.Slice(
                GeoJsonProjectedDataset.Project(parsed), GeoJsonTestFixtures.Tile(0, 0, 0),
                GeoJsonSliceOptions.Default);

            Assert.That(slice.Features[0].Source, Is.SameAs(parsed.Features[0]),
                "slicing must not copy or reinterpret identity and attributes");
            Assert.That(slice.Features[0].Source.Properties["name"].AsString(), Is.EqualTo("origin"));
        }

        /// <summary>Coordinates are quantized to the integer grid the downstream epsilons are calibrated
        /// against (<c>RingAssemblyJob</c>'s absolute area threshold is 1.0 tile unit²).</summary>
        [Test]
        public void SlicedCoordinatesAreQuantizedToIntegers()
        {
            IReadOnlyList<double2> ring = SingleRing(GeoJsonTestFixtures.Slice(
                GeoJsonTestFixtures.Feature("Polygon", $"[{GeoJsonTestFixtures.RectangleRing(-13.37, 7.11, 21.5, 42.9)}]"),
                GeoJsonTestFixtures.Tile(0, 0, 0), GeoJsonSliceOptions.Default));

            foreach (double2 v in ring)
            {
                Assert.That(v.x, Is.EqualTo(math.round(v.x)));
                Assert.That(v.y, Is.EqualTo(math.round(v.y)));
            }
        }

        /// <summary>
        /// A non-integral <c>Extent</c> is REJECTED, not silently rounded.
        ///
        /// <para>Tile-local coordinates are quantized to integers, and downstream the extent is read both as
        /// this <c>double</c> and as the whole number a decoded tile layer reports — so 4096.5 would arrive as
        /// both 4096.5 and 4096 and every consumer joining them would be off by a fraction of a tile with
        /// nothing to notice. No authoring intent is expressed by a fractional extent, so rounding it would
        /// substitute a value nobody asked for; the guard rejects it at the door instead.</para>
        ///
        /// <para>The valid arm is not decoration: without it "reject everything" satisfies the invalid ones.
        /// </para>
        /// </summary>
        [Test]
        public void ANonIntegralExtent_IsRejected_RatherThanRounded()
        {
            GeoJsonProjectedDataset dataset = GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(
                GeoJsonTestFixtures.Collection(GeoJsonTestFixtures.Feature(
                    "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(-10.0, -10.0, 10.0, 10.0)}]"))));
            TileId tile = GeoJsonTestFixtures.Tile(0, 0, 0);

            Assert.DoesNotThrow(
                () => GeoJsonTileSlicer.Slice(dataset, tile, GeoJsonTestFixtures.Options(4096.0, 64.0)),
                "precondition: a positive WHOLE extent must still slice, or the guard is simply refusing " +
                "everything and the arms below prove nothing");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => GeoJsonTileSlicer.Slice(dataset, tile, GeoJsonTestFixtures.Options(4096.5, 64.0)),
                "a fractional extent must be rejected. Rounded, it would leave the buffer's double extent " +
                "and the whole number a decoded layer reports disagreeing by half a tile unit — silently.");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GeoJsonTileSlicer.Slice(dataset, tile, GeoJsonTestFixtures.Options(0.0, 64.0)),
                "…and the pre-existing zero/negative rejection must survive the widening — a " +
                "`default(GeoJsonSliceOptions)` is the realistic way to arrive here");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GeoJsonTileSlicer.Slice(dataset, tile, GeoJsonTestFixtures.Options(-4096.0, 64.0)),
                "…including an outright negative one, which `!(x > 0)` and `x != floor(x)` catch for " +
                "different reasons");
        }

        /// <summary>
        /// The extent's domain has an UPPER bound as well: a whole number above <c>uint.MaxValue</c>, and
        /// <c>+∞</c>, are rejected.
        ///
        /// <para>Both are positive and both equal their own <c>floor</c>, so the integrality clause alone
        /// admits them — and both then reproduce the very defect that clause exists to close: the decoded
        /// layer narrows the extent to a <c>uint</c> by an UNCHECKED conversion while the materializer keeps
        /// the <c>double</c>, so the layer reports one extent and its geometry another. <c>+∞</c> does not
        /// even reach that far: <c>Window</c> computes <c>∞ − ∞</c>, every comparison against a NaN window
        /// is false, and the tile keeps every feature and then slices away every vertex.</para>
        ///
        /// <para>The <c>uint.MaxValue</c> arm PASSES, and is the point of the pair: the bound is the
        /// representable domain of the layer's <c>uint</c>, not a taste judgement about plausible extents.
        /// Without it, "reject anything large" satisfies both rejecting arms.</para>
        /// </summary>
        [Test]
        public void AnExtentOutsideTheRepresentableDomain_IsRejected()
        {
            GeoJsonProjectedDataset dataset = GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(
                GeoJsonTestFixtures.Collection(GeoJsonTestFixtures.Feature(
                    "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(-10.0, -10.0, 10.0, 10.0)}]"))));
            TileId tile = GeoJsonTestFixtures.Tile(0, 0, 0);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => GeoJsonTileSlicer.Slice(
                    dataset, tile, GeoJsonTestFixtures.Options((double)uint.MaxValue + 1.0, 64.0)),
                "a whole extent one above uint.MaxValue must be rejected. It passes `> 0` and `== floor`, " +
                "and the layer's unchecked (uint) narrowing then makes the extent the layer reports and the " +
                "extent its geometry was built at two different numbers.");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GeoJsonTileSlicer.Slice(
                    dataset, tile, GeoJsonTestFixtures.Options(double.PositiveInfinity, 64.0)),
                "…and so must +∞, which floor() reports as integral. It poisons Window with NaN before it " +
                "ever reaches the narrowing, so nothing downstream can catch it.");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GeoJsonTileSlicer.Slice(dataset, tile, GeoJsonTestFixtures.Options(double.NaN, 64.0)),
                "…and NaN, which fails every comparison — asserted so the three-clause condition cannot be " +
                "rewritten into one that lets it through");

            Assert.DoesNotThrow(
                () => GeoJsonTileSlicer.Slice(
                    dataset, tile, GeoJsonTestFixtures.Options((double)uint.MaxValue, 64.0)),
                "uint.MaxValue itself is INSIDE the domain and must slice: the bound is what the layer's " +
                "uint can represent, not an opinion about sensible extents. Without this arm the two " +
                "rejecting arms above are satisfied by a guard that simply refuses large numbers.");
        }

        private static IReadOnlyList<double2> SingleRing(TileSlice slice)
        {
            Assert.That(slice.Features.Count, Is.EqualTo(1), "expected exactly one feature in this tile");
            Assert.That(slice.Features[0].Paths.Count, Is.EqualTo(1), "expected exactly one ring");
            return slice.Features[0].Paths[0];
        }
    }
}
