// Unity EditMode only — the real runner entries over the committed fixture. NOT in core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Core.Text;
using MapRenderer.Unity.Text;
using MapRenderer.Tests.Tiles;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// IR C1 P3, tooth <b>C</b> — <b>one materialization per source-layer per tile, shared across BOTH
    /// cadences of a kick</b>.
    ///
    /// <para><b>What this is for.</b> P2's recorded finding 1 is that nothing observed the <i>point</i> of
    /// the conversion: changing the symbol processor's store argument to <c>null</c> left all 2286 tests
    /// green while silently reverting one-buffer-per-source-layer, because the fallback minted a private
    /// store and the OUTPUT was byte-identical. P3 deleted the store, so the P3 shape of that same silent
    /// regression is a <c>Geometry</c> getter that re-materializes per read, or a consumer that mints its
    /// own. Both are output-neutral. This is the tooth that sees them.</para>
    ///
    /// <para><b>Production configuration — the standing check, taken seriously.</b> The fan-in is ≥ 2 on
    /// BOTH sides and it CROSSES the mesh/symbol boundary: <c>countries</c> is named by two fill layers,
    /// <c>centroids</c> by <b>two symbol layers</b> (the half P2's finding says a mesh-only fixture would
    /// miss), and <c>geolines</c> by exactly one layer so the distinct-buffer count is not trivially
    /// satisfied by "everything is one buffer". Both passes run inside ONE decode scope — the shape
    /// <c>TileManager.KickMeshBuild</c> creates — and the real production processors run alongside the
    /// probes, so the arrangement under test is one that actually happens.</para>
    /// </summary>
    [TestFixture]
    public class SourceLayerBufferSharingTests
    {
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = Tile,
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        /// <summary>Counts how many times the tile's bytes were actually parsed.</summary>
        private sealed class CountingDecoder : ITileDecoder
        {
            private readonly ITileDecoder _inner = new MvtTileDecoder();
            public int DecodeCount { get; private set; }
            public IDecodedTile Decode(TileId id, byte[] bytes)
            {
                DecodeCount++;
                return _inner.Decode(id, bytes);
            }
        }

        /// <summary>Records the buffer a consumer of <paramref name="sourceLayer"/> would borrow, read through
        /// the SAME expression the production consumers use — <c>SourceLayerResolver.ResolveTileLayer(style,
        /// tile).Geometry</c> (<c>TileMeshLayerProcessor</c> and <c>SymbolFeatureExtractor</c> both do exactly
        /// this). Not a transcription of production: what is asserted is buffer IDENTITY across independent
        /// consumers, which a re-materializing getter breaks while leaving every output byte-identical.</summary>
        private sealed class BufferProbeMeshProcessor : ITileMeshLayerProcessor
        {
            private readonly StyleLayer _style;
            private readonly List<(string source, NativeArray<double2> buffer)> _log;
            public BufferProbeMeshProcessor(StyleLayer style, List<(string, NativeArray<double2>)> log)
            { _style = style; _log = log; }

            public LayerPhase Phase => LayerPhase.WorkerOnly;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
                => _log.Add((_style.SourceLayer,
                             SourceLayerResolver.ResolveTileLayer(_style, tile)?.Geometry.Vertices ?? default));
            public bool TryTakeGraphRequest(out ILayerMeshBuild build) { build = null; return false; }
            public void Release() { }
        }

        private sealed class BufferProbeSymbolProcessor : ITileWorkerThenMainLayerProcessor
        {
            private readonly StyleLayer _style;
            private readonly List<(string source, NativeArray<double2> buffer)> _log;
            public BufferProbeSymbolProcessor(StyleLayer style, List<(string, NativeArray<double2>)> log)
            { _style = style; _log = log; }

            public LayerPhase Phase => LayerPhase.WorkerThenMain;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
                => _log.Add((_style.SourceLayer,
                             SourceLayerResolver.ResolveTileLayer(_style, tile)?.Geometry.Vertices ?? default));
            public void CompleteOnMain(CancellationToken ct) { }
        }

        private static StyleLayer Fill(string id, string sourceLayer) => new StyleLayer
        {
            Id = id, LayerType = StyleLayerType.Fill, Source = "s", SourceLayer = sourceLayer,
            PaintJson = JsonParser.Parse("{\"fill-color\":\"#ffffff\"}"),
        };

        private static SymbolStyle.StyleLayer Symbol(string id, string sourceLayer) => new SymbolStyle.StyleLayer
        {
            Id = id, LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = sourceLayer,
            LayoutJson = JsonParser.Parse("{\"text-field\":\"{NAME}\"}"),
        };

        [Test]
        public void OneKick_MaterializesEachSourceLayerOnce_SharedAcrossFillAndSymbolConsumers()
        {
            var decoder = new CountingDecoder();
            var handle  = new SharedDisposable<IDecodedTile>(decoder.Decode(Tile, SampleTileFixture.Bytes()));
            var context = MakeContext();

            // Two fill layers over "countries", one over "geolines" (named by EXACTLY ONE style layer, so
            // the distinct-count assertion below is not satisfiable by collapsing everything onto one
            // buffer), and TWO symbol layers over "centroids".
            StyleLayer fillA = Fill("fill-a", "countries");
            StyleLayer fillB = Fill("fill-b", "countries");
            StyleLayer fillC = Fill("fill-c", "geolines");
            SymbolStyle.StyleLayer symbolA = Symbol("sym-a", "centroids");
            SymbolStyle.StyleLayer symbolB = Symbol("sym-b", "centroids");

            var meshLog   = new List<(string source, NativeArray<double2> buffer)>();
            var symbolLog = new List<(string source, NativeArray<double2> buffer)>();

            // A real builder over an empty glyph source: the worker step (ExtractLayers) never touches the
            // glyph cache — it is the main-thread Shape tail that does — so an empty source is enough to
            // run the PRODUCTION symbol processors here, which is the point (the fan-in under test must be
            // one production actually creates).
            using var glyphManager = new GlyphManager(
                TestGlyphSource.FromRanges(new Dictionary<(string fontStack, int rangeStart), byte[]>()), new GlyphAtlas(256, 256));
            var builder = new StyledSymbolTileBuilder(glyphManager);
            // 4.4c: the symbol processors now write their raw symbol slots into a reused SymbolTileBuffer (not a
            // per-symbol managed carrier list). This test only exercises the WORKER pass (ExtractLayers) + buffer-sharing —
            // the scratch is never read here, just handed to the processor ctors.
            var tileBuffer  = new MapRenderer.Core.Text.Placement.SymbolTileBuffer();

            // ONE reference around BOTH passes, released in a finally — TileManager.KickMeshBuild's shape
            // exactly.
            try
            {
                var meshProcessors = new ITileMeshLayerProcessor[]
                {
                    new BufferProbeMeshProcessor(fillA, meshLog),
                    new BufferProbeMeshProcessor(fillB, meshLog),
                    new BufferProbeMeshProcessor(fillC, meshLog),
                };
                TilePrologueOutput meshOutput = TileLayerProcessorRunner.RunWorkerPass(handle, in context, meshProcessors);
                for (int i = 0; i < meshOutput.Layers.Length; i++) meshOutput.Layers[i]?.Dispose();

                var symbolProcessors = new ITileWorkerThenMainLayerProcessor[]
                {
                    new BufferProbeSymbolProcessor(symbolA, symbolLog),
                    new BufferProbeSymbolProcessor(symbolB, symbolLog),
                    // The REAL symbol processors run in the same pass, so the arrangement under test is one
                    // production actually creates — two symbol layers naming one source-layer, extracting.
                    new TileSymbolLayerProcessor(builder, symbolA, 0, tileBuffer),
                    new TileSymbolLayerProcessor(builder, symbolB, 1, tileBuffer),
                };
                TileLayerProcessorRunner.RunSymbolWorkerPass(handle, in context, symbolProcessors);
            }
            finally { handle.Release(); }

            // ── Clause 0: ONE decode for the whole kick. ─────────────────────────────────────────────
            Assert.AreEqual(1, decoder.DecodeCount,
                "the mesh pass and the symbol pass of ONE kick share ONE decode — that is what the single " +
                "kick reference buys, and what B7's per-PASS store could never give (it materialized a " +
                "shared source-layer once per pass, i.e. twice per kick).");

            // ── Non-vacuity: every probe really saw a live buffer. ───────────────────────────────────
            Assert.AreEqual(3, meshLog.Count,   "precondition: all three mesh probes ran");
            Assert.AreEqual(2, symbolLog.Count, "precondition: both symbol probes ran");
            foreach ((string source, NativeArray<double2> buffer) in meshLog)
                Assert.IsTrue(buffer.IsCreated, $"precondition: '{source}' must really carry geometry");
            foreach ((string source, NativeArray<double2> buffer) in symbolLog)
                Assert.IsTrue(buffer.IsCreated, $"precondition: '{source}' must really carry geometry");

            // ── Clause i: the fan-in shares, WITHIN and ACROSS the cadences. ─────────────────────────
            // NativeArray<T>.Equals compares backing pointer + length, so these are IDENTITY assertions:
            // a getter that re-materialized would hand back equal CONTENT at a different pointer.
            Assert.IsTrue(meshLog[0].buffer.Equals(meshLog[1].buffer),
                "two fill layers naming 'countries' must borrow the SAME buffer");
            Assert.IsTrue(symbolLog[0].buffer.Equals(symbolLog[1].buffer),
                "two SYMBOL layers naming 'centroids' must borrow the SAME buffer — the half P2's recorded " +
                "finding 1 says a mesh-only fixture cannot see");

            // Cross-cadence sharing is clause 0 + clause ii together: ONE decode served both passes, and
            // one decode holds exactly one buffer per layer — so a mesh consumer and a symbol consumer of
            // the same source-layer necessarily hold the same allocation. (There is no scope to re-open and
            // no re-decode to provoke any more — D1 retired both. The tile is decoded ONCE, before any lease
            // exists, and every reference reads that same instance; the sharing is a property of the
            // decoded tile now, not of who happens to hold a scope open.)

            // ── Clause ii: distinct buffers == distinct source-layers touched. ───────────────────────
            var distinct = new List<NativeArray<double2>>();
            var sources  = new HashSet<string>();
            foreach ((string source, NativeArray<double2> buffer) in Concat(meshLog, symbolLog))
            {
                sources.Add(source);
                bool seen = false;
                foreach (NativeArray<double2> d in distinct) if (d.Equals(buffer)) { seen = true; break; }
                if (!seen) distinct.Add(buffer);
            }
            Assert.AreEqual(3, sources.Count,
                "precondition: the fixture must touch THREE distinct source-layers, one of them named by " +
                "exactly one style layer — otherwise 'distinct buffers == distinct source-layers' is " +
                "satisfiable by collapsing everything onto one buffer");
            Assert.AreEqual(sources.Count, distinct.Count,
                "the number of DISTINCT buffers must equal the number of distinct source-layers touched: " +
                "one materialization per source-layer, no more (a per-read materialization gives 5) and no " +
                "fewer (a collapsed memo gives 1).");
        }

        private static IEnumerable<(string source, NativeArray<double2> buffer)> Concat(
            List<(string source, NativeArray<double2> buffer)> a,
            List<(string source, NativeArray<double2> buffer)> b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }
    }
}
