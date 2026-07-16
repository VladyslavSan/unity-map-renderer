// Unity EditMode only — MvtTile/UniTask/CancellationToken over the committed fixture. NOT registered in
// core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Epic A / A3 acceptance tooth #5 (docs/per-layer-tile-processing-a3-plan.md §F): proves
    /// <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> decodes the fetched bytes exactly once,
    /// shares that same <see cref="IDecodedTile"/> reference across every processor in dense order, runs NO
    /// tail (the tail is the caller's main-thread step), rejects a <see cref="LayerPhase.WorkerOnly"/>
    /// processor (the mirrored A1/A2 guard), and propagates a decode fault rather than swallowing it (§B
    /// "fault policy: propagate, don't settle"). A6: <c>new SharedTileDecode(bytes)</c> becomes
    /// <c>new SharedTileDecode(bytes, new MvtTileDecoder())</c>.
    /// </summary>
    [TestFixture]
    public class TileSymbolWorkerPassTests
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

        /// <summary>Records ProcessOnWorker invocations (order + the observed decoded-tile reference) into a
        /// SHARED log, and counts CompleteOnMainAsync calls so a test can assert the runner never invokes
        /// the tail.</summary>
        private sealed class RecordingWorkerThenMainProcessor : ITileWorkerThenMainLayerProcessor
        {
            private readonly int _order;
            private readonly List<(int order, IDecodedTile tile)> _log;

            public int CompleteOnMainAsyncCallCount { get; private set; }

            public LayerPhase Phase { get; }

            public RecordingWorkerThenMainProcessor(int order, List<(int order, IDecodedTile tile)> log,
                LayerPhase phase = LayerPhase.WorkerThenMain)
            {
                _order = order;
                _log   = log;
                Phase  = phase;
            }

            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _log.Add((_order, tile));

            public UniTask CompleteOnMainAsync(CancellationToken ct)
            {
                CompleteOnMainAsyncCallCount++;
                return UniTask.CompletedTask;
            }
        }

        [Test]
        public void RunSymbolWorkerPass_InvokesEveryProcessorInOrder_WithTheSameDecodedTile_AndRunsNoTail()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log);
            var p1 = new RecordingWorkerThenMainProcessor(1, log);
            var p2 = new RecordingWorkerThenMainProcessor(2, log);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            TileLayerProcessorRunner.RunSymbolWorkerPass(new SharedTileDecode(FixtureBytes(), new MvtTileDecoder()), in context, processors);

            Assert.AreEqual(3, log.Count, "every processor must be invoked exactly once");
            Assert.AreEqual(0, log[0].order, "dense order 0 first");
            Assert.AreEqual(1, log[1].order, "dense order 1 second");
            Assert.AreEqual(2, log[2].order, "dense order 2 third");

            Assert.IsNotNull(log[0].tile, "the decoded tile must be non-null");
            Assert.AreSame(log[0].tile, log[1].tile, "every processor must observe the SAME decoded tile reference");
            Assert.AreSame(log[0].tile, log[2].tile, "every processor must observe the SAME decoded tile reference");

            Assert.AreEqual(0, p0.CompleteOnMainAsyncCallCount, "the runner must never invoke the main-thread tail");
            Assert.AreEqual(0, p1.CompleteOnMainAsyncCallCount, "the runner must never invoke the main-thread tail");
            Assert.AreEqual(0, p2.CompleteOnMainAsyncCallCount, "the runner must never invoke the main-thread tail");
        }

        [Test]
        public void RunSymbolWorkerPass_WorkerOnlyProcessor_Throws()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log, phase: LayerPhase.WorkerOnly);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0 };
            var context = MakeContext();

            Assert.Throws<NotSupportedException>(
                () => TileLayerProcessorRunner.RunSymbolWorkerPass(new SharedTileDecode(FixtureBytes(), new MvtTileDecoder()), in context, processors),
                "a WorkerOnly processor has no tail — the symbol pass exists to feed tails, so this is the " +
                "mirrored programming error of A1/A2's guard.");
        }

        [Test]
        public void RunSymbolWorkerPass_MalformedBytes_Propagates_AndInvokesNoProcessor()
        {
            // Anti-vacuity: prove the malformed bytes genuinely reject at the direct decode call before
            // trusting the runner-level assertions below.
            Assert.Throws<InvalidOperationException>(() => MvtDecoder.Decode(MalformedBytes),
                "the malformed fixture must be rejected by MvtDecoder.Decode directly (anti-vacuity)");

            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0 };
            var context = MakeContext();

            Assert.Throws<InvalidOperationException>(
                () => TileLayerProcessorRunner.RunSymbolWorkerPass(new SharedTileDecode(MalformedBytes, new MvtTileDecoder()), in context, processors),
                "a decode fault must PROPAGATE (§B fault policy: propagate, don't settle) — swallowing it " +
                "would let a subsequent tail run over an empty extraction and commit an empty label list.");
            Assert.AreEqual(0, log.Count, "a decode fault must invoke NO processors");
        }
    }
}
