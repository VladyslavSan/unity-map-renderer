// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Style;
using NUnit.Framework;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// Seam test: exercises FeatureSelector.SelectFeatures, verifying it routes through
    /// SourceLayerResolver (S08 seam) and applies the $type filter correctly against real MvtFeature geometry.
    /// </summary>
    [TestFixture]
    internal class FeatureSelectorTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        private static MvtTile BuildTile()
        {
            var tile = new MvtTile();

            // Layer "roads": 2 LineString features, 1 Point feature
            var roads = new MvtLayer { Name = "roads", Extent = 4096, Version = 2 };
            roads.Features.Add(new MvtFeature { GeometryType = TileGeometryType.LineString, Geometry = new uint[0] });
            roads.Features.Add(new MvtFeature { GeometryType = TileGeometryType.LineString, Geometry = new uint[0] });
            roads.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Point, Geometry = new uint[0] });
            tile.Layers.Add(roads);

            // Layer "landuse": 3 Polygon features
            var landuse = new MvtLayer { Name = "landuse", Extent = 4096, Version = 2 };
            landuse.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Polygon, Geometry = new uint[0] });
            landuse.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Polygon, Geometry = new uint[0] });
            landuse.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Polygon, Geometry = new uint[0] });
            tile.Layers.Add(landuse);

            return tile;
        }

        private static StyleLayer MakeLayer(string sourceLayer, string filterJson = null)
        {
            return new StyleLayer
            {
                Id = "test",
                Source = "mysource",
                SourceLayer = sourceLayer,
                Filter = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };
        }

        // ── Source-layer resolution (seam tests) ──────────────────────────────────────────────────

        [Test]
        public void WrongSourceLayer_ReturnsEmpty()
        {
            var tile = BuildTile();
            var layer = MakeLayer("nonexistent");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(0), "Wrong source-layer name should yield empty selection");
        }

        [Test]
        public void NullSourceLayer_ReturnsEmpty()
        {
            var tile = BuildTile();
            var layer = MakeLayer(null);
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(0), "Null source-layer should yield empty selection");
        }

        [Test]
        public void NullTile_ReturnsEmpty()
        {
            var layer = MakeLayer("roads");
            var result = FeatureSelector.SelectFeatures(layer, null);
            Assert.That(result.Count, Is.EqualTo(0), "Null tile should yield empty selection");
        }

        [Test]
        public void CorrectSourceLayer_NoFilter_ReturnsAllFeatures()
        {
            var tile = BuildTile();
            var layer = MakeLayer("roads");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(3), "roads layer has 3 features");
        }

        [Test]
        public void CorrectSourceLayer_Landuse_ReturnsAllLanduseFeatures()
        {
            var tile = BuildTile();
            var layer = MakeLayer("landuse");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(3), "landuse layer has 3 features");
        }

        // ── $type filter over real MvtFeature geometry ────────────────────────────────────────────

        [Test]
        public void TypeFilter_LineString_SelectsOnlyLineStrings()
        {
            var tile = BuildTile();
            var layer = MakeLayer("roads", "[\"==\",\"$type\",\"LineString\"]");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(2), "roads layer has 2 LineString features");
            foreach (var f in result)
                Assert.That(f.GeometryType, Is.EqualTo(TileGeometryType.LineString));
        }

        [Test]
        public void TypeFilter_Point_SelectsOnlyPoints()
        {
            var tile = BuildTile();
            var layer = MakeLayer("roads", "[\"==\",\"$type\",\"Point\"]");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(1), "roads layer has 1 Point feature");
            Assert.That(result[0].GeometryType, Is.EqualTo(TileGeometryType.Point));
        }

        [Test]
        public void TypeFilter_Polygon_SelectedFromLanduse()
        {
            var tile = BuildTile();
            var layer = MakeLayer("landuse", "[\"==\",\"$type\",\"Polygon\"]");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(3), "landuse layer has 3 Polygon features");
        }

        [Test]
        public void TypeFilter_WrongType_SelectsNothing()
        {
            var tile = BuildTile();
            // roads layer has LineString and Point, not Polygon
            var layer = MakeLayer("roads", "[\"==\",\"$type\",\"Polygon\"]");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(0), "roads layer has no Polygon features");
        }

        // ── Expression-form type filter over real features ────────────────────────────────────────

        [Test]
        public void ExpressionTypeFilter_SelectsSameAsLegacy()
        {
            var tile = BuildTile();
            var legacyLayer = MakeLayer("roads", "[\"==\",\"$type\",\"LineString\"]");
            var exprLayer = MakeLayer("roads", "[\"==\",[\"geometry-type\"],\"LineString\"]");

            var legacyResult = FeatureSelector.SelectFeatures(legacyLayer, tile);
            var exprResult = FeatureSelector.SelectFeatures(exprLayer, tile);

            Assert.That(legacyResult.Count, Is.EqualTo(exprResult.Count),
                "Legacy and expression type filters should select same count");
        }

        // ── none filter ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void NoneTypeFilter_ExcludesMatchingFeatures()
        {
            var tile = BuildTile();
            // none(type==LineString) -> excludes LineString -> only Point remains in roads
            var layer = MakeLayer("roads", "[\"none\",[\"==\",\"$type\",\"LineString\"]]");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(1), "Only 1 non-LineString feature (Point) in roads");
            Assert.That(result[0].GeometryType, Is.EqualTo(TileGeometryType.Point));
        }

        // ── all filter ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void AllFilter_MustSatisfyBothConditions()
        {
            var tile = BuildTile();
            // all(type==LineString, type==Point) -> impossible -> empty
            var layer = MakeLayer("roads",
                "[\"all\",[\"==\",\"$type\",\"LineString\"],[\"==\",\"$type\",\"Point\"]]");
            var result = FeatureSelector.SelectFeatures(layer, tile);
            Assert.That(result.Count, Is.EqualTo(0),
                "all(type==LineString, type==Point) is always false");
        }

        // ── NullLayer guard ───────────────────────────────────────────────────────────────────────

        [Test]
        public void NullStyleLayer_ReturnsEmpty()
        {
            var tile = BuildTile();
            var result = FeatureSelector.SelectFeatures(null, tile);
            Assert.That(result.Count, Is.EqualTo(0), "Null StyleLayer should yield empty selection");
        }
    }
}
