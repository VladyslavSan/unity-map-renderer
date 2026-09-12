// job-scheduling-design.md §8 stage 3 — SourceTileGraphBuildTests: the source tile's THREE-STEP polled
// progression (Prologue → Measure → Write, not two) and the pen at every step + teardown.
//
// Drive rules (lessons-learned.md): pump BEFORE testing AllTilesSettled(); assert a drive precondition
// before any outcome; every process-wide counter as a DELTA; never Await/Drain while a delay job holds a
// graph step — both Complete() on main, which does not hang (SpinUntilGateJob is bounded by
// MaxIterations) but burns the whole bound (NIT 3).
//
// Fixture: SampleTileFixture + a fill layer on `countries`. Most of this file's teeth use a z0
// single-tile cover — one tile's own step transition is the claim. Two teeth (job-scheduling-design.md
// §11 fork 2 — the deleted two-units-per-tile build-budget rule) instead use a z5 MULTI-tile cover: the
// property under test is about admission across tiles competing for one Tick's budget, which a
// single-tile fixture cannot exercise.

using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class SourceTileGraphBuildTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Fill-only style over a real MVT fixture on `countries` — the fixture this whole file
        /// shares (plan §5 preamble).</summary>
        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        // ── Tooth (a): three-step progression through Prologue → Measure → Write ─────────────────

        /// <summary>
        /// A source tile's kicked build is now a THREE-STEP polled progression — Prologue (a managed
        /// <c>IWorkScheduler</c> body) → Measure → Write (both graph jobs) — not two. Holds the PROLOGUE
        /// deterministically on <see cref="TileManager.MeshBuildGateForTest"/> (armed BEFORE the kick, so
        /// the worker parks before doing any work) and the MEASURE step on
        /// <see cref="TileManager.GraphDepsForTest"/> (a <see cref="SpinUntilGateJob"/> — the
        /// production-legitimate deps-parameter seam, job-scheduling-design.md E2 option iii). Releasing
        /// them in sequence proves each step is genuinely observable in isolation before the tile settles.
        ///
        /// <para><b>RED:</b> fold <c>CompleteMeasureAndScheduleWrite</c> into the prologue-complete arm —
        /// <c>GraphMeasureInFlight</c> never reads &gt;= 1 (the write is scheduled the same tick the
        /// prologue completes) and the settle-tick floor drops from 3 to 2.</para>
        /// </summary>
        [Test]
        public void SourceTile_ProgressesThroughPrologueThenMeasureThenWrite_BeforeSettling()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_A");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0; // z0: exactly one covered tile
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;
            var meshGate = new ManualResetEventSlim(false);

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                // Armed BEFORE the kick: the worker parks on it (jointly with the lifetime token) as its
                // very first statement, so the tile's prologue is held genuinely in-flight from the moment
                // it is kicked.
                view.TileManager.MeshBuildGateForTest = meshGate;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0,
                    "drive precondition: the source tile's build must have been started before anything " +
                    "below can observe a step.");

                var snap = view.CaptureTelemetry();
                Assert.GreaterOrEqual(snap.PrologueInFlight, 1,
                    "the tile must be observed in its PROLOGUE step — deterministic while the gate holds " +
                    "the worker before it does any work.");
                Assert.AreEqual(0, snap.GraphMeasureInFlight,
                    "no graph may exist yet — the prologue has not handed off to ScheduleMeasureFromDecode.");

                // Release the prologue. AwaitInFlightMeshBuilds parks on the WorkHandle only while Step is
                // still Prologue (it has not yet handed off), so this cannot race the hand-off or block on
                // the still-gated measure delay (NIT 3).
                meshGate.Set();
                view.AwaitInFlightMeshBuilds();
                view.LateUpdate();
                tick++; // this LateUpdate() is the prologue-complete tick — count it, or settleTick-kickTick
                        // under-reports by one (this WAS a real bug, caught by RED-verification below).

                snap = view.CaptureTelemetry();
                Assert.AreEqual(0, snap.PrologueInFlight,
                    "the prologue must have completed and handed off to the graph by now.");
                Assert.GreaterOrEqual(snap.GraphMeasureInFlight, 1,
                    "the tile must be observed in its MEASURE step — deterministic under the still-held " +
                    "delay job (GraphDepsForTest), since the measure graph cannot possibly have completed yet.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must not read settled while its measure step is genuinely held incomplete.");

                // Release the measure delay — Measure, then Write, proceed normally from here on. No
                // further Await here: the design's own RED claim (job-scheduling-design.md §8 stage 3
                // tooth (a)) is a STRUCTURAL fact about the dispatch (each pump arm `continue`s at most once
                // per tile per Tick), so it survives any completion latency, not a timing bet.
                gate[0] = 1;

                int settleTick = -1;
                for (; tick < 3000 && settleTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.AllTilesSettled()) settleTick = tick;
                }
                Assert.GreaterOrEqual(settleTick, 0, "the tile must eventually settle.");
                Assert.GreaterOrEqual(settleTick - kickTick, 3,
                    $"settling must take at least THREE ticks after the kick tick (kickTick={kickTick}, " +
                    $"settleTick={settleTick}) — kick@K (Prologue) → prologue-complete@K+1 (schedules " +
                    "Measure) → write-kick@K+2 (Measure complete, schedules Write) → consume@K+3 (Write " +
                    "complete). A pump that folds the measure-schedule into the prologue-complete arm would " +
                    "settle at kickTick+2, matching the RED this tooth exists to catch.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from " +
                    "a build that never happened.");
            }
            finally
            {
                // Unconditional, before any dispose — an early failure (before either gate is ever reached)
                // must still be able to release/complete both holds before disposing gate/started/outVals.
                meshGate.Set();
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                meshGate.Dispose();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── Tooth (b): the Burst work is not inside the prologue body, and fills allocate nothing at kick ──

        /// <summary>
        /// The prologue body itself (dispatched through <see cref="TileManager.WorkScheduler"/>) does
        /// selection + the paint bake + <c>BuildGraphRequest</c> — genuinely managed work — and returns
        /// WITHOUT touching Burst: no <see cref="Mesh.MeshDataArray"/> is allocated at kick for a fill-only
        /// style (stall #5, closed). The Burst measure graph is scheduled only once the PUMP (not the
        /// body) hands the prologue's output to <c>TileBuildGraph.ScheduleMeasureFromDecode</c>, on the
        /// NEXT tick — proven here by watching <see cref="FillGraphOutput.DebugLiveCount"/> move only
        /// after that tick, held open by <see cref="TileManager.GraphDepsForTest"/> so "the geometry left
        /// the body" is deterministic rather than a race against however fast the graph completes.
        ///
        /// <para><b>RED (job-scheduling-design.md §8 stage 5 Group B rewrite):</b> the original two-step
        /// recipe named <c>_graphArm</c>/<c>WriteInto</c> — both deleted by this stage, so neither step can
        /// fire any more (there is no seam arm left to fall back to). Working recipe over what actually
        /// remains: inject a bare <c>MeshDataPayload.AllocateTracked(1);</c> call at the top of
        /// <c>TileMeshLayerProcessor.AllocateForKick</c> — reds this test's own
        /// <c>Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount, …)</c> ("no MeshDataArray
        /// may be allocated at kick") with <c>Expected: 0, But was: 1</c>. Executed and reverted.</para>
        /// </summary>
        [Test]
        public void SourceTile_BurstWorkNotInsideThePrologueBody_AndFillsAllocateNothingAtKick()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_B");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;

                int caller = Environment.CurrentManagedThreadId;
                var spy    = new RecordingWorkScheduler(new InlineWorkScheduler());
                view.TileManager.WorkScheduler = spy;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0,
                    "drive precondition: the source tile's prologue must have been started.");

                Assert.GreaterOrEqual(spy.ScheduleCount, 1,
                    "the prologue kick must go THROUGH the injected scheduler — the seam still carries it.");
                Assert.GreaterOrEqual(spy.BodyThreadIds.Count, 1,
                    "the body must actually have run at least once, or the per-thread-id check below is vacuous.");
                foreach (int tid in spy.BodyThreadIds)
                    Assert.AreEqual(caller, tid,
                        "under Inline the prologue body runs on the CALLING thread, with zero dispatch.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataArray may be allocated at kick for a fill-only style — the graph arm " +
                    "allocates in its write step, not at kick (stall #5).");

                // Next tick: the prologue-complete arm hands off to ScheduleMeasureFromDecode — the
                // geometry LEAVES the body onto a real Burst measure graph, held open by the still-gated
                // delay job.
                view.LateUpdate();
                Assert.Greater(FillGraphOutput.DebugLiveCount, graphOutputBaseline,
                    "a real measure graph must have been scheduled by now — the geometry left the prologue body.");
                Assert.AreEqual(0, view.MeshDataArraysAllocatedLastKick(),
                    "the measure step allocates no MeshDataArray of its own — only the write step does.");

                // Release the delay — the write step allocates exactly one array (one non-empty fill layer).
                gate[0] = 1;
                int writeTick = -1;
                for (; tick < 3000 && writeTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.MeshDataArraysAllocatedLastKick() > 0) writeTick = tick;
                }
                Assert.GreaterOrEqual(writeTick, 0, "the write step must eventually allocate.");
                Assert.AreEqual(1, view.MeshDataArraysAllocatedLastKick(),
                    "exactly one MeshDataArray must be allocated on the write-kick tick — one per non-empty " +
                    "fill layer.");

                for (; tick < 3000 && !view.AllTilesSettled(); tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.IsTrue(view.AllTilesSettled(), "the tile must eventually settle.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from " +
                    "a build that never happened.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── Tooth (d): the pen at every step, and teardown ────────────────────────────────────────

        /// <summary>
        /// Case 1 (Prologue): a tile released mid-flight while genuinely in its PROLOGUE step — held on
        /// <see cref="TileManager.MeshBuildGateForTest"/>, armed before any kick. At this instant NOTHING
        /// graph-arm exists yet (the worker has not even started); the pen is
        /// <c>TilePrologueOutput.Dispose()</c> via <c>PendingDisposalQueue.DrainCompleted</c>'s <c>WorkHandle</c> sweep,
        /// which only runs once the released worker actually produces a result — <see cref="LayerMeshBuildCounters.DebugTotalBuildsCreated"/>
        /// is the non-vacuity witness (R4): it can only advance AFTER the gate opens, because the columns
        /// it counts do not exist until the worker's <c>BuildGraphRequest</c> call runs.
        ///
        /// <para><b>RED:</b> <c>PendingDisposalQueue.DrainCompleted</c> skips <c>GetResult().Dispose()</c> for a succeeded
        /// prologue task — <c>LayerMeshBuildCounters.DebugLiveBuilds</c> stays elevated above baseline forever.</para>
        /// </summary>
        [Test]
        public void ReleasedMidFlight_Prologue_StashesInThePen_ThenDrainsOnceTheWorkerCompletes()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_D_Prologue");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5; // NOT z0 — a z0 cover does not change under this pan, so
                                                    // nothing would ever be evicted (matches every other
                                                    // pan-to-evict tooth in this codebase: z2/z5, never z0).
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxReleasesPerTick     = 64; // evict the whole condemned cover on the pan tick
            view.WithTestCamera();

            var meshGate = new ManualResetEventSlim(false);

            try
            {
                long graphBaseline       = TileBuildGraph.DebugLiveCount;
                long requestsBaseline    = LayerMeshBuildCounters.DebugLiveBuilds;
                long totalCreatedBefore  = LayerMeshBuildCounters.DebugTotalBuildsCreated;
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;
                long negativesBaseline   = TileBuildGraph.DebugNegativeObservations;

                view.TileManager.MeshBuildGateForTest = meshGate; // armed BEFORE any kick

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                for (int f = 0; f < 3000 && view.CaptureTelemetry().PrologueInFlight < 1; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().PrologueInFlight, 1,
                    "drive precondition: the tile must be genuinely held in its PROLOGUE step before the pan.");

                // Pan far east — evict the tile while its prologue is still gated shut. Bounded pump, not a
                // single tick: eviction is not guaranteed to land in the very first LateUpdate() after the
                // pan (observed flaky at one tick), and the gate stays held throughout either way — nothing
                // here lets the prologue itself proceed.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                for (int f = 0; f < 3000 && view.ReleasedMidFlightCount() == 0; f++)
                    view.LateUpdate();

                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "positive control: the tile must have been released while its prologue task was still " +
                    "genuinely in-flight (gate held), or the pen assertions below are vacuous.");
                // This precondition is what makes the R4 witness below non-vacuous, and it is the one thing
                // distinguishing this case from the Measure case, where the SAME witness was dropped as
                // vacuous — guard it explicitly so a future edit can't silently reopen that gap here.
                Assert.AreEqual(totalCreatedBefore, LayerMeshBuildCounters.DebugTotalBuildsCreated,
                    "the counter must NOT have advanced while the gate is shut — this case's witness below " +
                    "depends on the shared gate blocking every cover worker. Under InlineWorkScheduler the " +
                    "rest of the cover completes during the wait and the witness becomes vacuous, which is " +
                    "exactly why the Measure case dropped it. It also depends on THIS fixture's style " +
                    "declaring no `background` layer — KickSourcelessBackground runs on the main thread " +
                    "ungated by MeshBuildGateForTest and, since LayerMeshBuildCounters counts unconditionally " +
                    "(unlike the retired per-factory counting), a background tile's build would advance " +
                    "this counter regardless of the gate; FillStyle() never declares one, so that path never " +
                    "runs here.");

                // Release the gate — the worker now runs (the record is already gone from _loaded, but the
                // WorkHandle keeps running independently) and PendingDisposalQueue.DrainCompleted will dispose its result.
                // Bounded but generous: this poll cannot Await (the record is gone from _loaded), so it
                // needs enough real wall-clock for a ThreadPool worker to actually run and be observed by a
                // later Tick's PendingDisposalQueue.DrainCompleted — a plain LateUpdate() call is cheap enough that even
                // 100k iterations finish in well under a second.
                meshGate.Set();

                for (int f = 0; f < 100_000 && LayerMeshBuildCounters.DebugTotalBuildsCreated <= totalCreatedBefore; f++)
                    view.LateUpdate();
                Assert.Greater(LayerMeshBuildCounters.DebugTotalBuildsCreated, totalCreatedBefore,
                    "the released worker must have built a real ILayerMeshBuild — the non-vacuity witness: " +
                    "this counter can only advance once the gate opens, since the columns it counts do not " +
                    "exist before the worker runs.");

                for (int f = 0; f < 100_000 && LayerMeshBuildCounters.DebugLiveBuilds > requestsBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "once the released prologue's result is drained, its build's own columns must be " +
                    "disposed — back to baseline.");
                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "no TileBuildGraph is ever created for a tile evicted before its prologue completed — " +
                    "it never reaches ScheduleMeasureFromDecode.");
                Assert.AreEqual(graphOutputBaseline, FillGraphOutput.DebugLiveCount,
                    "no measure graph is ever scheduled for a tile evicted before its prologue completed.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload was ever allocated for this tile.");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                meshGate.Set();
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                meshGate.Dispose();
            }
        }

        /// <summary>
        /// Case 2 (Measure): a tile released mid-flight while genuinely in its MEASURE step — the prologue
        /// runs INLINE (no async parking needed there; the measure step is what this test holds), gated on
        /// <see cref="TileManager.GraphDepsForTest"/>. <c>RenderTeardownRecord</c> stashes the still-live
        /// <see cref="TileBuildGraph"/> via <c>_pending.StashGraph</c> rather than disposing it inline
        /// (which would <c>Complete()</c> — and block on — the gated job).
        ///
        /// <para><b>RED:</b> the pen skips <c>graph.Dispose()</c> — <c>TileBuildGraph.DebugLiveCount</c>
        /// stays elevated above baseline forever.</para>
        ///
        /// <para><b>Recorded limitation: no non-vacuity witness for "a real request was created" survives
        /// this case's multi-tile cover.</b> <see cref="LayerMeshBuildCounters.DebugTotalBuildsCreated"/> is a
        /// process-wide monotonic counter; under <c>InlineWorkScheduler</c> every OTHER cover tile's
        /// prologue also runs synchronously (on whichever Tick first kicks it), so the counter advances from
        /// unrelated cover traffic during the very drive loop this test uses to reach MEASURE — independent
        /// of whether the released tile's own request or pen-drain ever ran correctly. Moving the baseline
        /// capture past <c>LoadTestStyle</c> closed the WORST form (guaranteed-true from load alone) but not
        /// this one; confirmed by instrumenting the counter right before the pan, which read 3 requests
        /// created with the gate still shut. Isolating to a single-tile cover, or witnessing a quantity
        /// scoped to the released tile's own <see cref="TileBuildGraph"/> instance rather than a process-wide
        /// static, would close it properly — deferred rather than attempted under this test's existing
        /// shared-gate, multi-tile shape.</para>
        /// </summary>
        [Test]
        public void ReleasedMidFlight_Measure_StashesInThePen_ThenDrainsOnceTheDelayJobCompletes()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_D_Measure");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5; // NOT z0 — see the Prologue case's own comment.
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxReleasesPerTick     = 64;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                long graphBaseline       = TileBuildGraph.DebugLiveCount;
                long requestsBaseline    = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long negativesBaseline   = TileBuildGraph.DebugNegativeObservations;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;
                view.TileManager.WorkScheduler     = new InlineWorkScheduler();

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 1; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "drive precondition: the tile must be genuinely held in its MEASURE step before the pan.");

                // Bounded pump, not a single tick — see the Prologue case's own comment on why (observed
                // flaky at one tick). The gate stays held throughout: nothing here lets the measure job proceed.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                for (int f = 0; f < 3000 && view.ReleasedMidFlightCount() == 0; f++)
                    view.LateUpdate();

                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "positive control: the tile must have been released while its measure step was still " +
                    "genuinely in-flight (gate held), or the pen assertions below are vacuous.");
                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "the evicted graph must still be LIVE right after eviction — the pen defers disposal, it " +
                    "does not skip it.");

                gate[0] = 1; // release — the pen's Dispose() can now Complete() without blocking

                // Bounded but generous — see the Prologue case's own comment on why this cannot Await.
                for (int f = 0; f < 100_000 && TileBuildGraph.DebugLiveCount > graphBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "once the gated job completes, the pen must drain: PendingDisposalQueue.DrainCompleted disposes the " +
                    "released graph on a later tick.");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "the graph's own builds' columns must be freed with it.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak once the pen has drained — the write step never ran.");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary>
        /// Case 3 (Write): a tile released once its WRITE step is COMPLETE but UNCONSUMED (<c>MaxConsumesPerTick
        /// = 0</c>) — the "complete but unconsumed" case <c>RenderTeardownRecord</c> also pens, distinct from
        /// the two genuinely-in-flight cases above: the write handle IS complete at release time, so
        /// <see cref="MapViewTestExtensions.ReleasedMidFlightCount"/> does NOT count it (it is guarded on
        /// <c>!lt.Graph.IsStepComplete</c>) — this case asserts only the pen/drain shape, not that guard.
        ///
        /// <para><b>RED:</b> <c>RenderTeardownRecord</c> skips <c>_pending.StashGraph</c> for
        /// <c>Step == Write</c> — the released graph is never disposed, <c>TileBuildGraph.DebugLiveCount</c>
        /// stays elevated above baseline forever.</para>
        ///
        /// <para><b>No <see cref="LayerMeshBuildCounters.DebugTotalBuildsCreated"/> witness — same recorded
        /// limitation as the Measure case's own doc: a process-wide monotonic counter can't attest to this
        /// specific tile's activity in a multi-tile cover, and this case already has a sound non-vacuity
        /// pair on <see cref="TileBuildGraph.DebugLiveCount"/> (the <c>Greater</c> right after eviction, the
        /// <c>AreEqual</c> after drain — see the comment above the first) that does not depend on it.</para>
        /// </summary>
        [Test]
        public void ReleasedCompleteButUnconsumed_Write_StashesInThePen_ThenDrains()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_D_Write");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5; // NOT z0 — see the Prologue case's own comment.
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxConsumesPerTick     = 0; // block consume — leave the write complete but untaken
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxReleasesPerTick     = 64;
            view.WithTestCamera();

            try
            {
                long graphBaseline       = TileBuildGraph.DebugLiveCount;
                long requestsBaseline    = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long negativesBaseline   = TileBuildGraph.DebugNegativeObservations;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Pump until the write step is complete but unconsumed — ConsumeBacklog sees it.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the tile's write step must be complete but unconsumed before the pan.");
                Assert.Greater(MeshDataPayload.DebugLiveAllocCount, payloadBaseline,
                    "non-vacuous precondition: the completed write must hold a real allocated payload.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate();

                // On its own this is true whether or not eviction landed (the graph is alive from load,
                // MaxConsumesPerTick=0 keeps it that way). It is a sound positive control only paired with
                // the AreEqual below: with consume blocked, a tile that was never evicted has no path to
                // disposal, so an un-evicted run would exhaust that bound and fail there instead.
                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "the evicted graph must still be LIVE right after eviction — this case's pen defers " +
                    "disposal to the drain, exactly like the genuinely-in-flight cases.");

                for (int f = 0; f < 100_000 && TileBuildGraph.DebugLiveCount > graphBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "the pen must drain the complete-but-unconsumed graph — PendingDisposalQueue.DrainCompleted disposes it " +
                    "(a Burst job cannot fault, so IsStepComplete is the only gate; no wait was needed here).");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "the graph's own builds' columns must be freed with it.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "the completed-but-never-taken write payload must be freed too — the pen disposes " +
                    "whatever CompleteWriteAndTakePayloads never claimed.");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// UMR-127 investigation: the SAME hold as the Write case above, but the graph is parked by calling
        /// <see cref="TileManager.SetSources"/> with an EMPTY source list rather than a pan — zero sources
        /// ⇒ zero cover ⇒ nothing is ever fetched, measured, or written again for the rest of this test, so
        /// no other job can incidentally flush the batch queue.
        ///
        /// <para>This proves the drain works when the parked graph's
        /// <see cref="TileBuildGraph.IsStepComplete"/> is <b>already true</b> at parking time (the common
        /// case — <c>DrainPendingDisposal</c> disposes it on the very next call, no other scheduling
        /// needed). It does <b>not</b> reproduce UMR-127's rare leak: a ~1-in-1000 pan-eviction run leaves
        /// a handful of <see cref="TileBuildGraph"/> instances live even though the pen ends up empty and
        /// every parked graph's handle completed — i.e. those instances never entered
        /// <c>_pendingGraphDisposal</c> at all. That leak was characterised with a throwaway repeated-trial
        /// harness (not landed — see the ticket) and is still open.</para>
        /// </summary>
        [Test]
        public void ParkedGraph_AlreadyComplete_DrainsWithoutAnyOtherJobScheduled()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_ParkedGraphDrain");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxConsumesPerTick     = 0; // block consume — leave the write complete but untaken
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxReleasesPerTick     = 64;
            view.WithTestCamera();

            try
            {
                long graphBaseline = TileBuildGraph.DebugLiveCount;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Pump until the write step is complete but unconsumed — same precondition as the Write case.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the tile's write step must be complete but unconsumed " +
                    "before eviction.");

                // Evict via the SAME RenderTeardownRecord funnel every abandonment path uses — but with
                // ZERO replacement sources, so no cover is ever recomputed and nothing is ever scheduled
                // again for the rest of this test.
                view.TileManager.SetSources(Array.Empty<TileManager.SourceSpec>(), view.Config.Backend);

                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "positive control: the evicted graph must still be LIVE right after eviction — the pen " +
                    "defers disposal to the drain.");

                // Small, bounded pump — nothing left in this test can ever schedule a new job, so a fixed
                // small budget is exactly as conclusive as a much larger one for this (already-complete) case.
                for (int f = 0; f < 1000 && TileBuildGraph.DebugLiveCount > graphBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "the pen must drain a parked graph whose handle is already complete, with no other tick " +
                    "activity required to notice it.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Case 4 (Teardown, R2): the SAME hold as the Measure case, but the tile is torn down through
        /// <see cref="MapView.Teardown"/> directly — without settling, and without ever panning it out of
        /// cover — proving <see cref="TileManager.DoDispose"/>'s own graph pen (fed by the SAME
        /// <c>RenderTeardownRecord</c> funnel every other abandonment path uses) disposes a genuinely
        /// in-flight graph rather than leaking it.
        ///
        /// <para><b>RED:</b> <c>DoDispose</c> skips the graph pen's <c>Dispose()</c> sweep —
        /// <c>TileBuildGraph.DebugLiveCount</c> stays elevated above baseline forever.</para>
        /// </summary>
        [Test]
        public void Teardown_WhileGenuinelyInMeasure_DrainsThePen_WithoutSettling()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_D_Teardown");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                long graphBaseline     = TileBuildGraph.DebugLiveCount;
                long requestsBaseline  = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline   = MeshDataPayload.DebugLiveAllocCount;
                long negativesBaseline = TileBuildGraph.DebugNegativeObservations;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;
                view.TileManager.WorkScheduler     = new InlineWorkScheduler();

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillStyle());

                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 1; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "drive precondition: the tile must be genuinely held in its MEASURE step before teardown.");

                // Release the gate before tearing down (NIT 3 — Complete()ing a still-gated job on main
                // would burn the whole spin bound; the claim under test is "teardown routes through the
                // SAME pen funnel", not "teardown blocks on a held job") — but the tile is torn down
                // WITHOUT ever settling or being consumed, which is the actual claim (R2).
                gate[0] = 1;
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "DoDispose must dispose every graph still live at teardown — including one whose measure " +
                    "step was genuinely in flight, through the same pen funnel every other abandonment path " +
                    "uses.");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "the graph's own builds' columns must be freed with it.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak — teardown of an in-flight graph must not strand any allocated " +
                    "Mesh.MeshDataArray (there is none here — the write step never ran).");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                // Unconditional, before any dispose — an early failure (before Teardown() ever ran) means
                // no completed sweep has touched delayHandle yet.
                gate[0] = 1;
                delayHandle.Complete();
                if (go != null)
                {
                    view.Teardown();
                    UnityEngine.Object.DestroyImmediate(go);
                }
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── Tooth (g): a mixed tile — fill AND line, both graph-arm — one mesh per produced ──────
        // layer, joined by slot (R3) ─────────────────────────────────────────────────────────────
        //
        // job-scheduling-design.md §8 stage 5 Group B: line moved onto the SAME job graph as fill — there
        // is no longer a seam arm for either kind, so "fill via graph, line via prologue" no longer
        // describes anything real. What both teeth below still pin — CompleteWriteAndTakePayloads joining
        // each request's payload at ITS OWN slot, not a shared/overwritten one — is unaffected by which
        // kind sits in which slot, and still worth pinning per-layer.

        /// <summary>
        /// Empty-line case: fill AND line both declared on <c>countries</c> — polygon rings drop at the
        /// line builder's <c>LineString</c> gate, so the line layer settles as an EMPTY (zero-vertex)
        /// request (no write step runs for it). Pins "an empty layer still takes a slot": <c>CompleteWriteAndTakePayloads</c> hands
        /// back one slot per REQUEST, but <c>ConsumeMeshBuild</c> only tracks a slot whose
        /// <c>Upload()</c> returned a real <see cref="Mesh"/> — so only the fill layer's mesh reaches
        /// <c>GetTileMeshes</c>/<c>GetTileMaterialIndices</c>, and no NativeArray leaks (there is nothing
        /// for the empty line request to leak — it never reaches the write step at all).
        ///
        /// <para><b>RED:</b> in <c>CompleteWriteAndTakePayloads</c>'s write branch, write <c>payloads[0]</c>
        /// instead of <c>payloads[i]</c> — see the real-join tooth below for the shape this defect takes;
        /// with only one non-empty layer here it happens to be inert (index 0 IS the fill's own slot), so
        /// this case alone cannot catch it — that is why the real-join case exists.</para>
        /// </summary>
        [Test]
        public void MixedTile_FillAndEmptyLine_EmptyLineLayer_StillTakesASlot()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_G_EmptyLine");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
                ""layers"": [
                    { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                      ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                    { ""id"": ""countries-line"", ""type"": ""line"", ""source"": ""maplibre"",
                      ""source-layer"": ""countries"" }
                ]
            }");

            try
            {
                long payloadBaseline = MeshDataPayload.DebugLiveAllocCount;
                var tileId = new TileId { Z = 0, X = 0, Y = 0 };

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }
                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the tile must have loaded.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the tile must settle.");

                Assert.AreEqual(1, view.GetTileMeshes(tileId)?.Length ?? 0,
                    "only the fill layer's REAL mesh may be tracked — the empty line layer's Upload() " +
                    "returns null, and ConsumeMeshBuild never calls AddTileLayer for a null mesh.");
                CollectionAssert.AreEqual(new[] { 0 }, view.GetTileMaterialIndices(tileId),
                    "the one tracked mesh must be the FILL layer's — material index 0, its declared position.");
                // What this actually pins post-migration: the fill layer's own consume-disposal, the same
                // assertion this file's own tooth (b) makes at its own consume point — no other test in this
                // file separately guards the empty line's non-existent write-step allocation, since the
                // empty line never reaches the write step to allocate anything in the first place.
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no leak: the fill layer's write-step array must be disposed at consume, and the empty " +
                    "line layer's request (settled with no write step at all — no array to leak) must not " +
                    "leave the counter elevated either.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>Fill on <c>countries</c> + line on <c>geolines</c> (the fixture's 6-feature line
        /// layer — <c>ThrottleTests</c>' own style) — both LAYERS produce a real, non-empty mesh, so each
        /// layer's own dense request lands at ITS OWN material index (job-scheduling-design.md §8 stage 5
        /// Group B: both are graph-arm now — there is no second arm's slot to join against any more, only
        /// this one dense array, joined by request index). Run for BOTH declaration orderings —
        /// <paramref name="lineFirst"/> — so a bug that only shows for one physical arrangement of "which
        /// kind sits at slot 0" cannot hide.</summary>
        /// <param name="lineFirst"><see langword="false"/>: fill declared first (material index 0), line
        /// second (index 1). <see langword="true"/>: the reverse.</param>
        /// <remarks><b>RED:</b> in <c>CompleteWriteAndTakePayloads</c>'s write branch, write
        /// <c>payloads[0]</c> instead of <c>payloads[i]</c> — in the fill@0/line@1 ordering
        /// (<paramref name="lineFirst"/> == <see langword="false"/>) the line's payload OVERWRITES the
        /// fill's at slot 0 (mesh count drops to 1, <c>MaterialIndices</c> reads <c>[1]</c> instead of
        /// <c>[0, 1]</c>); the line@0 ordering is UNAFFECTED (writing to index 0 there is a no-op — it
        /// already was line's own slot). The tooth reds through the FIRST ordering only, and that is the
        /// one to read.</remarks>
        [Test]
        public void MixedTile_FillAndLine_RealJoin_BothOrderings(
            [Values(false, true)] bool lineFirst)
        {
            const string fillLayer = @"{ ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }";
            const string lineLayer = @"{ ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                ""source-layer"": ""geolines"",
                ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }";
            string layers = lineFirst ? lineLayer + "," + fillLayer : fillLayer + "," + lineLayer;
            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
                ""layers"": [" + layers + @"]
            }");

            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_G_RealJoin");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            try
            {
                var tileId = new TileId { Z = 0, X = 0, Y = 0 };

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }
                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the tile must have loaded.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the tile must settle.");

                Assert.AreEqual(2, view.GetTileMeshes(tileId)?.Length ?? 0,
                    $"both layers must produce a non-empty mesh (lineFirst={lineFirst}) — fill on countries, " +
                    "line on geolines (a real 6-feature line layer, unlike the empty-line case above).");
                // Material index == declared position regardless of WHICH kind (fill/line) sits there —
                // always [0, 1] for two declared layers. The permutation this tooth exists to catch is a
                // wrong VALUE at a slot (or a missing slot), not a different set of index numbers.
                CollectionAssert.AreEqual(new[] { 0, 1 }, view.GetTileMaterialIndices(tileId),
                    $"MaterialIndices must join in DECLARED order (lineFirst={lineFirst}) — a permutation " +
                    "bug (writing every payload slot at index 0 instead of the request's own index) would " +
                    "corrupt this in the fill@0/line@1 ordering specifically.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── Budget rule (job-scheduling-design.md §11 fork 2): admission is tiles-per-Tick, and a step ──
        // transition of an already-admitted tile is uncharged ──────────────────────────────────────────

        /// <summary>
        /// With <c>MaxMeshBuildsPerTick = 1</c> and a multi-tile cover, some Tick both completes a
        /// write-step transition for an already-admitted tile AND admits a fresh one. Under the deleted
        /// two-units-per-tile rule that conjunction was arithmetically impossible: one budget unit, two
        /// claimants (the write kick and the fresh admission), mutually exclusive in either iteration
        /// order. Inline decode (<see cref="InlineWorkScheduler"/> on the fetch's decode hop) guarantees
        /// same-tick eligibility, so the scan measures budget behaviour, not decode-completion order.
        ///
        /// <para><b>RED:</b> reconstruct the deleted charge as a local, shared across all three admission
        /// guards — <c>if (TileBuildsStartedLastTick + transitionCharge >= buildCap) …</c> in the
        /// write-transition arm (incrementing the local after) AND in both kick arms. Under it the
        /// conjunction can never occur: the local re-forms the same single shared budget the deleted
        /// two-counter world had. The cap-binding assertion (below) stays green; the conjunction assertion
        /// goes RED.</para>
        /// </summary>
        [Test]
        public void WriteTransitionTick_CanAlsoAdmitAFreshTile_UnderCapOne()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_BudgetAdmitsWhileWriting");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // known >= 9-tile cover
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxMeshBuildsPerTick  = 1;             // the cap that makes the conjunction impossible
                                                                // under the deleted rule
            view.Config.MaxConsumesPerTick    = 0;             // consume blocked — candidates never run dry
            view.Config.MaxVerticesPerTick    = int.MaxValue;
            view.WithTestCamera();

            try
            {
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;

                // Inline decode — the same-tick-eligibility guarantee (TileManagerLoadPriorityTests' own
                // idiom): without it the scan can measure decode-completion order instead of budget
                // behaviour.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle(),
                    decodeScheduler: new InlineWorkScheduler());

                // Bounded scan (never iterate a runtime-derived count without a ceiling). No
                // DrainMeshBuilds/PumpUntilSettled here — drain ignores every per-tick cap by design and
                // would destroy the observation.
                bool anyStarted = false, anyAllocated = false, conjunctionSeen = false;
                for (int t = 0; t < 200; t++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    int  started   = view.TileBuildsStartedLastTick();
                    long allocated = view.MeshDataArraysAllocatedLastKick();
                    Assert.LessOrEqual(started, 1,
                        $"the cap must still bind at MaxMeshBuildsPerTick=1 on every scanned Tick (tick {t}).");
                    if (started   > 0) anyStarted   = true;
                    if (allocated > 0) anyAllocated = true;
                    if (started > 0 && allocated > 0) conjunctionSeen = true;
                }

                Assert.GreaterOrEqual(view.LoadedTileCount(), 3,
                    "non-vacuity: a 1- or 2-tile cover cannot produce the conjunction.");
                Assert.IsTrue(anyStarted,
                    "drive precondition: at least one scanned Tick must have started a tile, or this scan is dead.");
                Assert.IsTrue(anyAllocated,
                    "drive precondition: at least one scanned Tick must have completed a write transition, or " +
                    "this scan is dead.");
                Assert.IsTrue(conjunctionSeen,
                    "at least one Tick must both admit a fresh tile AND complete a write transition at cap=1 " +
                    "— impossible under the deleted two-units-per-tile rule, where a write kick and a fresh " +
                    "admission competed for the same single unit.");

                view.Teardown();
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "teardown of this unsettled cover (consume blocked) must not leak any allocated MeshDataArray.");
                Assert.AreEqual(graphOutputBaseline, FillGraphOutput.DebugLiveCount,
                    "teardown of this unsettled cover must not leak any live measure/write graph output.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// With <c>MaxMeshBuildsPerTick = 1</c>, a single Tick can complete write-step transitions for
        /// TWO OR MORE already-admitted tiles at once. Under the deleted rule this was impossible for a
        /// plain counting reason: one unit per Tick buys one charged kick. Unlike the sibling tooth above,
        /// this one reads <see cref="MapViewTestExtensions.MeshDataArraysAllocatedLastKick"/> — not the
        /// admission counter — so no reconstruction of the deleted charge can pollute it.
        ///
        /// <para>Holds three tiles in MEASURE simultaneously (the delay-job idiom this file already uses
        /// for the Measure/Teardown pen cases above), then opens the gate and completes them all in one
        /// <see cref="MapViewTestExtensions.AwaitInFlightMeshBuilds"/> call, so one <c>LateUpdate</c>
        /// write-kicks every one of them at once. The per-tile ceiling is structural, not borrowed: the
        /// write step allocates one <see cref="Mesh.MeshDataArray"/> per non-empty layer, and
        /// <see cref="FillStyle"/> declares exactly one fill layer.
        ///
        /// <para><b>RED:</b> the same reconstructed <c>transitionCharge</c> local as the sibling tooth
        /// above (one injected run serves both) — under it the single Tick charges one write kick and
        /// defers the rest, so <c>MeshDataArraysAllocatedLastKick() == 1</c>.</para>
        /// </summary>
        [Test]
        public void OneTick_CanCompleteTwoWriteTransitions_UnderCapOne()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_BudgetTwoWritesOneTick");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxMeshBuildsPerTick  = 1;
            view.Config.MaxConsumesPerTick    = 0;
            view.Config.MaxVerticesPerTick    = int.MaxValue;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;
                // Load-bearing, not decoration: `decodeScheduler` inlines only the decode hop — the
                // prologue still dispatches to the ThreadPool, and this drive forbids Await while the gate
                // holds a graph step (this file's own rule), with no yield to give workers wall-clock. A
                // tight bounded loop can exhaust before three prologues land without this.
                view.TileManager.WorkScheduler = new InlineWorkScheduler();

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Gate shut — held tiles pile up in MEASURE instead of racing through it. No Await/Drain
                // while the gate holds a graph step; a plain bounded LateUpdate()-only pump.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 3; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 3,
                    "drive precondition: at least three tiles must be genuinely held in MEASURE before the " +
                    "gate opens — reachable under both rules (admission is 1/Tick either way, and the " +
                    "prologue hand-off is uncharged before and after), so a RED here is a property failure, " +
                    "never a precondition failure.");

                gate[0] = 1;
                delayHandle.Complete();
                view.AwaitInFlightMeshBuilds(); // completes every held graph — all held tiles measure-complete at once

                view.LateUpdate();

                Assert.GreaterOrEqual(view.MeshDataArraysAllocatedLastKick(), 2,
                    "at least TWO tiles must complete their write transition on this ONE Tick at cap=1 — " +
                    "impossible under the deleted rule, where one budget unit buys one charged write kick " +
                    "per Tick.");

                view.Teardown();
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "teardown of this unsettled cover (write-kicked, consume blocked) must not leak any " +
                    "allocated MeshDataArray.");
                Assert.AreEqual(graphOutputBaseline, FillGraphOutput.DebugLiveCount,
                    "teardown of this unsettled cover must not leak any live measure/write graph output.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }
    }
}
