// Unity EditMode only — MvtDecoder decodes into NativeArray-backed buffers. NOT registered in
// core-tests.csproj (see core-tests.csproj's tile-decode-seam note).

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Mvt
{
    /// <summary>
    /// T1b + T2 (string→id key hoist) — the integration proof, over the real fixture, that
    /// <see cref="FeatureSelector"/>'s per-layer bind step resolves a filter's constant-key <c>get</c>/<c>has</c>
    /// names ONCE per <see cref="FeatureSelector.SelectFeatures(StyleLayer, ITileLayer, double, List{SelectedTileFeature})"/>
    /// call — not once per feature — and that the hoisted int-key binding path (via the
    /// <see cref="ITileLayer"/> overload) and the plain string-lookup path (via the
    /// <see cref="FeatureSelector.SelectFeatures(StyleLayer, IReadOnlyList{IFeature}, double, List{SelectedTileFeature})"/>
    /// features-list overload, which never probes for <see cref="IIndexedFeatureSource"/> capability and so
    /// always evaluates by name) select the same features over the SAME decoded feature set, exactly as
    /// before the hoist.
    /// </summary>
    [TestFixture]
    public class KeyBindingHoistTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"sample-tile.bytes not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static StyleLayer MakeCountriesLayer(string filterJson) => new StyleLayer
        {
            Id          = "test",
            Source      = "fixture",
            SourceLayer = "countries",
            Filter      = JsonParser.Parse(filterJson),
        };

        /// <summary>Counts <see cref="IFeatureKeyResolver.TryResolveKey"/> calls, forwarding to a real
        /// resolver — installed via <see cref="CountingIndexedTileLayer"/> so the count rides the seam
        /// itself, with zero production instrumentation.</summary>
        private sealed class CountingKeyResolver : IFeatureKeyResolver
        {
            private readonly IFeatureKeyResolver _inner;
            public int CallCount { get; private set; }
            public CountingKeyResolver(IFeatureKeyResolver inner) => _inner = inner;
            public bool TryResolveKey(string name, out int keyIndex)
            {
                CallCount++;
                return _inner.TryResolveKey(name, out keyIndex);
            }
        }

        /// <summary>A thin <see cref="ITileLayer"/>/<see cref="IIndexedFeatureSource"/> adapter over a real
        /// (already-decoded) <see cref="MvtLayer"/>: forwards <see cref="Features"/> as given (so a test can
        /// vary N independently of the layer's own feature count) and <see cref="Geometry"/> straight from
        /// the real layer (borrowed, never disposed here — the tracked <see cref="MvtTile"/> owns it), and
        /// answers <see cref="KeyResolver"/> with a <see cref="CountingKeyResolver"/> wrapping the real
        /// layer's Dense resolver.</summary>
        private sealed class CountingIndexedTileLayer : ITileLayer, IIndexedFeatureSource
        {
            private readonly MvtLayer _real;
            public readonly CountingKeyResolver Resolver;

            public CountingIndexedTileLayer(MvtLayer real, IReadOnlyList<IFeature> features)
            {
                _real = real;
                Features = features;
                Resolver = new CountingKeyResolver(real.DenseKeyResolver);
            }

            public string Name => _real.Name;
            public uint Extent => _real.Extent;
            public IReadOnlyList<IFeature> Features { get; }
            public TileGeometryBuffers Geometry => _real.Geometry;
            public IFeatureKeyResolver KeyResolver => Resolver;
        }

        // ── T1b: O(layers), not O(features) ──────────────────────────────────────────────────────

        [Test]
        public void SelectFeatures_ResolvesKeysOncePerCall_NotPerFeature_AndSelectionIsUnchanged()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            Assert.That(layer, Is.Not.Null, "precondition: fixture must have a 'countries' layer");
            Assert.That(layer.Features.Count, Is.GreaterThan(1),
                "precondition: need >1 feature to distinguish O(layers) from O(features)");

            StyleLayer styleLayer = MakeCountriesLayer("[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]");

            // An independent oracle via the STRING path (bypasses the binding entirely), so the
            // "selection unchanged" assertions below don't just trust the code under test.
            bool StringPathMatchesAfrica(IFeature f) =>
                f.TryGetProperty("CONTINENT", out Value v)
                && v.Type == MapRenderer.Core.Expressions.ValueType.String
                && v.AsString() == "Africa";

            var into = new List<SelectedTileFeature>();

            // N = 1 feature.
            var oneLayerAdapter = new CountingIndexedTileLayer(layer, new List<IFeature> { layer.Features[0] });
            FeatureSelector.SelectFeatures(styleLayer, oneLayerAdapter, 0.0, into);
            int oneFeatureResolveCount = oneLayerAdapter.Resolver.CallCount;
            int oneFeatureSelected = into.Count;

            // N = all features.
            var allLayerAdapter = new CountingIndexedTileLayer(layer, layer.Features);
            FeatureSelector.SelectFeatures(styleLayer, allLayerAdapter, 0.0, into);
            int allFeaturesResolveCount = allLayerAdapter.Resolver.CallCount;
            int allFeaturesSelected = into.Count;

            // The filter has exactly one get-node -> exactly one TryResolveKey call per SelectFeatures call,
            // however many features it scans. A per-feature lookup (the un-hoisted behaviour) would instead
            // scale with N: 1 at N=1, layer.Features.Count at N=all.
            Assert.That(oneFeatureResolveCount, Is.EqualTo(1),
                "one get-node -> exactly one TryResolveKey call, even at N=1");
            Assert.That(allFeaturesResolveCount, Is.EqualTo(1),
                "one get-node -> exactly one TryResolveKey call, however many features (this is the hoist)");
            Assert.That(allFeaturesResolveCount, Is.EqualTo(oneFeatureResolveCount),
                "resolve count must be IDENTICAL at N=1 and N=all — O(layers), not O(features)");

            // Selection must be unchanged and non-vacuous — guards against a tooth that passes by resolving
            // nothing (tooth-membership-is-not-coverage).
            bool expectedOne = StringPathMatchesAfrica(layer.Features[0]);
            int expectedAll = 0;
            foreach (IFeature f in layer.Features) if (StringPathMatchesAfrica(f)) expectedAll++;

            Assert.That(oneFeatureSelected, Is.EqualTo(expectedOne ? 1 : 0));
            Assert.That(allFeaturesSelected, Is.EqualTo(expectedAll));
            Assert.That(expectedAll, Is.EqualTo(54),
                "precondition: CONTINENT==Africa is pinned at 54 features elsewhere (PropertyFilterTests) — " +
                "if this drifts, the fixture changed underneath both tests, not just this one");
        }

        // ── T2: hoisted (int-key) and string-lookup selection still agree ───────────────────────────

        [TestCase("[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]")]
        [TestCase("[\"has\",\"NAME\"]")]
        [TestCase("[\"==\",\"NAME\",\"Aruba\"]")] // legacy form — also routes through the constant-key node
        public void Hoisted_And_StringPath_SelectFeatures_AgreeOnSelectedOrdinals(string filterJson)
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");

            StyleLayer styleLayer = MakeCountriesLayer(filterJson);

            var hoistedInto = new List<SelectedTileFeature>();
            var stringPathInto = new List<SelectedTileFeature>();
            // ITileLayer overload: probes IIndexedFeatureSource, binds the hoisted int-key path.
            FeatureSelector.SelectFeatures(styleLayer, (ITileLayer)layer, 0.0, hoistedInto);
            // Features-list overload: no ITileLayer to probe, so always evaluates by name (see its own doc).
            FeatureSelector.SelectFeatures(styleLayer, (IReadOnlyList<IFeature>)layer.Features, 0.0, stringPathInto);

            Assert.That(stringPathInto.Count, Is.GreaterThan(0), "precondition: filter must select something");
            Assert.That(hoistedInto.Count, Is.EqualTo(stringPathInto.Count),
                $"the hoisted int-key path and the string-lookup path must select the same COUNT for " +
                $"filter {filterJson}");

            var stringPathOrdinals = new HashSet<int>();
            foreach (var s in stringPathInto) stringPathOrdinals.Add(s.Ordinal);
            var hoistedOrdinals = new HashSet<int>();
            foreach (var s in hoistedInto) hoistedOrdinals.Add(s.Ordinal);
            Assert.That(hoistedOrdinals.SetEquals(stringPathOrdinals),
                $"the hoisted int-key path and the string-lookup path must select the SAME ordinals for " +
                $"filter {filterJson}");
        }
    }
}
