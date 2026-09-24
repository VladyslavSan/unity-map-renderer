// Engine-lane filter tests: MvtFeature as IFeature, FeatureSelector, and the native filter VM's wiring and
// parity oracle. PropertyFilterTests.cs is separate: its `using System;` makes the bare ValueType ambiguous.
//
// Contents:
//   MvtFeatureIFeatureContractTests   — MvtFeature implements IFeature directly against its exact documented
//                                       Null-Object semantics (TryGetProperty/Properties/GeometryType).
//   FeatureSelectorTests              — FeatureSelector.SelectFeatures: source-layer resolution, $type and
//                                       expression-form filters, ordinal reporting, the compiled-filter memo.
//   FeatureSelectorNativeFilterTests  — the native filter VM's production-boundary parity, dispatch
//                                       coverage, and both refusal-fallback gates.
//   NativeFilterVmTests               — the VM's own parity oracle against liberty.json's filter corpus,
//                                       hand-built match/has/compare parity, and the synthetic error model.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests;

namespace MapRenderer.Tests.Filters
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtFeatureIFeatureContractTests — MvtFeature implements IFeature directly, pinned to exact semantics
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>MvtFeature</c>'s <see cref="IFeature"/> semantics: with the <c>EmptyPropertyStore</c> Null Object
    /// or a missing key, <c>TryGetProperty</c> returns <c>false</c> + <c>Value.Null</c> and never throws;
    /// <c>Properties</c> is then empty; <c>GeometryType</c> passes through. <c>Id</c> decoding is pinned in
    /// <c>MvtPropertyDecodeTests</c>, and store forwarding in <c>DensePropertyStoreTests</c>.
    /// </summary>
    /// <remarks>
    /// Non-obvious why: the populated-bag tests use a <see cref="DictionaryFeature"/>, because
    /// <see cref="MvtFeature.Store"/> is internal and holds only a real
    /// <see cref="MapRenderer.Jobs.Mvt.DensePropertyStore"/>, which this file cannot hand-roll.
    /// </remarks>
    [TestFixture]
    public class MvtFeatureIFeatureContractTests
    {
        [Test]
        public void TryGetProperty_PresentKey_ReturnsTrueAndValue()
        {
            IFeature feature = new DictionaryFeature(
                new Dictionary<string, Value> { ["NAME"] = Value.String("Aruba") });

            bool found = feature.TryGetProperty("NAME", out Value value);

            Assert.IsTrue(found);
            Assert.AreEqual(Value.String("Aruba"), value);
        }

        [Test]
        public void TryGetProperty_MissingKey_ReturnsFalseAndNull()
        {
            IFeature feature = new DictionaryFeature(
                new Dictionary<string, Value> { ["NAME"] = Value.String("Aruba") });

            bool found = feature.TryGetProperty("ABBREV", out Value value);

            Assert.IsFalse(found, "a missing key must return false, not throw.");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void TryGetProperty_NoStore_ReturnsFalseAndNull_NeverThrows()
        {
            var feature = new MvtFeature(); // Store defaults to the EmptyPropertyStore Null Object
            IFeature asFeature = feature;

            bool found = asFeature.TryGetProperty("NAME", out Value value);

            Assert.IsFalse(found,
                "with no real store the default EmptyPropertyStore answers 'absent' — never throws.");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void Properties_NoStore_ExposesEmptyDictionary_NotNull()
        {
            var feature = new MvtFeature(); // Store defaults to the EmptyPropertyStore Null Object
            IFeature asFeature = feature;

            Assert.IsNotNull(asFeature.Properties,
                "Properties must never be null (the default EmptyPropertyStore exposes an empty dictionary).");
            Assert.AreEqual(0, asFeature.Properties.Count);
        }

        [Test]
        public void Properties_NonNullField_ExposesTheSameEntries()
        {
            IFeature feature = new DictionaryFeature(
                new Dictionary<string, Value> { ["CONTINENT"] = Value.String("Africa") });

            Assert.AreEqual(1, feature.Properties.Count);
            Assert.AreEqual(Value.String("Africa"), feature.Properties["CONTINENT"]);
        }

        [Test]
        public void GeometryType_PassesThroughUnchanged()
        {
            var feature = new MvtFeature { GeometryType = TileGeometryType.Polygon };
            IFeature asFeature = feature;

            Assert.AreEqual(TileGeometryType.Polygon, asFeature.GeometryType);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FeatureSelectorTests — source-layer resolution, $type filters, ordinals, the compile memo
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seam test: exercises FeatureSelector.SelectFeatures, verifying it routes through
    /// SourceLayerResolver and applies the $type filter correctly against real MvtFeature geometry.
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

        // ── The ordinal-returning overload ────────────────────────────────────────────────────────

        /// <summary>
        /// The ordinal is the feature's position in the <b>source layer</b>, never in the selection: per-feature
        /// side arrays and <c>RingFeatureIdx</c> key on the layer, so a slot ordinal mis-attributes colours,
        /// widths and symbols once a filter rejects anything. The only match sits at layer position 2 and
        /// selection position 0.
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

            // An unresolvable layer selects nothing. The cast picks the ITileLayer overload over its
            // IReadOnlyList<IFeature> sibling.
            FeatureSelector.SelectFeatures(layer, (ITileLayer)null, 0.0, selected);
            Assert.AreEqual(0, selected.Count, "a null tile layer must leave the output empty");
        }

        // ── Compiled-filter memo ──────────────────────────────────────────────────────────────────
        // ARRAY filters only: the shared null/true/false singletons pass a reference check with no memo.

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
            // StyleLayer.Filter is mutable: a memo keyed on the LAYER serves the stale compile here, while one
            // keyed on the node recompiles.
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // FeatureSelectorNativeFilterTests — native filter VM at the production FeatureSelector boundary
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The VM-wiring stage's acceptance teeth, at the PRODUCTION <see cref="FeatureSelector"/> boundary —
    /// as opposed to <c>NativeFilterVmTests</c>, whose oracle drives the VM directly. Covers: byte-identical
    /// parity across the three <see cref="ITileLayer"/> overloads, that the native seam is actually
    /// probed and dispatched to per-feature rather than silently skipped, and that both refusal gates
    /// — compile refusal and rebind refusal — correctly fall back to the managed path.
    /// </summary>
    [TestFixture]
    public class FeatureSelectorNativeFilterTests
    {
        private static MvtTile _tile;
        private static List<JsonValue> _coveredLibertyFilters;
        private static List<JsonValue> _parityFilters;

        [OneTimeSetUp]
        public void SetUp()
        {
            _tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            _coveredLibertyFilters = CollectCoveredLibertyFilters();
            // The liberty corpus selects all-or-nothing on this fixture, so add one partitioning filter for the
            // sweep's non-degeneracy guard.
            _parityFilters = new List<JsonValue>(_coveredLibertyFilters) { BuildDiscriminatingFilter() };
        }

        /// <summary><c>NAME == (first country's NAME)</c>, which partitions <c>countries</c> as
        /// <c>0 &lt; selected &lt; total</c>, so a stuck-constant native bug cannot pass parity vacuously. It
        /// uses the safe <c>get</c> == literal form, and it checks that it compiles and native-binds, so a
        /// fixture change fails here first.</summary>
        private static JsonValue BuildDiscriminatingFilter()
        {
            MvtLayer countries = _tile.GetLayer("countries");
            Assert.IsNotNull(countries, "precondition: fixture has a 'countries' layer");
            Assert.That(countries.Features.Count, Is.GreaterThan(1),
                "precondition: >1 country, so a NAME equality can select a proper non-empty subset");
            Assert.IsTrue(((IFeature)countries.Features[0]).TryGetProperty("NAME", out Value name0));
            Assert.That(name0.Type, Is.EqualTo(ValueType.String));

            JsonValue filter = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("=="),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("NAME") }),
                JsonValue.OfString(name0.AsString()),
            });

            // Self-check: it must be VM-compilable AND actually non-degenerate under managed over countries,
            // or it does not do its job.
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program),
                "precondition: the discriminating filter must be VM-compilable");
            // ...and it must native-BIND over countries: it is the sweep's only non-degenerate case, and a
            // refused Rebind would fall back to managed and disarm the native coverage.
            Assert.IsTrue(program.Rebind(countries.DenseKeyResolver, Allocator.TempJob, out NativeArray<int> probe),
                "precondition: the discriminating filter must native-bind over countries (unique NAME → one value id)");
            probe.Dispose();
            CompiledFilter managed = CompiledFilter.Compile(filter);
            int matched = 0;
            foreach (MvtFeature f in countries.Features)
                if (managed.Matches(f, 0.0)) matched++;
            Assert.That(matched, Is.GreaterThan(0).And.LessThan(countries.Features.Count),
                "precondition: the discriminating filter must select a proper non-empty subset of countries");
            return filter;
        }

        [OneTimeTearDown]
        public void TearDown() => _tile.Dispose();

        /// <summary>The same VM-compilable liberty.json filter corpus <c>NativeFilterVmTests</c> collects —
        /// a mix of <c>==</c>/<c>!=</c>, <c>!</c>, <c>all</c>, constant-key <c>get</c> and
        /// <c>geometry-type</c>, proven non-degenerate over this exact fixture by that suite's own
        /// <c>comparisons &gt; 10000</c> guard.</summary>
        private static List<JsonValue> CollectCoveredLibertyFilters()
        {
            var covered = new List<JsonValue>();
            foreach (StyleLayer layer in SymbolTestFixtures.LibertyDoc().Layers)
            {
                if (layer.Filter == null) continue;
                if (NativeFilterCompiler.TryCompile(layer.Filter, out _))
                    covered.Add(layer.Filter);
            }
            return covered;
        }

        private static StyleLayer MakeLayer(string sourceLayer, JsonValue filter) => new StyleLayer
        {
            Id = "t", Source = "s", SourceLayer = sourceLayer, Filter = filter,
        };

        /// <summary>The managed reference computation the parity and fallback tests compare the production
        /// selector against — independent of <see cref="FeatureSelector"/>'s own managed path (no key binding),
        /// so it cannot share a bug with either side of the dispatch it is checking.</summary>
        private static List<IFeature> ManagedSelect(JsonValue filterJson, MvtLayer layer)
        {
            CompiledFilter managed = CompiledFilter.Compile(filterJson);
            var result = new List<IFeature>();
            foreach (MvtFeature feature in layer.Features)
                if (managed.Matches(feature, 0.0))
                    result.Add(feature);
            return result;
        }

        // ── Production-boundary parity ─────────────────────────────────────────────────────

        [Test]
        public void SelectFeatures_ListOverload_MatchesManagedParity_ForEveryCoveredFilter_EveryLayer()
        {
            int comparisons = 0, nonDegenerate = 0;
            foreach (JsonValue filterJson in _parityFilters)
            {
                foreach (MvtLayer layer in _tile.Layers)
                {
                    if (layer.Features.Count == 0) continue;
                    List<IFeature> expected = ManagedSelect(filterJson, layer);

                    StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
                    IReadOnlyList<IFeature> actual = FeatureSelector.SelectFeatures(styleLayer, _tile, 0.0);

                    Assert.That(actual.Count, Is.EqualTo(expected.Count),
                        $"layer '{layer.Name}', filter {filterJson}: selected count differs");
                    for (int i = 0; i < expected.Count; i++)
                        Assert.AreSame(expected[i], actual[i],
                            $"layer '{layer.Name}', filter {filterJson}: feature[{i}] differs");

                    comparisons++;
                    if (expected.Count > 0 && expected.Count < layer.Features.Count) nonDegenerate++;
                }
            }

            Assert.That(comparisons, Is.GreaterThan(0), "precondition: the corpus must be non-empty");
            Assert.That(nonDegenerate, Is.GreaterThan(0),
                "guard: at least one (filter, layer) combination must select a non-trivial, non-degenerate " +
                "subset (0 < selected < total) — otherwise a collapsed all/none selection could pass vacuously");
        }

        [Test]
        public void SelectFeatures_OrdinalListOverload_MatchesManagedParity_ForEveryCoveredFilter_EveryLayer()
        {
            var into = new List<SelectedTileFeature>();
            int nonDegenerate = 0;
            foreach (JsonValue filterJson in _parityFilters)
            {
                foreach (MvtLayer layer in _tile.Layers)
                {
                    if (layer.Features.Count == 0) continue;
                    List<IFeature> expected = ManagedSelect(filterJson, layer);

                    StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
                    FeatureSelector.SelectFeatures(styleLayer, (ITileLayer)layer, 0.0, into);

                    Assert.That(into.Count, Is.EqualTo(expected.Count),
                        $"layer '{layer.Name}', filter {filterJson}: selected count differs");
                    for (int i = 0; i < expected.Count; i++)
                    {
                        Assert.AreSame(expected[i], into[i].Feature,
                            $"layer '{layer.Name}', filter {filterJson}: feature[{i}] differs");
                        int expectedOrdinal = layer.Features.IndexOf((MvtFeature)expected[i]);
                        Assert.AreEqual(expectedOrdinal, into[i].Ordinal,
                            $"layer '{layer.Name}', filter {filterJson}: ordinal[{i}] differs");
                    }

                    if (expected.Count > 0 && expected.Count < layer.Features.Count) nonDegenerate++;
                }
            }

            Assert.That(nonDegenerate, Is.GreaterThan(0),
                "guard: at least one (filter, layer) combination must be non-degenerate");
        }

        [Test]
        public void SelectFeatures_ScratchArrayOverload_MatchesManagedParity_ForEveryCoveredFilter_EveryLayer()
        {
            int nonDegenerate = 0;
            foreach (JsonValue filterJson in _parityFilters)
            {
                foreach (MvtLayer layer in _tile.Layers)
                {
                    if (layer.Features.Count == 0) continue;
                    List<IFeature> expected = ManagedSelect(filterJson, layer);

                    StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
                    var into = new SelectedTileFeature[layer.Features.Count];
                    int count = FeatureSelector.SelectFeatures(styleLayer, (ITileLayer)layer, 0.0, into);

                    Assert.That(count, Is.EqualTo(expected.Count),
                        $"layer '{layer.Name}', filter {filterJson}: selected count differs");
                    for (int i = 0; i < expected.Count; i++)
                        Assert.AreSame(expected[i], into[i].Feature,
                            $"layer '{layer.Name}', filter {filterJson}: feature[{i}] differs");

                    if (expected.Count > 0 && expected.Count < layer.Features.Count) nonDegenerate++;
                }
            }

            Assert.That(nonDegenerate, Is.GreaterThan(0),
                "guard: at least one (filter, layer) combination must be non-degenerate");
        }

        // ── Dispatch coverage (the vacuous-all-fallback guard) ─────────────────────────────

        /// <summary>Finds a covered filter that actually native-binds against <paramref name="layer"/> (not
        /// refused by the rebind's ≥2-id guard) — so the dispatch-coverage test's per-feature
        /// <c>Matches</c> count is meaningful.
        /// Disposes its own probe binding; returns null if none of the corpus binds.</summary>
        private static JsonValue FindNativeBindableFilter(MvtLayer layer)
        {
            foreach (JsonValue filterJson in _coveredLibertyFilters)
            {
                if (!NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program)) continue;
                if (!program.Rebind(layer.DenseKeyResolver, Allocator.TempJob, out NativeArray<int> binding))
                    continue;
                binding.Dispose();
                return filterJson;
            }
            return null;
        }

        private static MvtLayer FindNativeBindableLayer(out JsonValue filterJson)
        {
            foreach (MvtLayer layer in _tile.Layers)
            {
                if (layer.DenseKeyResolver == null || layer.Features.Count == 0) continue;
                JsonValue candidate = FindNativeBindableFilter(layer);
                if (candidate != null) { filterJson = candidate; return layer; }
            }
            filterJson = null;
            return null;
        }

        [Test]
        public void FeatureSelector_ProbesTheNativeSeam_AndBatchMatchesEveryFeature()
        {
            MvtLayer layer = FindNativeBindableLayer(out JsonValue filterJson);
            Assert.IsNotNull(layer, "precondition: at least one fixture layer must native-bind at least one covered filter");

            var spy = new NativeFilterSourceSpy(layer);
            StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
            var into = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(styleLayer, spy, 0.0, into);

            Assert.That(spy.TryBindNativeFilterCalls, Is.GreaterThanOrEqualTo(1),
                "FeatureSelector must probe the INativeFilterSource seam for a VM-compilable filter");
            Assert.That(spy.MatchAllCalls, Is.EqualTo(1),
                "the whole layer must be addressed through ONE batched MatchAll call, not per-feature dispatch");
        }

        // ── Fallback correctness (both refusal gates) ──────────────────────────────────────

        // Two-get comparison: the byte-identity argument only holds for get-vs-number-literal; a
        // get-vs-get shape could compare two strings, so it stays refused.
        [TestCase("[\"<\",[\"get\",\"a\"],[\"get\",\"b\"]]")]
        // Multi-arm match: outside the restricted single-arm membership shape match-widening accepts.
        [TestCase("[\"match\",[\"get\",\"class\"],\"a\",true,\"b\",false,true]")]
        public void NativeProgramFor_RefusesUnsupportedFilter_AndSelectionStaysManagedAndCorrect(string json)
        {
            JsonValue filterJson = JsonParser.Parse(json);
            Assert.IsNull(FeatureSelector.NativeProgramFor(filterJson), "must refuse to compile");

            MvtLayer layer = FirstNonEmptyLayer();
            List<IFeature> expected = ManagedSelect(filterJson, layer);

            StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
            IReadOnlyList<IFeature> actual = FeatureSelector.SelectFeatures(styleLayer, _tile, 0.0);

            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            for (int i = 0; i < expected.Count; i++)
                Assert.AreSame(expected[i], actual[i]);
        }

        private static MvtLayer FirstNonEmptyLayer()
        {
            foreach (MvtLayer l in _tile.Layers)
                if (l.Features.Count > 0) return l;
            Assert.Fail("precondition: fixture must have a non-empty layer");
            return null;
        }

        /// <summary>Direct unit tooth on the rebind refusal itself — a literal string that resolves to two
        /// or more ids in a layer's value table must refuse, never emit an unsound single-id compare. Built
        /// against a SYNTHETIC resolver with a deliberate duplicate (string dedup is an encoder property,
        /// not a decoder guarantee — the real fixture is not guaranteed to contain one).</summary>
        [Test]
        public void Rebind_RefusesWhenALiteralMatchesTwoOrMoreValueStrings()
        {
            Assert.IsTrue(NativeFilterCompiler.TryCompile(
                JsonParser.Parse("[\"==\",[\"get\",\"class\"],\"dup\"]"), out NativeFilterProgram program));

            var keys = new List<string> { "class" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0 };
            string[] valueStrings = { "dup", "dup" }; // deliberate duplicate
            var values = new NativeArray<MvtValueNative>(0, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(0, Allocator.Persistent);
            var tagOffsets = new NativeArray<int>(0, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(0, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

                bool ok = program.Rebind(resolver, Allocator.Persistent, out NativeArray<int> binding);
                Assert.IsFalse(ok,
                    "a literal matching 2+ value-strings must refuse — a single-id compare would be unsound");
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>The FeatureSelector-level half of the rebind-refusal case: when the seam itself refuses
        /// to bind (simulated here via the spy, standing in for a real rebind refusal), the seam is still
        /// probed but the selection falls back to the managed path and stays correct.</summary>
        [Test]
        public void FeatureSelector_FallsBackToManaged_WhenTheSeamRefusesToBind()
        {
            MvtLayer layer = FindNativeBindableLayer(out JsonValue filterJson);
            Assert.IsNotNull(layer, "precondition");

            List<IFeature> expected = ManagedSelect(filterJson, layer);

            var spy = new NativeFilterSourceSpy(layer, forceRefuse: true);
            StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
            var into = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(styleLayer, spy, 0.0, into);

            Assert.That(spy.TryBindNativeFilterCalls, Is.GreaterThanOrEqualTo(1), "the seam must still be probed");
            Assert.That(into.Count, Is.EqualTo(expected.Count));
            for (int i = 0; i < expected.Count; i++)
                Assert.AreSame(expected[i], into[i].Feature);
        }

        // ── Test-assembly spy decorator (dispatch coverage and rebind refusal) ───────────────────────────

        /// <summary>A test decorator around a real <see cref="MvtLayer"/> that forwards every call and counts
        /// <see cref="TryBindNativeFilterCalls"/> and the matcher's <see cref="MatchAllCalls"/>; a test-only
        /// counter may not live on <see cref="MvtLayer"/>. <paramref name="forceRefuse"/> still counts the probe
        /// but answers null, in place of a real rebind refusal.</summary>
        private sealed class NativeFilterSourceSpy : ITileLayer, IIndexedFeatureSource, INativeFilterSource
        {
            private readonly MvtLayer _inner;
            private readonly bool _forceRefuse;
            internal int TryBindNativeFilterCalls;
            internal int MatchAllCalls;

            internal NativeFilterSourceSpy(MvtLayer inner, bool forceRefuse = false)
            {
                _inner = inner;
                _forceRefuse = forceRefuse;
            }

            string ITileLayer.Name => ((ITileLayer)_inner).Name;
            uint ITileLayer.Extent => ((ITileLayer)_inner).Extent;
            IReadOnlyList<IFeature> ITileLayer.Features => ((ITileLayer)_inner).Features;
            TileGeometryBuffers ITileLayer.Geometry => ((ITileLayer)_inner).Geometry;

            IFeatureKeyResolver IIndexedFeatureSource.KeyResolver => ((IIndexedFeatureSource)_inner).KeyResolver;

            INativeFeatureMatcher INativeFilterSource.TryBindNativeFilter(NativeFilterProgram program)
            {
                TryBindNativeFilterCalls++;
                if (_forceRefuse) return null;
                INativeFeatureMatcher inner = ((INativeFilterSource)_inner).TryBindNativeFilter(program);
                return inner == null ? null : new MatcherSpy(this, inner);
            }

            private sealed class MatcherSpy : INativeFeatureMatcher
            {
                private readonly NativeFilterSourceSpy _owner;
                private readonly INativeFeatureMatcher _inner;

                internal MatcherSpy(NativeFilterSourceSpy owner, INativeFeatureMatcher inner)
                {
                    _owner = owner;
                    _inner = inner;
                }

                public NativeArray<byte> MatchAll()
                {
                    _owner.MatchAllCalls++;
                    return _inner.MatchAll();
                }

                public void Dispose() => _inner.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // NativeFilterVmTests — the VM's own parity oracle, hand-built teeth, and error model
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The native filter-VM prototype's acceptance teeth: a byte-identical parity oracle against every
    /// liberty filter the VM's op subset covers, zero-managed-lookup on the VM entry, refusal correctness,
    /// and the synthetic error-model tooth the liberty corpus can't exercise on its own (it never triggers
    /// a VM error — see <see cref="All_NonBooleanArg_ExcludesViaNonBooleanError_AndManagedAlsoExcludes"/>).
    /// </summary>
    [TestFixture]
    public class NativeFilterVmTests
    {
        private static MvtTile _tile;

        /// <summary>A second real fixture (croatia-dalmatia). <c>sample-tile.bytes</c>'s only layer carries none
        /// of liberty's match keys (<c>class</c>/<c>brunnel</c>), so it never runs <c>InStringSet</c>'s
        /// membership SCAN; this tile has the most <c>transportation</c> features, 3 with <c>brunnel</c>.</summary>
        private static MvtTile _croatiaTile;

        private static List<JsonValue> _coveredLibertyFilters;
        private static List<string> _refusedLibertyLayerIds;
        private static int _totalFilteredLibertyLayers;

        [OneTimeSetUp]
        public void SetUp()
        {
            _tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            _croatiaTile = MvtDecoder.Decode(new TileId { Z = 9, X = 279, Y = 187 }, File.ReadAllBytes(
                Path.Combine(Application.dataPath, "Fixtures", "water-real-croatia-dalmatia-9-279-187.pbf.bytes")));
            _coveredLibertyFilters = CollectCoveredLibertyFilters();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _tile.Dispose();
            _croatiaTile.Dispose();
        }

        /// <summary>True iff <paramref name="node"/> is exactly <c>["get", <paramref name="key"/>]</c>.</summary>
        private static bool IsGetKey(JsonValue node, string key)
        {
            if (node == null || !node.IsArray) return false;
            IReadOnlyList<JsonValue> items = node.Items;
            return items.Count >= 2 && items[0].AsString() == "get" && items[1].AsString() == key;
        }

        /// <summary>True iff <paramref name="node"/> contains a <c>["match", ["get", <paramref name="key"/>],
        /// ...]</c> sub-tree. Only a <c>match</c> input compiles to <c>InStringSet</c>; an <c>==</c>/<c>!=</c>
        /// on the key does not scan, so key presence alone would over-claim that the scan ran.</summary>
        private static bool FilterUsesGetKeyViaMatch(JsonValue node, string key)
        {
            if (node == null || !node.IsArray) return false;
            IReadOnlyList<JsonValue> items = node.Items;
            if (items.Count >= 2 && items[0].AsString() == "match" && IsGetKey(items[1], key))
                return true;
            foreach (JsonValue item in items)
                if (FilterUsesGetKeyViaMatch(item, key)) return true;
            return false;
        }

        /// <summary>Compiles every filtered liberty.json layer. Returns the covered filters, and records the
        /// refused layer ids in <see cref="_refusedLibertyLayerIds"/> and the total in
        /// <see cref="_totalFilteredLibertyLayers"/>. The refused set is kept by identity, because a count
        /// cannot tell one filter gained and another lost.</summary>
        private static List<JsonValue> CollectCoveredLibertyFilters()
        {
            var covered = new List<JsonValue>();
            var refused = new List<string>();
            int total = 0;
            foreach (StyleLayer layer in SymbolTestFixtures.LibertyDoc().Layers)
            {
                if (layer.Filter == null) continue;
                total++;
                if (NativeFilterCompiler.TryCompile(layer.Filter, out _))
                    covered.Add(layer.Filter);
                else
                    refused.Add(layer.Id);
            }
            _refusedLibertyLayerIds = refused;
            _totalFilteredLibertyLayers = total;
            return covered;
        }

        // ── coverage pin ────────────────────────────────────────────────────────────────────────

        /// <summary>All 105 of liberty.json's filtered layers compile through the accepted op subset. The
        /// 10+-label <c>road_link</c> matches fit the VM's op budget only because <c>InStringSet</c> compiles
        /// them in O(1) ops whatever the label count. The refused set must be empty, which a count alone
        /// cannot show.</summary>
        [Test]
        public void CoveredLibertyFilters_Count_Is105()
        {
            Assert.That(_totalFilteredLibertyLayers, Is.EqualTo(105), "precondition: liberty.json's filtered layer count");
            Assert.That(_refusedLibertyLayerIds, Is.Empty,
                "refused layer(s): " + string.Join(", ", _refusedLibertyLayerIds));
            Assert.That(_coveredLibertyFilters.Count, Is.EqualTo(105));
        }

        // ── Parity oracle ─────────────────────────────────────────────────────────────

        /// <summary>
        /// For every covered liberty filter and every feature of every layer of BOTH fixture tiles, the native
        /// VM's decision must equal managed <see cref="CompiledFilter.Matches"/>. Filters are pure functions of
        /// the feature, so crossing layers widens coverage. Separate per-KEY floors for <c>class</c> and
        /// <c>brunnel</c> prove <c>InStringSet</c>'s scan ran; <c>ramp</c> has none, because every liberty
        /// filter on it is numeric equality, never <c>match</c>.
        /// </summary>
        /// <remarks>
        /// Limitation: croatia's <c>brunnel</c> is on <c>transportation</c> only, so 4 of liberty's 62
        /// <c>brunnel</c> filters (<c>waterway</c>/<c>water</c>) stay uncovered. Its 3 <c>brunnel</c> features
        /// are all <c>tunnel</c>, so the match-true "bridge" arm is never taken.
        /// </remarks>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForEveryCoveredFilter_EveryFeature_EveryLayer()
        {
            int comparisons = 0;
            int classNonNull = 0, brunnelNonNull = 0;
            MvtTile[] tiles = { _tile, _croatiaTile }; // hoisted out of the per-filter loop (NIT, review)

            foreach (JsonValue filterJson in _coveredLibertyFilters)
            {
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                    "a filter collected as covered must still compile");
                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                bool usesClass = FilterUsesGetKeyViaMatch(filterJson, "class");
                bool usesBrunnel = FilterUsesGetKeyViaMatch(filterJson, "brunnel");

                foreach (MvtTile tile in tiles)
                foreach (MvtLayer layer in tile.Layers)
                {
                    if (layer.DenseKeyResolver == null || layer.Features.Count == 0) continue;
                    // Through the production seam (INativeFilterSource), exactly as FeatureSelector binds it
                    // — the ≥2-id literal-string rebind refusal is a null return here too.
                    INativeFeatureMatcher matcher = ((INativeFilterSource)layer).TryBindNativeFilter(program);
                    if (matcher == null) continue; // duplicate value-string in this layer: legitimately stays managed

                    try
                    {
                        NativeArray<byte> results = matcher.MatchAll();
                        for (int fi = 0; fi < layer.Features.Count; fi++)
                        {
                            bool nativeMatched = results[fi] != 0;
                            IFeature feature = layer.Features[fi];
                            bool managedMatched = managed.Matches(feature, 0.0);
                            comparisons++;
                            Assert.That(nativeMatched, Is.EqualTo(managedMatched),
                                $"layer '{layer.Name}' feature[{fi}]: native/managed disagree for {filterJson}");

                            // Counts a pair that drives InStringSet's scan: the get resolves to a real String.
                            if (usesClass && feature.TryGetProperty("class", out Value cv) && cv.Type == ValueType.String) classNonNull++;
                            if (usesBrunnel && feature.TryGetProperty("brunnel", out Value bv) && bv.Type == ValueType.String) brunnelNonNull++;
                        }
                    }
                    finally
                    {
                        matcher.Dispose();
                    }
                }
            }

            // Floor rule, as for the per-key floors below: above half the corpus count (133350), so a halved
            // count fails, and below it, so ordinary variation does not.
            Assert.That(comparisons, Is.GreaterThan(90000),
                "precondition: the oracle must exercise many (filter, layer, feature) combinations — " +
                "too few and a shallow/broken VM could pass this test vacuously (measured 133350)");

            // Per-key floors, never summed (`class` alone would satisfy a sum), gated on match-form use. The
            // corpus gives class=27864, brunnel=57; each floor sits between half and the full count.
            Assert.That(classNonNull, Is.GreaterThan(20000),
                "class: InStringSet's membership scan must run densely via match forms (measured 27864)");
            Assert.That(brunnelNonNull, Is.GreaterThan(40),
                "brunnel: InStringSet's membership scan must run via match forms for the croatia " +
                "transportation features carrying this key (measured 57)");
        }

        // ── Explicit match parity (membership + negated) ───────────────────────────────

        /// <summary>
        /// Hand-built <c>match</c> parity, membership and negated, over 'countries'. The corpus's match filters
        /// key on properties this layer lacks, so this test is what runs a real String <c>get</c> against real
        /// labels. Each case must select a proper non-empty subset.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltMatch_MembershipAndNegated()
        {
            MvtLayer countries = _tile.GetLayer("countries");
            Assert.IsNotNull(countries, "precondition: fixture has a 'countries' layer");

            var names = new List<string>();
            foreach (MvtFeature f in countries.Features)
            {
                if (((IFeature)f).TryGetProperty("NAME", out Value v) && v.Type == ValueType.String
                    && !names.Contains(v.AsString()))
                {
                    names.Add(v.AsString());
                    if (names.Count == 2) break;
                }
            }
            Assert.That(names.Count, Is.EqualTo(2), "precondition: fixture 'countries' needs 2+ distinct NAMEs");

            AssertMatchParity(BuildMatchFilter("NAME", names, member: true), countries);
            AssertMatchParity(BuildMatchFilter("NAME", names, member: false), countries);

            // A scalar label runs TryEmitMatch's non-array normalization, which the liberty corpus never hits:
            // all its match nodes carry array labels.
            AssertMatchParity(BuildScalarMatchFilter("NAME", names[0], member: true), countries);
        }

        /// <summary>Builds <c>["match",["get",key],[labels...],output,default]</c> with complementary
        /// boolean outputs — <paramref name="member"/> selects the membership (labels → true) or negated
        /// (labels → false) form.</summary>
        private static JsonValue BuildMatchFilter(string key, IReadOnlyList<string> labels, bool member)
        {
            var labelItems = new List<JsonValue>();
            foreach (string label in labels) labelItems.Add(JsonValue.OfString(label));
            return JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
                JsonValue.OfArray(labelItems),
                JsonValue.OfBool(member),
                JsonValue.OfBool(!member),
            });
        }

        /// <summary>Builds <c>["match",["get",key],label,output,default]</c> with a SCALAR (non-array)
        /// label — the shape whose single-label branch <see cref="BuildMatchFilter"/>'s array form does not
        /// cover.</summary>
        private static JsonValue BuildScalarMatchFilter(string key, string label, bool member)
            => JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
                JsonValue.OfString(label),
                JsonValue.OfBool(member),
                JsonValue.OfBool(!member),
            });

        // ── Explicit `has` parity ───────────────────────────────────────────────────

        /// <summary>
        /// Hand-built <c>["has","class"]</c> over a synthetic 2-feature layer where <c>class</c> is present
        /// on feature 0 and absent on feature 1 — a proper-subset presence shape the real fixture cannot
        /// supply (every property in the sample tile is present on ALL or NONE of a layer's features, by
        /// inspection). VM must agree with managed <c>FeatureKeyExpression(isHas:true)</c> on both.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltHas()
        {
            MvtLayer layer = BuildHasProperSubsetLayer(
                out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
                out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths);
            try
            {
                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("has"),
                    JsonValue.OfString("class"),
                });
                AssertMatchParity(filter, layer);
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>
        /// <c>["has","class"]</c> where feature 0's <c>class</c> tag points at a value that decoded to
        /// <see cref="MvtValueNative.Null"/> (an empty Value sub-message a non-conformant tile can carry).
        /// Managed <c>has</c> reports the key PRESENT, and the VM must agree: presence is tag existence, not
        /// <c>value.Type != Null</c>. RED against a VM that tests <c>ReadTag(k).Type != Null</c>.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltHas_PresentButNullValue()
        {
            var keys = new List<string> { "class", "other" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0, ["other"] = 1 };
            string[] valueStrings = System.Array.Empty<string>();
            // The single value in the table is Null-typed — the present-but-Null case.
            var values = new NativeArray<MvtValueNative>(new[] { MvtValueNative.Null }, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0, 1, 0 }, Allocator.Persistent); // f0:(class,val0=Null); f1:(other,val0)
            var tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 2, 2 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_has_null" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                // has(class): PRESENT on feature 0 (tag exists, value is Null) — managed AND native must
                // select exactly feature 0 (proper subset), not exclude it.
                AssertMatchParity(JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("has"), JsonValue.OfString("class"),
                }), layer);
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>Builds a 2-feature layer: feature 0 has a <c>class</c> tag, and feature 1 has a tag for the
        /// DIFFERENT key <c>other</c>, not an empty slice, so the absent case runs <c>ReadTag</c>'s backward
        /// scan as a real MVT does. The out parameters are the backing native arrays; the caller disposes
        /// them.</summary>
        private static MvtLayer BuildHasProperSubsetLayer(
            out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
            out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths)
        {
            var keys = new List<string> { "class", "other" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0, ["other"] = 1 };
            string[] valueStrings = { "residential" };
            values = new NativeArray<MvtValueNative>(new[] { MvtValueNative.String(0) }, Allocator.Persistent);
            tagWords = new NativeArray<uint>(new uint[] { 0, 0, 1, 0 }, Allocator.Persistent); // f0: (class,val0); f1: (other,val0)
            tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            tagLengths = new NativeArray<int>(new[] { 2, 2 }, Allocator.Persistent); // one pair each

            var resolver = new MvtLayerPropertyResolver(
                keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

            var layer = new MvtLayer { Name = "synthetic_has" };
            layer.Features.Add(new MvtFeature
                { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
            layer.Features.Add(new MvtFeature
                { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
            layer.DenseKeyResolver = resolver;
            return layer;
        }

        // ── Many-label InStringSet evaluation: real membership scan + absent-label sentinel ──

        /// <summary>
        /// A 15-label <c>match</c> (see <see cref="TryCompile_AcceptsManyLabelMatch_ViaInStringSet"/>) over a
        /// synthetic layer whose <c>class</c> values run the scan. It matches at the first, a middle and the
        /// last label, and misses every label while 5 labels are absent from the layer and rebind to -1
        /// (<see cref="NativeFilterRebind.Rebind"/>): the case <c>InStringSet</c>'s byte identity leans on
        /// most.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltManyLabelMatch_MembershipAndNegated()
        {
            MvtLayer layer = BuildManyLabelInStringSetLayer(
                out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
                out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths);
            try
            {
                // Interleave present and absent labels so slot 0 and the LAST slot are live, matched labels; a
                // trailing run of absent (-1) labels would leave a truncated scan unpinned.
                var labels = new List<string>
                {
                    "class0",                                                             // slot 0  — feature 0
                    "classA", "classB", "classC", "classD", "classE",                     // absent (-1)
                    "class5",                                                             // slot 6  — feature 1
                    "classF", "classG", "classH", "classI", "classJ", "classK", "classL", // absent (-1)
                    "class9",                                                             // slot 14 — feature 2 (LAST)
                };

                AssertMatchParity(BuildMatchFilter("class", labels, member: true), layer);
                AssertMatchParity(BuildMatchFilter("class", labels, member: false), layer);
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>Builds a 4-feature layer, key <c>class</c>, whose value-strings are <c>class0</c>..
        /// <c>class9</c> plus <c>other_value</c> (11 entries, ids 0..10) — features carry <c>class</c> =
        /// the first label (id 0), a mid-list label (id 5), the last label (id 9), and a value outside the
        /// label set entirely (id 10), a proper non-empty subset for the many-label InStringSet tooth above. Out
        /// parameters are the backing native arrays; caller disposes them.</summary>
        private static MvtLayer BuildManyLabelInStringSetLayer(
            out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
            out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths)
        {
            var keys = new List<string> { "class" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0 };
            var valueStrings = new string[11];
            var valueList = new List<MvtValueNative>();
            for (int i = 0; i < 10; i++) { valueStrings[i] = "class" + i; valueList.Add(MvtValueNative.String(i)); }
            valueStrings[10] = "other_value";
            valueList.Add(MvtValueNative.String(10));
            values = new NativeArray<MvtValueNative>(valueList.ToArray(), Allocator.Persistent);

            // f0: class0 (first label); f1: class5 (mid list); f2: class9 (last label); f3: other_value
            // (outside the label set) — one (class,val) tag pair each.
            tagWords = new NativeArray<uint>(
                new uint[] { 0, 0, 0, 5, 0, 9, 0, 10 }, Allocator.Persistent);
            tagOffsets = new NativeArray<int>(new[] { 0, 2, 4, 6 }, Allocator.Persistent);
            tagLengths = new NativeArray<int>(new[] { 2, 2, 2, 2 }, Allocator.Persistent);

            var resolver = new MvtLayerPropertyResolver(
                keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

            var layer = new MvtLayer { Name = "synthetic_many_label_in_string_set" };
            for (int fi = 0; fi < 4; fi++)
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, fi) });
            layer.DenseKeyResolver = resolver;
            return layer;
        }

        // ── Explicit numeric-compare parity, both directions ───────────────────────────

        /// <summary>Hand-built <c>["&lt;",["get",key],N]</c> and <c>["&gt;",N,["get",key]]</c> (operand
        /// order reversed) over a fixture numeric property spanning <c>N</c> for a proper subset of
        /// features — VM must agree with managed <c>DecisionOps.CompareValues</c> in both operand
        /// orders.</summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltCompare_BothOperandOrders()
        {
            (MvtLayer layer, string key, double threshold) = FindNumericKeySpanningThreshold();
            Assert.IsNotNull(layer,
                "precondition: fixture must have a numeric property spanning some threshold on some layer");

            JsonValue getVsLiteral = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("<"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
                JsonValue.OfNumber(threshold),
            });
            AssertMatchParity(getVsLiteral, layer);

            JsonValue literalVsGet = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString(">"),
                JsonValue.OfNumber(threshold),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
            });
            AssertMatchParity(literalVsGet, layer);
        }

        /// <summary>Scans every fixture layer's features for a Number-typed property whose values span more
        /// than one distinct value, and returns its midpoint as a threshold guaranteed to split them into a
        /// proper non-empty subset on each side.</summary>
        private static (MvtLayer, string, double) FindNumericKeySpanningThreshold()
        {
            foreach (MvtLayer layer in _tile.Layers)
            {
                if (layer.DenseKeyResolver == null || layer.Features.Count < 2) continue;
                var keys = new HashSet<string>();
                foreach (MvtFeature f in layer.Features)
                    foreach (string k in ((IFeature)f).Properties.Keys)
                        keys.Add(k);

                foreach (string key in keys)
                {
                    var numbers = new List<double>();
                    foreach (MvtFeature f in layer.Features)
                        if (((IFeature)f).TryGetProperty(key, out Value v) && v.Type == ValueType.Number)
                            numbers.Add(v.AsNumber());
                    if (numbers.Count < 2) continue;

                    double min = numbers[0], max = numbers[0];
                    foreach (double n in numbers)
                    {
                        if (n < min) min = n;
                        if (n > max) max = n;
                    }
                    if (max <= min) continue;
                    return (layer, key, (min + max) / 2.0);
                }
            }
            return (null, null, 0.0);
        }

        // ── Type-mismatch compare excludes on both sides ───────────────────────────────

        /// <summary>
        /// <c>["&lt;",["get","NAME"],5]</c> over a "countries" feature: <c>NAME</c> is a STRING, so managed
        /// <c>CompareValues</c>'s type gate throws → <c>CompiledFilter.Matches</c> catches → excludes. The
        /// VM must reach the same exclude via <see cref="NativeFilterError.NonComparable"/>, mirroring
        /// <see cref="All_NonBooleanArg_ExcludesViaNonBooleanError_AndManagedAlsoExcludes"/>'s pattern for
        /// the compare op instead of <c>all</c>.
        /// </summary>
        [Test]
        public void Compare_TypeMismatch_ExcludesOnBothSides_ViaNonComparableError()
        {
            MvtLayer countries = _tile.GetLayer("countries");
            Assert.IsNotNull(countries, "precondition: fixture has a 'countries' layer");

            JsonValue filterJson = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("<"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("NAME") }),
                JsonValue.OfNumber(5),
            });
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program));

            IFeature probe = countries.Features[0];
            Assert.IsTrue(probe.TryGetProperty("NAME", out Value nameValue));
            Assert.That(nameValue.Type, Is.EqualTo(ValueType.String),
                "precondition: fixture's first countries feature must have a STRING NAME");

            var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)countries).TryBindNativeFilter(program);
            Assert.IsNotNull(matcher, "precondition: must native-bind over countries");
            try
            {
                NativeArray<byte> results = matcher.MatchAll();
                bool nativeMatched = results[0] != 0;
                NativeFilterError error = (NativeFilterError)matcher.Errors[0];

                Assert.That(error, Is.EqualTo(NativeFilterError.NonComparable));
                Assert.IsFalse(nativeMatched);

                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                Assert.IsFalse(managed.Matches(countries.Features[0], 0.0),
                    "managed side must also exclude via the CompareValues type-mismatch throw");
            }
            finally
            {
                matcher.Dispose();
            }
        }

        /// <summary>Compiles, native-binds, and per-feature-compares <paramref name="filterJson"/> against
        /// managed <see cref="CompiledFilter"/> over <paramref name="layer"/>; fails loudly (rather than
        /// vacuously) if the filter doesn't compile, doesn't bind, or selects an all/none subset.</summary>
        private static void AssertMatchParity(JsonValue filterJson, MvtLayer layer)
        {
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                $"must compile: {filterJson}");
            INativeFeatureMatcher matcher = ((INativeFilterSource)layer).TryBindNativeFilter(program);
            Assert.IsNotNull(matcher, $"must native-bind over '{layer.Name}': {filterJson}");
            try
            {
                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                NativeArray<byte> results = matcher.MatchAll();
                int selected = 0;
                for (int fi = 0; fi < layer.Features.Count; fi++)
                {
                    bool nativeMatched = results[fi] != 0;
                    bool managedMatched = managed.Matches(layer.Features[fi], 0.0);
                    Assert.That(nativeMatched, Is.EqualTo(managedMatched),
                        $"layer '{layer.Name}' feature[{fi}]: native/managed disagree for {filterJson}");
                    if (nativeMatched) selected++;
                }
                Assert.That(selected, Is.GreaterThan(0).And.LessThan(layer.Features.Count),
                    $"precondition: {filterJson} must select a proper non-empty subset of '{layer.Name}'");
            }
            finally
            {
                matcher.Dispose();
            }
        }

        // ── No managed property-store lookup on the VM entry (structural) ───────────────

        /// <summary>Structural: the Burst job's field surface is entirely blittable. With the evaluator reading
        /// its inputs through <c>INativeFilterColumns</c>, not an <c>IMvtPropertyStore</c>, this means the VM
        /// entry does no managed property lookup.</summary>
        [Test]
        public void NativeFilterEvaluationJob_FieldSurface_IsEntirelyBlittable()
        {
            var fields = typeof(NativeFilterEvaluationJob).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (var f in fields)
                Assert.That(f.FieldType, Is.Not.EqualTo(typeof(string)).And.Not.EqualTo(typeof(object)),
                    $"field '{f.Name}' must stay Burst-blittable");
        }

        // ── Refusal correctness ──────────────────────────────────────────────────────

        [Test]
        public void TryCompile_AcceptsAllCoveredLibertyFilters()
        {
            foreach (JsonValue filterJson in _coveredLibertyFilters)
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out _), filterJson.ToString());
        }

        // ── A many-label match compiles via InStringSet (road_link regression) ────────────────

        /// <summary>
        /// A synthetic <c>match</c> with 15 string labels — the shape that overflowed the VM's bounded
        /// op-count under the old <c>!all(input != a, …)</c> desugaring (3 ops per label + 1 <c>AllStep</c>
        /// per label, which liberty's real <c>road_link</c>/<c>road_link_casing</c> hit at 10+ labels). The
        /// compact <c>InStringSet</c> path emits <c>Get; InStringSet; Not</c> regardless of label
        /// count, so this must now compile.
        /// </summary>
        [Test]
        public void TryCompile_AcceptsManyLabelMatch_ViaInStringSet()
        {
            var labels = new List<JsonValue>();
            for (int i = 0; i < 15; i++) labels.Add(JsonValue.OfString("label" + i));
            JsonValue filterJson = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("class") }),
                JsonValue.OfArray(labels),
                JsonValue.OfBool(true),
                JsonValue.OfBool(false),
            });
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                "a 15-label match must compile via the compact InStringSet path");
            Assert.IsNotNull(program);
        }

        /// <summary>
        /// A match with more labels than the literal cap refuses. InStringSet emits O(1) ops regardless of label
        /// count, so <c>MaxOperations</c> no longer bounds a match's label list — the explicit literal cap does, and
        /// this pins it (the per-feature InStringSet loop, the Rebind scan, and the binding allocation must all stay
        /// bounded — "always bound loops"). Uses only a handful of ops, so <c>MaxOperations</c> is NOT what refuses
        /// it: RED-verify by removing the <c>MaxLiterals</c> guard and this compiles.
        /// </summary>
        [Test]
        public void TryCompile_RefusesMatch_WithMoreLabelsThanTheLiteralCap()
        {
            var labels = new List<JsonValue>();
            for (int i = 0; i < 257; i++) labels.Add(JsonValue.OfString("label" + i)); // > MaxLiterals (256)
            JsonValue filterJson = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("class") }),
                JsonValue.OfArray(labels),
                JsonValue.OfBool(true),
                JsonValue.OfBool(false),
            });
            Assert.IsFalse(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                "a match with more labels than the literal cap must refuse (bounds the InStringSet loop / Rebind / binding)");
            Assert.IsNull(program);
        }

        // Multi-arm match: two (label, output) pairs — the restricted single-arm shape requires exactly
        // 5 items ["match", input, label, output, default].
        [TestCase("[\"match\",[\"get\",\"class\"],\"a\",true,\"b\",false,true]", TestName = "Refuses_Match_MultiArm")]
        // Non-boolean output/default: match-widening covers only the boolean-output membership shape.
        [TestCase("[\"match\",[\"get\",\"class\"],[\"x\"],1,0]", TestName = "Refuses_Match_NonBooleanOutput")]
        // Non-complementary booleans: output and default must be exact opposites for the desugar to be
        // a pure membership test.
        [TestCase("[\"match\",[\"get\",\"class\"],[\"x\"],true,true]", TestName = "Refuses_Match_NonComplementaryBooleans")]
        // Computed input: only "get"/"geometry-type" inputs are accepted.
        [TestCase("[\"match\",[\"+\",1,1],[\"x\"],true,false]", TestName = "Refuses_Match_ComputedInput")]
        [TestCase("[\"in\",[\"get\",\"class\"],[\"literal\",[\"a\",\"b\"]]]", TestName = "Refuses_In")]
        [TestCase("[\"has\",[\"get\",\"x\"]]", TestName = "Refuses_Has_DynamicKey")]
        [TestCase("[\"has\",\"a\",\"b\"]", TestName = "Refuses_Has_TwoArg")]
        [TestCase("[\"<\",[\"get\",\"a\"],[\"get\",\"b\"]]", TestName = "Refuses_LessThan_TwoGet")]
        [TestCase("[\"<\",[\"get\",\"a\"],\"str\"]", TestName = "Refuses_LessThan_GetVsString")]
        // Literal-vs-literal is NESTED under "!": a bare root ["<",1,2] is legacy dialect, and
        // LegacyFilterTranslator refuses it before TryEmitCompare's gate.
        [TestCase("[\"!\",[\"<\",1,2]]", TestName = "Refuses_LessThan_LiteralVsLiteral")]
        [TestCase("[\"get\",\"class\"]", TestName = "Refuses_NonBooleanRoot")]
        [TestCase("[\"==\",[\"geometry-type\"],[\"get\",\"x\"]]", TestName = "Refuses_GeometryTypeAgainstDynamicKey")]
        // Unsafe id-vs-byte shapes: literal==literal (both rebind to -1, so the VM says equal) and get==get
        // (duplicate strings at distinct ids). Nested under "!", because legacy translation rewrites a bare root.
        [TestCase("[\"!\",[\"==\",\"foo\",\"bar\"]]", TestName = "Refuses_NestedLiteralEqualsLiteral")]
        [TestCase("[\"==\",[\"get\",\"a\"],[\"get\",\"b\"]]", TestName = "Refuses_GetEqualsGet")]
        public void TryCompile_RefusesUnsupportedFilters(string json)
        {
            Assert.IsFalse(NativeFilterCompiler.TryCompile(JsonParser.Parse(json), out NativeFilterProgram program));
            Assert.IsNull(program);
        }

        // ── Error model (synthetic; the liberty corpus never errors) ─────────────────

        /// <summary>
        /// <c>["all",["get","NAME"]]</c> over a "countries" feature: <c>NAME</c> is a non-empty string, so
        /// managed <c>all</c>'s <c>AsBool()</c> throws → <c>CompiledFilter.Matches</c> catches → excludes.
        /// The VM must reach the same exclude via <see cref="NativeFilterError.NonBoolean"/>, not silently
        /// — this is the tooth nothing else in the suite exercises (the liberty corpus never errors).
        /// </summary>
        [Test]
        public void All_NonBooleanArg_ExcludesViaNonBooleanError_AndManagedAlsoExcludes()
        {
            JsonValue filterJson = JsonParser.Parse("[\"all\",[\"get\",\"NAME\"]]");
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program));

            MvtLayer layer = _tile.GetLayer("countries");
            IFeature probe = layer.Features[0];
            Assert.IsTrue(probe.TryGetProperty("NAME", out Value nameValue));
            Assert.That(nameValue.Type, Is.EqualTo(ValueType.String));
            Assert.That(nameValue.AsString().Length, Is.GreaterThan(0),
                "precondition: fixture's first countries feature must have a non-empty NAME");

            var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
            Assert.IsNotNull(matcher, "precondition: must native-bind over countries");
            try
            {
                NativeArray<byte> results = matcher.MatchAll();
                bool nativeMatched = results[0] != 0;
                NativeFilterError error = (NativeFilterError)matcher.Errors[0];

                Assert.That(error, Is.EqualTo(NativeFilterError.NonBoolean));
                Assert.IsFalse(nativeMatched);

                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                Assert.IsFalse(managed.Matches(layer.Features[0], 0.0),
                    "managed side must also exclude via the AsBool error");
            }
            finally
            {
                matcher.Dispose();
            }
        }

        // ── Batched Execute: error independence across features (synthetic, mandatory) ──

        /// <summary>
        /// One feature's error must not bleed into the next in a batched <c>Execute</c>; the liberty corpus
        /// never errors, so only this test sees it. Feature 0 forces <see cref="NativeFilterError.NonComparable"/>
        /// and feature 1 must match. A per-feature error code that is not reset halts feature 1 at once.
        /// </summary>
        [Test]
        public void MatchAll_ErrorOnOneFeature_DoesNotExcludeTheNext()
        {
            var keys = new List<string> { "key" };
            var keyIndex = new Dictionary<string, int> { ["key"] = 0 };
            string[] valueStrings = { "notANumber" };
            // val0 = String("notANumber") — feature 0's compare errors; val1 = Number(2.0) — feature 1's
            // compare (< 5) genuinely matches.
            var values = new NativeArray<MvtValueNative>(
                new[] { MvtValueNative.String(0), MvtValueNative.Number(2.0) }, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0, 0, 1 }, Allocator.Persistent); // f0:(key,val0); f1:(key,val1)
            var tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 2, 2 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_error_independence" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("<"),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("key") }),
                    JsonValue.OfNumber(5),
                });
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program));

                var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
                Assert.IsNotNull(matcher, "precondition: must native-bind");
                try
                {
                    NativeArray<byte> results = matcher.MatchAll();
                    Assert.That((NativeFilterError)matcher.Errors[0], Is.EqualTo(NativeFilterError.NonComparable),
                        "precondition: feature 0 must error (String compared with <)");
                    Assert.That(results[1], Is.Not.EqualTo((byte)0),
                        "feature 1 must be INCLUDED — feature 0's error must not bleed into feature 1's evaluation");
                    Assert.That((NativeFilterError)matcher.Errors[1], Is.EqualTo(NativeFilterError.None));
                }
                finally
                {
                    matcher.Dispose();
                }
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        // ── Batched Execute: operand stack re-initialises per feature (synthetic) ──────

        /// <summary>
        /// <c>["all",["get","a"],["get","b"]]</c>: feature 0's <c>a</c> is <c>false</c>, so <c>all</c>
        /// short-circuits and leaves an element on the stack. Feature 1 must match. A stack that is not reset
        /// per feature fails the end check (<c>stack.Length == 1</c>) and reports
        /// <see cref="NativeFilterError.StackOverflow"/>.
        /// </summary>
        [Test]
        public void MatchAll_StackResidue_DoesNotBleedIntoNextFeature()
        {
            var keys = new List<string> { "a", "b" };
            var keyIndex = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1 };
            string[] valueStrings = System.Array.Empty<string>();
            var values = new NativeArray<MvtValueNative>(
                new[] { MvtValueNative.Bool(false), MvtValueNative.Bool(true) }, Allocator.Persistent);
            // f0: (a,val0=false) — one pair, short-circuits before reading b.
            // f1: (a,val1=true),(b,val1=true) — two pairs, runs to completion.
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0, 0, 1, 1, 1 }, Allocator.Persistent);
            var tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 2, 4 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_stack_residue" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("all"),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("a") }),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("b") }),
                });
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program));

                var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
                Assert.IsNotNull(matcher, "precondition: must native-bind");
                try
                {
                    NativeArray<byte> results = matcher.MatchAll();
                    Assert.That(results[0], Is.EqualTo((byte)0), "precondition: feature 0 (a=false) excludes");
                    Assert.That(results[1], Is.Not.EqualTo((byte)0),
                        "feature 1 (a=true, b=true) must match — feature 0's short-circuit residue must not " +
                        "corrupt feature 1's operand stack");
                    Assert.That((NativeFilterError)matcher.Errors[1], Is.EqualTo(NativeFilterError.None));
                }
                finally
                {
                    matcher.Dispose();
                }
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        // ── Batched Execute: geometry kind read per feature (synthetic on both halves) ──

        /// <summary>
        /// liberty.json has no <c>geometry-type</c> filter, so the filter and fixture are hand-built.
        /// <c>["==",["geometry-type"],"Polygon"]</c> over a Point then a Polygon: an <c>Execute</c> that read
        /// the geometry-kind column at a fixed index would give both features the SAME answer.
        /// </summary>
        [Test]
        public void MatchAll_GeometryKind_IsReadPerFeature()
        {
            var keys = new List<string>();
            var keyIndex = new Dictionary<string, int>();
            string[] valueStrings = System.Array.Empty<string>();
            var values = new NativeArray<MvtValueNative>(0, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(0, Allocator.Persistent);
            var tagOffsets = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_geometry_kind" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Point, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Polygon, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("=="),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("geometry-type") }),
                    JsonValue.OfString("Polygon"),
                });
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program));

                var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
                Assert.IsNotNull(matcher, "precondition: must native-bind");
                try
                {
                    NativeArray<byte> results = matcher.MatchAll();
                    Assert.That(results[0], Is.EqualTo((byte)0), "feature 0 is a Point: must exclude");
                    Assert.That(results[1], Is.Not.EqualTo((byte)0), "feature 1 is a Polygon: must include");
                }
                finally
                {
                    matcher.Dispose();
                }
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }
    }
}
