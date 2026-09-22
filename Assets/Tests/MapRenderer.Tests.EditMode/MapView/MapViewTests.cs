// MapView/MapViewTests.cs — MapHost wiring and MapView telemetry/mesh-build teeth (EditMode).
//
// MapViewLightingTests.cs holds the two files that would collide on bare CameraProperties (see its
// header). MapViewLiveLoopTests.cs stays its own file: it imports System, and every file here calls bare
// Object.DestroyImmediate (UnityEngine.Object) — a using collision the merge rule resolves by not
// merging, never by qualifying. SceneIntegrityTests.cs stays its own file too — its [TearDown] calls
// EditorSceneManager.NewScene, process-state per test-conventions.md §4.
//
// Contents:
//   MapRootWiringTests          — MapHost.Wire() wiring graph: MapController.Map/.Camera and the MapView built over the camera.
//   MapTelemetryTests           — render/tile telemetry, EditMode half: GC.Alloc teeth and the unwired-panel no-op.
//   MapViewAsyncMeshBuildTests  — async non-blocking tile mesh build, EditMode half: off-main marker, profiler harness, drain, BuildMeshData/UploadMesh round trip.

using NUnit.Framework;
using UnityEngine;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapController   = MapRenderer.App.Controller;
using TouchController = MapRenderer.App.TouchController;
using MapHost = MapRenderer.App.MapHost;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.App;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Map;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.TestTools;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Jobs.Geometry;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;


namespace MapRenderer.Tests.MapViews
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MapRootWiringTests — MapHost.Wire() — the MapController/Camera/MapView wiring graph
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S41 wiring tests for <see cref="MapHost.Wire"/>.
    ///
    /// These tests fail on the pre-S41 code-base (where Map was set only in Play via GetComponent
    /// on the same Camera GO — never tested). They are the regression guard for the "unset Map
    /// reference" class of bug.
    /// </summary>
    [TestFixture]
    public class MapRootWiringTests : BaseTestFixture
    {
        // ── (1) Happy-path wiring ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wire(root, camera, source, view) sets MapController.Camera == the supplied camera,
        /// MapController.Map != null, and builds the MapView over the camera.
        /// </summary>
        [Test]
        public void Wire_HappyPath_SetsControllerFieldsAndInitialisesMapView()
        {
            // Arrange: a MapRoot GO carrying MapView + MapController.
            var rootGo = Track(new GameObject("MapRoot"));
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();

            // A plain camera GO tagged MainCamera (so Camera.main resolves to it).
            var cameraGo = Track(new GameObject("MainCamera"));
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();

            // A minimal in-memory data source.
            using var source = TestDataSource.Absent();
            var initialView = new CameraProperties(new GeoCoordinate3D { Longitude = 0.0, Latitude = 20.0, Altitude = 0 }, 2.0, 0, 0);

            {
                // Act.
                MapHost.Wire(rootGo, cam, initialView);

                // Assert — tooth 1 of S41 acceptance:
                var ctrl    = rootGo.GetComponent<MapController>();
                var mapView = rootGo.GetComponent<MapView>();

                Assert.IsNotNull(ctrl.Map,    "MapController.Map must be set after Wire()");
                Assert.AreEqual(cam, ctrl.Camera,
                    "MapController.Camera must equal the camera passed to Wire()");
                Assert.IsNotNull(mapView.Camera,
                    "Wire must build the MapView over the camera (data loads separately via SetStyle)");
            }
        }

        // ── (2) Missing camera — logs, no NRE ────────────────────────────────────────────────────

        /// <summary>
        /// Wire(root, null) must not throw. A MapCamera requires a real camera, so with none the MapView
        /// is not built — but MapController.Map is still set and nothing crashes.
        /// (In practice the scene always has a main camera; this only pins the graceful no-camera path.)
        /// </summary>
        [Test]
        public void Wire_NullCamera_DoesNotThrow_NoMapBuilt()
        {
            var rootGo = Track(new GameObject("MapRoot"));
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();
            using var source = TestDataSource.Absent();

            {
                // Must not throw.
                Assert.DoesNotThrow(
                    () => MapHost.Wire(rootGo, null, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 2, 0, 0)),
                    "Wire(root, null) must not throw even when the camera is missing.");

                var ctrl    = rootGo.GetComponent<MapController>();
                var mapView = rootGo.GetComponent<MapView>();

                Assert.IsNull(ctrl.Camera, "MapController.Camera must stay null when no camera is provided");
                Assert.IsNotNull(ctrl.Map,  "MapController.Map must still be set");
                Assert.IsNull(mapView.View,
                    "with no camera the MapView is not built (a MapCamera requires a real camera) — but no crash");
            }
        }

        // ── (3) Missing MapView — logs, no NRE ───────────────────────────────────────────────────

        [Test]
        public void Wire_MissingMapView_DoesNotThrow()
        {
            var rootGo = Track(new GameObject("MapRoot"));
            // Deliberately omit MapView — Wire should log and return gracefully.

            Assert.DoesNotThrow(
                () => MapHost.Wire(rootGo, null),
                "Wire must not throw even when MapView is absent from the root.");
        }

        // ── (4) S74 — TouchController wired by Wire() ────────────────────────────────────────────

        /// <summary>
        /// S74: Wire() must add (or find) a <see cref="TouchController"/> on root and set its
        /// Map and Camera fields — locks the wiring so touch can't silently un-wire.
        /// </summary>
        [Test]
        public void Wire_HappyPath_WiresTouchController()
        {
            var rootGo = Track(new GameObject("MapRoot"));
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();

            var cameraGo = Track(new GameObject("MainCamera"));
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();

            using var source = TestDataSource.Absent();
            var initialView = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0, Altitude = 0 }, 2.0, 0, 0);

            {
                MapHost.Wire(rootGo, cam, initialView);

                var touch = rootGo.GetComponent<TouchController>();
                Assert.IsNotNull(touch,    "S74: Wire() must add TouchController to root");
                Assert.IsNotNull(touch.Map, "S74: TouchController.Map must be set after Wire()");
                Assert.AreEqual(cam, touch.camera,
                    "S74: TouchController.camera must equal the camera passed to Wire()");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapTelemetryTests — render/tile telemetry, EditMode half — GC.Alloc teeth and the unwired-panel no-op
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapTelemetryTests : BaseTestFixture
    {
        // ── Helpers ────────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""S85"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>Deterministically settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick consumes
        /// them. No frame yielding — mirrors <c>PreparedCacheTests.PumpUntilSettled</c>.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2500)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        [Test]
        public void MapTelemetryPanel_Pull_NoOpsCleanly_WhenUnwired()
        {
            var panelGo = Track(new GameObject("S85_TelemetryPanel_Unwired"));
            var panel = panelGo.AddComponent<MapTelemetryPanel>();
            panel.Map = null;
            Assert.DoesNotThrow(() => panel.Pull());
        }

        // ── Allocation-free read (N-tick gate) ────────────────────────────────────────────────────

        [Test]
        public void CaptureTelemetry_AllocationFree_AcrossNTicks()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_S85_Alloc"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg; // zero-alloc is the BRG backend's contract
            try
            {
                view.Config.TileSelection.MinZoom = 2; view.Config.TileSelection.MaxZoom = 2;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must settle before measuring steady state.");

                // Prime reused buffers (TileCoverStats' two HashSet<int>, the CaptureTelemetry _loaded pass)
                // to steady capacity before measuring.
                view.LateUpdate();
                view.CaptureTelemetry();

                Assert.That(() =>
                {
                    for (int i = 0; i < 64; i++)
                    {
                        view.LateUpdate();
                        view.CaptureTelemetry();
                    }
                },
                Is.Not.AllocatingGCMemory(),
                "CaptureTelemetry (TileCoverStats' scratch reuse + the Pending/ConsumeBacklog pass) must not " +
                "allocate across a RUN of Ticks — a single call can read clean while a loop of N trips the " +
                "recorder. A LINQ-based impl, or one that news a List/HashSet per " +
                "capture, fails this.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>
        /// The PUBLISH path must be allocation-free too — and this is the boxing tooth of
        /// docs/telemetry-design.md §6: the provider→reader path must stay copy-free and unboxed. Returning a
        /// snapshot by value, or erasing one to <c>object</c> / a non-generic interface anywhere on the path,
        /// shows up here as a per-frame allocation.
        /// </summary>
        [Test]
        public void PullTelemetry_IntoAPanel_IsAllocationFree_AcrossNFrames()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_Telemetry_PublishAlloc"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg; // zero-alloc is the BRG backend's contract
            try
            {
                view.Config.TileSelection.MinZoom = 2; view.Config.TileSelection.MaxZoom = 2;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick   = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must settle before measuring steady state.");

                var panelGo = Track(new GameObject("TelemetryPanel_PublishAlloc"));
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;

                // Prime the reused capture scratch, and prove the pull actually lands before measuring — otherwise
                // this would be an allocation test over a read that never happens.
                view.LateUpdate();
                panel.Pull();
                Assert.Greater(panel.VisibleTileCount, 0, "positive control: the panel must be reading real levels.");

                Assert.That(() =>
                {
                    for (int i = 0; i < 64; i++) { view.LateUpdate(); panel.Pull(); }
                },
                Is.Not.AllocatingGCMemory(),
                "each provider's refresh + the ref-return read + the panel's field writes must not allocate per " +
                "frame. A by-value accessor, a boxed snapshot (erased to object / a non-generic interface), or a " +
                "per-frame closure fails this.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── ConsumeBacklog: the S95 "measure first" signal ────────────────────────────────────────
        // EditMode-only: this test blocks CONSUME (MaxConsumesPerTick=0) so completed mesh builds pile up
        // as an unconsumed backlog. It needs the ThreadPool builds to COMPLETE (wall-clock) WITHOUT being
        // consumed — DrainMeshBuilds would consume them to Built (backlog→0, defeating the scenario), and a
        // PlayMode yield-pump stalls the pipeline under consume=0 backpressure. AwaitInFlightMeshBuilds gives
        // each iteration the ThreadPool wall-clock a completed build needs WITHOUT consuming — the only
        // mechanism that fits.
        [Test]
        public void ConsumeBacklog_TracksTheThrottledBuildBacklog_ThenDrainsToZero()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_S85_Backlog"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 0;  // blocks consume entirely (the S87 backlog-builder)
                view.Config.MaxMeshBuildsPerTick = 64; // don't cap mesh build kicks

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Mesh builds complete one at a time on the ThreadPool, so ConsumeBacklog trickles up across
                // several Ticks. Wait for it to STABILIZE at PendingTileCount (⇒ no record still in-flight);
                // AwaitInFlightMeshBuilds gives each iteration the wall-clock a completed build needs (see
                // note above) WITHOUT consuming, so the backlog it measures stays intact.
                TileTelemetrySnapshot snap = default;
                for (int f = 0; f < 3000; f++)
                {
                    view.LateUpdate();
                    snap = view.CaptureTelemetry();
                    if (snap.PendingTileCount > 0 && snap.ConsumeBacklog == snap.PendingTileCount) break;
                    view.AwaitInFlightMeshBuilds();
                }

                Assert.Greater(snap.ConsumeBacklog, 0,
                    "with MaxConsumesPerTick=0, completed mesh builds must pile up as backlog, not be reported " +
                    "as a constant 0 (which would fail this throttled side).");
                Assert.AreEqual(snap.PendingTileCount, snap.ConsumeBacklog,
                    "nothing here is fetch/mesh build-in-flight — Pending IS the backlog in this scenario");
                Assert.AreEqual(0, snap.InFlightFetches);

                // Raise the budget and drain — proves the OTHER side: real state that drains, not a stuck counter.
                view.Config.MaxConsumesPerTick = 64;
                for (int f = 0; f < 200 && !view.AllTilesSettled(); f++)
                {
                    view.LateUpdate();
                    view.DrainMeshBuilds();
                }

                Assert.IsTrue(view.AllTilesSettled(), "raising the budget must let the tiles finish settling");
                Assert.AreEqual(0, view.CaptureTelemetry().ConsumeBacklog, "the backlog must drain to 0");
            }
            finally
            {
                view.Teardown();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewAsyncMeshBuildTests — async non-blocking tile mesh build, EditMode half
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S47 async non-blocking mesh build acceptance tests.
    ///
    /// Architecture note on tooth 1 / .Schedule().Complete() greppability:
    ///   The stage names 5 sites. Only ONE is on the live Update path: the old
    ///   StyledFillTileBuilder.cs:125 site (now replaced by managed projection in BuildMeshData).
    ///   The other sites are off-path test-only utilities:
    ///     - FillMeshPipeline.cs:208,245,443 — test-only jobified path; no MapView caller.
    ///   (S54 retired the Gen-1 MapFillBootstrap single-tile sync bootstrap entirely.)
    ///   A naive grep of the full tree finds these; they are intentionally not in the live Update path.
    /// </summary>
    [TestFixture]
    public class MapViewAsyncMeshBuildTests : BaseTestFixture
    {
        /// <summary>
        /// Ring capacity requested from every <see cref="ProfilerRecorder"/> here, and therefore the only
        /// safe upper bound when reading samples back — <c>Count</c> is NOT one once the ring has wrapped.
        /// </summary>
        private const int RecorderCapacity = 64;

        // ── Helpers ────────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": {
                        ""fill-color"": [""rgba"", 200, 50, 50, 1]
                    }
                }
            ]
        }");


        /// <summary>Deterministically settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick consumes
        /// them. The async-settle behavioural teeth (Tooth1/3/4 + tilt) live in the PlayMode half
        /// (MapRenderer.Tests.PlayMode.MapViews.MapViewAsyncMeshBuildTests); this half's off-main / profiler /
        /// drain-determinism teeth need EditMode, so they warm up with this deterministic drain.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2500)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        // ── Tooth 2: RETIRED — see this file's header comment for the before/after. ───────────

        // ── Tooth 2b: S46 profiler-marker harness — main-thread PmBuildMesh count == 0 ────────

        /// <summary>
        /// Pins the concrete numeric criterion for S47 Tooth 2 ("max single-frame stall drops sharply
        /// vs the S46 baseline") using the S46 profiler-marker harness (ProfilerRecorder), as required
        /// by the reviewer.
        ///
        /// S46 baseline (pre-S47 sync path): PmBuildMesh fired on the MAIN THREAD (>0 main-thread
        ///   hits) because BuildMesh was called synchronously inside Tick on the main thread.
        ///   Concrete baseline: ≥1 main-thread sample per tile built.
        ///
        /// S47 async path: BuildMeshData (which fires PmBuildMesh) runs inside Task.Run on a
        ///   ThreadPool thread. The main thread never enters BuildMeshData during Tick.
        ///   Concrete bound: ZERO main-thread PmBuildMesh samples after a full async tile load.
        ///
        /// Two recorders (both CollectOnlyOnCurrentThread = main thread only):
        ///   A) MapRenderer.Meshing.StyledFillTileBuilder.BuildLayerInput → must be ZERO (prologue off main
        ///      thread; job-scheduling-design.md §8 stage 3 — production fires this marker now, not
        ///      WriteMeshData, which stays reachable only through a direct call, e.g. from tests)
        ///   B) MapRenderer.Mesh.Upload      → must be > ZERO (consume/upload still runs on main thread)
        ///
        /// Recorder B is the positive control: it confirms that PumpUntilSettled actually built the
        /// tile on this thread, so the ==0 for A is meaningful (not "nothing happened").
        ///
        /// [UnityTest] coroutine: yields frames so the profiler commits accumulated samples, mirroring
        /// the technique used in ProfilerMarkerTests.ProfilerRecorder_BuildMarker_HasSamplesAfterTileLoad.
        /// </summary>
        [UnityTest]
        public IEnumerator Tooth2b_MainThreadBuildMarker_ZeroHits_AfterAsyncLoad()
        {
            const string buildMarkerName  = StyledFillTileBuilder.ProfilerMarkerNames.BuildLayerInput;
            const string uploadMarkerName = "MapRenderer.Mesh.Upload";

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_T2b"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            // Start recorders BEFORE the tile load. CollectOnlyOnCurrentThread restricts capture to the
            // main (test) thread — so Task.Run background samples for PmBuildMesh are NOT counted.
            // SumAllSamplesInFrame accumulates sample hits per frame (not just durations).
            using var tessRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Scripts, buildMarkerName, capacity: RecorderCapacity,
                options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                         ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            using var uploadRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Scripts, uploadMarkerName, capacity: RecorderCapacity,
                options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                         ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);

                // Drive the async tile load. PumpUntilSettled calls Tick() repeatedly on the main thread.
                // Mesh build runs on a background Task.Run thread; UploadMesh runs on this (main) thread.
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(),
                    "Tile must settle before reading profiler samples — otherwise markers may not have fired.");

                // Yield frames to let the profiler commit accumulated sample data.
                yield return null;
                yield return null;

                // Sum marker invocations across all recorded frames.
                // ProfilerRecorderSample.Count = number of Begin/End marker firings in that frame
                // (correct hit-count metric — recorder.Count alone counts frame-buffer entries, not firings).
                // Clamp to the ring's CAPACITY, not Count: ProfilerRecorder is a fixed-size ring, and the
                // pump above runs far more frames than that, so once it wraps `Count` stops being a valid
                // index bound and GetSample() throws IndexOutOfRange (intermittently, on slow machines).
                long tessMainHits = 0L;
                for (int i = 0; i < math.min(tessRecorder.Count, RecorderCapacity); i++)
                    tessMainHits += tessRecorder.GetSample(i).Count;

                long uploadMainHits = 0L;
                for (int i = 0; i < math.min(uploadRecorder.Count, RecorderCapacity); i++)
                    uploadMainHits += uploadRecorder.GetSample(i).Count;

                // ── Positive control: PmMeshUpload fired on the main thread (consume did run here) ──
                // If this is 0, the tile never built — the build==0 assertion would be vacuously
                // true and meaningless. Upload runs in ConsumeMeshBuild on the main thread.
                Assert.Greater(uploadMainHits, 0L,
                    $"Tooth 2b positive control: PmMeshUpload ('{uploadMarkerName}') must have fired " +
                    $">0 times on the main thread (got {uploadMainHits}). "                            +
                    "If 0, no tile was built — the ==0 build assertion would be vacuous. "             +
                    "Check AllTilesSettled() and that the fixture path is correct.");

                // ── Concrete bound: PmBuildMesh == 0 on the main thread ─────────────────────────
                // S46 baseline: ≥1 main-thread PmBuildMesh hit per tile (BuildMesh was synchronous).
                // S47 bound:    0 main-thread PmBuildMesh hits (BuildMeshData runs in Task.Run).
                // Delta: main-thread build cost drops from full-tile duration to ZERO — the
                // largest possible improvement in main-thread blocking, confirming S47's headline fix.
                Assert.AreEqual(0L, tessMainHits,
                    $"Tooth 2b: PmBuildMesh ('{buildMarkerName}') fired {tessMainHits} time(s) "        +
                    $"on the main thread — expected 0. "                                                +
                    "S46 baseline: ≥1 main-thread hit per tile (sync BuildMesh path). "                 +
                    "S47 async path: BuildMeshData runs in Task.Run (off main thread), so "             +
                    "PmBuildMesh must NEVER fire on the main thread during Tick. "                      +
                    "If non-zero, mesh build is still synchronous on the main thread (S47 regressed). " +
                    $"Upload hits (positive control) = {uploadMainHits}.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ── Tooth 5: drain determinism — DrainMeshBuilds() settles all tiles ──────────────────

        /// <summary>
        /// DrainMeshBuilds() must block until all outstanding mesh build tasks complete and
        /// AllTilesSettled() returns true immediately after.
        /// </summary>
        [Test]
        public void Tooth5_DrainMeshBuilds_SettlesAllTiles()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_T5"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);

                // First Tick: kicks fetch (sync) and mesh build Task.
                view.LateUpdate();

                // DrainMeshBuilds: blocks until tasks finish, then consumes them.
                view.DrainMeshBuilds();

                Assert.IsTrue(view.AllTilesSettled(),
                    "Tooth 5: AllTilesSettled() must be true immediately after DrainMeshBuilds().");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "Tooth 5: The z0/0/0 tile must be built after explicit drain.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ── BuildMeshData / UploadMesh split sanity ────────────────────────────────────────────

        /// <summary>
        /// Sanity test: BuildMeshData → UploadMesh round-trip produces the same vertex count and positions
        /// as the synchronous BuildMesh convenience.
        ///
        /// job-scheduling-design.md §8 stage 4 Group B: <c>WriteMeshData</c> now SCHEDULES the graph rather
        /// than running it, and job scheduling is main-thread-only (see <c>WriteMeshData</c>'s own doc) — so
        /// this write happens on the main thread too, matching what production actually does (the graph
        /// arm's write step is scheduled from the pump, never from a worker). Both paths still go through
        /// the same <c>FillStreamWriteJob</c>, so positions must be bit-for-bit identical (no ULP drift from
        /// the split).
        /// </summary>
        [Test]
        public void BuildMeshDataAndUploadMesh_RoundTrip_MatchesSyncBuildMesh()
        {
            byte[] bytes     = SampleTileFixture.Bytes();
            using var    mvtTile   = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, bytes);
            var    style     = MinimalStyle();
            var    fillLayer = style.Layers[0];
            var    paint     = ((Fill.StyleLayer)fillLayer).Paint;
            var    features  = FeatureSelector.SelectFeatures(fillLayer, mvtTile, 0.0);
            var    mvtLayer  = MapRenderer.Jobs.Tiles.SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);

            Assert.IsNotNull(mvtLayer);
            Assert.Greater(features.Count, 0);

            var (bMin, _) = new TileId { Z = 0, X = 0, Y = 0 }.MercatorBounds();
            var tileOrigin = new double2(bMin.x, bMin.y);

            // Reference path: build + apply entirely on THIS (main) thread.
            Mesh syncMesh = TestTileMeshBuilder.BuildFillFromLayer(
                mvtLayer, TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0), paint, 0.0,
                new TileId { Z = 0, X = 0, Y = 0 });

            // Split path (production shape): allocate, schedule+write (WriteMeshData is main-thread-only —
            // see its own doc), apply — all on THIS (main) thread.
            var mda = Mesh.AllocateWritableMeshData(1);
            // IR C1 P3: the layer's own buffer, BORROWED — the decoded tile owns and frees it.
            TileGeometryBuffers geometry = mvtLayer.Geometry;
            SyncMeshWrite.Fill(
                mda[0], TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0), geometry, paint, 0.0,
                new double3(tileOrigin.x, 0.0, tileOrigin.y), out int splitVertexCount, out _);

            Mesh splitMesh = null;
            if (splitVertexCount > 0)
            {
                splitMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                Mesh.ApplyAndDisposeWritableMeshData(mda, splitMesh);
            }
            else mda.Dispose();

            Assert.IsNotNull(syncMesh,  "Main-thread build must return a mesh for the fixture");
            Assert.IsNotNull(splitMesh, "Off-main write + main apply must return a mesh for the fixture");

            Assert.AreEqual(syncMesh.vertexCount, splitMesh.vertexCount,
                "Off-main WriteMeshData + main apply must produce the same vertex count as the main-thread build.");

            // Vertex positions must match exactly — both call the same ProjectVerticesManaged,
            // so there should be no ULP difference between the split and sync paths.
            Vector3[] syncVerts  = syncMesh.vertices;
            Vector3[] splitVerts = splitMesh.vertices;

            if (syncVerts.Length > 0)
            {
                int mid  = syncVerts.Length / 2;
                int last = syncVerts.Length - 1;

                Assert.AreEqual(syncVerts[0], splitVerts[0],
                    "First vertex position must match between the main-thread and off-main builds.");
                Assert.AreEqual(syncVerts[mid], splitVerts[mid],
                    $"Middle vertex [{mid}] position must match.");
                Assert.AreEqual(syncVerts[last], splitVerts[last],
                    $"Last vertex [{last}] position must match.");
            }
        }
    }
}
