using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Headless validation of the Batch-1 decode + coordinate path against the real fixture tile.
    /// No rendering / GUI required: Window → General → Test Runner → EditMode → Run All.
    /// </summary>
    public class DecodeTests
    {
        /// <summary>The address the committed fixture is decoded at. IR C1 P3: the decode stamps it into
        /// every layer's buffer, so it must be the same one the projection assertions use below.</summary>
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static byte[] LoadFixture()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"fixture missing at {path}");
            return File.ReadAllBytes(path);
        }

        [Test]
        public void Decodes_expected_layers_and_counts()
        {
            using var tile = MvtDecoder.Decode(FixtureTile, LoadFixture());

            Assert.IsNotNull(tile.GetLayer("countries"), "countries layer present");
            Assert.IsNotNull(tile.GetLayer("geolines"), "geolines layer present");
            Assert.IsNotNull(tile.GetLayer("centroids"), "centroids layer present");

            Assert.AreEqual(239, tile.GetLayer("countries").Features.Count, "country feature count");
            Assert.AreEqual(6, tile.GetLayer("geolines").Features.Count, "geoline feature count");
            Assert.AreEqual(4096u, tile.GetLayer("countries").Extent, "default extent");
        }

        /// <summary>IR C1 P3: a decoded feature no longer carries a command stream — geometry belongs to the
        /// LAYER. The "every country is a polygon WITH geometry" claim is therefore split across the two
        /// things that now hold the halves: the feature's declared kind, and the layer's own buffer.</summary>
        [Test]
        public void Country_features_are_polygons_and_the_layer_carries_their_geometry()
        {
            using var tile = MvtDecoder.Decode(FixtureTile, LoadFixture());
            var layer = tile.GetLayer("countries");
            foreach (var f in layer.Features)
                Assert.AreEqual(TileGeometryType.Polygon, f.GeometryType);

            Assert.IsTrue(layer.Geometry.IsCreated, "the layer must own a materialized buffer");
            Assert.AreEqual(layer.Features.Count, layer.Geometry.FeatureCount,
                "the buffer's per-feature kind column must span EVERY feature of the layer — a buffer sized " +
                "to some subset is the mis-bucketing hazard the ordinal join depends on not having");
            Assert.Greater(layer.Geometry.RingCount, 0, "…and it must actually hold rings");
        }

        [Test]
        public void Geometry_decodes_into_nonempty_rings()
        {
            // Arm A: the independent fixture reader + the managed reference decoder (IR C1 P3 — the decoded
            // feature has no stream to read, and reading the layer's buffer would make this self-referential).
            var layer = MvtFixtureStreams.ReadLayer(LoadFixture(), "countries");
            int totalRings = 0;
            for (int fi = 0; fi < layer.Commands.Count; fi++)
            {
                var rings = MvtGeometry.Decode(layer.Commands[fi]);
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
            var layer = MvtFixtureStreams.ReadLayer(LoadFixture(), "countries");
            var t = FixtureTile;
            var (min, max) = t.MercatorBounds();

            double marginX = (max.x - min.x) * 0.05;
            double marginY = (max.y - min.y) * 0.05;
            int checkd = 0;

            foreach (uint[] commands in layer.Commands)
            foreach (var ring in MvtGeometry.Decode(commands))
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
