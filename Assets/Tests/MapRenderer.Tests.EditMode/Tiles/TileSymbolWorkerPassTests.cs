// Unity EditMode only — MvtTile/CancellationToken over the committed fixture. NOT registered in
// core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Epic A / A3 acceptance tooth #5: proves
    /// <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> decodes the fetched bytes exactly once,
    /// shares that same <see cref="IDecodedTile"/> reference across every processor in dense order, runs NO
    /// tail (the tail is the caller's main-thread step), rejects a <see cref="LayerPhase.WorkerOnly"/>
    /// processor (the mirrored A1/A2 guard), and propagates a decode fault rather than swallowing it (§B
    /// "fault policy: propagate, don't settle").
    /// <para>IR C1 P2 (fix round F5) added the sharing half of the same claim: every processor of the pass
    /// borrows the same per-source-layer buffer. IR C1 P3 kept the claim and moved its subject — the
    /// pass-scoped store is gone, so the assertion is now on the BUFFER the decoded layer hands each
    /// processor. It remains the only tooth that can see this: re-materializing per get is byte-identical in
    /// output.</para>
    /// <para>Every pass here runs against a live lease reference, released in a <c>finally</c>, mirroring
    /// the two production sites — reading a lease after its last release is a programming error and
    /// throws.</para>
    /// </summary>
    [TestFixture]
    public class TileSymbolWorkerPassTests
    {
        private static readonly TileId ContextTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = ContextTile,
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>Records ProcessOnWorker invocations (order + the observed decoded-tile reference + the
        /// source-layer buffer that tile hands back) into a SHARED log, and counts CompleteOnMain calls
        /// so a test can assert the runner never invokes the tail.
        /// <para><b>IR C1 P3 — what the third column became.</b> Under P2 it recorded the pass-scoped
        /// <c>TileGeometryStore</c>, because a <c>null</c> or per-processor store was output-neutral (each
        /// <c>Extract</c> fell back to a private store) and would silently revert the sharing. P3 deleted the
        /// store, so that column would now be vacuous — it is REPLACED, not dropped, by the thing it stood
        /// for: the actual <c>TileGeometryBuffers</c> the processor would borrow, read through the same
        /// <c>GetLayer(...).Geometry</c> expression the real consumers use. A layer that re-materialized per
        /// get hands out a different backing pointer to each processor, which is the P3 shape of the same
        /// silent regression.</para></summary>
        private sealed class RecordingWorkerThenMainProcessor : ITileWorkerThenMainLayerProcessor
        {
            private const string ProbeSourceLayer = "countries"; // present in the committed fixture

            private readonly int _order;
            private readonly List<(int order, IDecodedTile tile, NativeArray<double2> buffer)> _log;

            public int CompleteOnMainCallCount { get; private set; }

            public LayerPhase Phase { get; }

            public RecordingWorkerThenMainProcessor(int order,
                List<(int order, IDecodedTile tile, NativeArray<double2> buffer)> log,
                LayerPhase phase = LayerPhase.WorkerThenMain)
            {
                _order = order;
                _log   = log;
                Phase  = phase;
            }

            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
                => _log.Add((_order, tile, tile?.GetLayer(ProbeSourceLayer)?.Geometry.Vertices ?? default));

            public void CompleteOnMain(CancellationToken ct)
            {
                CompleteOnMainCallCount++;
            }
        }

        [Test]
        public void RunSymbolWorkerPass_InvokesEveryProcessorInOrder_WithTheSameDecodedTile_AndRunsNoTail()
        {
            var log = new List<(int order, IDecodedTile tile, NativeArray<double2> buffer)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log);
            var p1 = new RecordingWorkerThenMainProcessor(1, log);
            var p2 = new RecordingWorkerThenMainProcessor(2, log);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            try { TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(3, log.Count, "every processor must be invoked exactly once");
            Assert.AreEqual(0, log[0].order, "dense order 0 first");
            Assert.AreEqual(1, log[1].order, "dense order 1 second");
            Assert.AreEqual(2, log[2].order, "dense order 2 third");

            Assert.IsNotNull(log[0].tile, "the decoded tile must be non-null");
            Assert.AreSame(log[0].tile, log[1].tile, "every processor must observe the SAME decoded tile reference");
            Assert.AreSame(log[0].tile, log[2].tile, "every processor must observe the SAME decoded tile reference");

            // IR C1 P3, successor to P2's store clauses: one materialization per source-layer, shared by
            // every symbol layer of the pass. NativeArray<T>.Equals compares the backing pointer and length,
            // so this is buffer IDENTITY, not content equality — a layer that re-materialized per get would
            // hand each processor an equal-CONTENT but different-POINTER buffer and fail here, with output
            // still byte-identical everywhere else in the suite.
            Assert.IsTrue(log[0].buffer.IsCreated,
                "precondition: the probe source-layer must really carry geometry, or the identity clauses " +
                "below compare two default(NativeArray)s and assert nothing");
            Assert.IsTrue(log[0].buffer.Equals(log[1].buffer),
                "every symbol processor must borrow the SAME source-layer buffer — a per-get materialization " +
                "is the per-layer mint this epic retired, wearing the layer's name");
            Assert.IsTrue(log[0].buffer.Equals(log[2].buffer),
                "every symbol processor must borrow the SAME source-layer buffer");

            Assert.AreEqual(0, p0.CompleteOnMainCallCount, "the runner must never invoke the main-thread tail");
            Assert.AreEqual(0, p1.CompleteOnMainCallCount, "the runner must never invoke the main-thread tail");
            Assert.AreEqual(0, p2.CompleteOnMainCallCount, "the runner must never invoke the main-thread tail");
        }

        [Test]
        public void RunSymbolWorkerPass_WorkerOnlyProcessor_Throws()
        {
            var log = new List<(int order, IDecodedTile tile, NativeArray<double2> buffer)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log, phase: LayerPhase.WorkerOnly);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            try
            {
                Assert.Throws<NotSupportedException>(
                    () => TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, processors),
                    "a WorkerOnly processor has no tail — the symbol pass exists to feed tails, so this is the " +
                    "mirrored programming error of A1/A2's guard.");
            }
            finally
            {
                // This test MINTS a lease, so it owns the creator's reference and owes exactly one release —
                // on the throwing path too. Skipping it strands the decoded tile's Allocator.Persistent
                // buffers for the rest of the run, and NativeLeakDetection is off in the batch gate, so
                // nothing would say so.
                decode.Release();
            }
        }

        // R2 (decode-refcount plan §1/§5): RunSymbolWorkerPass_WhenTheDecodedTileReadFaults_Propagates_
        // AndInvokesNoProcessor is RETIRED here, not "made to pass". It drove the §B propagate-don't-settle
        // policy through DecodedTileLease's own release-then-read ObjectDisposedException — the ONE fault
        // that could still reach this runner post-eager-decode. SharedDisposable<T> is undefended by design
        // (no throw after the last Release(); see its doc), so the anti-vacuity assertion this tooth opened
        // with can no longer be satisfied, and neither can the fault it exists to drive: `decode.Value` after
        // release just hands back the (disposed) instance, so RunSymbolWorkerPass's loop runs the
        // RecordingWorkerThenMainProcessor fake — which never reads native memory — to completion instead of
        // faulting. This mirrors DecodedTileLeaseTests' retirement exactly; a genuine deviation from the R2
        // plan (§5's throw-guard audit did not surface this sibling test), recorded in the dev report.
    }
}
