// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Style;
using NUnit.Framework;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

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
            roads.Features.Add(new MvtFeature { GeometryType = TileGeometryType.LineString });
            roads.Features.Add(new MvtFeature { GeometryType = TileGeometryType.LineString });
            roads.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Point });
            tile.Layers.Add(roads);

            // Layer "landuse": 3 Polygon features
            var landuse = new MvtLayer { Name = "landuse", Extent = 4096, Version = 2 };
            landuse.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Polygon });
            landuse.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Polygon });
            landuse.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Polygon });
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

        // ── IR B7: the ordinal-returning overload ─────────────────────────────────────────────────

        /// <summary>
        /// The ordinal is the feature's position in the <b>source layer's</b> feature list — never its
        /// position in the selection. Every per-feature side array a consumer joins to the shared geometry
        /// buffer is keyed on it, and <c>RingFeatureIdx</c> indexes the layer, so a slot-based ordinal
        /// mis-attributes colours, widths and labels the moment a filter rejects anything.
        ///
        /// <para>The fixture makes the two differ: the only matching feature sits at layer position 2 and
        /// selection position 0, so a slot-indexed implementation reports 0 and fails here.</para>
        /// </summary>
        [Test]
        public void SelectFeaturesByOrdinal_ReportsThePositionInTheLayer_NotInTheSelection()
        {
            MvtTile tile = BuildTile();
            ITileLayer roads = tile.GetLayer("roads");
            var layer = MakeLayer("roads", "[\"==\",\"$type\",\"Point\"]");

            var selected = new System.Collections.Generic.List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(layer, roads, 0.0, selected);

            Assert.AreEqual(3, roads.Features.Count, "precondition: 'roads' holds three features");
            Assert.AreEqual(1, selected.Count, "precondition: exactly one of them is a Point");
            Assert.AreEqual(2, selected[0].Ordinal,
                "the Point is the THIRD feature of 'roads', so its ordinal is 2 — a selected-slot ordinal " +
                "would report 0 here and silently mis-join every per-feature side array");
            Assert.AreSame(roads.Features[2], selected[0].Feature,
                "…and the pair must carry the feature that ordinal names");
        }

        /// <summary>An unfiltered selection reports the identity ordinals — the case every consumer's
        /// existing behaviour depends on — and the output list is cleared first, so a reused buffer cannot
        /// accumulate two layers' selections into one join.</summary>
        [Test]
        public void SelectFeaturesByOrdinal_UnfilteredIsIdentity_AndClearsTheOutputList()
        {
            MvtTile tile = BuildTile();
            ITileLayer landuse = tile.GetLayer("landuse");
            var layer = MakeLayer("landuse");

            var selected = new System.Collections.Generic.List<SelectedTileFeature>
            {
                // Deliberate leftover: a selection that appended without clearing would keep it.
                new SelectedTileFeature { Feature = landuse.Features[0], Ordinal = 99 },
            };
            FeatureSelector.SelectFeatures(layer, landuse, 0.0, selected);

            Assert.AreEqual(3, selected.Count,
                "the output list must be CLEARED first — three features selected, not four");
            for (int i = 0; i < selected.Count; i++)
            {
                Assert.AreEqual(i, selected[i].Ordinal, $"unfiltered ordinal {i} must be the identity");
                Assert.AreSame(landuse.Features[i], selected[i].Feature, $"…paired with feature {i}");
            }

            // Null-tolerance, matching the IDecodedTile overload: an unresolvable layer selects nothing.
            // Cast disambiguates the null between the ITileLayer overload and its IReadOnlyList<IFeature>
            // sibling (added for the read-once worker path) — this pins the ITileLayer one specifically.
            FeatureSelector.SelectFeatures(layer, (ITileLayer)null, 0.0, selected);
            Assert.AreEqual(0, selected.Count, "a null tile layer must leave the output empty");
        }

        // ── Compiled-filter memo ──────────────────────────────────────────────────────────────────
        // Every case below uses an ARRAY filter on purpose. Compile() returns shared MatchAll/MatchNone
        // SINGLETONS for null and for bare true/false, so a reference-equality tooth written against
        // those would pass with no memo at all — it cannot discriminate. An array filter is the value
        // where the code is not inert: uncached, each Compile allocates a fresh CompiledFilter.

        private const string ArrayFilter = "[\"==\",\"$type\",\"LineString\"]";

        [Test]
        public void CompiledFilter_SameLayer_IsCompiledOnce()
        {
            var layer = MakeLayer("roads", ArrayFilter);
            Assert.AreSame(FeatureSelector.FilterFor(layer), FeatureSelector.FilterFor(layer),
                "An array filter must be compiled once and reused — a second CompiledFilter instance " +
                "means the memo missed and the filter was recompiled.");
        }

        [Test]
        public void CompiledFilter_DistinctFilterNodes_DoNotShare()
        {
            // Same filter TEXT, two independently parsed nodes: the memo must key on the node, so these
            // are two entries. Guards against a "cache" that collapses onto one global CompiledFilter.
            Assert.AreNotSame(
                FeatureSelector.FilterFor(MakeLayer("roads", ArrayFilter)),
                FeatureSelector.FilterFor(MakeLayer("roads", ArrayFilter)),
                "Two separately parsed filter nodes must compile to two CompiledFilters.");
        }

        [Test]
        public void CompiledFilter_ReassignedFilter_IsRecompiled()
        {
            // StyleLayer.Filter is a public mutable field. Keying the memo on the LAYER would serve the
            // stale compile here; keying on the node misses and recompiles. This is the assertion that
            // makes the choice of key load-bearing rather than incidental.
            var layer = MakeLayer("roads", ArrayFilter);
            var before = FeatureSelector.FilterFor(layer);

            layer.Filter = JsonParser.Parse("[\"==\",\"$type\",\"Point\"]");
            var after = FeatureSelector.FilterFor(layer);

            Assert.AreNotSame(before, after, "Reassigning Filter must not keep serving the old compile.");

            var tile = BuildTile();
            Assert.That(FeatureSelector.SelectFeatures(layer, tile).Count, Is.EqualTo(1),
                "…and the new filter must actually be the one applied: 'roads' holds 1 Point feature.");
        }

        [Test]
        public void CompiledFilter_MalformedFilter_ThrowsEveryTime()
        {
            // Nothing is memoized on the throwing path, so the second call must throw too rather than
            // silently succeeding off a half-populated entry.
            var layer = MakeLayer("roads", "[\"==\"]");
            Assert.Throws<MapRenderer.Core.Expressions.ExpressionParseException>(
                () => FeatureSelector.FilterFor(layer));
            Assert.Throws<MapRenderer.Core.Expressions.ExpressionParseException>(
                () => FeatureSelector.FilterFor(layer));
        }
    }
}
