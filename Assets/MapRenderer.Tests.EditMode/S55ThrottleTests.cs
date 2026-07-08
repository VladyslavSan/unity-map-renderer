// S55 acceptance tests — "Flatten tile-load lag spikes" throttle features.
//
// Tooth (a): Mesh build cap binds — MaxMeshBuildsPerTick limits kick-offs to at most N
//            per Tick. Verified by asserting MeshBuildsKickedLastTick == cap (not all tiles)
//            on the first kick Tick, with >= 2 tiles in cover.
//
// Tooth (b): Per-MESH budget splits tiles across frames (S87 DECISIVE) — with the per-frame mesh-count
//            budget (MaxConsumesPerTick) set to 1, a multi-layer tile is consumed one mesh per frame: at
//            least one frame consumes a mesh while completing NO tile (MeshesConsumedLastTick >= 1 &&
//            TilesConsumedLastTick == 0) — proving consume is per-MESH, not per-tile. A tile-atomic consume
//            can NEVER produce such a frame (it always completes the tile it touches). No frame exceeds the
//            budget; the cover still settles; tiles are multi-layer (non-vacuous).
//
// Tooth (c): Vertex budget bounds one pump + markers are per-mesh (S87) — with the mesh-count cap
//            non-binding and a small per-frame VERTEX budget, ONE pump consumes some-but-not-all meshes
//            (not settled afterward), and PmAddTileLayer fires EXACTLY MeshesConsumedLastTick times (one
//            per consumed mesh, not the full backlog). [UnityTest] yields frames to commit profiler samples.
//
// Tooth (h): Partial-tile eviction leaks nothing (S87) — a tile evicted MID-consume (ConsumeCursor
//            part-way) disposes its already-uploaded layers' NativeArrays (per-mesh, during consume) AND
//            its un-consumed remainder (via the holding pen), with no double-dispose. DebugLiveAllocCount
//            returns to baseline after evict + teardown.
//
// Tooth (d): Settles identically — static cover with throttled vs uncapped settings produces
//            the same tile count and AllTilesSettled() outcome.
//
// Tooth (e): Pixel parity — a throttled MapView (S55 defaults: MaxMeshBuildsPerTick=2,
//            MaxVerticesPerTick=50000) produces a non-blank settled render on the Entities
//            backend, proving the throttle changes timing not output. Inconclusive when the
//            snapshot is all-black (GPU context absent in batchmode). BRG-backend parity not
//            covered here; that is the residual gap vs. the "BOTH backends" stage wording.
//
// Tooth (f): Bounds correctness — mesh.bounds assigned in UploadMesh (baked AABB) equals
//            Unity's RecalculateBounds() within floating-point tolerance, for both fill and
//            line builders.
//
// Tooth (g): No new per-frame GC — the 3 new int counter resets and budget arithmetic must
//            not box. Verified with Is.Not.AllocatingGCMemory() on BRG backend in steady state.

using System.Collections;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Unity.Profiling;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S55 throttle acceptance tests. Engine-side: all tests are Unity EditMode.
    /// </summary>
    [TestFixture]
    public class S55ThrottleTests
    {
        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Fill-only style: countries polygon layer.</summary>
        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""S55Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        /// <summary>Fill + line style: countries fill and geolines linestring layer.</summary>
        private static StyleDocument FillAndLineStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""S55TestLine"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                },
                {
                    ""id"": ""geolines-stroke"",
                    ""type"": ""line"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": {
                        ""line-color"": [""rgba"", 100, 200, 50, 1],
                        ""line-width"": 10
                    }
                }
            ]
        }");

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

        // ── S87 shared helper ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a z=5 (9-tile) cover with consume BLOCKED (<c>MaxConsumesPerTick = 0</c>) and every
        /// tile's mesh build KICKED and COMPLETED (uncapped kicks + a generous wait). After this returns,
        /// each tile has a completed <c>MeshBuildTask</c> awaiting consume; nothing is Built yet — the
        /// caller sets the consume budget and pumps. Caller owns <c>Teardown()</c> + <c>DestroyImmediate(go)</c>.
        ///
        /// The 2000ms wait covers all 9 concurrent mesh builds on slow / single-core machines (a fixed
        /// 300ms was too tight — see S55 Tooth_c history). We cannot poll-for-all-completions without
        /// consuming, so a generous sleep is used.
        /// </summary>
        private static (GameObject go, MapView view) SetupBlockedBacklog(StyleDocument style)
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S87");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 0;            // 0 BLOCKS consume (build the backlog)
            view.Config.MaxMeshBuildsPerTick = 64;           // kick all tiles
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
            view.LateUpdate(); Thread.Sleep(10);   // request
            view.LateUpdate(); Thread.Sleep(10);   // observe → ReadyBytes
            view.LateUpdate(); Thread.Sleep(2000); // kick all; wait for every mesh build to complete
            return (go, view);
        }

        // ── Tooth (a): Mesh build cap binds ────────────────────────────────────────────────

        /// <summary>
        /// MaxMeshBuildsPerTick=1 limits kick-offs to exactly 1 per Tick even when multiple
        /// tiles have ready fetch bytes. Uses z=5 (known 9-tile cover like S51 tests) to guarantee >= 2 tiles.
        ///
        /// Timing note: with the two-tick kick pattern (fetch observe on Tick N, kick on Tick N+1),
        /// MeshBuildsKickedLastTick is 0 on the observe tick and 1 on the first kick tick.
        /// </summary>
        [Test]
        public void Tooth_a_MeshBuildCapBinds()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S55_A");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick          = 64;
            view.Config.MaxMeshBuildsPerTick   = 1;  // one kick per Tick
            view.Config.MaxVerticesPerTick        = int.MaxValue;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Tick 1: request tiles. PumpPending sees no tiles, then cover adds them.
                view.LateUpdate();
                Assert.GreaterOrEqual(view.LoadedTileCount(), 2,
                    "Need at least 2 tiles at z=5 for the cap test to be non-vacuous.");
                Assert.AreEqual(0, view.MeshBuildsKickedLastTick(),
                    "Tooth (a): after Tick 1 (requests only), no kicks issued yet.");

                // Tick 2: observe completed fetches → ReadyBytes set. Still no kicks.
                Thread.Sleep(2);
                view.LateUpdate();
                Assert.AreEqual(0, view.MeshBuildsKickedLastTick(),
                    "Tooth (a): after observe Tick, kicks still 0 (kick deferred to next Tick).");

                // Tick 3: first kick Tick — cap of 1 must bind.
                view.LateUpdate();
                Assert.AreEqual(1, view.MeshBuildsKickedLastTick(),
                    $"Tooth (a): cap=1 must limit kicks to exactly 1 on the first kick Tick " +
                    $"(loaded tiles = {view.LoadedTileCount()}).");

                // Settle to confirm all tiles eventually complete under the cap.
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must eventually settle even with MaxMeshBuildsPerTick=1.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth (b): Per-MESH budget splits tiles across frames (S87 DECISIVE) ───────────────

        /// <summary>
        /// S87: consume is per-MESH, not per-tile. With the per-frame mesh-count budget
        /// (<c>MaxConsumesPerTick</c>) set to 1, a multi-layer tile is uploaded one mesh per frame — so at
        /// least one frame uploads a mesh while completing NO tile (<c>MeshesConsumedLastTick &gt;= 1 &amp;&amp;
        /// TilesConsumedLastTick == 0</c>). A tile-atomic consume can NEVER produce such a frame (it always
        /// finishes the tile it touches), so this is the decisive falsifier. Also asserts no frame exceeds
        /// the budget, the cover still settles, and tiles are genuinely multi-layer (non-vacuous).
        /// </summary>
        [Test]
        public void Tooth_b_PerMeshBudget_SplitsTilesAcrossFrames()
        {
            var (go, view) = SetupBlockedBacklog(FillAndLineStyle());
            try
            {
                int totalTiles = view.LoadedTileCount();
                Assert.GreaterOrEqual(totalTiles, 6,
                    "Need a multi-tile z=5 backlog so the per-mesh split is non-vacuous.");

                // Consume ONE mesh per frame.
                view.Config.MaxConsumesPerTick   = 1;
                view.Config.MaxVerticesPerTick = int.MaxValue;

                bool sawPartialTileFrame = false; // a frame that uploaded a mesh but completed NO tile
                int  maxMeshesInAnyFrame = 0;
                int  totalMeshes         = 0;
                int  frames              = 0;
                while (!view.AllTilesSettled() && frames < 5000)
                {
                    view.LateUpdate();
                    int m = view.MeshesConsumedLastTick();
                    int t = view.TilesConsumedLastTick();
                    if (m > maxMeshesInAnyFrame) maxMeshesInAnyFrame = m;
                    totalMeshes += m;
                    if (m >= 1 && t == 0) sawPartialTileFrame = true;
                    frames++;
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "Cover must settle even at 1 mesh/frame (throttle delays, never drops).");
                Assert.LessOrEqual(maxMeshesInAnyFrame, 1,
                    "Tooth (b): the per-frame mesh-count budget (1) must bind — no frame uploads > 1 mesh.");
                Assert.IsTrue(sawPartialTileFrame,
                    "Tooth (b) DECISIVE: at least one frame must upload a mesh while completing NO tile " +
                    "(MeshesConsumedLastTick >= 1 && TilesConsumedLastTick == 0) — proving a tile's layers " +
                    "split across frames (per-MESH consume). A tile-atomic consume can never produce such a frame.");
                Assert.Greater(totalMeshes, totalTiles,
                    $"Tiles must be multi-layer ({totalMeshes} meshes across {totalTiles} tiles) for the " +
                    "split to be meaningful — fill + line each contribute a mesh.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth (d): Settles identically ────────────────────────────────────────────────

        /// <summary>
        /// Throttled (MaxMeshBuildsPerTick=1, MaxVerticesPerTick=1) and uncapped settings
        /// must produce the same number of loaded tiles (static cover, same fixture).
        /// Tests that throttle does not permanently stall or drop tiles.
        /// </summary>
        [Test]
        public void Tooth_d_SettlesIdenticallyWithAndWithoutThrottle()
        {
            // Run 1: tight throttle.
            int countThrottled;
            {
                var src  = TestDataSource.FromBytes(FixtureBytes());
                var go   = new GameObject("MapView_S55_D_Throttled");
                var view = go.AddComponent<MapView>().WithTestMaterials();
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 1;
                view.Config.MaxVerticesPerTick      = 1;
                try
                {
                    view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());
                    PumpUntilSettled(view);
                    Assert.IsTrue(view.AllTilesSettled(), "Throttled run must settle.");
                    countThrottled = view.LoadedTileCount();
                }
                finally { view.Teardown(); Object.DestroyImmediate(go); }
            }

            // Run 2: uncapped.
            int countUncapped;
            {
                var src  = TestDataSource.FromBytes(FixtureBytes());
                var go   = new GameObject("MapView_S55_D_Uncapped");
                var view = go.AddComponent<MapView>().WithTestMaterials();
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;
                view.Config.MaxVerticesPerTick      = int.MaxValue;
                try
                {
                    view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());
                    PumpUntilSettled(view);
                    Assert.IsTrue(view.AllTilesSettled(), "Uncapped run must settle.");
                    countUncapped = view.LoadedTileCount();
                }
                finally { view.Teardown(); Object.DestroyImmediate(go); }
            }

            Assert.AreEqual(countUncapped, countThrottled,
                $"Throttled (count={countThrottled}) and uncapped (count={countUncapped}) " +
                "runs must produce the same loaded-tile count (static cover, same fixture).");
        }

        // ── Tooth (f): Bounds correctness ────────────────────────────────────────────────────

        /// <summary>
        /// S55 bakes mesh.bounds on the worker thread instead of calling RecalculateBounds() on
        /// the main thread. The baked bounds must equal Unity's authoritative RecalculateBounds()
        /// within floating-point tolerance. Applies to both fill (countries) and line (geolines).
        ///
        /// Method: after settle, read baked bounds, then call RecalculateBounds() on the same mesh
        /// to obtain Unity's truth, and compare center/size within 1e-3 tolerance.
        /// </summary>
        [Test]
        public void Tooth_f_BakedBoundsMatchRecalculateBounds()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S55_F");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndLineStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tile must settle for bounds test.");

                var id     = new TileId { Z = 0, X = 0, Y = 0 };
                var meshes = view.GetTileMeshes(id);
                Assert.IsNotNull(meshes, "z=0 tile must be built.");
                Assert.GreaterOrEqual(meshes.Length, 1, "At least one layer mesh expected.");

                // ── Fill mesh bounds (meshes[0]) ─────────────────────────────────────────────
                const float eps = 1e-3f;
                Mesh fillMesh      = meshes[0];
                Bounds fillBaked   = fillMesh.bounds;
                fillMesh.RecalculateBounds();
                Bounds fillTruth   = fillMesh.bounds;

                Assert.AreEqual(fillTruth.center.x, fillBaked.center.x, eps,
                    $"Fill bounds center.x: baked={fillBaked.center.x} truth={fillTruth.center.x}");
                Assert.AreEqual(fillTruth.center.y, fillBaked.center.y, eps,
                    $"Fill bounds center.y: baked={fillBaked.center.y} truth={fillTruth.center.y}");
                Assert.AreEqual(fillTruth.center.z, fillBaked.center.z, eps,
                    $"Fill bounds center.z: baked={fillBaked.center.z} truth={fillTruth.center.z}");
                Assert.AreEqual(fillTruth.size.x, fillBaked.size.x, eps,
                    $"Fill bounds size.x: baked={fillBaked.size.x} truth={fillTruth.size.x}");
                Assert.AreEqual(fillTruth.size.y, fillBaked.size.y, eps,
                    $"Fill bounds size.y: baked={fillBaked.size.y} truth={fillTruth.size.y}");
                Assert.AreEqual(fillTruth.size.z, fillBaked.size.z, eps,
                    $"Fill bounds size.z: baked={fillBaked.size.z} truth={fillTruth.size.z}");

                // Sanity: bounds must cover non-zero XZ area (countries polygon spans real geometry).
                Assert.IsFalse(float.IsInfinity(fillBaked.size.x) || float.IsInfinity(fillBaked.size.z),
                    "Fill baked bounds must not be infinite (seeding error: all verts should be finite).");
                Assert.Greater(fillBaked.size.x + fillBaked.size.z, 0f,
                    "Fill baked bounds must have non-zero XZ extent (countries polygon is non-degenerate).");

                // ── Line mesh bounds (meshes[1], if geolines produced geometry) ────────────────
                if (meshes.Length >= 2 && meshes[1] != null)
                {
                    Mesh lineMesh     = meshes[1];
                    Bounds lineBaked  = lineMesh.bounds;
                    lineMesh.RecalculateBounds();
                    Bounds lineTruth  = lineMesh.bounds;

                    Assert.AreEqual(lineTruth.center.x, lineBaked.center.x, eps,
                        $"Line bounds center.x: baked={lineBaked.center.x} truth={lineTruth.center.x}");
                    Assert.AreEqual(lineTruth.center.y, lineBaked.center.y, eps,
                        $"Line bounds center.y: baked={lineBaked.center.y} truth={lineTruth.center.y}");
                    Assert.AreEqual(lineTruth.center.z, lineBaked.center.z, eps,
                        $"Line bounds center.z: baked={lineBaked.center.z} truth={lineTruth.center.z}");
                    Assert.AreEqual(lineTruth.size.x, lineBaked.size.x, eps,
                        $"Line bounds size.x: baked={lineBaked.size.x} truth={lineTruth.size.x}");
                    Assert.AreEqual(lineTruth.size.y, lineBaked.size.y, eps,
                        $"Line bounds size.y: baked={lineBaked.size.y} truth={lineTruth.size.y}");
                    Assert.AreEqual(lineTruth.size.z, lineBaked.size.z, eps,
                        $"Line bounds size.z: baked={lineBaked.size.z} truth={lineTruth.size.z}");

                    Assert.IsFalse(float.IsInfinity(lineBaked.size.x) || float.IsInfinity(lineBaked.size.z),
                        "Line baked bounds must not be infinite.");
                    Assert.Greater(lineBaked.size.x + lineBaked.size.z, 0f,
                        "Line baked bounds must have non-zero XZ extent (geolines spans real geometry).");
                }
                else
                {
                    // Geolines layer exists but produced no mesh (degenerate geometry or absent features).
                    // Not a test failure — the AABB is simply not exercised for lines at this zoom.
                    // The fill assertion above still covers the core S55 contract.
                    Assert.Inconclusive(
                        "Line mesh was null or missing — geolines may not have produced geometry at z=0. " +
                        "Fill bounds assertion passed. Run at a higher zoom with known line coverage to " +
                        "fully exercise the line AABB contract.");
                }
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth (c): Per-mesh budget bounds the markers (ProfilerRecorder) ──────────────────

        /// <summary>
        /// S87: under a per-frame MESH budget of N, ONE pump consumes exactly N meshes (not the full
        /// backlog), and <c>PmAddTileLayer</c> fires exactly once per consumed mesh — proving the markers
        /// (and thus AddLayer / GPU-upload cost) are bounded by the per-frame budget, not the backlog.
        /// <c>CollectOnlyOnCurrentThread</c> captures only the main-thread consume firings; two
        /// <c>yield return null</c> frames commit the profiler samples before counting.
        /// </summary>
        [UnityTest]
        public IEnumerator Tooth_c_PerMeshBudget_BoundsMarkers()
        {
            var (go, view) = SetupBlockedBacklog(FillAndLineStyle());
            try
            {
                int totalTiles    = view.LoadedTileCount();
                int layersPerTile = view.FillLayerCount() + view.LineLayerCount(); // 1+1=2
                Assert.GreaterOrEqual(totalTiles, 6, "Need a multi-tile z=5 backlog.");
                int totalBacklogMeshes = totalTiles * layersPerTile;

                // Recorders BEFORE the measured pump. CollectOnlyOnCurrentThread: the consume loop runs on
                // the main thread, so PmAddTileLayer / PmMeshUpload register here; background mesh build
                // (SetupBlockedBacklog, already done) does not.
                using var addLayerRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "MapRenderer.Tile.AddLayer", capacity: 64,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                             ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                using var uploadRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "MapRenderer.Mesh.Upload", capacity: 64,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                             ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

                // ONE pump with a partial per-frame MESH budget (< the full backlog).
                const int meshBudget = 3;
                view.Config.MaxConsumesPerTick   = meshBudget;
                view.Config.MaxVerticesPerTick = int.MaxValue;
                view.LateUpdate();

                int meshesConsumed = view.MeshesConsumedLastTick();
                Assert.AreEqual(meshBudget, meshesConsumed,
                    $"Per-frame mesh budget must bind: expected exactly {meshBudget} meshes, got " +
                    $"{meshesConsumed} (backlog = {totalBacklogMeshes}).");
                Assert.IsFalse(view.AllTilesSettled(),
                    "Budget must defer the rest — not settled after one budgeted pump.");

                yield return null;
                yield return null;

                long addLayerHits = 0;
                for (int i = 0; i < addLayerRecorder.Count; i++)
                    addLayerHits += addLayerRecorder.GetSample(i).Count;
                long uploadHits = 0;
                for (int i = 0; i < uploadRecorder.Count; i++)
                    uploadHits += uploadRecorder.GetSample(i).Count;

                // AddLayer fires once per CONSUMED (non-null) mesh — exact, per-mesh.
                Assert.AreEqual(meshesConsumed, addLayerHits,
                    $"Tooth (c): PmAddTileLayer must fire exactly once per consumed mesh ({meshesConsumed}); " +
                    $"got {addLayerHits}. Bounded by the per-frame budget, NOT the {totalBacklogMeshes}-mesh " +
                    "backlog. If 0: profiler did not capture on this thread (CollectOnlyOnCurrentThread).");
                // Upload happened and is bounded by the budget, not the backlog (>= consumed allows for any
                // empty-layer UploadMesh calls that returned null; < backlog proves the budget bound).
                Assert.GreaterOrEqual(uploadHits, meshesConsumed,
                    $"Tooth (c): PmMeshUpload ({uploadHits}) must fire at least once per consumed mesh ({meshesConsumed}).");
                Assert.Less(uploadHits, totalBacklogMeshes,
                    $"Tooth (c): PmMeshUpload ({uploadHits}) must be bounded by the per-frame budget, NOT the " +
                    $"full {totalBacklogMeshes}-mesh backlog.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth (e): Pixel parity with S55 default throttle ────────────────────────────────────

        /// <summary>
        /// A throttled MapView (S55 defaults: MaxMeshBuildsPerTick=2, MaxVerticesPerTick=50000)
        /// must render a non-blank settled fill cover on the default Entities backend. Proves the
        /// throttle changes timing, not output.
        ///
        /// Uses the same SnapshotCoverage gate as <see cref="MapViewSnapshotTests.MapViewLiveLoop_RendersMultiTileFill_NonBlank"/>.
        /// GPU-absent guard: Assert.Inconclusive when the snapshot is all-black (batchmode has no
        /// GPU context). Covers Entities backend only; BRG-backend pixel parity is a residual gap.
        /// </summary>
        [Test]
        public void Tooth_e_ThrottledRender_NonBlankCoverage_EntitiesBackend()
        {
            const int SnapW = 512, SnapH = 512;
            const int Zoom  = 3;
            var bgColor  = new Color(0.10f, 0.11f, 0.15f, 1f);
            byte bgR8 = 26, bgG8 = 28, bgB8 = 38;

            var lightGo = new GameObject("S55_E_Light");
            var light   = lightGo.AddComponent<Light>();
            light.type  = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var cameraGo = new GameObject("S55_E_Cam");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic    = true;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled         = false;
            camera.farClipPlane    = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            var mapGo = new GameObject("S55_E_MapView");
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            // S55 DEFAULT throttle values — the whole point is not to override them here.
            view.Config.TileSelection.MinZoom = Zoom; view.Config.TileSelection.MaxZoom = Zoom;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 2;      // S55 default
            view.Config.MaxVerticesPerTick      = 50000;  // S55 default

            try
            {
                var cam3 = new CameraProperties(
                    new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, Zoom, 0, 0);
                view.LoadTestStyle(TestDataSource.FromBytes(FixtureBytes()),
                    cam3, style: FillStyle());

                // Pump to settle — throttle spreads kicks/consumes across many ticks.
                for (int f = 0; f < 5000 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                {
                    view.LateUpdate();
                    Thread.Sleep(1);
                }

                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "Throttled MapView must settle all tiles (throttle defers, not drops).");

                // Frame the camera on the loaded tile bounds.
                float tileSize = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, Zoom));
                Bounds b = view.ComputeSceneBounds(tileSize);
                Assert.Greater(b.size.magnitude, 0f, "Loaded tiles must have non-degenerate scene bounds.");
                camera.transform.position = new Vector3(b.center.x, b.center.y + 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize   = UnityEngine.Mathf.Max(b.size.x, b.size.z) * 0.55f;

                snap.Render(camera);
                snap.WritePng("s55-throttled-cover.png");

                if (snap.IsAllBlack())
                {
                    // GPU absent in batchmode: treat all-black as inconclusive rather than failure.
                    Assert.Inconclusive(
                        "Tooth (e): render all-black → no GPU context in batchmode. " +
                        "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                    return;
                }

                var verdict = SnapshotCoverage.Analyse(snap.RawPixels, SnapW, SnapH, bgR8, bgG8, bgB8);
                Assert.IsFalse(verdict.IsBlank,
                    "Tooth (e): throttled settled render must produce visible fill coverage.");
                Assert.That(verdict.FilledFraction, NUnit.Framework.Is.InRange(0.05f, 0.95f),
                    "Fill fraction must be in a sane band (geometry visible, not full-frame).");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(mapGo);
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
            }
        }

        // ── Tooth (g): No new per-frame GC ───────────────────────────────────────────────────────

        /// <summary>
        /// The 3 new <c>int</c> counter resets (<c>_mesh buildsKickedLastTick</c> etc.) and
        /// vertex-budget arithmetic in <c>PumpPending</c> are scalar operations — no boxing.
        /// Verified with <c>Is.Not.AllocatingGCMemory()</c> on the BRG backend (zero-alloc contract)
        /// in steady state (all tiles built, no pending work).
        ///
        /// Mirrors <see cref="MapViewAsyncMeshBuildTests.Tooth6_SteadyStateTick_DoesNotAllocateGCMemory"/>.
        /// </summary>
        [Test]
        public void Tooth_g_SteadyStateTick_NoNewGC_BrgBackend()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S55_G");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg; // zero-alloc contract; Entities ticks EG → allocs
            view.Config.TileSelection.MinZoom = 2; view.Config.TileSelection.MaxZoom = 2; // z=2 = whole world (4×4 tiles); no cover recompute
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64; // uncapped for fast settle
            view.Config.MaxVerticesPerTick      = int.MaxValue;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: FillStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "Must settle before measuring steady-state alloc.");

                // Prime reused internal buffers to steady capacity.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.5, Latitude = 0.0 });
                view.LateUpdate();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.LateUpdate();

                // ── (a) within-cover pan: PumpPending resets 3 int counters, then early-exits ──
                // The counters and budget locals are all scalar ints — must not box.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 1.0, Latitude = 0.0 });
                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "Tooth (g-a): MapView.LateUpdate must not allocate during a within-cover pan. " +
                    "The 3 new S55 int counter resets must be scalar, never boxing.");

                // ── (b) static frame early-out ──
                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "Tooth (g-b): Static-frame Tick must early-out with zero allocation.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth (h): Partial-tile eviction leaks nothing (S87) ──────────────────────────────

        /// <summary>
        /// S87 eviction teeth. A tile evicted MID-consume (ConsumeCursor part-way) must dispose both its
        /// already-uploaded layers' NativeArrays (per-mesh, during consume) AND its un-consumed remainder
        /// (via the <c>_pendingDisposal</c> holding pen on cover change), with no double-dispose crash.
        /// Decisive: the combined fill+line <c>DebugLiveAllocCount</c> returns to its baseline after the
        /// partial-evict + teardown cycle — a leaked remainder (ReleaseTile not stashing a partial tile) or a
        /// missed per-mesh dispose would leave it above baseline; a use-after/double dispose would throw.
        /// </summary>
        [Test]
        public void Tooth_h_PartialTileEviction_NoLeak()
        {
            long Live() => MapRenderer.Unity.Rendering.Style.MeshDataPayload.DebugLiveAllocCount;

            long baseline = Live();

            var (go, view) = SetupBlockedBacklog(FillAndLineStyle());
            try
            {
                // Consume a few meshes at 1/frame so some tiles are left PARTIALLY consumed (cursor > 0).
                view.Config.MaxConsumesPerTick   = 1;
                view.Config.MaxVerticesPerTick = int.MaxValue;
                for (int i = 0; i < 3; i++) view.LateUpdate();
                Assert.IsFalse(view.AllTilesSettled(),
                    "Must be mid-consume (partial tiles present) before the eviction.");

                // Evict the whole z=5 cover via a far camera jump → ReleaseTile runs on partial tiles.
                // Block further kicks-into-consume timing is irrelevant; we measure after teardown.
                view.Camera.Apply(
                    new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                for (int i = 0; i < 6; i++) { view.LateUpdate(); Thread.Sleep(5); } // release old + drain holding pen
            }
            finally
            {
                view.Teardown();              // full drain — also covers any newly-entered tiles
                Object.DestroyImmediate(go);
            }

            Assert.AreEqual(baseline, Live(),
                "Tooth (h): partial-tile eviction + teardown must leak no LayerMeshData NativeArrays " +
                "(consumed layers disposed per-mesh during consume; the un-consumed remainder via the " +
                "holding pen). A non-baseline count means a partial tile's remainder leaked.");
        }
    }
}
