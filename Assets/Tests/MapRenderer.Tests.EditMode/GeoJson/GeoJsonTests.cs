// GeoJSON parsing, tiling, slicing and clipping tests. Engine-free: Tools/core-tests also compiles this
// file. GeoJsonSourceTests.cs is separate because it is the folder's only engine-bound file.
//
// Contents:
//   GeoJsonForkNeutralityTests  — cloning a GeoJSON tile fork does not perturb the source.
//   WebMercatorTilingTests      — the web-Mercator tiling math GeoJSON slicing rides on.
//   GeoJsonParserTests          — GeoJSON parsing into the Core model.
//   GeoJsonTileSlicerTests      — slicing GeoJSON features into tile-local geometry.
//   WindowClipperTests          — the ring (Sutherland-Hodgman) and open-polyline (Liang-Barsky) window clippers GeoJsonTileSlicer uses.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Geometry;


namespace MapRenderer.Tests.GeoJsons
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonForkNeutralityTests — cloning a GeoJSON tile fork does not perturb the source
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A structural guard on where the GeoJSON stack may live. Core's parse/project/slice stack stays
    /// engine-free, so it stays in the <c>dotnet test</c> loop: a <c>NativeArray</c>, a Burst attribute or a
    /// <c>TileGeometryBuffers</c> in these files (see <see cref="ForbiddenTokens"/>) would drag it into the
    /// Unity gate. Carrier work belongs in the decoder that consumes <c>TileSlice</c>.
    /// </summary>
    [TestFixture]
    public class GeoJsonForkNeutralityTests
    {
        private static readonly string[] ForbiddenTokens =
        {
            "uint[]",              // the MVT opcode-stream carrier
            "TileGeometryBuffers", // the geometry-IR carrier
            "ITileLayer",          // the decoded-tile source interfaces
            "IDecodedTile",        // (these two inherited the role a deleted ITileFeature had)
            "NativeArray",
            "Unity.Burst",
            "Unity.Collections"
        };

        [Test]
        public void TheGeoJsonStageNamesNeitherGeometryCarrier()
        {
            List<string> files = StageSourceFiles();

            // Non-vacuity: a glob that matched nothing would pass this test without reading a byte.
            Assert.That(files.Count, Is.GreaterThanOrEqualTo(5),
                "expected the GeoJson/ sources plus both window clippers — a scan of nothing proves nothing");

            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                foreach (string token in ForbiddenTokens)
                    Assert.That(text, Does.Not.Contain(token),
                        $"{Path.GetFileName(file)} names \"{token}\": the fence is scoped to what is identical " +
                        "under both arms of the carrier decision, and must not pre-empt it (nor leave the " +
                        "engine-free fast-test loop).");
            }
        }

        private static List<string> StageSourceFiles()
        {
            string core = ResolveUp(Path.Combine("Assets", "Code", "MapRenderer.Core"));
            var files = new List<string>();

            files.AddRange(Directory.GetFiles(Path.Combine(core, "GeoJson"), "*.cs"));
            files.Add(Path.Combine(core, "Geometry", "RingWindowClipper.cs"));
            files.Add(Path.Combine(core, "Geometry", "PolylineWindowClipper.cs"));
            files.Add(Path.Combine(core, "Coordinates", "WebMercatorTiling.cs"));

            foreach (string file in files)
                FileAssert.Exists(file);

            return files;
        }

        /// <summary>Walks up from the working directory and from <see cref="AppContext.BaseDirectory"/> —
        /// Unity batch mode and <c>dotnet test</c> start in different places.</summary>
        private static string ResolveUp(string relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, relative);
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            throw new DirectoryNotFoundException(
                $"'{relative}' not found walking up from cwd={Directory.GetCurrentDirectory()} " +
                $"or {AppContext.BaseDirectory}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WebMercatorTilingTests — the web-Mercator tiling math GeoJSON slicing rides on
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The projection boundary of the GeoJSON source: geodetic → unit square → tile-local, what survives
    /// quantization, the y-flip, and the pole clamp.
    /// </summary>
    [TestFixture]
    public class WebMercatorTilingTests
    {
        private static double2 TileLocal(double longitude, double latitude, TileId tile, double extent)
            => WebMercatorTiling.TileLocal(
                WebMercatorTiling.UnitSquareFromLonLat(
                    new GeoCoordinate { Latitude = latitude, Longitude = longitude }),
                tile, extent);

        // ── round trip through the independently-authored inverse ──────────────────────────────────

        /// <summary>
        /// Quantized tile-local coordinates round-trip through <see cref="TileId.ToLonLat"/>, an independent
        /// oracle, within the analytic bound <c>WorldExtent / (extent · 2^z)</c> (half a tile unit). The z0 and
        /// z14 bounds differ by 2¹⁴, so no single slack constant satisfies both rows.
        /// </summary>
        [Test]
        public void QuantizedTileLocal_RoundTripsWithinHalfATileUnit()
        {
            (double lon, double lat, int z, double extent)[] rows =
            {
                (   0.0,    0.0,  0, 4096.0),
                ( 179.9,  -85.0,  0, 4096.0),
                (  13.4,   52.52, 1, 4096.0),
                ( -74.0,   40.7,  8,  512.0),
                (  13.4,   52.52, 14, 4096.0),
                (-122.42,  37.77, 14, 4096.0),
                ( 151.21, -33.87, 14, 8192.0),
            };

            foreach ((double lon, double lat, int z, double extent) row in rows)
            {
                TileId tile = GeoJsonTestFixtures.TileOf(row.lon, row.lat, row.z);

                double2 local     = TileLocal(row.lon, row.lat, tile, row.extent);
                double2 quantized = new double2(math.round(local.x), math.round(local.y));
                double2 lonLat    = tile.ToLonLat(quantized.x, quantized.y, row.extent);

                // Measure in Mercator metres, where the tile grid is uniform, so the bound is exact rather
                // than latitude-dependent (it would not be in degrees).
                double2 expected = WebMercator.FromLonLat(
                    new GeoCoordinate3D { Latitude = row.lat, Longitude = row.lon });
                double2 actual = WebMercator.FromLonLat(
                    new GeoCoordinate3D { Latitude = lonLat.y, Longitude = lonLat.x });

                double residualX = math.abs(actual.x - expected.x);
                double residualY = math.abs(actual.y - expected.y);
                double bound     = WebMercator.WorldExtent / (row.extent * math.pow(2.0, row.z));

                TestContext.WriteLine(
                    $"lon={row.lon} lat={row.lat} z={row.z} extent={row.extent} tile={tile} " +
                    $"local=({local.x:F4},{local.y:F4}) → residual=({residualX:F6} m, {residualY:F6} m), " +
                    $"bound={bound:F6} m");

                Assert.That(residualX, Is.LessThanOrEqualTo(bound * (1.0 + 1e-9) + 1e-6),
                    $"x residual exceeds half a tile unit at z={row.z}, extent={row.extent}");
                Assert.That(residualY, Is.LessThanOrEqualTo(bound * (1.0 + 1e-9) + 1e-6),
                    $"y residual exceeds half a tile unit at z={row.z}, extent={row.extent}");
            }
        }

        /// <summary>The round trip's hand-checked structural rows: the null island and the antimeridian land on exact
        /// integers, in the exact tiles the slippy-map layout puts them in.</summary>
        [Test]
        public void StructuralAnchors_AreExact()
        {
            // (0, 0) is the meeting point of all four z1 tiles: extent-corner of (0,0), origin of (1,1).
            double2 originOfSouthEast = TileLocal(0.0, 0.0, GeoJsonTestFixtures.Tile(1, 1, 1), 4096.0);
            Assert.That(originOfSouthEast.x, Is.EqualTo(0.0).Within(1e-6));
            Assert.That(originOfSouthEast.y, Is.EqualTo(0.0).Within(1e-6));

            double2 cornerOfNorthWest = TileLocal(0.0, 0.0, GeoJsonTestFixtures.Tile(1, 0, 0), 4096.0);
            Assert.That(cornerOfNorthWest.x, Is.EqualTo(4096.0).Within(1e-6));
            Assert.That(cornerOfNorthWest.y, Is.EqualTo(4096.0).Within(1e-6));

            TileId world = GeoJsonTestFixtures.Tile(0, 0, 0);
            Assert.That(TileLocal( 180.0, 0.0, world, 4096.0).x, Is.EqualTo(4096.0).Within(1e-9));
            Assert.That(TileLocal(-180.0, 0.0, world, 4096.0).x, Is.EqualTo(0.0).Within(1e-9));
        }

        // ── the y-flip anchor, which goes through neither identity ─────────────────────────────────

        /// <summary>
        /// A round trip passes trivially if the forward and inverse formulas share a sign error,
        /// so this asserts SEMANTICS instead: northern latitudes are in the northern tile row, eastern
        /// longitudes in the eastern column. The values are non-zero and non-symmetric — a sign flip at a
        /// symmetric value is invisible.
        /// </summary>
        [Test]
        public void NorthIsRowZero_EastIsColumnOne()
        {
            Assert.That(GeoJsonTestFixtures.TileOf(13.4, 51.5, 1).Y, Is.EqualTo(0),
                "Berlin (lat +51.5) must be in the NORTHERN z1 tile row");
            Assert.That(GeoJsonTestFixtures.TileOf(18.4, -33.9, 1).Y, Is.EqualTo(1),
                "Cape Town (lat -33.9) must be in the SOUTHERN z1 tile row");

            Assert.That(GeoJsonTestFixtures.TileOf(13.4, 51.5, 1).X, Is.EqualTo(1),
                "lon +13.4 must be in the EASTERN z1 tile column");
            Assert.That(GeoJsonTestFixtures.TileOf(-74.0, 40.7, 1).X, Is.EqualTo(0),
                "lon -74 must be in the WESTERN z1 tile column");
        }

        /// <summary>The y-flip in tile-local terms: within one tile, py must DECREASE as latitude increases.</summary>
        [Test]
        public void TileLocalY_DecreasesNorthward()
        {
            TileId world = GeoJsonTestFixtures.Tile(0, 0, 0);
            double south = TileLocal(0.0, 10.0, world, 4096.0).y;
            double north = TileLocal(0.0, 60.0, world, 4096.0).y;

            Assert.That(north, Is.LessThan(south), "py must grow SOUTHWARD (origin top-left, Y down)");
        }

        // ── the pole clamp is a recorded limitation, not silent drift ───────────────────────────────

        /// <summary>
        /// <c>|lat| &gt; MaxLatitude</c> has no Mercator image, so it is CLAMPED. The second row is the
        /// non-vacuity clause: at lat 89 alone, "clamped" and "computed" are indistinguishable — a latitude
        /// strictly inside the limit must land strictly south of the clamped one, proving the clamp is not
        /// simply pinning everything.
        /// </summary>
        [Test]
        public void LatitudeBeyondMercatorLimit_ClampsRatherThanDiverging()
        {
            TileId world = GeoJsonTestFixtures.Tile(0, 0, 0);

            double atLimit = TileLocal(0.0, WebMercator.MaxLatitude, world, 4096.0).y;
            double at89    = TileLocal(0.0, 89.0, world, 4096.0).y;
            double at80    = TileLocal(0.0, 80.0, world, 4096.0).y;

            Assert.That(at89, Is.EqualTo(atLimit), "lat 89 must clamp to MaxLatitude exactly");
            Assert.That(double.IsInfinity(at89) || double.IsNaN(at89), Is.False);

            Assert.That(at80, Is.GreaterThan(atLimit),
                "lat 80 is inside the limit and must land strictly SOUTH of (below) the clamped value");

            double atSouthLimit = TileLocal(0.0, -WebMercator.MaxLatitude, world, 4096.0).y;
            Assert.That(TileLocal(0.0, -89.0, world, 4096.0).y, Is.EqualTo(atSouthLimit),
                "lat -89 must clamp to -MaxLatitude exactly");
        }

        /// <summary>The world's four corners in unit-square terms — the clamp's own frame of reference.</summary>
        [Test]
        public void UnitSquare_SpansExactlyZeroToOne()
        {
            double2 northWest = WebMercatorTiling.UnitSquareFromLonLat(
                new GeoCoordinate { Latitude = WebMercator.MaxLatitude, Longitude = -180.0 });
            double2 southEast = WebMercatorTiling.UnitSquareFromLonLat(
                new GeoCoordinate { Latitude = -WebMercator.MaxLatitude, Longitude = 180.0 });

            Assert.That(northWest.x, Is.EqualTo(0.0).Within(1e-12));
            Assert.That(northWest.y, Is.EqualTo(0.0).Within(1e-8));
            Assert.That(southEast.x, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(southEast.y, Is.EqualTo(1.0).Within(1e-8));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonParserTests — GeoJSON parsing into the Core model
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// RFC 7946 parsing: the rejections this source makes loudly (antimeridian, out-of-range coordinates,
    /// GeometryCollection), the attribute mapping, and the ring closure convention.
    /// </summary>
    [TestFixture]
    public class GeoJsonParserTests
    {
        private static string Point(double lon, double lat)
            => GeoJsonTestFixtures.Feature("Point", GeoJsonTestFixtures.Position(lon, lat));

        // ── the antimeridian is rejected, and only the antimeridian ─────────────────────────────────

        /// <summary>
        /// RFC 7946 §3.1.9 tells authors to split geometry crossing the antimeridian; interpreting such
        /// a segment literally draws it the long way around the world. v1 fails loudly instead. The
        /// exception must name the FEATURE INDEX — "something threw" is not the assertion.
        /// </summary>
        [Test]
        public void AntimeridianCrossingSegment_ThrowsNamingTheFeatureIndex()
        {
            string json = GeoJsonTestFixtures.Collection(
                Point(0.0, 0.0),
                GeoJsonTestFixtures.Feature("LineString", GeoJsonTestFixtures.Positions(170.0, 10.0, -170.0, 10.0)));

            var ex = Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(json));
            Assert.That(ex.Message, Does.Contain("feature 1"), "the message must name the offending feature");
            Assert.That(ex.Message, Does.Contain("antimeridian"));
        }

        /// <summary>The antimeridian test's companion: a 179° step is the largest legal one and must parse
        /// cleanly, proving the check is not firing on every long segment.</summary>
        [Test]
        public void LargestLegalLongitudeStep_Parses()
        {
            string json = GeoJsonTestFixtures.Feature(
                "LineString", GeoJsonTestFixtures.Positions(-90.0, 10.0, 89.0, 10.0)); // |Δlon| = 179

            GeoJsonDataset dataset = GeoJsonParser.Parse(json);
            Assert.That(dataset.Features.Count, Is.EqualTo(1));
            Assert.That(dataset.Features[0].Paths[0].Count, Is.EqualTo(2));
        }

        // ── out-of-range coordinates are malformed, not clamped ─────────────────────────────────────

        [Test]
        public void OutOfRangeCoordinates_Throw()
        {
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(Point(0.0, 91.0)),
                "latitude 91 is outside RFC 7946 §3.1.1's range");
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(Point(181.0, 0.0)),
                "longitude 181 is outside RFC 7946 §3.1.1's range");
        }

        /// <summary>The range check's non-vacuity clause: 89.9 is a valid latitude beyond the Mercator limit.
        /// It must PARSE (the limit is handled by the projection's clamp, not by rejection) — which proves the
        /// range check is a range check and not the clamp misfiring.</summary>
        [Test]
        public void ValidLatitudeBeyondTheMercatorLimit_Parses()
        {
            GeoJsonDataset dataset = GeoJsonParser.Parse(Point(0.0, 89.9));
            Assert.That(dataset.Features[0].Paths[0][0].Latitude, Is.EqualTo(89.9));
        }

        [Test]
        public void PositionIsLongitudeFirstOnTheWire_AndLatitudeFirstInTheModel()
        {
            // [13.4, 52.5] is Berlin: longitude 13.4, latitude 52.5. A missing swap would put it at 13.4°N.
            GeoCoordinate berlin = GeoJsonParser.Parse(Point(13.4, 52.5)).Features[0].Paths[0][0];

            Assert.That(berlin.Longitude, Is.EqualTo(13.4));
            Assert.That(berlin.Latitude, Is.EqualTo(52.5));
        }

        [Test]
        public void PositionWithAltitude_ReadsTheFirstTwoAndDiscardsTheRest()
        {
            GeoJsonDataset dataset = GeoJsonParser.Parse(
                "{\"type\":\"Point\",\"coordinates\":[13.4,52.5,120.0]}");

            GeoCoordinate p = dataset.Features[0].Paths[0][0];
            Assert.That(p.Longitude, Is.EqualTo(13.4));
            Assert.That(p.Latitude, Is.EqualTo(52.5));
        }

        // ── properties and id ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>JsonKind</c>'s six kinds map one-to-one onto <see cref="Value"/>'s factories, so the
        /// mapping is a total, lossless structural recursion. Asserting the LEAF (and the array length) is
        /// the non-vacuity clause: a stringify implementation also yields a non-empty properties map.
        /// </summary>
        [Test]
        public void NestedProperties_RecurseStructurallyToTheLeaf()
        {
            string json = GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0),
                properties: "{\"meta\":{\"scores\":[1,2.5,3],\"name\":\"kiosk\"},\"open\":true}");

            IReadOnlyDictionary<string, Value> properties = GeoJsonParser.Parse(json).Features[0].Properties;

            Assert.That(properties["open"].AsBool(), Is.True);

            IReadOnlyDictionary<string, Value> meta = properties["meta"].AsObject();
            Assert.That(meta["name"].AsString(), Is.EqualTo("kiosk"));

            IReadOnlyList<Value> scores = meta["scores"].AsArray();
            Assert.That(scores.Count, Is.EqualTo(3), "the array must survive as an array, not as text");
            Assert.That(scores[1].AsNumber(), Is.EqualTo(2.5), "the numeric LEAF must survive as a number");
        }

        [Test]
        public void NullProperties_BecomeAnEmptyNonNullMap()
        {
            GeoJsonFeature feature = GeoJsonParser.Parse(Point(0.0, 0.0)).Features[0];

            Assert.That(feature.Properties, Is.Not.Null, "IFeature.Properties is consumed unconditionally");
            Assert.That(feature.Properties.Count, Is.EqualTo(0));
        }

        [Test]
        public void IdSurvivesAsStringOrNumber_AndIsAbsentWhenUnset()
        {
            GeoJsonFeature stringId = GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0), idMember: "\"id\":\"node/42\",")).Features[0];
            Assert.That(stringId.Id.IsNull, Is.False);
            Assert.That(stringId.Id.AsString(), Is.EqualTo("node/42"));

            GeoJsonFeature numberId = GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0), idMember: "\"id\":42,")).Features[0];
            Assert.That(numberId.Id.IsNull, Is.False);
            Assert.That(numberId.Id.AsNumber(), Is.EqualTo(42.0));

            GeoJsonFeature noId = GeoJsonParser.Parse(Point(0.0, 0.0)).Features[0];
            Assert.That(noId.Id.IsNull, Is.True);
        }

        [Test]
        public void IdOfAnUnsupportedKind_Throws()
        {
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0), idMember: "\"id\":[1,2],")));
        }

        // ── rings are implicitly closed, like MVT's ──────────────────────────────────────────────────

        /// <summary>
        /// RFC 7946 §3.1.6 rings repeat their first position last; every downstream stage assumes the
        /// MVT convention instead (implicitly closed). Asserting the COUNT RELATION — exactly one fewer than
        /// the input — is the tooth: <c>first != last</c> alone would also pass on a ring that had lost some
        /// other vertex.
        /// </summary>
        [Test]
        public void ClosingDuplicateIsStripped()
        {
            string ring = GeoJsonTestFixtures.RectangleRing(-20.0, -20.0, 20.0, 20.0);
            GeoJsonFeature feature = GeoJsonParser.Parse(
                GeoJsonTestFixtures.Feature("Polygon", $"[{ring}]")).Features[0];

            IReadOnlyList<GeoCoordinate> parsed = feature.Paths[0];

            Assert.That(parsed.Count, Is.EqualTo(4), "a 5-position RFC ring carries 4 distinct vertices");
            Assert.That(parsed[0].Latitude  != parsed[parsed.Count - 1].Latitude ||
                        parsed[0].Longitude != parsed[parsed.Count - 1].Longitude, Is.True,
                "the ring must be implicitly closed (first vertex not repeated)");
        }

        [Test]
        public void MalformedRings_Throw()
        {
            string tooFew = GeoJsonTestFixtures.Positions(0.0, 0.0, 1.0, 0.0, 0.0, 0.0);
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(
                GeoJsonTestFixtures.Feature("Polygon", $"[{tooFew}]")), "a LinearRing needs 4+ positions");

            string unclosed = GeoJsonTestFixtures.Positions(0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0);
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(
                GeoJsonTestFixtures.Feature("Polygon", $"[{unclosed}]")), "a LinearRing must be closed");
        }

        // ── GeometryCollection is fenced, and only it ────────────────────────────────────────────────

        /// <summary>
        /// <c>TileGeometryType</c> is a per-FEATURE property and <c>IFeature.GeometryType</c> is
        /// singular, so a mixed-kind feature would make a per-ring type tag load-bearing again. Rejected,
        /// with the kind named.
        /// </summary>
        [Test]
        public void GeometryCollection_ThrowsNamingTheKind()
        {
            string geometryCollection =
                "{\"type\":\"Feature\",\"properties\":null,\"geometry\":{\"type\":\"GeometryCollection\"," +
                "\"geometries\":[{\"type\":\"Point\",\"coordinates\":[0,0]}]}}";

            var ex = Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(
                GeoJsonTestFixtures.Collection(Point(1.0, 1.0), geometryCollection)));

            Assert.That(ex.Message, Does.Contain("GeometryCollection"));
        }

        /// <summary>The GeometryCollection rejection's non-vacuity clause: the same collection minus the
        /// GeometryCollection parses, so the rejection is targeted at the kind rather than at the shape of
        /// the document.</summary>
        [Test]
        public void CollectionWithoutTheGeometryCollection_Parses()
        {
            GeoJsonDataset dataset = GeoJsonParser.Parse(GeoJsonTestFixtures.Collection(Point(1.0, 1.0)));

            Assert.That(dataset.Features.Count, Is.EqualTo(1));
            Assert.That(dataset.Features[0].GeometryType, Is.EqualTo(TileGeometryType.Point));
        }

        // ── Roots, Multi* flattening, unlocated features ─────────────────────────────────────────────

        [Test]
        public void AllThreeRootFormsAreAccepted()
        {
            Assert.That(GeoJsonParser.Parse("{\"type\":\"Point\",\"coordinates\":[1,2]}").Features.Count,
                Is.EqualTo(1), "bare geometry root (RFC §3)");
            Assert.That(GeoJsonParser.Parse(Point(1.0, 2.0)).Features.Count,
                Is.EqualTo(1), "bare Feature root");
            Assert.That(GeoJsonParser.Parse(GeoJsonTestFixtures.Collection(Point(1.0, 2.0), Point(3.0, 4.0)))
                .Features.Count, Is.EqualTo(2), "FeatureCollection root");
        }

        /// <summary>Multi* flattens the way <c>MvtGeometry.Decode</c> already shapes MVT geometry: a
        /// MultiPoint is N single-coordinate paths, not one N-coordinate path.</summary>
        [Test]
        public void MultiGeometriesFlattenToTheMvtPathShape()
        {
            GeoJsonFeature multiPoint = GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "MultiPoint", GeoJsonTestFixtures.Positions(0.0, 0.0, 10.0, 10.0, 20.0, 20.0))).Features[0];

            Assert.That(multiPoint.GeometryType, Is.EqualTo(TileGeometryType.Point));
            Assert.That(multiPoint.Paths.Count, Is.EqualTo(3));
            Assert.That(multiPoint.Paths[0].Count, Is.EqualTo(1));

            string outer = GeoJsonTestFixtures.RectangleRing(-20.0, -20.0, 20.0, 20.0);
            string hole  = GeoJsonTestFixtures.RectangleRing(-10.0, -10.0, 10.0, 10.0);
            string far   = GeoJsonTestFixtures.RectangleRing(40.0, 40.0, 60.0, 60.0);

            GeoJsonFeature multiPolygon = GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "MultiPolygon", $"[[{outer},{hole}],[{far}]]")).Features[0];

            Assert.That(multiPolygon.GeometryType, Is.EqualTo(TileGeometryType.Polygon));
            Assert.That(multiPolygon.Paths.Count, Is.EqualTo(3));
            Assert.That(multiPolygon.PolygonRingCounts, Is.EqualTo(new[] { 2, 1 }),
                "the per-polygon grouping is what lets the slicer drop a polygon's holes with its exterior");
        }

        [Test]
        public void UnlocatedFeature_ParsesAndCarriesNoGeometry()
        {
            // RFC §3.2 permits `"geometry": null`. Valid input must not throw; it simply reaches no tile.
            GeoJsonFeature feature = GeoJsonParser.Parse(
                "{\"type\":\"Feature\",\"properties\":{\"n\":1},\"geometry\":null}").Features[0];

            Assert.That(feature.Paths.Count, Is.EqualTo(0));
            Assert.That(feature.GeometryType, Is.EqualTo(TileGeometryType.Unknown));
            Assert.That(feature.Properties["n"].AsNumber(), Is.EqualTo(1.0));
        }

        [Test]
        public void MalformedJson_PropagatesTheJsonParseException()
        {
            Assert.Throws<JsonParseException>(() => GeoJsonParser.Parse("{\"type\":"));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonTileSlicerTests — slicing GeoJSON features into tile-local geometry
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Client-side slicing end to end — parse → project → cut one tile: the straddle partition, the
    /// containing polygon, line pieces, ring role and winding, the simplification deferral, and the
    /// buffer window.
    /// </summary>
    [TestFixture]
    public class GeoJsonTileSlicerTests
    {
        private static double TileLocalX(double longitude, TileId tile, double extent)
            => WebMercatorTiling.TileLocal(
                   WebMercatorTiling.UnitSquareFromLonLat(
                       new GeoCoordinate { Latitude = 0.0, Longitude = longitude }), tile, extent).x;

        // ── a polygon straddling a seam is PARTITIONED, not truncated ───────────────────────────────

        /// <summary>
        /// A rectangle across the z1 seam at longitude 0, sliced into both tiles with the buffer OFF: the
        /// areas sum to the input's, and both pieces keep its shoelace sign and are implicitly closed. Each piece
        /// must hold a synthesised vertex EXACTLY on the seam; a clipper that only drops out-of-bounds vertices
        /// makes none and loses area.
        /// </summary>
        [Test]
        public void PolygonStraddlingATileSeam_PartitionsAcrossBothTiles()
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
                $"seam input area ={inputArea:F3}, sliced = {sliced:F3}, |Δ| = {math.abs(sliced - inputArea):F3}, " +
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

        // ── a polygon containing the tile becomes the window itself ─────────────────────────────────

        /// <summary>
        /// Sutherland–Hodgman reduces a subject enclosing the whole window to the window rectangle with
        /// no special case. Non-vacuity: NONE of the input's vertices is inside the window, so a
        /// "keep the vertices that are inside" clipper emits nothing here and this reds.
        /// </summary>
        [Test]
        public void PolygonContainingTheTile_BecomesTheWindowRectangle()
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

        // ── winding comes from ROLE, and rings stay implicitly closed ───────────────────────────────

        /// <summary>
        /// RFC 7946 §3.1.6 gives ring role POSITIONALLY and tells parsers not to reject non-conforming winding,
        /// so role must be re-encoded as winding. A polygon authored AGAINST the right-hand rule must slice
        /// IDENTICALLY to its conforming twin: exterior positive, hole negative in tile space (the MVT
        /// convention the assembler classifies on).
        /// </summary>
        [Test]
        public void RingWindingIsNormalisedFromRole_NotFromAuthoring()
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

        /// <summary>Implicit closure at the slicer's output: a 5-position RFC ring emits exactly 4 tile-space vertices,
        /// first ≠ last. The count relation is the tooth — inequality alone would also hold if a vertex had
        /// been lost elsewhere.</summary>
        [Test]
        public void SlicedRingsAreImplicitlyClosed()
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

        // ── a polygon's holes leave with its exterior ───────────────────────────────────────────────

        /// <summary>
        /// If a polygon's exterior clips away, its holes must go too: an orphan hole would become the feature's
        /// FIRST ring downstream, set the exterior sign, and render as an inverted patch. The fixture is
        /// MALFORMED (polygon 2's hole lies outside its exterior), the only way the case arises. The orphan
        /// lies inside the tile, so dropping only the exterior leaves a visible inverted ring.
        /// </summary>
        [Test]
        public void ExteriorClippedAway_TakesItsHolesWithIt()
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

        // ── the simplification deferral is observable ───────────────────────────────────────────────

        /// <summary>
        /// Simplification is deferred, and a silently-ignored parameter is indistinguishable from an
        /// implemented one — so a non-zero tolerance THROWS. The default's numeric value is asserted
        /// explicitly, so a later silent change of default reds here.
        /// </summary>
        [Test]
        public void NonZeroSimplifyTolerance_ThrowsNotSupported()
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
        /// The <b>validator</b> rejects a non-zero tolerance, so a source that keeps options for its whole life
        /// rejects it when it accepts them. It calls <c>Validate</c> directly because <c>Slice</c>'s own guard
        /// (<see cref="NonZeroSimplifyTolerance_ThrowsNotSupported"/>) runs first. The NaN arm fails a
        /// <c>&lt; 0</c> or <c>&gt; 0</c> rewrite of <c>!= 0</c>; the valid arm fails "reject everything".
        /// </summary>
        [Test]
        public void ANonZeroSimplifyTolerance_IsRejectedByTheValidator()
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

        // ── the slicer's window IS the fill pipeline's window ───────────────────────────────────────

        /// <summary>
        /// At the default buffer the slicer's window and <c>TileBufferClip</c>'s are EQUAL, so the pipeline's
        /// clip is a no-op on GeoJSON tiles. The extents 1000 and 4095 give a fractional margin, so a formula
        /// that ignores the rescale, rounds or truncates reds. Limitation: <c>ReferenceExtent</c> is 4096, so
        /// every association of <c>b·e/R</c> is bit-identical and no extent can pin the order of the factors.
        /// </summary>
        [Test]
        public void DefaultBufferWindow_EqualsTheFillPipelineWindow()
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
                    $"window extent={extent}: slicer [{sliceMin.x}, {sliceMax.x}] vs " +
                    $"pipeline [{pipelineMin.x}, {pipelineMax.x}]");

                Assert.That(sliceMin.x, Is.EqualTo(pipelineMin.x));
                Assert.That(sliceMin.y, Is.EqualTo(pipelineMin.y));
                Assert.That(sliceMax.x, Is.EqualTo(pipelineMax.x));
                Assert.That(sliceMax.y, Is.EqualTo(pipelineMax.y));
            }
        }

        /// <summary>
        /// The window carries <c>TileBufferClip</c>'s input GUARDS, not merely its arithmetic. A
        /// NEGATIVE margin would erode INTO the tile (min &gt; 0, max &lt; extent), silently dropping
        /// geometry the tile owns; a NaN one would make an all-NaN window against which every vertex tests
        /// outside. Both must degrade to "cut exactly at the tile boundary" — and, since the two types claim
        /// to be the same window, must still agree with the pipeline's on the same input.
        /// </summary>
        [Test]
        public void NegativeAndNaNBuffers_DegradeToTheTileBoundaryLikeTheFillPipeline()
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
        /// A NaN margin must not clip the map away: every comparison against NaN is false, so an unguarded
        /// window makes <see cref="RingWindowClipper"/> reject EVERY vertex and the source renders blank with no
        /// error. <see cref="NegativeAndNaNBuffers_DegradeToTheTileBoundaryLikeTheFillPipeline"/> pins the
        /// window; this pins what the window does.
        /// </summary>
        [Test]
        public void ANaNBufferDoesNotClipTheMapAway()
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

        // ── the two tiles either side of a seam agree where the seam is ─────────────────────────────

        /// <summary>
        /// The seam-vertex boundary assignment survives the frames the slicer builds, not only the clipper's
        /// idealised one. Tiles either side of a seam compute <c>(u·2^z − x)·extent</c> with a different
        /// <c>x</c>, so only a boundary LITERAL makes them agree. Non-obvious why: the eastern tile matters
        /// because its crossing is near ZERO, where <c>a + t·d</c> lands at <c>1.14e−13</c>. It clips rather
        /// than slices, because the slicer's integer rounding hides an error this small.
        /// </summary>
        [Test]
        public void AdjacentTilesPutTheirSharedSeamVertexOnTheirOwnBoundary()
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

            // The eastern tile's interpolated crossing must miss its western edge, or the test passes an
            // implementation that never assigns the boundary.
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
        /// duplication is the point of the margin, and it is what lets a symbol be placed from either tile.
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
        /// A non-integral <c>Extent</c> is REJECTED, not rounded. Downstream reads the extent both as this
        /// <c>double</c> and as the decoded layer's whole number, so 4096.5 would arrive as both values and
        /// every consumer joining them would be off by a fraction of a tile. The valid arm fails "reject
        /// everything".
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
        /// The extent has an UPPER bound: a whole number above <c>uint.MaxValue</c>, and <c>+∞</c>, are
        /// rejected. Both pass the integrality check, but the layer narrows the extent to <c>uint</c> unchecked,
        /// so layer and geometry disagree; <c>+∞</c> gives a NaN window that slices away every vertex. The
        /// <c>uint.MaxValue</c> arm PASSES: the bound is the layer's <c>uint</c> domain, and the arm fails
        /// "reject anything large".
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // WindowClipperTests — the ring (Sutherland-Hodgman) and open-polyline (Liang-Barsky) window clippers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two window clippers, in tile-local coordinates: rings (Sutherland–Hodgman) and open polylines
    /// (Liang–Barsky). Lines are NOT the polygon case with the closure removed, and these teeth
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

        // ── Polylines ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A polyline that traverses the window, leaves, and traverses it again must emit TWO open
        /// paths. The count is asserted exactly (not <c>&gt;= 1</c>), and neither path may be closed —
        /// substituting the ring clipper here yields one closed ring, joining the two traverses across a gap
        /// the input never had.
        /// </summary>
        [Test]
        public void LineCrossingTheWindowTwice_EmitsTwoOpenPaths()
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
        /// Leaving and re-entering through the SAME edge also yields two paths. Distinct from
        /// <see cref="LineCrossingTheWindowTwice_EmitsTwoOpenPaths"/>, whose runs enter through different
        /// edges: this catches an implementation that only breaks a run when the crossing changes axis.
        /// </summary>
        [Test]
        public void LineLeavingAndReenteringThroughTheSameEdge_EmitsTwoPaths()
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

        // ── the boundary ASSIGNMENT — the contract nothing above observes ───────────────────────────
        // The tests above pass an interpolating `a + t·d`; these two pick fixtures where `a + t·d` misses.

        /// <summary>
        /// One line clipped from two adjacent tiles' frames must put the shared seam vertex EXACTLY on the
        /// seam: <c>x = Extent</c> in the western tile, <c>x = 0</c> in the eastern one, so both agree on their
        /// common edge. The test asserts that the interpolation MISSES the boundary here, so the fixture is not
        /// vacuous. The full-vertex bit-match is only a corollary; the two boundary-literal assertions carry
        /// the discrimination.
        /// </summary>
        [Test]
        public void OneLineClippedFromTwoAdjacentTiles_PutsTheSeamVertexExactlyOnTheSeam()
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
        /// A segment meeting the window exactly at a CORNER is put there by two half-planes at the same
        /// parameter, so BOTH of its coordinates must be assigned. Recording only the first plane tried
        /// leaves the other interpolated: on these fixtures that lands the vertex off the corner — outside
        /// the window on entry, inside it on exit — which is the disagreement the assignment
        /// exists to remove.
        /// </summary>
        [Test]
        public void SegmentCrossingExactlyAtAWindowCorner_LandsOnBothBoundariesExactly()
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
