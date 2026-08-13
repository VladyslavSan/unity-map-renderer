// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The projection boundary of the GeoJSON source (T5, T5b, T4b): geodetic → unit square → tile-local, and
    /// what survives quantization.
    /// </summary>
    [TestFixture]
    public class WebMercatorTilingTests
    {
        private static double2 TileLocal(double longitude, double latitude, TileId tile, double extent)
            => WebMercatorTiling.TileLocal(
                WebMercatorTiling.UnitSquareFromLonLat(
                    new GeoCoordinate { Latitude = latitude, Longitude = longitude }),
                tile, extent);

        // ── T5: round trip through the independently-authored inverse ──────────────────────────────

        /// <summary>
        /// T5. Quantized tile-local coordinates round-trip through <see cref="TileId.ToLonLat"/> — authored
        /// years earlier, for a different purpose, which is what makes it an INDEPENDENT oracle rather than a
        /// restatement of the forward formula. The residual is printed in Mercator metres alongside the
        /// analytic bound <c>WorldExtent / (extent · 2^z)</c> (= half a tile unit).
        ///
        /// <para>The z0 and z14 rows are the non-vacuity clause: their bounds differ by 2¹⁴, so no single
        /// slack constant can satisfy both.</para>
        /// </summary>
        [Test]
        public void T5_QuantizedTileLocal_RoundTripsWithinHalfATileUnit()
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

        /// <summary>T5, hand-checked structural rows: the null island and the antimeridian land on exact
        /// integers, in the exact tiles the slippy-map layout puts them in.</summary>
        [Test]
        public void T5_StructuralAnchors_AreExact()
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

        // ── T5b: the y-flip anchor, which goes through neither identity ────────────────────────────

        /// <summary>
        /// T5b. A round trip passes by construction if the forward and inverse formulas share a sign error,
        /// so this asserts SEMANTICS instead: northern latitudes are in the northern tile row, eastern
        /// longitudes in the eastern column. The values are non-zero and non-symmetric — a sign flip at a
        /// symmetric value is invisible.
        /// </summary>
        [Test]
        public void T5b_NorthIsRowZero_EastIsColumnOne()
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

        /// <summary>T5b, in tile-local terms: within one tile, py must DECREASE as latitude increases.</summary>
        [Test]
        public void T5b_TileLocalY_DecreasesNorthward()
        {
            TileId world = GeoJsonTestFixtures.Tile(0, 0, 0);
            double south = TileLocal(0.0, 10.0, world, 4096.0).y;
            double north = TileLocal(0.0, 60.0, world, 4096.0).y;

            Assert.That(north, Is.LessThan(south), "py must grow SOUTHWARD (origin top-left, Y down)");
        }

        // ── T4b: the pole clamp is a recorded limitation, not silent drift ──────────────────────────

        /// <summary>
        /// T4b. <c>|lat| &gt; MaxLatitude</c> has no Mercator image, so it is CLAMPED. The second row is the
        /// non-vacuity clause: at lat 89 alone, "clamped" and "computed" are indistinguishable — a latitude
        /// strictly inside the limit must land strictly south of the clamped one, proving the clamp is not
        /// simply pinning everything.
        /// </summary>
        [Test]
        public void T4b_LatitudeBeyondMercatorLimit_ClampsRatherThanDiverging()
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
}
