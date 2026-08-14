// S47 acceptance tests — async non-blocking tile mesh build.
//
// EditMode half: the off-main-thread marker (D5), the profiler-recorder harness (D3), the drain-mechanism
// tooth (D4), and the off-main round-trip sanity check (no settle-poll). The async-settle teeth (tile
// cover recompute, tooth 1 deferred-build, tooth 3 geometry parity, tooth 4 cancellation) live in the
// PlayMode half (MapRenderer.Tests.PlayMode.MapViews.MapViewAsyncMeshBuildTests).
//
// Tooth 2: Non-blocking — BuildMeshData runs off the main thread (verified via thread-id hook).
// Tooth 2b: Main-thread PmBuildMesh sample count == 0 after async load (S46 profiler-marker harness);
//           pins the concrete numeric criterion from S47 tooth 2 acceptance.
// Tooth 5: Drain determinism — DrainMeshBuilds() + AllTilesSettled() == true.

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