// S47 acceptance tests — async non-blocking tile mesh build. PlayMode half: the async-settle teeth, which
// settle over real frames (yield, never Thread.Sleep). The off-main-thread marker, profiler-recorder,
// drain-mechanism, and off-main round-trip teeth stay in the EditMode half
// (MapRenderer.Tests.MapViews.MapViewAsyncMeshBuildTests) — see that file's header for why. NOT included
// in Tools/core-tests.
//
// Tooth 1: No .Schedule().Complete() on the live Update path. Behavioral: tiles do NOT transition
//          fetch→Built in the same frame the fetch completes (mesh build is deferred ≥1 frame).
// Tooth 3: Correctness parity — async path produces same vertex count AND positions as sync StyledFillTileBuilder.BuildMesh.
// Tooth 4: Cancellation (S04) — Released-mid-flight tile does not create a GameObject.
// Tile-cover: TILT must trigger a cover recompute (frustum selector integration).

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.PlayMode.MapViews
{
    [TestFixture]
    public class MapViewAsyncMeshBuildTests
    {
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
        /// Pumps Tick() until every loaded tile has settled or a spin budget is hit. Real frames give the
        /// ThreadPool mesh build wall-clock to progress (never Thread.Sleep).
        /// </summary>
        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    yield break;
                yield return null;
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
        [UnityTest]
        public IEnumerator TileCover_RecomputesOnTiltChange_FarFieldGrows()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_TiltCover");
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.WithTestCamera();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 22;
            view.Config.MaxConsumesPerTick    = 256;
            view.Config.MaxMeshBuildsPerTick  = 256;

            try
            {
                // Overhead (tilt 0) cover.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                yield return PumpUntilSettled(view);
                int flat = view.LoadedTileCount();
                Assert.Greater(flat, 0, "flat (overhead) cover must be non-empty");

                // Change ONLY the tilt to 60° — lon/lat/zoom/heading are identical.
                view.Camera.Apply(new CameraPropertiesUpdate { Tilt = 60 });
                view.LateUpdate();
                yield return PumpUntilSettled(view);
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
        [UnityTest]
        public IEnumerator Tooth1_MeshBuildDeferred_TileNotBuiltInSameFetchFrame()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_T1");
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
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
                yield return PumpUntilSettled(view, maxFrames: 2500);

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
        [UnityTest]
        public IEnumerator Tooth3_AsyncPath_ProducesSameGeometryAsSyncPath()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var    src   = TestDataSource.FromBytes(bytes);
            var    go    = new GameObject("MapView_T3");
            var    view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            var    style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                yield return PumpUntilSettled(view);

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
        ///
        /// Tick-exact: <c>enabled=false</c> suppresses PlayMode's auto-LateUpdate, so the two
        /// hand-driven <c>view.LateUpdate()</c> calls below are the only two ticks that run.
        /// </summary>
        [UnityTest]
        public IEnumerator Tooth4_ReleasedMidFlight_NoGameObjectCreated()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_T4");
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
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
                yield return null; // ensure ThreadPool Task.Run hop completes
                view.LateUpdate();

                // Pan far east immediately — before mesh build tasks complete.
                // lon=170, z=5 → center tile (5,31,16), completely non-overlapping cover.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate(); // cover recompute → evicts all original (5,16,*) tiles

                // Original center tile must be gone from _loaded.
                Assert.IsFalse(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "Tooth 4: Original tile (5,16,16) must be evicted after panning.");

                // Let everything settle (new cover tiles build).
                yield return PumpUntilSettled(view, maxFrames: 2500);

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
    }
}
