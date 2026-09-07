// Unity EditMode only — MeshDataPayload/Mesh.MeshDataArray are Unity types. NOT registered in
// core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Epic A / A1 acceptance tooth #2: proves
    /// <see cref="TileLayerProcessorRunner.RunWorkerPass"/> decodes the fetched bytes exactly once and
    /// shares that same <see cref="IDecodedTile"/> reference across every processor in dense order, and that
    /// the pre-A1 fault policy (abort-on-first-fault, settle-every-processor) survives the move unchanged.
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

        /// <summary>Records ProcessOnWorker invocations (order + the observed decoded-tile reference) into a
        /// SHARED log so a test can assert cross-processor invocation order/identity, and counts
        /// Release() calls so a test can assert exactly-once settlement.
        ///
        /// <para>job-scheduling-design.md §8 stage 5 Group B (§3.1): <see cref="TryTakeGraphRequest"/> hands
        /// back <see cref="Build"/>, a stub <see cref="ILayerMeshBuild"/> that owns nothing — R1 (the
        /// per-layer build-object stage): the interface's four members drop <c>MaterialIndex</c>, so the
        /// dense-slot contract the runner's settle loop actually produces (job-scheduling-design.md §3.1) is
        /// now observed as reference IDENTITY (<c>Assert.AreSame(processor.Build, output.Layers[i])</c>),
        /// not a carried field. This fake owns no native columns and is never rented from
        /// <c>LayerMeshBuildPool</c>, so it must never be <c>Dispose</c>d as though it did — unlike every
        /// other <see cref="ILayerMeshBuild"/> in the tree.</para></summary>
        private sealed class RecordingProcessor : ITileMeshLayerProcessor
        {
            /// <summary>A stub <see cref="ILayerMeshBuild"/> owning nothing — see this outer type's own doc.</summary>
            private sealed class StubBuild : ILayerMeshBuild
            {
                public JobHandle ScheduleMeasure(JobHandle deps) => default;
                public bool TryScheduleWrite(out JobHandle writeHandle) { writeHandle = default; return false; }
                public MeshDataPayload TakePayload() => null;
                public void Dispose() { }
            }

            private readonly int _order;
            private readonly List<(int order, IDecodedTile tile)> _log;
            private readonly bool _throwOnProcess;

            public int ReleaseCallCount { get; private set; }

            public LayerPhase Phase { get; }

            /// <summary>The stub <see cref="TryTakeGraphRequest"/> hands back — captured so a test can assert
            /// dense-slot IDENTITY against <c>output.Layers[i]</c> directly, by reference.</summary>
            public ILayerMeshBuild Build { get; } = new StubBuild();

            public RecordingProcessor(int order, List<(int order, IDecodedTile tile)> log,
                LayerPhase phase = LayerPhase.WorkerOnly, bool throwOnProcess = false)
            {
                _order          = order;
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

            public bool TryTakeGraphRequest(out ILayerMeshBuild build)
            {
                build = Build;
                return true;
            }

            public void Release() => ReleaseCallCount++;
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
                Geometry = MvtGeometryMaterializerTestFactory.Materialize(tile, Extent, kinds, commands);
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

        /// <summary>A render layer that records that it was reached and builds no graph request — the store
        /// call in <see cref="TileMeshLayerProcessor.ProcessOnWorker"/> happens BEFORE this, so a no-op body
        /// still exercises the memo. §3.5: <c>WriteIntoCallCount</c> is now <see cref="BuildGraphRequestCallCount"/>,
        /// returning <c>default</c> (<c>HasWork == false</c>, nothing to dispose).</summary>
        private sealed class NoGeometryTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public int BuildGraphRequestCallCount { get; private set; }

            public NoGeometryTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

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
                Assert.IsTrue(geometry.IsCreated, "the processor must hand BuildGraphRequest a live shared buffer");
                return null;
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
            TilePrologueOutput output = TileLayerProcessorRunner.RunWorkerPass(handle, in context, processors);

            try
            {
                // Non-vacuity: all three layers really ran and really received geometry. Without this, a pass
                // that faulted on the first processor would report a low count and pass.
                for (int i = 0; i < LayerCount; i++)
                    Assert.AreEqual(1, renderLayers[i].BuildGraphRequestCallCount,
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
                for (int i = 0; i < output.Layers.Length; i++) output.Layers[i]?.Dispose();
                // The LEASE owns the decoded tile, and the decoded tile owns this source layer — so the one
                // release below is what disposes it. Disposing `sourceLayer` here directly and leaving the
                // lease live was a borrower freeing its lender's buffers and then a live owner sitting
                // around an already-disposed tile: two ownership violations for one missing line.
                handle.Release();
            }
        }

        // ── Primary semantic tooth ────────────────────────────────────────────────────────────────────

        /// <summary>§3.1: the slot-join half that used to read <c>payloads[i].MaterialIndex</c> off a
        /// <c>FakePayload</c> the runner wrapped, then <c>output.Layers[i].MaterialIndex</c> directly, is now
        /// observed as reference IDENTITY (<c>output.Layers[i]</c> IS the processor's own build) — the
        /// four-member <see cref="ILayerMeshBuild"/> interface (R1) drops <c>MaterialIndex</c> entirely, so
        /// this is the dense-slot contract job-scheduling-design.md §3.1 actually specifies, observed on the
        /// array production uses.</summary>
        [Test]
        public void RunWorkerPass_InvokesEveryProcessorOnceInDenseOrder_WithTheSameDecodedTile()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, log);
            var p1 = new RecordingProcessor(1, log);
            var p2 = new RecordingProcessor(2, log);
            var processors = new ITileMeshLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(3, log.Count, "every processor must be invoked exactly once");
            Assert.AreEqual(0, log[0].order, "dense order 0 first");
            Assert.AreEqual(1, log[1].order, "dense order 1 second");
            Assert.AreEqual(2, log[2].order, "dense order 2 third");

            Assert.IsNotNull(log[0].tile, "the decoded tile must be non-null");
            Assert.AreSame(log[0].tile, log[1].tile, "every processor must observe the SAME decoded tile reference");
            Assert.AreSame(log[0].tile, log[2].tile, "every processor must observe the SAME decoded tile reference");

            Assert.AreEqual(3, output.Layers.Length, "one request slot per input slot");
            Assert.AreSame(p0.Build, output.Layers[0], "dense-slot identity (job-scheduling-design.md §3.1)");
            Assert.AreSame(p1.Build, output.Layers[1], "dense-slot identity (job-scheduling-design.md §3.1)");
            Assert.AreSame(p2.Build, output.Layers[2], "dense-slot identity (job-scheduling-design.md §3.1)");
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

        /// <summary>§3.2: mechanically preserved (<c>ReleaseCallCount</c>/<c>output.Layers.Length</c>), but
        /// its STATED REASON is rewritten — the old text said "no stranded array"; after B.3 no array is
        /// allocated at kick, so nothing can be stranded. What is actually at stake now is that every
        /// processor is returned to <see cref="TileMeshLayerProcessorPool"/> exactly once and no graph
        /// request is left un-taken.</summary>
        [Test]
        public void RunWorkerPass_WhenAProcessorThrows_StopsLaterProcessors_ButReleasesEveryProcessor()
        {
            var log = new List<(int order, IDecodedTile tile)>();
            var p0 = new RecordingProcessor(0, log);
            var p1 = new RecordingProcessor(1, log, throwOnProcess: true);
            var p2 = new RecordingProcessor(2, log);
            var processors = new ITileMeshLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(1, log.Count, "only the processor BEFORE the fault runs");
            Assert.AreEqual(0, log[0].order);

            Assert.AreEqual(1, p0.ReleaseCallCount, "p0 (ran normally) still settles exactly once");
            Assert.AreEqual(1, p1.ReleaseCallCount, "p1 (threw) still settles exactly once");
            Assert.AreEqual(1, p2.ReleaseCallCount, "p2 (never invoked) still settles exactly once — every processor returns to its pool");

            Assert.AreEqual(3, output.Layers.Length);
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
            var p0 = new RecordingProcessor(0, log, throwOnProcess: true);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeNamedContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(NamedTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            // Settle-everything behaviour is UNCHANGED by the log — asserted here so a future "simplify the
            // catch" cannot trade the fault policy for the diagnostic.
            Assert.AreEqual(1, p0.ReleaseCallCount, "the faulting processor still settles exactly once");
            Assert.AreEqual(1, output.Layers.Length);
            Assert.AreSame(p0.Build, output.Layers[0], "the faulting processor's slot still carries its own build");
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
            var p0 = new RecordingProcessor(0, log, phase: LayerPhase.WorkerThenMain);
            var processors = new ITileMeshLayerProcessor[] { p0 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(0, log.Count, "A1 must never run a WorkerThenMain processor's worker step");
            Assert.AreEqual(1, p0.ReleaseCallCount, "the reserved-phase processor must still settle (no stranded array)");
            Assert.AreEqual(1, output.Layers.Length);
            Assert.AreSame(p0.Build, output.Layers[0]);
        }

        // ── TileMeshLayerProcessor settlement (real adapter, not a recording fake) ────────────────────

        /// <summary>A minimal graph-arm layer whose <see cref="BuildGraphRequest"/> rents a REAL
        /// <see cref="FillLayerBuild"/> (<c>FillLayerBuild.Rent</c> — increments
        /// <see cref="LayerMeshBuildCounters.DebugLiveBuilds"/>) — the non-vacuity witness §3.4 requires: a build
        /// that can actually make the counter rise, so "returns to baseline" is a real property rather than
        /// trivially true on an empty ledger.</summary>
        private sealed class CountedGraphInputRenderLayer : ITileMeshRenderLayer
        {
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material Material => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public CountedGraphInputRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
                => FillLayerBuild.Rent(
                    new FillMeshPipeline.LayerInput
                    {
                        Geometry     = geometry,
                        RingVisitOrder = new NativeArray<int>(1, Allocator.Persistent),
                        OriginRender = context.TileOriginRender,
                        Projection   = context.Projection,
                    },
                    new NativeArray<Vector4>(1, Allocator.Persistent), materialIndex, payloadName);
        }

        /// <summary>A minimal graph-arm layer whose <see cref="BuildGraphRequest"/> always throws — proves
        /// <see cref="TileMeshLayerProcessor"/>'s settlement path on a real (not recording-fake) processor,
        /// on the graph-arm fault site that replaces the retired seam-arm <c>WriteInto</c> fault (§3.4).</summary>
        private sealed class ThrowingGraphInputRenderLayer : ITileMeshRenderLayer
        {
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material Material => null;
            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public ThrowingGraphInputRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
                => throw new InvalidOperationException("ThrowingGraphInputRenderLayer deliberate fault (test)");
        }

        /// <summary>§3.4: replaces
        /// <c>TileMeshLayerProcessor_FaultingWrite_ReturnsEmptyPayload_AndReleasesTrackedMeshData</c>. Its
        /// (a)-(d) assertions (a tracked <c>MeshDataArray</c>, a zero-vertex settle, the material index
        /// surviving the fault, no native leak on that array) lose their subject after B.3 deletes the
        /// kick-time allocation — a legitimate retirement, but the HAZARD CLASS this test guarded does not
        /// cease to exist, it RELOCATES: a fault inside a per-layer body now strands the graph build's own
        /// <c>Allocator.Persistent</c> columns instead, parked in <c>TileMeshLayerProcessor._build</c> —
        /// a field whose own doc calls its <c>Reset</c> dispose a "never-fired backstop". This test observes
        /// THAT relocated hazard directly via <see cref="Meshing.LayerMeshBuildCounters.DebugLiveBuilds"/>, the real
        /// native-leak guard now.
        ///
        /// <para>Two layers, the thrower SECOND, the first a real request-producing graph layer (required
        /// shape, §3.4) — otherwise "returns to baseline" would be trivially true on an empty ledger. The
        /// non-vacuity witness (<c>DebugLiveBuilds &gt; baseline</c>, asserted below before disposal) proves
        /// the counted layer's request really was counted before this test's own cleanup frees it.</para>
        ///
        /// <para><b>RED:</b> plan §3.4's own mandated injection — a no-op <c>Reset</c> backstop
        /// (<c>if (_build != null) _build.Dispose();</c> in <c>TileMeshLayerProcessor.Reset</c>) — CANNOT fire
        /// against this fixture: <see cref="ThrowingGraphInputRenderLayer.BuildGraphRequest"/> throws BEFORE
        /// building a request, so <c>_build</c> is never set and that backstop never
        /// runs for either processor. Working RED: drop the <c>LayerMeshBuildCounters.RecordDisposed()</c> call in
        /// <c>FillLayerBuild.Dispose()</c> (the disposal this test's own cleanup loop drives) —
        /// reds this test's own <c>Assert.AreEqual(baseline, LayerMeshBuildCounters.DebugLiveBuilds, …)</c> with
        /// <c>Expected: 0, But was: 1</c>. Executed and reverted.</para>
        /// </summary>
        [Test]
        public void FaultingGraphRequest_StillReturnsItsProcessor_AndLeaksNoRequest()
        {
            var countedLayer  = new CountedGraphInputRenderLayer(new StyleLayer { Id = "counted-test-layer", SourceLayer = "countries" });
            var throwingLayer = new ThrowingGraphInputRenderLayer(new StyleLayer { Id = "throwing-test-layer", SourceLayer = "countries" });

            var p0 = TileMeshLayerProcessor.AllocateForKick(countedLayer, materialIndex: 11);
            var p1 = TileMeshLayerProcessor.AllocateForKick(throwingLayer, materialIndex: 12);
            var processors = new ITileMeshLayerProcessor[] { p0, p1 };

            long baseline         = LayerMeshBuildCounters.DebugLiveBuilds;
            long negativeBaseline = TileBuildGraph.DebugNegativeObservations;

            var context = MakeContext();
            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            TilePrologueOutput output;
            try { output = TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(2, output.Layers.Length);

            // Non-vacuity witness (§3.4's own warning against a trivially-passing empty ledger): the counted
            // layer's factory-built request really is live before this test disposes it below.
            Assert.Greater(LayerMeshBuildCounters.DebugLiveBuilds, baseline,
                "the layer BEFORE the fault must have produced a real, counted graph build");

            try
            {
                Assert.IsTrue(ProbePoolContainsAndRestore(TileMeshLayerProcessorPool.Rent, TileMeshLayerProcessorPool.Return, p0),
                    "the layer BEFORE the fault must still be returned to TileMeshLayerProcessorPool by Release()");
                Assert.IsTrue(ProbePoolContainsAndRestore(TileMeshLayerProcessorPool.Rent, TileMeshLayerProcessorPool.Return, p1),
                    "the FAULTING layer's own processor must still be returned to TileMeshLayerProcessorPool by Release()");
            }
            finally
            {
                for (int i = 0; i < output.Layers.Length; i++) output.Layers[i]?.Dispose();
            }

            Assert.AreEqual(baseline, LayerMeshBuildCounters.DebugLiveBuilds,
                "every counted build must be freed once disposed — no native leak on the fault path");
            Assert.AreEqual(negativeBaseline, TileBuildGraph.DebugNegativeObservations,
                "no TileBuildGraph was ever double-disposed — this test never touches one, so the counter " +
                "must stay exactly where it started");
        }

        /// <summary>Rents up to <paramref name="maxProbe"/> times looking for <paramref name="target"/> by
        /// reference identity, then hands every rented instance back (restoring pool state) before
        /// returning whether it was found. A bounded, order-agnostic way to observe "was this instance
        /// returned to its pool" against a process-global <c>ConcurrentBag</c> pool shared with every other
        /// test in the run — <c>Rent</c>/<c>Return</c> give no ordering guarantee, so asserting identity on
        /// the very next <c>Rent()</c> alone would be flaky.</summary>
        private static bool ProbePoolContainsAndRestore<T>(Func<T> rent, Action<T> giveBack, T target, int maxProbe = 32)
            where T : class
        {
            var pulled = new List<T>();
            bool found = false;
            for (int i = 0; i < maxProbe; i++)
            {
                T candidate = rent();
                pulled.Add(candidate);
                if (ReferenceEquals(candidate, target)) { found = true; break; }
            }
            foreach (T item in pulled) giveBack(item);
            return found;
        }
    }
}
