// Unity EditMode only — MvtTile/Mesh types over the committed fixture. NOT registered in core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Epic A / A4 acceptance teeth: proves
    /// <see cref="SharedTileDecode"/> decodes ONCE and shares the same <see cref="IDecodedTile"/> reference
    /// across both cadences regardless of arrival order (F-1), caches a decode fault and rethrows the SAME
    /// exception instance to every caller (F-4), and survives a bounded two-thread <c>GetOrDecode</c> race
    /// (F-5). No lifetime-protocol teeth — <see cref="SharedTileDecode"/> has none (Q1): retention is
    /// plain GC reachability. A6: <c>new SharedTileDecode(bytes)</c> becomes <c>new SharedTileDecode(bytes,
    /// new MvtTileDecoder())</c> — these teeth exercise the real MVT decoder, so the injected decoder is
    /// still MVT (a non-MVT decoder is <c>A6NonMvtDecoderTests</c>' job).
    /// </summary>
    [TestFixture]
    public class SharedTileDecodeTests
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

        /// <summary>Captures the observed <see cref="IDecodedTile"/> reference into a shared box, so a test
        /// can compare it across cadences.</summary>
        private sealed class CapturingMeshProcessor : ITileMeshLayerProcessor
        {
            private readonly IDecodedTile[] _box;
            public LayerPhase Phase => LayerPhase.WorkerOnly;
            public CapturingMeshProcessor(IDecodedTile[] box) => _box = box;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _box[0] = tile;
            public IRenderLayerPayload Complete() => new FakePayload(0);
        }

        private sealed class CapturingSymbolProcessor : ITileWorkerThenMainLayerProcessor
        {
            private readonly IDecodedTile[] _box;
            public LayerPhase Phase => LayerPhase.WorkerThenMain;
            public CapturingSymbolProcessor(IDecodedTile[] box) => _box = box;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _box[0] = tile;
            public UniTask CompleteOnMainAsync(CancellationToken ct) => UniTask.CompletedTask;
        }

        // ── F-1: decode-once, cross-cadence ReferenceEquals, both arrival orders ─────────────────────────

        [Test]
        public void GetOrDecode_MeshFirstThenSymbol_BothCadencesObserveTheSameDecodedTile()
        {
            var decode = new SharedTileDecode(FixtureBytes(), new MvtTileDecoder());
            var context = MakeContext();

            var meshBox = new IDecodedTile[1];
            TileLayerProcessorRunner.RunWorkerPass(decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(meshBox) });

            var symbolBox = new IDecodedTile[1];
            TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(symbolBox) });

            Assert.IsNotNull(meshBox[0], "the mesh pass must observe a decoded tile");
            Assert.AreSame(meshBox[0], symbolBox[0],
                "two independent MvtDecoder.Decode calls can never return the same instance — reference " +
                "identity across cadences IS the decode-count-1 proof (mesh decoded first, symbol reused it).");
        }

        [Test]
        public void GetOrDecode_SymbolFirstThenMesh_BothCadencesObserveTheSameDecodedTile()
        {
            var decode = new SharedTileDecode(FixtureBytes(), new MvtTileDecoder());
            var context = MakeContext();

            var symbolBox = new IDecodedTile[1];
            TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(symbolBox) });

            var meshBox = new IDecodedTile[1];
            TileLayerProcessorRunner.RunWorkerPass(decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(meshBox) });

            Assert.IsNotNull(symbolBox[0], "the symbol pass must observe a decoded tile");
            Assert.AreSame(symbolBox[0], meshBox[0],
                "reference identity across cadences IS the decode-count-1 proof (symbol decoded first, mesh reused it).");
        }

        // ── F-4: a cached decode fault rethrows the SAME exception instance to every caller ─────────────

        [Test]
        public void GetOrDecode_MalformedBytes_CachesTheFault_SameExceptionInstanceToEveryCaller()
        {
            // Anti-vacuity: prove the malformed bytes genuinely reject at the direct decode call.
            Assert.Throws<InvalidOperationException>(() => MvtDecoder.Decode(MalformedBytes),
                "the malformed fixture must be rejected by MvtDecoder.Decode directly (anti-vacuity)");

            var decode = new SharedTileDecode(MalformedBytes, new MvtTileDecoder());
            var context = MakeContext();

            // Mesh pass: the fault aborts the worker loop internally (RunWorkerPass's own catch) and still
            // settles every payload — the A1 fault-policy tooth, re-asserted through the shared entry.
            var payloads = TileLayerProcessorRunner.RunWorkerPass(
                decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(new IDecodedTile[1]) });
            Assert.AreEqual(1, payloads.Length);
            Assert.IsNotNull(payloads[0], "a decode fault must still settle every payload slot (A1 policy, unchanged by A4)");

            // Symbol pass: the SAME entry, already decode-attempted once above — must PROPAGATE the cached
            // fault (§B "fault policy: propagate, don't settle"), not attempt a second decode.
            InvalidOperationException fromSymbolPass = null;
            try
            {
                TileLayerProcessorRunner.RunSymbolWorkerPass(
                    decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(new IDecodedTile[1]) });
            }
            catch (InvalidOperationException ex) { fromSymbolPass = ex; }
            Assert.IsNotNull(fromSymbolPass, "the symbol pass must observe the decode fault");

            // A third, direct read of the same entry must rethrow the EXACT SAME instance — proving the
            // fault was cached, not re-decoded (re-decoding a malformed input yields a NEW exception
            // instance every time, so instance identity is the cache proof).
            InvalidOperationException direct = null;
            try { decode.GetOrDecode(); }
            catch (InvalidOperationException ex) { direct = ex; }
            Assert.IsNotNull(direct);
            Assert.AreSame(fromSymbolPass, direct,
                "the decode fault must be cached and rethrown as the SAME instance — re-decoding on each " +
                "call would yield distinct exception instances.");
        }

        // ── F-5: concurrency smoke — decode-inside-lock means at most one decode, no exception ──────────

        [Test]
        public void GetOrDecode_TwoConcurrentCallers_ObserveOneInstance_NoException()
        {
            // Bounded: exactly two pool tasks, once each — not a proof, a canary for decode-inside-lock (Q3).
            var decode = new SharedTileDecode(FixtureBytes(), new MvtTileDecoder());
            var results = new IDecodedTile[2];

            var t0 = Task.Run(() => results[0] = decode.GetOrDecode());
            var t1 = Task.Run(() => results[1] = decode.GetOrDecode());
            Assert.IsTrue(Task.WaitAll(new[] { t0, t1 }, TimeSpan.FromSeconds(10)), "both callers must complete");

            Assert.IsNotNull(results[0]);
            Assert.AreSame(results[0], results[1], "concurrent callers must observe the SAME decoded instance");
        }
    }
}
