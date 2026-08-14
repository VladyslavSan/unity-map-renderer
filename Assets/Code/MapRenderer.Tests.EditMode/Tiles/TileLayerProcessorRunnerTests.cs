// Unity EditMode only — MeshDataPayload/Mesh.MeshDataArray are Unity types. NOT registered in
// core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Epic A / A1 acceptance tooth #2: proves
    /// <see cref="TileLayerProcessorRunner.RunWorkerPass"/> decodes the fetched bytes exactly once and
    /// shares that same <see cref="IDecodedTile"/> reference across every processor in dense order, and that
    /// the pre-A1 fault policy (abort-on-first-fault, wrap-every-allocation) survives the move unchanged.
    /// A6: the decoder is injected, not hardcoded. D1: the decode happens BEFORE the lease exists (at the
    /// source's <c>GetTile</c>), so these fixtures mint one the same way production does — decode, then wrap
    /// — and release in a <c>finally</c>, mirroring the kick lambda.
    /// </summary>
    [TestFixture]
    public class TileLayerProcessorRunnerTests
    {
        /// <summary>IR C1 P3: the ONE address these teeth use — the decode's id and the context's tile are
        /// the same thing now, so a fixture that let them drift would be building the mispairing C1 removes.</summary>
        private static readonly TileId ContextTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = ContextTile,
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        private sealed class FakePayload : IRenderLayerPayload
        {
            public int VertexCount { get; }
            public int MaterialIndex { get; }
            public FakePayload(int materialIndex, int vertexCount = 0) { MaterialIndex = materialIndex; VertexCount = vertexCount; }
            public Mesh Upload() => null;
            public void Dispose() { }
        }

        /// <summary>Records ProcessOnWorker invocations (order + the observed decoded-tile reference) into a
        /// SHARED log so a test can assert cross-processor invocation order/identity, and counts
        /// Complete() calls so a test can assert exactly-once settlement.</summary>
        private sealed class RecordingProcessor : ITileMeshLayerProcessor
        {
            private readonly int _order;
            private readonly int _materialIndex;
            private readonly List<(int order, IDecodedTile tile)> _log;
            private readonly bool _throwOnProcess;

            public int CompleteCallCount { get; private set; }

            public LayerPhase Phase { get; }

            public RecordingProcessor(int order, int materialIndex, List<(int order, IDecodedTile tile)> log,
                LayerPhase phase = LayerPhase.WorkerOnly, bool throwOnProcess = false)
            {
                _order          = order;
                _materialIndex  = materialIndex;
                _log            = log;
                Phase           = phase;
                _throwOnProcess = throwOnProcess;
            }

            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
            {
                if (_throwOnProcess)
                    throw new InvalidOperationException("RecordingProcessor deliberate fault (test)");
                _log.Add((_order, tile));
            }

            public IRenderLayerPayload Complete()
            {
                CompleteCallCount++;
                return new FakePayload(_materialIndex);
            }
        }

        /// <summary>A minimal <see cref="ITileMeshRenderLayer"/> whose <see cref="WriteInto"/> always
        /// throws — proves <see cref="TileMeshLayerProcessor"/>'s settlement path on a real (not
        /// recording-fake) processor.</summary>
        private sealed class ThrowingTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base; // mirrors a tile-mesh layer (G7/D7)
            public Material Material => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public ThrowingTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
                out int vertexCount, out Bounds bounds)
            {
                throw new InvalidOperationException("ThrowingTileMeshRenderLayer deliberate fault (test)");
            }
        }

        /// <summary>A tile layer that counts how many times its <c>Features</c> list is read, and — like a
        /// decoded layer since IR C1 P3 — OWNS its geometry, materialized once at construction. The counter
        /// is how "the geometry read costs no Features walk" becomes observable without putting a test-only
        /// member on a production class.</summary>
        private sealed class CountingTileLayer : ITileLayer, IDisposable
        {
            private readonly IReadOnlyList<IFeature> _features;
            public int FeaturesReadCount { get; private set; }

            public CountingTileLayer(string name, IReadOnlyList<IFeature> features, TileId tile)
            {
                Name = name;
                _features = features;
                var kinds    = new List<TileGeometryType>();
                var commands = new List<uint[]>();
                for (int i = 0; i < features.Count; i++)
                {
                    kinds.Add(features[i].GeometryType);
                    commands.Add((features[i] as ITileCommandStreamFeature)?.Geometry);
                }
                // Deliberately NOT counted as a Features read: the buffer is built here, once, exactly as the
                // decoder builds a real layer's — so any read the runner performs is the runner's own.
                Geometry = new MvtGeometryMaterializer(tile, Extent, kinds, commands).Materialize();
            }

            public string Name   { get; }
            public uint   Extent => 4096;
            public TileGeometryBuffers Geometry { get; private set; }

            public IReadOnlyList<IFeature> Features
            {
                get { FeaturesReadCount++; return _features; }
            }

            public void Dispose() { TileGeometryBuffers g = Geometry; g.Dispose(); Geometry = default; }
        }

        private sealed class OneLayerDecodedTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            public OneLayerDecodedTile(ITileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
            public void Dispose() => (_layer as IDisposable)?.Dispose();
        }

        /// <summary>A render layer that records that it was reached and writes nothing — the store call in
        /// <see cref="TileMeshLayerProcessor.ProcessOnWorker"/> happens BEFORE this, so a no-op body still
        /// exercises the memo.</summary>
        private sealed class NoGeometryTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public int WriteIntoCallCount { get; private set; }

            public NoGeometryTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public RenderLayerBuild Build           => RenderLayerBuild.TileMesh;
            public DrawPersistence  Persistence     => DrawPersistence.Persistent;
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public Material         Material        => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
                out int vertexCount, out Bounds bounds)
            {
                WriteIntoCallCount++;
                Assert.IsTrue(geometry.IsCreated, "the processor must hand WriteInto a live shared buffer");
                vertexCount = 0;
                bounds      = default;
            }
        }

        /// <summary>
        /// IR B7a T4a — one source-layer is materialized <b>once per worker pass</b>, however many style
        /// layers name it. This is the tooth that would have caught the shape B7 exists to fix: three fill
        /// layers over one source-layer used to decode its geometry three times.
        ///
        /// <para><b>How to read the number — INVERTED in IR C1 P3.</b> Under B7 the store contributed one
        /// extra <c>Features</c> read on its memo miss, so N = 3 gave 3 + 1 = 4 and a dead memo gave 6. P3
        /// removed the store: the layer already holds its buffer, so obtaining geometry reads
        /// <c>Features</c> <b>zero</b> times and the count is exactly N. The assertion is therefore
        /// "<b>exactly</b> the layer count, not one more" — and it is still discriminating in the same
        /// direction: any consumer that re-derived geometry from the feature list (a re-materializing
        /// property, a resurrected per-pass store) would push it above N.</para>
        /// </summary>
        [Test]
        public void RunWorkerPass_ObtainsGeometryWithoutReReadingTheFeatureList()
        {
            const string SourceLayerName = "shared-source";
            const int    LayerCount      = 3;

            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                // One 300-unit square, well inside the tile: real rings, so the store really materializes.
                Geometry = new uint[]
                {
                    (1u << 3) | 1u, 200u, 200u,       // MoveTo (100, 100)
                    (3u << 3) | 2u, 600u, 0u,         // LineTo +300, 0
                                    0u,   600u,       // LineTo 0, +300
                                    599u, 0u,         // LineTo -300, 0
                    (1u << 3) | 7u,                   // ClosePath
                },
            };
            var sourceLayer = new CountingTileLayer(SourceLayerName, new IFeature[] { feature }, ContextTile);
            // Was a FixedDecodeHandle test double — "a handle over an ALREADY-BUILT tile". That is what a
            // lease now is, so the double is gone and this uses the production type.
            var handle      = new SharedDisposable<IDecodedTile>(new OneLayerDecodedTile(sourceLayer));

            var renderLayers = new NoGeometryTileMeshRenderLayer[LayerCount];
            var processors   = new ITileMeshLayerProcessor[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                renderLayers[i] = new NoGeometryTileMeshRenderLayer(new StyleLayer
                {
                    Id = $"fill-{i}", Source = "src", SourceLayer = SourceLayerName,
                });
                processors[i] = TileMeshLayerProcessor.AllocateForKick(renderLayers[i], materialIndex: i);
            }

            var context = MakeContext();
            IRenderLayerPayload[] payloads =
                TileLayerProcessorRunner.RunWorkerPass(handle, in context, processors);

            try
            {
                // Non-vacuity: all three layers really ran and really received geometry. Without this, a pass
                // that faulted on the first processor would report a low count and pass.
                for (int i = 0; i < LayerCount; i++)
                    Assert.AreEqual(1, renderLayers[i].WriteIntoCallCount,
                        $"precondition: layer {i} must have been reached with a live buffer");

                Assert.AreEqual(LayerCount, sourceLayer.FeaturesReadCount,
                    $"the source layer's Features must be read exactly {LayerCount} times — once per style " +
                    "layer by FeatureSelector (each has its own filter) and NOT AT ALL to obtain geometry, " +
                    "which the layer already owns (IR C1 P3). A consumer that re-derived the buffer from the " +
                    $"feature list would read it {LayerCount * 2} times and decode the same geometry once per " +
                    "layer — the 108-materializations-per-tile shape this epic exists to remove.");
            }
            finally
            {
                foreach (IRenderLayerPayload payload in payloads) payload?.Dispose();
                // The LEASE owns the decoded tile, and the decoded tile owns this source layer — so the one
                // release below is what disposes it. Disposing `sourceLayer` here directly and leaving the
                // lease live was a borrower freeing its lender's buffers and then a live owner sitting
                // around an already-disposed tile: two ownership violations for one missing line.
                handle.Release();
            }
        }

        // ── Primary semantic tooth ────────────────────────────────────────────────────────────────────

        [Test]
        public void RunWorkerPass_InvokesEveryProcessorOnceInDenseOrder_WithTheSameDecodedTile()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var processors = new ITileMeshLayerProcessor[]
            {
                new RecordingProcessor(0, materialIndex: 3, log),
                new RecordingProcessor(1, materialIndex: 5, log),
                new RecordingProcessor(2, materialIndex: 9, log),
            };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            IRenderLayerPayload[] payloads;
            try { payloads = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(3, log.Count, "every processor must be invoked exactly once");
            Assert.AreEqual(0, log[0].order, "dense order 0 first");
            Assert.AreEqual(1, log[1].order, "dense order 1 second");
            Assert.AreEqual(2, log[2].order, "dense order 2 third");

            Assert.IsNotNull(log[0].tile, "the decoded tile must be non-null");
            Assert.AreSame(log[0].tile, log[1].tile, "every processor must observe the SAME decoded tile reference");
            Assert.AreSame(log[0].tile, log[2].tile, "every processor must observe the SAME decoded tile reference");

            Assert.AreEqual(3, payloads.Length, "one payload per input slot");
            Assert.AreEqual(3, payloads[0].MaterialIndex);
            Assert.AreEqual(5, payloads[1].MaterialIndex);
            Assert.AreEqual(9, payloads[2].MaterialIndex);
        }

        // ── Fault-parity: the fan-out read faults ─────────────────────────────────────────────────────
        //
        // R2 (decode-refcount plan §1/§5): RunWorkerPass_WhenTheDecodedTileReadFaults_InvokesNoProcessors_
        // ButCompletesEveryPayload is RETIRED here, not "made to pass" — a genuine deviation from the R2
        // plan (§5's throw-guard audit did not surface this sibling of DecodedTileLeaseTests; recorded in
        // the dev report). It drove the "a fault AT THE FAN-OUT READ invokes no processors" half of A1's
        // fault policy through DecodedTileLease's release-then-read ObjectDisposedException — thrown from
        // `decode.Tile` BEFORE the processor loop even starts. SharedDisposable<T> is undefended by design
        // (no throw after the last Release()), so `decode.Value` after release just hands back the
        // (disposed) instance and the loop proceeds to RecordingProcessor — a fake that never reads native
        // memory — which then runs to completion instead of faulting: the anti-vacuity assertion this tooth
        // opened with can no longer be satisfied, and the read-fault site it existed to drive is gone.

        // ── Fault-parity: a processor throws ──────────────────────────────────────────────────────────

        [Test]
        public void RunWorkerPass_WhenAProcessorThrows_StopsLaterProcessors_ButCompletesEveryPayload()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, materialIndex: 1, log);
            var p1 = new RecordingProcessor(1, materialIndex: 2, log, throwOnProcess: true);
            var p2 = new RecordingProcessor(2, materialIndex: 3, log);
            var processors = new ITileMeshLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            IRenderLayerPayload[] payloads;
            try { payloads = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(1, log.Count, "only the processor BEFORE the fault runs");
            Assert.AreEqual(0, log[0].order);

            Assert.AreEqual(1, p0.CompleteCallCount, "p0 (ran normally) still settles exactly once");
            Assert.AreEqual(1, p1.CompleteCallCount, "p1 (threw) still settles exactly once");
            Assert.AreEqual(1, p2.CompleteCallCount, "p2 (never invoked) still settles exactly once — no stranded array");

            Assert.AreEqual(3, payloads.Length);
        }

        // ── The fault is VISIBLE, not just survivable (IR C1 fix stage, B1) ───────────────────────────
        //
        // RunWorkerPass' catch settles every processor as zero-vertex and carries on — correct, and
        // deliberately unchanged. What it must not do is stay SILENT: unrelated faults land in that one
        // catch and produce the identical invisible outcome, and one of them is the ObjectDisposedException
        // the lease raises when its tile is read after the last reference went — which the lease chose
        // precisely so a use-after-free would be loud. The symbol cadence already logs; these pin that the
        // mesh cadence, with 100+ layers behind it, does too — and that the log NAMES THE TILE, which is
        // the only thing that makes the warning actionable when many tiles are in flight.

        /// <summary>A distinctive address, so "the warning names the tile" cannot be satisfied by a zero
        /// that could have come from anywhere. Decode id and context tile stay the same value (this file's
        /// convention — IR C1 removed the second address copy).</summary>
        private static readonly TileId NamedTile = new TileId { Z = 9, X = 274, Y = 168 };

        private static TileLayerProcessContext MakeNamedContext() => new TileLayerProcessContext
        {
            Tile             = NamedTile,
            Zoom             = 9.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        [Test]
        public void RunWorkerPass_WhenAProcessorThrows_LogsAWarningNamingTheTile()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"9/274/168"));

            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, materialIndex: 1, log, throwOnProcess: true);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeNamedContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(NamedTile, SampleTileFixture.Bytes()));
            IRenderLayerPayload[] payloads;
            try { payloads = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            // Settle-everything behaviour is UNCHANGED by the log — asserted here so a future "simplify the
            // catch" cannot trade the fault policy for the diagnostic.
            Assert.AreEqual(1, p0.CompleteCallCount, "the faulting processor still settles exactly once");
            Assert.AreEqual(1, payloads.Length);
            Assert.IsNotNull(payloads[0]);
        }

        // R2 (decode-refcount plan §1/§5): RunWorkerPass_ReadingAReleasedLease_LogsAWarningNamingTheTile is
        // RETIRED alongside its sibling above, for the identical reason — it drove the SAME
        // release-then-read fault, over a NAMED tile, to pin that the runner's warning names the tile even
        // on this fault (not just a processor throw). With the read no longer able to fault, there is
        // nothing left for that log-message assertion to observe; RunWorkerPass_WhenAProcessorThrows_
        // LogsAWarningNamingTheTile (above) still pins the "log names the tile" property on the fault that
        // DOES still reach this runner.

        // ── A1 does not choreograph WorkerThenMain ────────────────────────────────────────────────────

        [Test]
        public void RunWorkerPass_WorkerThenMainProcessor_IsNotInvoked_AndStillSettles()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, materialIndex: 4, log, phase: LayerPhase.WorkerThenMain);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            IRenderLayerPayload[] payloads;
            try { payloads = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(0, log.Count, "A1 must never run a WorkerThenMain processor's worker step");
            Assert.AreEqual(1, p0.CompleteCallCount, "the reserved-phase processor must still settle (no stranded array)");
            Assert.AreEqual(1, payloads.Length);
            Assert.IsNotNull(payloads[0]);
        }

        // ── TileMeshLayerProcessor settlement (real adapter, not a recording fake) ────────────────────

        [Test]
        public void TileMeshLayerProcessor_FaultingWrite_ReturnsEmptyPayload_AndReleasesTrackedMeshData()
        {
            // "countries" is the fill-bearing source-layer in the fixture (see DisposalLeakGuardTests'
            // positive control) — a real match here proves WriteInto was actually reached before faulting,
            // not skipped by an empty feature-selection short-circuit.
            var styleLayer = new StyleLayer { Id = "throwing-test-layer", SourceLayer = "countries" };
            var fakeLayer  = new ThrowingTileMeshRenderLayer(styleLayer);

            long baseline = MeshDataPayload.DebugLiveAllocCount;

            var processor = TileMeshLayerProcessor.AllocateForKick(fakeLayer, materialIndex: 7);
            long afterAlloc = MeshDataPayload.DebugLiveAllocCount;
            Assert.Greater(afterAlloc, baseline,
                "AllocateForKick must track its writable MeshDataArray (MeshDataPayload.AllocateTracked)");

            var context = MakeContext();
            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            IRenderLayerPayload[] payloads;
            try
            {
                payloads = TileLayerProcessorRunner.RunWorkerPass(
                    decode, in context, new ITileMeshLayerProcessor[] { processor });
            }
            finally { decode.Release(); }

            Assert.AreEqual(1, payloads.Length);
            Assert.IsNotNull(payloads[0]);
            Assert.AreEqual(0, payloads[0].VertexCount,
                "a faulting WriteInto must settle as a zero-vertex payload, not upload partially-written data");
            Assert.AreEqual(7, payloads[0].MaterialIndex, "the material index survives the fault");

            payloads[0].Dispose();

            long afterDispose = MeshDataPayload.DebugLiveAllocCount;
            Assert.AreEqual(baseline, afterDispose,
                "the tracked MeshDataArray must be released after Dispose — no native leak on a faulting processor");
        }
    }
}
