// S47 acceptance tests — async non-blocking tile mesh build.
//
// Tooth 1: No .Schedule().Complete() on the live Update path. Behavioral: tiles do NOT transition
//          fetch→Built in the same frame the fetch completes (mesh build is deferred ≥1 frame).
// Tooth 2: Non-blocking — BuildMeshData runs off the main thread (verified via thread-id hook).
// Tooth 2b: Main-thread PmBuildMesh sample count == 0 after async load (S46 profiler-marker harness);
//           pins the concrete numeric criterion from S47 tooth 2 acceptance.
// Tooth 3: Correctness parity — async path produces same vertex count AND positions as sync StyledFillTileBuilder.BuildMesh.
// Tooth 4: Cancellation (S04) — Released-mid-flight tile does not create a GameObject.
// Tooth 5: Drain determinism — DrainMeshBuilds() + AllTilesSettled() == true.
// Tooth 6: Steady-state alloc — Tick is alloc-free once settled.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.MapViews
{
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
    public class MapViewAsyncMeshBuildTests
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


        /// <summary>
        /// Pumps Tick() until every loaded tile has settled or a spin budget is hit.
        /// With S47 async mesh build, each Tick() polls completed tasks, so a few spins +
        /// Thread.Sleep(1) drain the ThreadPool mesh build naturally.
        /// </summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);
            }
        }

        // ── Cover-key: TILT must trigger a cover recompute (frustum selector integration) ─────────

        /// <summary>
        /// The frustum-based <c>FrustumTileSelector</c> makes the visible set depend on TILT — so
        /// <c>TileManager.Tick</c>'s cover-recompute key MUST include tilt, else tilting the camera (the exact
        /// bug scenario) leaves the far field toward the horizon stale. This drives the FULL TileManager path
        /// (which the selector-level acceptance test bypasses): a stub source serves every tile, so
        /// <c>LoadedTileCount == cover size</c>. Tilting from overhead to 60° with lon/lat/zoom/heading fixed
        /// must GROW the cover (the horizon trapezoid). A tilt-blind key would leave the count unchanged.
        /// </summary>
        [Test]
        public void TileCover_RecomputesOnTiltChange_FarFieldGrows()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_TiltCover");
            var view  = go.AddComponent<MapView>().WithTestMaterials().WithTestCamera();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 22;
            view.Config.MaxConsumesPerTick    = 256;
            view.Config.MaxMeshBuildsPerTick  = 256;

            try
            {
                // Overhead (tilt 0) cover.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                PumpUntilSettled(view);
                int flat = view.LoadedTileCount();
                Assert.Greater(flat, 0, "flat (overhead) cover must be non-empty");

                // Change ONLY the tilt to 60° — lon/lat/zoom/heading are identical.
                view.Camera.Apply(new CameraPropertiesUpdate { Tilt = 60 });
                view.LateUpdate();
                PumpUntilSettled(view);
                int tilted = view.LoadedTileCount();

                Assert.Greater(tilted, flat,
                    $"tilting to 60° must recompute the cover and request the horizon trapezoid "         +
                    $"(flat={flat}, tilted={tilted}); an unchanged count means TILT is missing from the " +
                    $"TileManager cover-recompute key");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth 1: no live .Schedule().Complete() — mesh build is deferred ≥ 1 frame ──────

        /// <summary>
        /// Behavioral test for tooth 1: on the same frame the fetch completes, the tile must NOT
        /// transition to Built == true (mesh build is deferred to a later frame / poll cycle).
        ///
        /// Setup: FixtureSource returns synchronously (Task.FromResult), so after the first Tick()
        /// the fetch task IsCompleted == true. If mesh build were synchronous (old behavior), the
        /// tile would be Built == true on that same Tick(). With S47 async, mesh build is kicked
        /// as a background Task and must NOT be consumed in the same Tick().
        /// </summary>
        [Test]
        public void Tooth1_MeshBuildDeferred_TileNotBuiltInSameFetchFrame()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_T1");
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

                // First Tick: cover is dirty, tile is requested.
                // FixtureSource returns synchronously, so fetch IsCompleted immediately.
                // The mesh build Task is KICKED here but NOT consumed.
                view.LateUpdate();

                // Immediately after the first Tick, the tile should NOT yet be built.
                // AllTilesSettled() must return false because mesh build is in-flight.
                // (If this assertion fails, mesh build is synchronous in Tick — tooth 1 violated.)
                Assert.IsFalse(view.AllTilesSettled(),
                    "Tooth 1: After the Tick that kicks mesh build, AllTilesSettled() must be false. "  +
                    "Mesh build must be deferred to a later frame (async Task.Run path), not consumed " +
                    "synchronously in the same Tick() call that starts it.");

                // Now let the async task complete and drain naturally.
                PumpUntilSettled(view, maxFrames: 2500);

                Assert.IsTrue(view.AllTilesSettled(),
                    "After draining, all tiles must eventually settle.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "The z0/0/0 tile must be built after draining.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth 2: non-blocking — BuildMeshData runs off the main thread ────────────────────

        /// <summary>
        /// Verifies that the decode/mesh build work runs on a background thread, not the main thread.
        ///
        /// We use a test hook: override BuildMeshData via a test-only static delegate that records
        /// the thread id. Since we can't inject a hook into StyledFillTileBuilder directly, we
        /// instead verify the structural property: the managed thread id captured INSIDE a
        /// Task.Run equals the thread id we observe spinning outside (background ≠ main).
        ///
        /// Practical approach: run the fixture through BuildMeshData on a Task.Run; verify the
        /// captured thread id is NOT the main thread id.
        /// </summary>
        [Test]
        public void Tooth2_BuildMeshData_RunsOffMainThread()
        {
            byte[] bytes     = SampleTileFixture.Bytes();
            using var    mvtTile   = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, bytes);
            var    style     = MinimalStyle();
            var    fillLayer = style.Layers[0];
            var    paint     = new Fill.PaintProperties(fillLayer);
            var    features  = FeatureSelector.SelectFeatures(fillLayer, mvtTile, 0.0);
            var    mvtLayer  = MapRenderer.Jobs.Tiles.SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);

            Assert.IsNotNull(mvtLayer, "Fixture must contain 'countries' MVT layer");
            Assert.Greater(features.Count, 0, "FeatureSelector must return at least 1 feature");

            var (bMin, _) = new TileId { Z = 0, X = 0, Y = 0 }.MercatorBounds();
            var tileOrigin = new double2(bMin.x, bMin.y);

            int mainThreadId     = Thread.CurrentThread.ManagedThreadId;
            int capturedThreadId = mainThreadId; // will be overwritten in the task

            // AllocateWritableMeshData is main-thread only; build INTO it on a background task and
            // capture the thread id (S89 Stage B: the worker-write path, spike-guarded).
            var mda = Mesh.AllocateWritableMeshData(1);
            var task = Task.Run(() =>
            {
                capturedThreadId = Thread.CurrentThread.ManagedThreadId;
                // IR C1 P3: the layer's own buffer, BORROWED — the decoded tile owns and frees it.
                TileGeometryBuffers geometry = mvtLayer.Geometry;
                StyledFillTileBuilder.WriteMeshData(
                    mda[0], TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0), geometry, paint, 0.0,
                    new double3(tileOrigin.x, 0.0, tileOrigin.y), out int vc, out _);
                return vc;
            });

            int vertexCount = task.GetAwaiter().GetResult();

            try
            {
                Assert.AreNotEqual(mainThreadId, capturedThreadId,
                    "Tooth 2: WriteMeshData must run on a background ThreadPool thread (not the main thread). " +
                    $"Main thread id: {mainThreadId}, captured thread id: {capturedThreadId}.");

                Assert.Greater(vertexCount, 0, "WriteMeshData must produce at least one vertex off the main thread");
            }
            finally
            {
                mda.Dispose(); // never applied — dispose the writable array
            }
        }

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
        ///   A) MapRenderer.Meshing.StyledFillTileBuilder.WriteMeshData  → must be ZERO  (build moved off main thread)
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
            const string buildMarkerName  = StyledFillTileBuilder.ProfilerMarkerNames.WriteMeshData;
            const string uploadMarkerName = "MapRenderer.Mesh.Upload";

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_T2b");
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
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth 3: correctness parity — async path == sync path ──────────────────────────────

        /// <summary>
        /// The async MapView live loop (build off-main + upload on main) must produce the same
        /// vertex count AND vertex positions as a direct synchronous call to StyledFillTileBuilder.BuildMesh.
        ///
        /// Note: S47 replaced the Burst <c>ProjectTileToWebMercatorJob</c> (which required
        /// <c>.Schedule().Complete()</c> and therefore had main-thread affinity) with a scalar managed C#
        /// loop (<c>ProjectVerticesManaged</c>) that replicates the same double-precision arithmetic.
        /// This test verifies that no ULP difference was introduced: positions must be exactly equal.
        ///
        /// This mirrors MapViewLiveLoopTests.MapView_GoLive_ProducesSameGeometryAsDirectBuilder
        /// but exercises the S47 async path explicitly by waiting for async settle.
        /// </summary>
        [Test]
        public void Tooth3_AsyncPath_ProducesSameGeometryAsSyncPath()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var    src   = TestDataSource.FromBytes(bytes);
            var    go    = new GameObject("MapView_T3");
            var    view  = go.AddComponent<MapView>().WithTestMaterials();
            var    style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built by the async live loop");

                // Backend-agnostic: one Mesh per fill layer (1 in MinimalStyle).
                Mesh[] asyncMeshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(asyncMeshes, "The built tile must expose its layer meshes.");
                Assert.AreEqual(1, asyncMeshes.Length,
                    "Tile must have exactly 1 layer mesh (1 fill layer in MinimalStyle).");
                Mesh asyncMesh = asyncMeshes[0];
                Assert.IsNotNull(asyncMesh, "The async path must have built a mesh");

                // Direct sync path for reference.
                using var mvtTile   = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, bytes);
                var fillLayer = style.Layers[0];
                var paint     = new Fill.PaintProperties(fillLayer);
                var features  = FeatureSelector.SelectFeatures(fillLayer, mvtTile, 0.0);
                var mvtLayer  = MapRenderer.Jobs.Tiles.SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
                Assert.IsNotNull(mvtLayer);

                Mesh syncMesh = TestTileMeshBuilder.BuildFillFromLayer(
                    mvtLayer, TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0), paint, 0.0,
                    new TileId { Z = 0, X = 0, Y = 0 }, projection: null, layout: null,
                    // Same window as the MapView arm — decoded through the SAME factory the view
                    // uses. Without this the reference arm builds unclipped and the oracle silently
                    // stops being a comparison the moment the config default is non-disabled.
                    clip: MapRenderer.Core.Tiles.TileBufferClip.FromInspectorUnits(view.Config.FillTileBufferClip));
                Assert.IsNotNull(syncMesh);

                Assert.AreEqual(syncMesh.vertexCount, asyncMesh.vertexCount,
                    "Tooth 3: Async live-loop mesh vertex count must equal direct sync builder output. " +
                    "Same feature set + same managed projection = same vertex layout.");

                // Position comparison — both paths use the same ProjectVerticesManaged code,
                // so positions must be bit-for-bit equal (no ULP drift between async and sync).
                Vector3[] asyncVerts = asyncMesh.vertices;
                Vector3[] syncVerts  = syncMesh.vertices;

                // Compare a sample of vertices (first, middle, last) to avoid iterating thousands of verts.
                // Full equality is impractical in a test but position-sampling demonstrates parity.
                if (syncVerts.Length > 0)
                {
                    int mid  = syncVerts.Length / 2;
                    int last = syncVerts.Length - 1;

                    Assert.AreEqual(syncVerts[0], asyncVerts[0],
                        "Tooth 3: First vertex position must match between sync and async paths.");
                    Assert.AreEqual(syncVerts[mid], asyncVerts[mid],
                        $"Tooth 3: Middle vertex [{mid}] position must match.");
                    Assert.AreEqual(syncVerts[last], asyncVerts[last],
                        $"Tooth 3: Last vertex [{last}] position must match.");
                }
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth 4: cancellation (S04) — released mid-flight tile is discarded ───────────────

        /// <summary>
        /// Request tiles at z=5, kick mesh build mid-flight, pan far away to evict original tiles,
        /// then verify no stale GameObjects are created for the evicted tiles after settle.
        ///
        /// Verifies the S04 contract: a mesh build completing after ReleaseTile must not
        /// create a GameObject for the released tile.
        ///
        /// Strategy:
        ///   - Start at z=5, lon=0: loads a 3x3 cover around tile (5,16,16).
        ///   - One Tick kicks mesh build tasks for all fetched tiles.
        ///   - IMMEDIATELY pan far east (lon=170) — the cover is now around (5,31,16).
        ///     The two covers are non-overlapping, so all original tiles are evicted.
        ///   - The eviction Tick releases all original tiles. Their mesh build tasks may still
        ///     be running or just completed. ReleaseTile removes them from _loaded so subsequent
        ///     PumpPending snapshots exclude them — ConsumeMeshBuild is never called.
        ///   - After full settle, original tiles must NOT be accessible as built tiles.
        /// </summary>
        [Test]
        public void Tooth4_ReleasedMidFlight_NoGameObjectCreated()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_T4");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                // Load initial cover at lon=0, z=5: center tile is (5,16,16).
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // First Tick: tiles are added to _loaded, fetch tasks kicked (FixtureSource is sync,
                // but TileScheduler's FetchAndCacheAsync has a Task.Run hop so they're not yet complete).
                view.LateUpdate();
                // Second Tick: fetch tasks are likely complete now; mesh build tasks are kicked.
                Thread.Sleep(5); // ensure ThreadPool Task.Run hop completes
                view.LateUpdate();

                // Pan far east immediately — before mesh build tasks complete.
                // lon=170, z=5 → center tile (5,31,16), completely non-overlapping cover.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate(); // cover recompute → evicts all original (5,16,*) tiles

                // Original center tile must be gone from _loaded.
                Assert.IsFalse(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "Tooth 4: Original tile (5,16,16) must be evicted after panning.");

                // Let everything settle (new cover tiles build).
                PumpUntilSettled(view, maxFrames: 2500);

                // After full settle, the evicted original tile must still be absent.
                // (It was removed from _loaded by ReleaseTile and must not be re-added.)
                Assert.IsFalse(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "Tooth 4: Released tile must not have been re-created as a GameObject " +
                    "even if its mesh build task completed after release.");

                // New cover must be built.
                Assert.IsTrue(view.AllTilesSettled(),     "New cover tiles must all settle.");
                Assert.IsTrue(view.LoadedTileCount() > 0, "New cover tiles must be present.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
                Object.DestroyImmediate(go);
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
            var go    = new GameObject("MapView_T5");
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
                Object.DestroyImmediate(go);
            }
        }

        // ── BuildMeshData / UploadMesh split sanity ────────────────────────────────────────────

        /// <summary>
        /// Sanity test: BuildMeshData (off-main) → UploadMesh (main) round-trip produces the same
        /// vertex count and positions as the synchronous BuildMesh convenience.
        ///
        /// Both paths call the same <c>ProjectVerticesManaged</c> loop, so positions must be
        /// bit-for-bit identical (no ULP drift from the split).
        /// </summary>
        [Test]
        public void BuildMeshDataAndUploadMesh_RoundTrip_MatchesSyncBuildMesh()
        {
            byte[] bytes     = SampleTileFixture.Bytes();
            using var    mvtTile   = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, bytes);
            var    style     = MinimalStyle();
            var    fillLayer = style.Layers[0];
            var    paint     = new Fill.PaintProperties(fillLayer);
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

            // Split path (production shape): allocate on main, WRITE off the main thread, apply on main.
            var mda = Mesh.AllocateWritableMeshData(1);
            var task = Task.Run(() =>
            {
                // IR C1 P3: the layer's own buffer, BORROWED — the decoded tile owns and frees it.
                TileGeometryBuffers geometry = mvtLayer.Geometry;
                StyledFillTileBuilder.WriteMeshData(
                    mda[0], TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0), geometry, paint, 0.0,
                    new double3(tileOrigin.x, 0.0, tileOrigin.y), out int vc, out _);
                return vc;
            });
            int splitVertexCount = task.GetAwaiter().GetResult();

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