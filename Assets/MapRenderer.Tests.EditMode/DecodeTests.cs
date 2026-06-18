using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Headless validation of the Batch-1 decode + coordinate path against the real fixture tile.
    /// No rendering / GUI required: Window → General → Test Runner → EditMode → Run All.
    /// </summary>
    public class DecodeTests
    {
        private static byte[] LoadFixture()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"fixture missing at {path}");
            return File.ReadAllBytes(path);
        }

        [Test]
        public void Decodes_expected_layers_and_counts()
        {
            var tile = MvtDecoder.Decode(LoadFixture());

            Assert.IsNotNull(tile.GetLayer("countries"), "countries layer present");
            Assert.IsNotNull(tile.GetLayer("geolines"), "geolines layer present");
            Assert.IsNotNull(tile.GetLayer("centroids"), "centroids layer present");

            Assert.AreEqual(239, tile.GetLayer("countries").Features.Count, "country feature count");
            Assert.AreEqual(6, tile.GetLayer("geolines").Features.Count, "geoline feature count");
            Assert.AreEqual(4096u, tile.GetLayer("countries").Extent, "default extent");
        }

        [Test]
        public void Country_features_are_polygons_with_geometry()
        {
            var tile = MvtDecoder.Decode(LoadFixture());
            foreach (var f in tile.GetLayer("countries").Features)
            {
                Assert.AreEqual(MvtGeometryType.Polygon, f.GeometryType);
                Assert.IsNotNull(f.Geometry);
                Assert.Greater(f.Geometry.Length, 0);
            }
        }

        [Test]
        public void Geometry_decodes_into_nonempty_rings()
        {
            var tile = MvtDecoder.Decode(LoadFixture());
            var layer = tile.GetLayer("countries");
            int totalRings = 0;
            foreach (var f in layer.Features)
            {
                var rings = MvtGeometry.Decode(f.Geometry);
                foreach (var ring in rings)
                    Assert.GreaterOrEqual(ring.Count, 3, "a polygon ring needs >= 3 points");
                totalRings += rings.Count;
            }
            Assert.Greater(totalRings, 0, "expected at least one decoded ring");
        }

        [Test]
        public void Projected_vertices_fall_within_tile_world_bounds()
        {
            // Catches gross scale/parse bugs (e.g. forgetting tile→Mercator, leaving raw 0..4096 coords).
            // NOTE: at z0 the tile bbox is symmetric about the origin, so this does NOT catch a Y-flip;
            // a non-z0 fixture would. Y-orientation is validated visually in Batch 2.
            var tile = MvtDecoder.Decode(LoadFixture());
            var layer = tile.GetLayer("countries");
            var t = new TileId(0, 0, 0);
            var (min, max) = t.MercatorBounds();

            double marginX = (max.x - min.x) * 0.05;
            double marginY = (max.y - min.y) * 0.05;
            int checkd = 0;

            foreach (var f in layer.Features)
            foreach (var ring in MvtGeometry.Decode(f.Geometry))
            foreach (var p in ring)
            {
                double2 m = t.ToMercator(p.x, p.y, layer.Extent);
                Assert.That(m.x, Is.GreaterThanOrEqualTo(min.x - marginX).And.LessThanOrEqualTo(max.x + marginX));
                Assert.That(m.y, Is.GreaterThanOrEqualTo(min.y - marginY).And.LessThanOrEqualTo(max.y + marginY));
                checkd++;
            }
            Assert.Greater(checkd, 0);
        }
    }
}
