// Unity EditMode only — MvtTile/Mesh types over the committed fixture. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Epic A / A4 acceptance teeth, carried onto the reference-counted <see cref="SharedDisposable{T}"/>: the
    /// mesh and symbol cadences of ONE kick observe the SAME <see cref="IDecodedTile"/> instance, in either
    /// arrival order. Successor to <c>SharedTileDecodeTests</c>.
    ///
    /// <para><b>The claim got stronger and the tooth got weaker, deliberately.</b> Under the lazy handle this
    /// asserted a real race outcome — whichever cadence read first performed the one decode and the other
    /// reused it. Under the eager decode the tile is already built when the kick receives it, so sharing is
    /// true BY CONSTRUCTION. It is still worth pinning: nothing else in the suite would notice a future
    /// change that re-introduced a per-pass decode (say, a handle that cloned its tile per reader), and the
    /// per-source-layer buffer sharing the whole IR C1 design rests on is exactly what that would break.</para>
    ///
    /// <para><b>What retired here.</b> The cached-fault tooth
    /// (<c>GetOrDecode_MalformedBytes_CachesTheFault_SameExceptionInstanceToEveryCaller</c>) is gone: there
    /// is no cache because there is no second decode, and malformed bytes can no longer reach a lease at all
    /// — <c>TileDecodeDispatch.DecodeAsync</c> faults the source's task and mints nothing. Its two surviving
    /// halves moved: <b>a fault in the mesh pass still settles every payload slot</b> and <b>the symbol pass
    /// PROPAGATES</b> are re-asserted in <c>TileLayerProcessorRunnerTests</c> against a released lease (the
    /// only fault that still reaches the runner), and <b>the fault is reported, once, as a DECODE fault</b>
    /// is <c>A7TileFeatureSourceTests</c>' and <c>EagerDecodeOwnershipTests</c>' now. The two-concurrent-caller
    /// canary retired too, replaced by <c>SharedDisposableTests</c>' refcount race — the quantity under
    /// contention changed from a lazy decode to a counter, and the old canary would be green against a
    /// broken counter.</para>
    /// </summary>
    [TestFixture]
    public class SharedDisposableSharingTests
    {
        /// <summary>The address these teeth decode at — the same one <see cref="MakeContext"/> processes at,
        /// because IR C1 P3 makes the decode's id the buffers' id and a mismatch would be a mispairing.</summary>
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = new TileId { Z = 0, X = 0, Y = 0 },
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        /// <summary>The production mint shape, minus the pool hop.</summary>
        private static SharedDisposable<IDecodedTile> Mint() =>
            new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(Tile, SampleTileFixture.Bytes()));

        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>Captures the observed <see cref="IDecodedTile"/> reference into a shared box, so a test
        /// can compare it across cadences.</summary>
        private sealed class CapturingMeshProcessor : ITileMeshLayerProcessor
        {
            private readonly IDecodedTile[] _box;
            public LayerPhase Phase => LayerPhase.WorkerOnly;
            public CapturingMeshProcessor(IDecodedTile[] box) => _box = box;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _box[0] = tile;
            public bool TryTakeGraphRequest(out ILayerMeshBuild build) { build = null; return false; }
            public void Release() { }
        }

        private sealed class CapturingSymbolProcessor : ITileWorkerThenMainLayerProcessor
        {
            private readonly IDecodedTile[] _box;
            public LayerPhase Phase => LayerPhase.WorkerThenMain;
            public CapturingSymbolProcessor(IDecodedTile[] box) => _box = box;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _box[0] = tile;
            public void CompleteOnMain(CancellationToken ct) { }
        }

        // ── F-1: cross-cadence ReferenceEquals, both arrival orders ──────────────────────────────────────

        [Test]
        public void OneKick_MeshFirstThenSymbol_BothCadencesObserveTheSameDecodedTile()
        {
            SharedDisposable<IDecodedTile> decode = Mint();
            var context = MakeContext();

            var meshBox = new IDecodedTile[1];
            var symbolBox = new IDecodedTile[1];

            // ONE reference around BOTH passes — the exact shape of TileManager.KickMeshBuild's pool lambda,
            // which wraps RunWorkerPass and symbolPass.RunWorkerAndHandoff together and releases in a finally.
            try
            {
                TileLayerProcessorRunner.RunWorkerPass(decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(meshBox) });
                TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(symbolBox) });
            }
            finally { decode.Release(); }

            Assert.IsNotNull(meshBox[0], "the mesh pass must observe a decoded tile");
            Assert.AreSame(meshBox[0], symbolBox[0],
                "two independent MvtDecoder.Decode calls can never return the same instance — reference " +
                "identity across cadences IS the decode-count-1 proof for ONE KICK. Both cadences read the " +
                "same tile, hence the same per-source-layer geometry buffers.");
        }

        [Test]
        public void OneKick_SymbolFirstThenMesh_BothCadencesObserveTheSameDecodedTile()
        {
            SharedDisposable<IDecodedTile> decode = Mint();
            var context = MakeContext();

            var symbolBox = new IDecodedTile[1];
            var meshBox = new IDecodedTile[1];

            try
            {
                TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(symbolBox) });
                TileLayerProcessorRunner.RunWorkerPass(decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(meshBox) });
            }
            finally { decode.Release(); }

            Assert.IsNotNull(symbolBox[0], "the symbol pass must observe a decoded tile");
            Assert.AreSame(symbolBox[0], meshBox[0],
                "reference identity across cadences IS the decode-count-1 proof for one kick — arrival order " +
                "must not change it.");
        }

        // ── Anti-vacuity for the two above: the fixture's bytes really do decode, and to a FRESH instance ──

        /// <summary>
        /// The <c>AreSame</c> assertions above are only meaningful if two decodes of these bytes would
        /// genuinely produce two instances. Stated here rather than assumed, because a decoder that
        /// memoized by tile id would make identity true for the wrong reason and silently retire both teeth.
        /// </summary>
        [Test]
        public void TwoDecodesOfTheSameBytes_ProduceDistinctTiles()
        {
            var decoder = new MvtTileDecoder();
            byte[] bytes = SampleTileFixture.Bytes();
            IDecodedTile a = decoder.Decode(Tile, bytes);
            IDecodedTile b = decoder.Decode(Tile, bytes);
            try
            {
                Assert.AreNotSame(a, b,
                    "precondition for the identity teeth above: the decoder is not memoized, so observing the " +
                    "SAME instance across two passes really does prove they shared one decode");
            }
            finally { a.Dispose(); b.Dispose(); }
        }
    }
}
