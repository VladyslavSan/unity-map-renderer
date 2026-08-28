// Unity EditMode only — decodes MvtLayer (NativeArray/Burst) and drives the native filter VM through the
// production FeatureSelector boundary. Not registered in core-tests.csproj (see NativeFilterVmTests.cs).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// The VM-wiring stage's acceptance teeth, at the PRODUCTION <see cref="FeatureSelector"/> boundary —
    /// as opposed to <c>NativeFilterVmTests</c>, whose oracle drives the VM directly. Covers: byte-identical
    /// parity across the three <see cref="ITileLayer"/> overloads (T1), that the native seam is actually
    /// probed and dispatched to per-feature rather than silently skipped (T2), and that both refusal gates
    /// — compile refusal and rebind refusal — correctly fall back to the managed path (T3).
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
            // The parity sweep needs at least one filter that partitions a fixture layer non-trivially, or
            // its non-degeneracy guard is unsatisfiable (see BuildDiscriminatingFilter). The liberty corpus
            // happens to select all-or-nothing on every layer of this small fixture.
            _parityFilters = new List<JsonValue>(_coveredLibertyFilters) { BuildDiscriminatingFilter() };
        }

        /// <summary>A filter guaranteed to partition the fixture's <c>countries</c> layer non-trivially —
        /// <c>NAME == (first country's NAME)</c> — so the parity sweep always contains at least one
        /// <c>0 &lt; selected &lt; total</c> case. Without it the non-degeneracy guard would be
        /// unsatisfiable over this fixture, and a stuck-constant native bug (always include / always exclude)
        /// could pass parity vacuously wherever managed is itself all-or-nothing. Compilable (<c>get</c> ==
        /// string literal — the safe id-vs-byte form) and native-bindable; self-checked here so a fixture
        /// change surfaces loudly rather than as a mysterious guard failure.</summary>
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
            // ...and it must actually native-BIND over countries — this is T1's ONLY non-degenerate case, so
            // the anti-vacuity guard only protects the NATIVE path if the VM actually runs here. Were name0 to
            // collide in the countries value table (a fixture property), Rebind would refuse and this case
            // would silently fall to managed, disarming T1's native coverage. Assert it so a fixture change
            // fails LOUDLY in setup rather than quietly.
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

        /// <summary>The managed reference computation T1/T3 compare the production selector against —
        /// deliberately independent of <see cref="FeatureSelector"/>'s own managed path (no key binding),
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

        // ── T1 — production-boundary parity ─────────────────────────────────────────────────────

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

        // ── T2 — dispatch coverage (the vacuous-all-fallback guard) ─────────────────────────────

        /// <summary>Finds a covered filter that actually native-binds against <paramref name="layer"/> (not
        /// refused by the rebind's ≥2-id guard) — so T2's per-feature <c>Matches</c> count is meaningful.
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
        public void FeatureSelector_ProbesTheNativeSeam_AndDispatchesPerFeature()
        {
            MvtLayer layer = FindNativeBindableLayer(out JsonValue filterJson);
            Assert.IsNotNull(layer, "precondition: at least one fixture layer must native-bind at least one covered filter");

            var spy = new NativeFilterSourceSpy(layer);
            StyleLayer styleLayer = MakeLayer(layer.Name, filterJson);
            var into = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(styleLayer, spy, 0.0, into);

            Assert.That(spy.TryBindNativeFilterCalls, Is.GreaterThanOrEqualTo(1),
                "FeatureSelector must probe the INativeFilterSource seam for a VM-compilable filter");
            Assert.That(spy.MatchesCalls, Is.EqualTo(layer.Features.Count),
                "every feature must be addressed through the native matcher, not silently skipped to managed");
        }

        // ── T3 — fallback correctness (both refusal gates) ──────────────────────────────────────

        // Two-get comparison: the byte-identity argument only holds for get-vs-number-literal (F3); a
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

        /// <summary>The FeatureSelector-level half of T3b: when the seam itself refuses to bind (simulated
        /// here via the spy, standing in for a real rebind refusal), the seam is still probed but the
        /// selection falls back to the managed path and stays correct.</summary>
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

        // ── Test-assembly spy decorator (T2/T3b) ────────────────────────────────────────────────

        /// <summary>Test-assembly capability decorator around a real <see cref="MvtLayer"/> — forwards every
        /// <see cref="ITileLayer"/>/<see cref="IIndexedFeatureSource"/>/<see cref="INativeFilterSource"/>
        /// call to the inner layer, counting <see cref="TryBindNativeFilterCalls"/> and the returned
        /// matcher's <see cref="MatchesCalls"/>. Never a production member — conventions forbid a test-only
        /// counter on <see cref="MvtLayer"/> itself, so this decorator lives here instead, exactly the
        /// allowed footprint for test-code (an adapter in the test assembly, not a member on the production
        /// type).
        ///
        /// <para><paramref name="forceRefuse"/> makes <see cref="TryBindNativeFilterCalls"/> still count the
        /// probe but always answer null — standing in for a real rebind refusal (T3b), without depending on
        /// the fixture actually containing one.</para></summary>
        private sealed class NativeFilterSourceSpy : ITileLayer, IIndexedFeatureSource, INativeFilterSource
        {
            private readonly MvtLayer _inner;
            private readonly bool _forceRefuse;
            internal int TryBindNativeFilterCalls;
            internal int MatchesCalls;

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

                public bool Matches(int ordinal)
                {
                    _owner.MatchesCalls++;
                    return _inner.Matches(ordinal);
                }

                public void Dispose() => _inner.Dispose();
            }
        }
    }
}
