// Unity EditMode only — decodes MvtLayer (NativeArray/Burst) through the production TileMeshLayerProcessor
// boundary. Not registered in core-tests.csproj (see NativeFilterVmTests.cs).

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Proves the mesh-build path's <see cref="TileMeshLayerProcessor.ProcessOnWorker"/> call sites actually
    /// REACH the native-filter seam (<see cref="FeatureSelector.SelectFeatures(StyleLayer, ITileLayer, IReadOnlyList{IFeature}, double, List{SelectedTileFeature})"/>
    /// and its scratch-array twin) — a check neither the golden manifest nor
    /// <c>NativeFilterVmTests</c>/<c>FeatureSelectorNativeFilterTests</c> performs, since both stay green
    /// whether the mesh-build call site binds natively or not: they observe the VM's own answers, never
    /// which overload the processor called.
    /// </summary>
    [TestFixture]
    public class TileMeshLayerProcessorNativeFilterRoutingTests
    {
        /// <summary>R1 (review): <paramref name="buffers"/> non-null reaches
        /// <see cref="TileMeshLayerProcessor.ProcessOnWorker"/>'s pooled ARRAY arm (`:85`) — what
        /// <c>TileLayerProcessorRunner.RunWorkerPass</c> always supplies in production
        /// (`TileLayerProcessorRunner.cs:59,72`); null reaches the non-pooled LIST arm (`:91`), what most
        /// other EditMode tests reach. The tooth must cover both — they are two different
        /// <c>FeatureSelector.SelectFeatures</c> overloads, and reverting either alone must be caught.</summary>
        private static TileLayerProcessContext MakeContext(TileBuildBuffers buffers) => new TileLayerProcessContext
        {
            Tile             = new TileId { Z = 0, X = 0, Y = 0 },
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
            Buffers          = buffers,
        };

        /// <summary>A filter proven to native-bind against the fixture's <c>countries</c> layer:
        /// <c>NAME == (first country's NAME)</c> — id-vs-string-literal, inside the accepted subset, and
        /// non-degenerate (selects a proper non-empty subset), mirroring
        /// <c>FeatureSelectorNativeFilterTests.BuildDiscriminatingFilter</c>.</summary>
        private static JsonValue BuildDiscriminatingFilter(MvtLayer countries, out List<IFeature> expected)
        {
            Assert.That(countries.Features.Count, Is.GreaterThan(1), "precondition: >1 country");
            Assert.IsTrue(((IFeature)countries.Features[0]).TryGetProperty("NAME", out Value name0));
            Assert.That(name0.Type, Is.EqualTo(ValueType.String));

            JsonValue filter = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("=="),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("NAME") }),
                JsonValue.OfString(name0.AsString()),
            });

            Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program),
                "precondition: must be VM-compilable");
            Assert.IsTrue(program.Rebind(countries.DenseKeyResolver, Allocator.TempJob, out NativeArray<int> probe),
                "precondition: must native-bind over countries (unique NAME -> one value id)");
            probe.Dispose();

            CompiledFilter managed = CompiledFilter.Compile(filter);
            expected = new List<IFeature>();
            foreach (MvtFeature f in countries.Features)
                if (managed.Matches(f, 0.0))
                    expected.Add(f);
            Assert.That(expected.Count, Is.GreaterThan(0).And.LessThan(countries.Features.Count),
                "precondition: filter must select a proper non-empty subset of countries");
            return filter;
        }

        /// <summary>Forwards every <see cref="ITileLayer"/>/<see cref="IIndexedFeatureSource"/>/
        /// <see cref="INativeFilterSource"/> call to a real decoded <see cref="MvtLayer"/>, counting
        /// <see cref="TryBindNativeFilterCalls"/> — the routing tooth's own instrument. Test-assembly-only
        /// (conventions forbid a test counter on the production <c>MvtLayer</c> itself).</summary>
        private sealed class NativeFilterSourceSpy : ITileLayer, IIndexedFeatureSource, INativeFilterSource
        {
            private readonly MvtLayer _inner;
            internal int TryBindNativeFilterCalls;
            internal int TryBindNativeFilterNonNullReturns;

            internal NativeFilterSourceSpy(MvtLayer inner) => _inner = inner;

            string ITileLayer.Name => ((ITileLayer)_inner).Name;
            uint ITileLayer.Extent => ((ITileLayer)_inner).Extent;
            IReadOnlyList<IFeature> ITileLayer.Features => ((ITileLayer)_inner).Features;
            TileGeometryBuffers ITileLayer.Geometry => ((ITileLayer)_inner).Geometry;

            IFeatureKeyResolver IIndexedFeatureSource.KeyResolver => ((IIndexedFeatureSource)_inner).KeyResolver;

            INativeFeatureMatcher INativeFilterSource.TryBindNativeFilter(NativeFilterProgram program)
            {
                TryBindNativeFilterCalls++;
                INativeFeatureMatcher matcher = ((INativeFilterSource)_inner).TryBindNativeFilter(program);
                if (matcher != null) TryBindNativeFilterNonNullReturns++;
                return matcher;
            }
        }

        private sealed class OneLayerDecodedTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            private readonly string _name;
            public OneLayerDecodedTile(ITileLayer layer, string name) { _layer = layer; _name = name; }
            public ITileLayer GetLayer(string name) => name == _name ? _layer : null;
            public void Dispose() { }
        }

        /// <summary>Records the <see cref="SelectedTileFeature"/>s <see cref="TileMeshLayerProcessor"/>
        /// hands to <see cref="BuildGraphRequest"/> — the routing tooth's second half, comparing them
        /// against the managed reference computed independently in <see cref="BuildDiscriminatingFilter"/>.</summary>
        private sealed class CapturingTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public IReadOnlyList<SelectedTileFeature> Captured { get; private set; }
            public int BuildGraphRequestCallCount { get; private set; }

            public CapturingTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public RenderLayerBuild Build           => RenderLayerBuild.TileMesh;
            public DrawPersistence  Persistence     => DrawPersistence.Persistent;
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material         Material        => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                BuildGraphRequestCallCount++;
                Captured = new List<SelectedTileFeature>(selected);
                return null;
            }
        }

        // R1 (review): pooled (Buffers != null — production's ONLY arm) and non-pooled (Buffers == null —
        // the arm most other EditMode tests reach) share this one spy/oracle. Reverting either of
        // TileMeshLayerProcessor.cs's two call sites alone must red the matching case.
        [TestCase(true)]
        [TestCase(false)]
        public void ProcessOnWorker_ProbesTheNativeSeam_AndSelectsTheManagedAnswer(bool pooled)
        {
            using MvtTile tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            MvtLayer countries = tile.GetLayer("countries");
            Assert.IsNotNull(countries, "precondition: fixture has a 'countries' layer");

            JsonValue filter = BuildDiscriminatingFilter(countries, out List<IFeature> expected);

            var spy = new NativeFilterSourceSpy(countries);
            var decodedTile = new OneLayerDecodedTile(spy, "countries");
            var styleLayer = new StyleLayer
            {
                Id = "t", Source = "s", SourceLayer = "countries", Filter = filter,
                LayerType = StyleLayerType.Fill,
            };
            var renderLayer = new CapturingTileMeshRenderLayer(styleLayer);
            var processor = TileMeshLayerProcessor.AllocateForKick(renderLayer, materialIndex: 0);

            var context = MakeContext(pooled ? new TileBuildBuffers() : null);
            processor.ProcessOnWorker(decodedTile, in context);

            Assert.That(spy.TryBindNativeFilterCalls, Is.EqualTo(1),
                $"TryBindNativeFilter must be called exactly once for this one style layer (pooled={pooled}) " +
                "— a routing regression (reverting this arm's call site to the old two-argument overload) " +
                "leaves this at 0.");
            // NIT (review): being PROBED isn't being BOUND — this filter is proven to native-bind against
            // 'countries' (BuildDiscriminatingFilter's own precondition), so a non-null return must follow.
            Assert.That(spy.TryBindNativeFilterNonNullReturns, Is.EqualTo(1),
                "the seam must not just be probed — this filter is proven to bind, so a matcher must come back");
            Assert.IsNotNull(renderLayer.Captured, "BuildGraphRequest must have been reached");
            Assert.That(renderLayer.Captured.Count, Is.EqualTo(expected.Count));
            for (int i = 0; i < expected.Count; i++)
                Assert.AreSame(expected[i], renderLayer.Captured[i].Feature,
                    $"feature[{i}]: routed selection must match the independently-computed managed answer");
        }
    }
}
