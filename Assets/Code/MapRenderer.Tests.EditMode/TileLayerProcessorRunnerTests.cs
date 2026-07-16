// Unity EditMode only — MeshDataPayload/Mesh.MeshDataArray are Unity types. NOT registered in
// core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Epic A / A1 acceptance tooth #2 (docs/per-layer-tile-processing-a1-plan.md): proves
    /// <see cref="TileLayerProcessorRunner.RunWorkerPass"/> decodes the fetched bytes exactly once and
    /// shares that same <see cref="IDecodedTile"/> reference across every processor in dense order, and that
    /// the pre-A1 fault policy (abort-on-first-fault, wrap-every-allocation) survives the move unchanged.
    /// A6: <c>new SharedTileDecode(bytes)</c> becomes <c>new SharedTileDecode(bytes, new MvtTileDecoder())</c>.
    /// </summary>
    [TestFixture]
    public class TileLayerProcessorRunnerTests
    {
        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        // A deliberately truncated length-delimited TileLayers field (tag 0x1A = field 3 wiretype 2, then a
        // varint length of 100 with zero bytes following) — MvtDecoder.Decode must throw decoding it.
        private static readonly byte[] MalformedBytes = { 0x1A, 0x64 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = new TileId { Z = 0, X = 0, Y = 0 },
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
            public Material Material => null;
            public void ApplyZoom(double zoom) { }
            public void Dispose() { }

            public ThrowingTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<ITileFeature> features, double zoom, double extent,
                TileId id, double3 tileOriginRender, IProjection projection, out int vertexCount, out Bounds bounds)
            {
                throw new InvalidOperationException("ThrowingTileMeshRenderLayer deliberate fault (test)");
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

            IRenderLayerPayload[] payloads =
                TileLayerProcessorRunner.RunWorkerPass(new SharedTileDecode(FixtureBytes(), new MvtTileDecoder()), in context, processors);

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

        // ── Fault-parity: malformed tile ──────────────────────────────────────────────────────────────

        [Test]
        public void RunWorkerPass_MalformedTile_InvokesNoProcessors_ButCompletesEveryPayload()
        {
            // Anti-vacuity: prove the malformed bytes genuinely reject at the direct decode call before
            // trusting the runner-level assertions below.
            Assert.Throws<InvalidOperationException>(() => MvtDecoder.Decode(MalformedBytes),
                "the malformed fixture must be rejected by MvtDecoder.Decode directly (anti-vacuity)");

            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, materialIndex: 1, log);
            var p1 = new RecordingProcessor(1, materialIndex: 2, log);
            var p2 = new RecordingProcessor(2, materialIndex: 3, log);
            var processors = new ITileMeshLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            IRenderLayerPayload[] payloads =
                TileLayerProcessorRunner.RunWorkerPass(new SharedTileDecode(MalformedBytes, new MvtTileDecoder()), in context, processors);

            Assert.AreEqual(0, log.Count, "a decode fault must invoke NO processors");

            Assert.AreEqual(1, p0.CompleteCallCount);
            Assert.AreEqual(1, p1.CompleteCallCount);
            Assert.AreEqual(1, p2.CompleteCallCount);

            Assert.AreEqual(3, payloads.Length);
            Assert.IsNotNull(payloads[0]); Assert.IsNotNull(payloads[1]); Assert.IsNotNull(payloads[2]);
        }

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

            IRenderLayerPayload[] payloads =
                TileLayerProcessorRunner.RunWorkerPass(new SharedTileDecode(FixtureBytes(), new MvtTileDecoder()), in context, processors);

            Assert.AreEqual(1, log.Count, "only the processor BEFORE the fault runs");
            Assert.AreEqual(0, log[0].order);

            Assert.AreEqual(1, p0.CompleteCallCount, "p0 (ran normally) still settles exactly once");
            Assert.AreEqual(1, p1.CompleteCallCount, "p1 (threw) still settles exactly once");
            Assert.AreEqual(1, p2.CompleteCallCount, "p2 (never invoked) still settles exactly once — no stranded array");

            Assert.AreEqual(3, payloads.Length);
        }

        // ── A1 does not choreograph WorkerThenMain ────────────────────────────────────────────────────

        [Test]
        public void RunWorkerPass_WorkerThenMainProcessor_IsNotInvoked_AndStillSettles()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, materialIndex: 4, log, phase: LayerPhase.WorkerThenMain);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeContext();

            IRenderLayerPayload[] payloads =
                TileLayerProcessorRunner.RunWorkerPass(new SharedTileDecode(FixtureBytes(), new MvtTileDecoder()), in context, processors);

            Assert.AreEqual(0, log.Count, "A1 must never run a WorkerThenMain processor's worker step");
            Assert.AreEqual(1, p0.CompleteCallCount, "the reserved-phase processor must still settle (no stranded array)");
            Assert.AreEqual(1, payloads.Length);
            Assert.IsNotNull(payloads[0]);
        }

        // ── TileMeshLayerProcessor settlement (real adapter, not a recording fake) ────────────────────

        [Test]
        public void TileMeshLayerProcessor_FaultingWrite_ReturnsEmptyPayload_AndReleasesTrackedMeshData()
        {
            // "countries" is the fill-bearing source-layer in the fixture (see S51DisposalLeakGuardTests'
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
            IRenderLayerPayload[] payloads = TileLayerProcessorRunner.RunWorkerPass(
                new SharedTileDecode(FixtureBytes(), new MvtTileDecoder()), in context, new ITileMeshLayerProcessor[] { processor });

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
