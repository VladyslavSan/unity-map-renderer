// Epic A / A2 acceptance — the source-less per-covered-tile background processor's TileManager wiring
// (plan §F teeth 1, 2, 11, 12). Drives the real MapView/TileManager cover→build→consume loop with a
// background-only style (no fill/line/symbol layer, so NO SourceSpec is ever created — LoadTestStyle's
// injected source is unreachable by construction) and reads the GameObject backend's live Transform
// hierarchy (GameObjectTileRendererTests' pattern) so a single assertion set proves per-tile COUNT, the
// correct material SLOT, and a non-empty mesh together.

using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TileManagerBackgroundRegistrationTests
    {
        private static string BackgroundOnlyStyle(string colorHex = "#00ff00") => $@"{{
            ""version"": 8,
            ""layers"": [ {{ ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": {{ ""background-color"": ""{colorHex}"" }} }} ]
        }}";

        /// <summary>Two dense background layers over one covered tile — the reachable proxy, in THIS stage,
        /// for "a tile with many fill layers" (stage 3's actual multi-layer case): each becomes its own
        /// <c>ILayerMeshBuild</c>/write output, so a <c>MaxConsumesPerTick</c> budget of 1 forces
        /// a genuine partial consume — one layer taken this Tick, one left for the next.</summary>
        private static string TwoBackgroundLayersStyle() => @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""bg1"", ""type"": ""background"", ""paint"": { ""background-color"": ""#00ff00"" } },
                { ""id"": ""bg2"", ""type"": ""background"", ""paint"": { ""background-color"": ""#0000ff"" } }
            ]
        }";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static (GameObject go, MapView view) NewView(RenderBackend backend, int zoom)
        {
            var go   = new GameObject("BgRegistrationMapView");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.WithTestCamera();
            view.Config.Backend               = backend;
            view.Config.MaxConsumesPerTick    = 64;
            view.Config.MaxMeshBuildsPerTick  = 64;
            return (go, view);
        }

        /// <summary>Pumps the MapView across editor frames until its cover has settled, yielding a frame
        /// each iteration (never Thread.Sleep — a blocked thread does not advance the player loop and races
        /// the async decode/mesh-build). Callers are <c>[UnityTest]</c> coroutines: <c>yield return</c> this.</summary>
        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        // ── Tooth 1: per-covered-tile registration (primary semantic tooth) ──────────────────────────

        [UnityTest]
        public IEnumerator BackgroundStyle_RegistersOneQuadPerCoveredTile()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3); // z3: a multi-tile cover (non-vacuous N)
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                yield return PumpUntilSettled(view);

                int loaded = view.LoadedTileCount();
                Assert.Greater(loaded, 1,
                    "non-vacuous: the cover must contain MORE than one tile, or 'one quad per tile' is untested.");
                Assert.IsTrue(view.AllTilesSettled());

                var gor = view.GameObjectRenderer();
                Assert.IsNotNull(gor, "GameObject renderer must be constructed when Backend == GameObject.");

                var keys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(keys);
                Assert.AreEqual(loaded, keys.Count);

                Material backgroundMaterial = view.Layers[0].Material; // the ONLY layer — DrawIndex 0
                Assert.IsNotNull(backgroundMaterial);

                foreach (var key in keys)
                {
                    Transform container = gor.Container(key.Tile);
                    Assert.IsNotNull(container, $"a per-tile container must exist for {key.Tile}.");
                    Assert.AreEqual(1, container.childCount,
                        $"exactly one draw item (the background quad) per covered tile ({key.Tile}).");

                    Transform quad = container.GetChild(0);
                    var mf = quad.GetComponent<MeshFilter>();
                    var mr = quad.GetComponent<MeshRenderer>();
                    Assert.IsNotNull(mf?.sharedMesh, $"the background quad at {key.Tile} must carry a mesh.");
                    Assert.Greater(mf.sharedMesh.vertexCount, 0,
                        $"the background quad's mesh at {key.Tile} must be non-empty.");
                    Assert.AreSame(backgroundMaterial, mr.sharedMaterial,
                        $"the background quad at {key.Tile} must draw with the background layer's material " +
                        "(its declared DrawIndex/material slot).");
                }

                Assert.AreEqual(loaded, gor.ContainerCount(), "exactly one container per covered tile — no extras.");
                Assert.AreEqual(loaded, gor.DrawItemCount(), "exactly one draw item per covered tile — no extras.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth (a): step progression — job-scheduling-design.md §8 stage 2. ────────────────────────
        //
        // A single-tile cover (z0 — exactly one covered tile), so "Measure observed, then Write observed
        // later" is a claim about ONE tile's own BuildStep transition (None → Measure → Write → None, which
        // the code only ever advances in that order) rather than an interleaving artefact across several
        // tiles independently reaching different steps on the same tick — the "readiness, not order" trap.
        // Formulated as "visits both steps before settling", not "exactly at tick N+1": a job's completion
        // latency is not something this test controls. What it proves and no more (design §7.4): scheduling
        // ORDER, never execution PLACEMENT — EditMode is not the web, and a Burst-off Editor would leave the
        // same trail.

        [UnityTest]
        public IEnumerator BackgroundCover_VisitsMeasureThenWrite_BeforeSettling()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 0); // z0: exactly one covered tile
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            // Declared here, not inside try: an early failure (before any graph is ever scheduled against
            // it) must still be able to Complete() this in finally, unconditionally, before disposing
            // gate/started/outVals — see the finally block's own comment.
            JobHandle delayHandle = default;
            try
            {
                // Hold the measure graph on a gated delay job (job-scheduling-design.md E2 option iii) —
                // GraphMeasureInFlight >= 1 is then DETERMINISTIC, not a race against however fast a 4-vertex
                // quad's measure graph happens to complete (the original form of this tooth sampled after
                // the fact via CaptureTelemetry() and reliably missed the window on this machine).
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                view.LoadTestStyle(null, Cam(0, 0, 0), StyleParser.Parse(BackgroundOnlyStyle()));

                int kickTick = -1, tick = 0;
                for (; tick < 10000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                    yield return null;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the background graph must have been kicked.");

                DelayGateJobInstrument.WaitForStart(started);

                var snap = view.CaptureTelemetry();
                Assert.GreaterOrEqual(snap.GraphMeasureInFlight, 1,
                    "the graph must be observed in its MEASURE step — deterministic while the delay job " +
                    "holds it, since the measure graph cannot possibly have completed yet.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must not read settled while its measure step is genuinely held incomplete.");

                gate[0] = 1; // release — Measure, then Write, proceed normally from here on

                // The design's own RED claim (job-scheduling-design.md §8 stage 2 tooth (a)): "a pump that
                // Complete()s at kick settles in one Tick." PumpPending's arms (3) then (2) each gate on
                // IsStepComplete and each visits a tile AT MOST once per Tick (every arm ends in `continue`),
                // so a correct three-step pump (kick tick → measure-complete/schedule-write tick →
                // write-complete/consume tick) cannot settle before kickTick+2 — this is a STRUCTURAL fact
                // about the dispatch, not a timing bet, so it survives any completion latency, gated or not.
                // A pump that folds write-completion into the measure-complete tick (this tooth's RED) would
                // settle at kickTick+1.
                // Record the tick the WRITE step was kicked (arm 3 allocates the MeshDataArray) and the
                // tick the tile was CONSUMED (arm 2). Those are the two steps whose separation this tooth
                // exists to prove, and each is observable through a counter the pump sets on exactly the
                // tick its arm ran.
                int allocTick = -1, consumeTick = -1, settleTick = -1;
                for (; tick < 10000 && settleTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (allocTick   < 0 && view.MeshDataArraysAllocatedLastKick() > 0) allocTick   = tick;
                    if (consumeTick < 0 && view.TilesConsumedLastTick()           > 0) consumeTick = tick;
                    if (view.AllTilesSettled()) settleTick = tick;
                    yield return null;
                }
                Assert.GreaterOrEqual(settleTick, 0, "the cover must eventually settle.");

                Assert.AreEqual(1, view.LoadedTileCount(),
                    "precondition: exactly ONE covered tile. Both counters below are per-Tick GLOBALS, so " +
                    "with two tiles one tile's alloc could coincide with another's consume and the ordering " +
                    "assertions would read a coincidence rather than this tile's step separation.");
                Assert.GreaterOrEqual(allocTick,   0, "the write step must have allocated a MeshDataArray.");
                Assert.GreaterOrEqual(consumeTick, 0, "the tile must have been consumed.");
                Assert.Greater(allocTick, kickTick,
                    $"the write step must be kicked on a LATER tick than the measure kick (kickTick={kickTick}, " +
                    $"allocTick={allocTick}) — a pump that Complete()s the measure graph at kick would " +
                    "allocate on the kick tick itself. `allocTick != consumeTick` does not catch that: alloc " +
                    "landing on the kick tick still leaves the two different. Deterministic for the same " +
                    "reason as below — arm (6) kicks and `continue`s, so arm (3) cannot run for this tile in " +
                    "the same pass.");
                Assert.Greater(consumeTick, allocTick,
                    $"the write step must be KICKED on one tick (allocTick={allocTick}) and CONSUMED on a " +
                    $"LATER one (consumeTick={consumeTick}) — they are separate polled steps. A pump that " +
                    "folds write-completion into the measure-complete arm does both on the same tick, which " +
                    "is exactly the defect this tooth names. Unlike a settle-tick gap, this cannot be " +
                    "satisfied by the graph's jobs merely taking a while to complete: both counters are set " +
                    "by the pump on the tick its own arm ran, so job latency moves both together.");
                Assert.GreaterOrEqual(settleTick - kickTick, 2,
                    $"settling must take at least two ticks after the kick tick (kickTick={kickTick}, " +
                    $"settleTick={settleTick}) — one to complete measure and schedule write, another to " +
                    "complete write and consume. A pump that folds write-completion into the measure-complete " +
                    "tick would settle at kickTick+1.");

                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from a " +
                    "build that never happened.");
            }
            finally
            {
                // Unconditional, before any dispose — same masked-exception discipline as
                // ZeroWorkerCount_DoesNotHoldAScheduledJobIncomplete. On the happy path Teardown() already
                // completes this transitively (the graph's handle depends on it); on an EARLY failure (e.g.
                // the kickTick precondition), no graph was ever scheduled against delayHandle, so nothing
                // else would complete it — disposing gate/started/outVals while it is still running would
                // throw and replace the real assertion failure with a disposal error.
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                Object.DestroyImmediate(go);
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> make the pump complete the write handle in the same pass it
        /// schedules it (fold <c>CompleteWriteAndTakePayloads</c> into the measure-complete arm) — a correct
        /// pump's settleTick - kickTick >= 2 floor drops to 1, and the tick-count assertion catches it.</summary>

        // ── Tooth (c): the pen — a graph released mid-flight is STASHED, not blocked on ────────────────
        //
        // job-scheduling-design.md §6 exit (2)/(3): RenderTeardownRecord's graph-arm twin of the seam pen
        // (mirrors DisposalLeakGuardTests.ReleaseMidFlight_NoOrphanedMesh's pan-to-evict drive). Held
        // genuinely in-flight via TileManager.GraphDepsForTest — the deps-parameter delay instrument
        // (job-scheduling-design.md E2 option iii), never JobsUtility.JobWorkerCount, which does not hold a
        // scheduled job incomplete (JobGraphInstrumentTests.ZeroWorkerCount_DoesNotHoldAScheduledJobIncomplete).
        // TileBuildGraph.DebugLiveCount/MeshDataPayload.DebugLiveAllocCount are process-wide static counters
        // shared across the whole batch run — every assertion below is a BEFORE/AFTER delta, never an
        // absolute reading.

        [UnityTest]
        public IEnumerator ReleaseMidFlight_GraphArm_StashesInThePen_ThenDrainsOnceTheDelayJobCompletes()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 2);
            view.Config.MaxReleasesPerTick = 64; // evict the whole condemned cover on the pan tick
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            // Declared here, not inside try — see the finally block's comment.
            JobHandle delayHandle = default;
            try
            {
                long baselineGraphs = TileBuildGraph.DebugLiveCount;
                long baselinePayloads = MeshDataPayload.DebugLiveAllocCount;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                view.LoadTestStyle(null, Cam(0, 0, 2), StyleParser.Parse(BackgroundOnlyStyle()));

                int kicked = 0;
                for (int f = 0; f < 2000 && kicked == 0; f++)
                {
                    view.LateUpdate();
                    kicked += view.TileBuildsStartedLastTick();
                    yield return null;
                }
                Assert.Greater(kicked, 0, "drive precondition: at least one background graph must have been kicked.");

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(view.AllTilesSettled(),
                    "the covered tile(s) must still be genuinely unsettled — held on the delay job's gate.");

                // Pan far east — evict the covered tile(s) while their graph is still genuinely in its
                // Measure step (gate held). This tick must return PROMPTLY: RenderTeardownRecord must stash
                // the in-flight graph via _pending.StashGraph, never call Dispose() (which would Complete()
                // — and block — on the gated job) synchronously here.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                view.LateUpdate();
                sw.Stop();

                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "positive control: at least one tile must have been released while its graph was still " +
                    "genuinely in-flight (gate held), or the pen assertions below are vacuous.");
                // THE decisive assertion, and it is first on purpose. An eviction that disposes inline
                // instead of penning runs Complete() on the gated spin and decrements the live count before
                // control returns here, so this reads == baseline. Deterministic: no clock, no margin.
                Assert.Greater(TileBuildGraph.DebugLiveCount, baselineGraphs,
                    "the evicted graph must still be LIVE right after eviction — the pen defers disposal, it " +
                    "does not skip it. An inline Dispose() would already have completed the gated job and " +
                    "decremented the count by now.");

                // NOT the tooth — a hang guard, deliberately generous. It measures the gated spin's wall
                // time, which is MaxIterations divided by this machine's throughput, so no constant makes it
                // decisive: a bound loose enough to be safe here would pass on a faster machine that was
                // still blocking. It exists only so a pen that stashes AND blocks (a Complete() added before
                // the stash — which the ownership assertion above would not catch) fails loudly instead of
                // hanging the suite.
                Assert.Less(sw.ElapsedMilliseconds, 20000,
                    "eviction must not block on the gated job's Complete() — hang guard only; the live-count " +
                    "assertion above is what proves the graph was penned rather than disposed.");

                // Release the gate and pump until the pen drains.
                gate[0] = 1;
                int guard = 0;
                while (TileBuildGraph.DebugLiveCount > baselineGraphs && guard++ < 2000)
                {
                    view.LateUpdate();
                    yield return null;
                }

                Assert.AreEqual(baselineGraphs, TileBuildGraph.DebugLiveCount,
                    "once the gated job completes, the pen must drain: PendingDisposalQueue.DrainCompleted disposes the " +
                    "released graph on a later tick.");
                Assert.AreEqual(baselinePayloads, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak once the pen has drained.");
            }
            finally
            {
                // Unconditional, before any dispose — same masked-exception discipline as tooth (a)'s
                // finally: an early failure (e.g. the "kicked > 0" precondition) means no graph was ever
                // scheduled against delayHandle, so nothing else would complete it before dispose runs.
                gate[0] = 1; // in case an assertion above failed before the release
                delayHandle.Complete();
                view.Teardown();
                Object.DestroyImmediate(go);
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> in <c>RenderTeardownRecord</c>, replace the
        /// <c>_pending.StashGraph(lt.Graph); lt.Graph = null;</c> stash with an immediate
        /// <c>lt.Graph.Dispose()</c>. The assertion that actually catches it is
        /// <c>TileBuildGraph.DebugLiveCount &gt; baselineGraphs</c> right after eviction: an immediate
        /// <c>Dispose()</c> blocks until the gated job finishes, then decrements the live count before
        /// <c>LateUpdate()</c> even returns, so the graph reads back at (or below) baseline instead of
        /// still-live — a deterministic delta failure, not a timing race. The elapsed-time assertion is
        /// marginal by comparison (its pass/fail depends on how long the gated spin happens to take) and is
        /// not the one this tooth's correctness rests on.</summary>

        // ── Tooth (d): full teardown with a graph genuinely in-flight ──────────────────────────────────
        //
        // job-scheduling-design.md §6 exit (4): TileManager.DoDispose's graph-arm sweep — Complete() from
        // the main thread executes a not-yet-started job inline (§3.3), so DoDispose can (and must) drive a
        // still-in-flight graph to completion synchronously, unlike the seam pen's UniTask bridge. The gate
        // is released from a background timer AFTER a short delay, so Teardown()'s Complete() call below
        // genuinely blocks on a still-running job for a measurable interval, rather than racing a release
        // that could land before Teardown ever starts.

        [UnityTest]
        public IEnumerator Teardown_WithGraphGenuinelyInFlight_CompletesAndDisposes_NoLeak()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 0);
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            bool torndown = false;
            // Declared here, not inside try — see the finally block's comment.
            JobHandle delayHandle = default;
            System.Threading.Tasks.Task releaseTask = System.Threading.Tasks.Task.CompletedTask;
            try
            {
                long baselineGraphs = TileBuildGraph.DebugLiveCount;
                long baselinePayloads = MeshDataPayload.DebugLiveAllocCount;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                view.LoadTestStyle(null, Cam(0, 0, 0), StyleParser.Parse(BackgroundOnlyStyle()));

                int kicked = 0;
                for (int f = 0; f < 2000 && kicked == 0; f++)
                {
                    view.LateUpdate();
                    kicked += view.TileBuildsStartedLastTick();
                    yield return null;
                }
                Assert.Greater(kicked, 0, "drive precondition: the background graph must have been kicked.");

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must still be genuinely unsettled — the graph is held on the delay job's gate.");

                // Release the gate from a background timer, not here: Teardown() below must find the job
                // STILL running when it calls Complete(), or the "in-flight at teardown" claim is untested.
                // Captured (not fire-and-forget): the finally block Wait()s this before disposing gate — the
                // task still writes gate[0] on an early-failure path, and disposing out from under it would race.
                releaseTask = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(200);
                    gate[0] = 1;
                });

                // Precondition, asserted at the moment it matters — immediately before Teardown(), with no
                // yield in between — not merely earlier via WaitForStart. The delay above is a bet (a loaded
                // machine could burn through it before Teardown() reaches the graph sweep); this assertion is
                // what makes the claim honest: a premature release fails LOUDLY here instead of letting
                // Teardown() silently exercise the already-complete-handle path while the test still passes.
                Assert.IsFalse(delayHandle.IsCompleted,
                    "precondition: the delay job must still be genuinely running right before Teardown() is " +
                    "called, or Teardown()'s Complete() call below exercises the ALREADY-COMPLETE path — " +
                    "the 'genuinely in-flight at teardown' claim this tooth makes would then be untested " +
                    "despite a passing assertion.");
                Assert.Greater(TileBuildGraph.DebugLiveCount, baselineGraphs,
                    "precondition: the graph must still be live and undisposed right before Teardown().");

                view.Teardown();
                torndown = true;
                Object.DestroyImmediate(go);

                Assert.AreEqual(baselineGraphs, TileBuildGraph.DebugLiveCount,
                    "every TileBuildGraph — including one genuinely in-flight at teardown — must be disposed.");
                Assert.AreEqual(0, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never observe a negative live count.");
                Assert.AreEqual(baselinePayloads, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak — teardown while a graph is in-flight must not strand any " +
                    "allocated Mesh.MeshDataArray.");
            }
            finally
            {
                // Unconditional, before any dispose — same masked-exception discipline as tooth (a)'s
                // finally: an early failure (before Teardown() ever runs — e.g. the "kicked > 0" or
                // "!delayHandle.IsCompleted" preconditions) means no completed sweep has touched
                // delayHandle yet, so it must be completed directly here, not just released.
                gate[0] = 1;
                delayHandle.Complete();
                // The release task ALSO writes gate[0] — Wait() it out before disposing gate below, or an
                // early-failure path (finally running before the task's 200ms sleep elapses) races the
                // dispose against that write.
                releaseTask.Wait(5000);
                if (!torndown)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> in <c>PendingDisposalQueue.FlushAll</c> (called from
        /// <c>DoDispose</c> via <c>_pending.FlushAll()</c>), replace the unconditional
        /// <c>foreach (var graph in _graph) graph.Dispose();</c> sweep with one that skips a
        /// graph whose <c>IsStepComplete</c> is still false — the live-count assertion above catches the
        /// leak (this repo's build never hangs a headless run on a Complete() call, so there is no timeout
        /// hazard from testing the synchronous-block path directly).</summary>

        // ── A partially-consumed graph tile keeps its Graph alive across ticks ─────────────────────────
        //
        // TileManager.FinishConsume is the single disposal site for LoadedTile.Graph (its own doc states
        // the invariant: non-null iff Step is Measure or Write). Two dense background layers force a
        // genuine partial consume under MaxConsumesPerTick=1 — one layer taken per Tick — so Step stays
        // Write across more than one Tick while Graph must stay usable. The un-fixed code disposed and
        // nulled Graph the moment CompleteWriteAndTakePayloads() first ran (before ConsumeMeshBuild even
        // reported whether it finished), so the NEXT Tick's `lt.Step == BuildStep.Write && lt.Graph.IsStepComplete`
        // guard dereferenced a null Graph — an NRE, not an assertion failure.

        [UnityTest]
        public IEnumerator PartiallyConsumedGraphTile_KeepsGraphAlive_UntilBothLayersSettle()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 0); // z0: exactly one covered tile
            view.Config.MaxConsumesPerTick = 1; // forces the partial consume: 2 layers, budget 1/tick
            try
            {
                Assert.AreEqual(1, view.Config.MaxConsumesPerTick,
                    "precondition: MaxConsumesPerTick=1 must actually be in effect on the driven view.");

                long baselineGraphs = TileBuildGraph.DebugLiveCount;
                long baselinePayloads = MeshDataPayload.DebugLiveAllocCount;

                view.LoadTestStyle(null, Cam(0, 0, 0), StyleParser.Parse(TwoBackgroundLayersStyle()));

                // Drive precondition: AllTilesSettled() reads vacuously true on an empty/never-requested
                // cover — a trap independent of this tooth — so this pumps until the tile is genuinely
                // loaded and kicked before relying on that helper for anything below.
                int kickTick = -1, tick = 0;
                for (; tick < 2000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                    yield return null;
                }
                Assert.Greater(view.LoadedTileCount(), 0,
                    "drive precondition: the single covered tile must have been loaded.");
                Assert.GreaterOrEqual(kickTick, 0,
                    "drive precondition: the background graph must have been kicked.");

                // Fixture precondition: BOTH background layers must be classified dense and both allocate a
                // write array on the SAME measure-complete tick — otherwise there is only one payload ever,
                // the partial-consume path this tooth targets is unreachable, and the "two meshes" outcome
                // below would be vacuous even against a correct fix.
                long allocated = 0;
                for (; tick < 2000 && allocated == 0; tick++)
                {
                    view.LateUpdate();
                    allocated += view.MeshDataArraysAllocatedLastKick();
                    yield return null;
                }
                Assert.AreEqual(2, allocated,
                    "fixture precondition: the style's two background layers must both be dense and both " +
                    "allocate a write array together — the partial-consume path is unreachable otherwise.");

                // Now drive to settle across the partial-consume ticks the budget forces.
                int guard = 0;
                while (!view.AllTilesSettled() && guard++ < 200)
                {
                    view.LateUpdate();
                    yield return null;
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "the tile must settle across multiple partial-consume ticks, not just the first.");
                Assert.AreEqual(2, view.GameObjectRenderer().DrawItemCount(),
                    "both background layers must have registered a mesh — a tile that settled with only one " +
                    "consumed would mean the second layer's payload leaked, not that it was never built.");
                Assert.AreEqual(baselineGraphs, TileBuildGraph.DebugLiveCount,
                    "no TileBuildGraph leak — the delta must return to baseline once settled.");
                Assert.AreEqual(baselinePayloads, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak once settled.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        /// <summary><b>RED injection:</b> in <c>PumpPending</c>'s write-consume arm, restore the original
        /// <c>lt.Graph.Dispose(); lt.Graph = null;</c> pair immediately after <c>CompleteWriteAndTakePayloads()</c>
        /// (before <c>ConsumeMeshBuild</c> even runs) — the second Tick that revisits this tile while
        /// <c>Step == Write</c> throws a <see cref="System.NullReferenceException"/> evaluating
        /// <c>lt.Graph.IsStepComplete</c>, which NUnit reports as a test ERROR rather than an assertion
        /// failure.</summary>

        // ── Tooth 2: no fetch, no decode for background ───────────────────────────────────────────────

        [UnityTest]
        public IEnumerator BackgroundStyle_IssuesNoFetch_AndNeverDecodes()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            // A source that FAULTS if ever invoked — background must never route through the fetch path at
            // all (no SourceSpec is derived for a source-less style; RenderLayerFactory.TryGetFetchSource
            // excludes it structurally). Falsifies the rejected "empty-bytes through the real fetch/decode
            // path" design alternative (plan §B Q1).
            var neverCalled = TestDataSource.FromFetch(_ => throw new System.InvalidOperationException(
                "background must never fetch — the source-less pipeline has no Scheduler/Source at all."));
            try
            {
                view.LoadTestStyle(neverCalled, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled());
                Assert.Greater(view.LoadedTileCount(), 0, "background must still load and register.");
                Assert.AreEqual(0, neverCalled.FetchCount, "the injected source must NEVER be consulted.");
                Assert.AreEqual(0, view.InFlightCount(), "no fetch may ever be in flight for a background-only style.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth 11: source-less builds honor the build cap (HIGH 2) ─────────────────────────────────

        [UnityTest]
        public IEnumerator BackgroundCover_KicksAtMostBuildCapPerTick()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            view.Config.MaxMeshBuildsPerTick = 1; // force per-tick throttling
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));

                // Tile-load smoothness: admission now runs EVERY Tick — including the SAME Tick as the
                // cover recompute that creates these pending records — so PumpPending's kick for the FIRST
                // (highest-priority) record can fire on tick 1 itself (time-to-first-paint improvement; the
                // old "records cannot be kicked this same frame" behaviour was an artifact of PumpPending
                // running BEFORE the request loop, not a load-bearing invariant (the intended behaviour is
                // exactly the opposite: time-to-first-paint at the center drops). The per-Tick CAP itself is
                // unchanged and still the property under test here.
                view.LateUpdate();
                int loaded = view.LoadedTileCount();
                Assert.Greater(loaded, 1, "non-vacuous: must cover more than one tile.");
                Assert.LessOrEqual(view.TileBuildsStartedLastTick(), 1,
                    "at most MaxMeshBuildsPerTick source-less tiles may be started on the cover-recompute " +
                    "tick, same as any other tick — the cap binds even on tick 1.");

                int guard = 0;
                while (!view.AllTilesSettled() && guard++ < 10000)
                {
                    view.LateUpdate();
                    Assert.LessOrEqual(view.TileBuildsStartedLastTick(), 1,
                        "at most MaxMeshBuildsPerTick source-less tiles may be started in a single Tick — " +
                        "never the whole cover synchronously in one burst.");
                    yield return null;
                }
                Assert.IsTrue(view.AllTilesSettled(), "the throttled cover must still fully drain eventually.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth 12: DrainMeshBuilds builds source-less records (HIGH a) ─────────────────────────────

        [Test]
        public void DrainMeshBuilds_BuildsSourcelessBackground()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                // One LateUpdate to run the cover recompute (creates records; PumpPending, running BEFORE
                // the recompute in the same call, sees none of them yet — so nothing is kicked either way).
                view.LateUpdate();
                Assert.Greater(view.LoadedTileCount(), 0, "the cover must have created source-less records.");
                Assert.IsFalse(view.AllTilesSettled(), "records must still be pending before the drain.");

                view.DrainMeshBuilds(); // uncapped — must build every un-kicked source-less record inline

                Assert.IsTrue(view.AllTilesSettled(), "DrainMeshBuilds must settle every source-less record.");
                var gor = view.GameObjectRenderer();
                Assert.Greater(gor.DrawItemCount(), 0,
                    "DrainMeshBuilds must have produced a non-empty background mesh + AddTileLayer for the " +
                    "un-kicked record — the un-fixed drain settles with ZERO draws (falls to the no-mesh else).");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
