// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// RFC 7946 parsing: the rejections this source makes loudly (T4a, T4c, T11), the attribute mapping
    /// (T6), and the ring closure convention (T7b).
    /// </summary>
    [TestFixture]
    public class GeoJsonParserTests
    {
        private static string Point(double lon, double lat)
            => GeoJsonTestFixtures.Feature("Point", GeoJsonTestFixtures.Position(lon, lat));

        // ── T4a: the antimeridian is rejected, and only the antimeridian ────────────────────────────

        /// <summary>
        /// T4a. RFC 7946 §3.1.9 tells authors to split geometry crossing the antimeridian; interpreting such
        /// a segment literally draws it the long way around the world. v1 fails loudly instead. The
        /// exception must name the FEATURE INDEX — "something threw" is not the assertion.
        /// </summary>
        [Test]
        public void T4a_AntimeridianCrossingSegment_ThrowsNamingTheFeatureIndex()
        {
            string json = GeoJsonTestFixtures.Collection(
                Point(0.0, 0.0),
                GeoJsonTestFixtures.Feature("LineString", GeoJsonTestFixtures.Positions(170.0, 10.0, -170.0, 10.0)));

            var ex = Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(json));
            Assert.That(ex.Message, Does.Contain("feature 1"), "the message must name the offending feature");
            Assert.That(ex.Message, Does.Contain("antimeridian"));
        }

        /// <summary>T4a's companion: a 179° step is the largest legal one and must parse cleanly, proving
        /// the check is not firing on every long segment.</summary>
        [Test]
        public void T4a_LargestLegalLongitudeStep_Parses()
        {
            string json = GeoJsonTestFixtures.Feature(
                "LineString", GeoJsonTestFixtures.Positions(-90.0, 10.0, 89.0, 10.0)); // |Δlon| = 179

            GeoJsonDataset dataset = GeoJsonParser.Parse(json);
            Assert.That(dataset.Features.Count, Is.EqualTo(1));
            Assert.That(dataset.Features[0].Paths[0].Count, Is.EqualTo(2));
        }

        // ── T4c: out-of-range coordinates are malformed, not clamped ────────────────────────────────

        [Test]
        public void T4c_OutOfRangeCoordinates_Throw()
        {
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(Point(0.0, 91.0)),
                "latitude 91 is outside RFC 7946 §3.1.1's range");
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(Point(181.0, 0.0)),
                "longitude 181 is outside RFC 7946 §3.1.1's range");
        }

        /// <summary>T4c's non-vacuity clause: 89.9 is a valid latitude beyond the Mercator limit. It must
        /// PARSE (the limit is handled by the projection's clamp, not by rejection) — which proves the range
        /// check is a range check and not the clamp misfiring.</summary>
        [Test]
        public void T4c_ValidLatitudeBeyondTheMercatorLimit_Parses()
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

        // ── T6: properties and id ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// T6. <c>JsonKind</c>'s six kinds map one-to-one onto <see cref="Value"/>'s factories, so the
        /// mapping is a total, lossless structural recursion. Asserting the LEAF (and the array length) is
        /// the non-vacuity clause: a stringify implementation also yields a non-empty properties map.
        /// </summary>
        [Test]
        public void T6_NestedProperties_RecurseStructurallyToTheLeaf()
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
        public void T6_NullProperties_BecomeAnEmptyNonNullMap()
        {
            GeoJsonFeature feature = GeoJsonParser.Parse(Point(0.0, 0.0)).Features[0];

            Assert.That(feature.Properties, Is.Not.Null, "IFeature.Properties is consumed unconditionally");
            Assert.That(feature.Properties.Count, Is.EqualTo(0));
        }

        [Test]
        public void T6_IdSurvivesAsStringOrNumber_AndIsAbsentWhenUnset()
        {
            GeoJsonFeature stringId = GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0), idMember: "\"id\":\"node/42\",")).Features[0];
            Assert.That(stringId.HasId, Is.True);
            Assert.That(stringId.Id.AsString(), Is.EqualTo("node/42"));

            GeoJsonFeature numberId = GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0), idMember: "\"id\":42,")).Features[0];
            Assert.That(numberId.HasId, Is.True);
            Assert.That(numberId.Id.AsNumber(), Is.EqualTo(42.0));

            GeoJsonFeature noId = GeoJsonParser.Parse(Point(0.0, 0.0)).Features[0];
            Assert.That(noId.HasId, Is.False);
            Assert.That(noId.Id.IsNull, Is.True);
        }

        [Test]
        public void IdOfAnUnsupportedKind_Throws()
        {
            Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(GeoJsonTestFixtures.Feature(
                "Point", GeoJsonTestFixtures.Position(0.0, 0.0), idMember: "\"id\":[1,2],")));
        }

        // ── T7b: rings are implicitly closed, like MVT's ─────────────────────────────────────────────

        /// <summary>
        /// T7b. RFC 7946 §3.1.6 rings repeat their first position last; every downstream stage assumes the
        /// MVT convention instead (implicitly closed). Asserting the COUNT RELATION — exactly one fewer than
        /// the input — is the tooth: <c>first != last</c> alone would also pass on a ring that had lost some
        /// other vertex.
        /// </summary>
        [Test]
        public void T7b_ClosingDuplicateIsStripped()
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

        // ── T11: GeometryCollection is fenced, and only it ───────────────────────────────────────────

        /// <summary>
        /// T11. <c>TileGeometryType</c> is a per-FEATURE property and <c>IFeature.GeometryType</c> is
        /// singular, so a mixed-kind feature would make a per-ring type tag load-bearing again. Rejected,
        /// with the kind named.
        /// </summary>
        [Test]
        public void T11_GeometryCollection_ThrowsNamingTheKind()
        {
            string geometryCollection =
                "{\"type\":\"Feature\",\"properties\":null,\"geometry\":{\"type\":\"GeometryCollection\"," +
                "\"geometries\":[{\"type\":\"Point\",\"coordinates\":[0,0]}]}}";

            var ex = Assert.Throws<GeoJsonFormatException>(() => GeoJsonParser.Parse(
                GeoJsonTestFixtures.Collection(Point(1.0, 1.0), geometryCollection)));

            Assert.That(ex.Message, Does.Contain("GeometryCollection"));
        }

        /// <summary>T11's non-vacuity clause: the same collection minus the GeometryCollection parses, so the
        /// rejection is targeted at the kind rather than at the shape of the document.</summary>
        [Test]
        public void T11_TheSiblingFeatureAloneParses()
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
}
