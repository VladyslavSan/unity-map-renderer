// Tiles/TileCacheTests.cs — prepared-cache and deferred-release-budget teeth, PlayMode half.
//
// Contents:
//   PreparedCacheTests        — PreparedTileCache MapView-integration teeth: real cover-fetch-build-consume-evict cycles, PlayMode half.
//   Stall2ReleaseBudgetTests  — the deferred-release queue budgets how many (tile, source) records free per Update.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Style;
using MapRenderer.Unity.Jobs.Geometry;
using Fill = MapRenderer.Unity.Style.Fill;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Cameras;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Layers;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.Jobs.Mvt;
using static MapRenderer.Tests.MapViewPump;


namespace MapRenderer.Tests.PlayMode.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // PreparedCacheTests — PreparedTileCache MapView-integration teeth, PlayMode half
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class PreparedCacheTests : BaseTestFixture
    {
        // z=4 tile containing (lon=10, lat=10) — verified to sit comfortably inside a tile, away from any
        // tile-boundary floating-point edge case.
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        private static StyleDocument InterpFillStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            FileAssert.Exists(path);
            return TestStyle.Document(File.ReadAllText(path));
        }

        /// <summary>Composite (zoom + feature) fill-color — unlike <see cref="InterpFillStyle"/>'s pure
        /// Zoom-kind expression, this bakes a per-feature COLOR stream, so
        /// <see cref="ZoomBake_AtIdZ_NotStaleCamZoom"/>'s "bake happens at id.Z, not the
        /// fractional camera zoom" claim stays observable on the mesh.</summary>
        private static StyleDocument InterpFillCompositeStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-composite-style.json");
            FileAssert.Exists(path);
            return TestStyle.Document(File.ReadAllText(path));
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>
        /// Independent ground truth: the uniform baked vertex color a FRESH <see cref="SyncMeshWrite.Fill"/>
        /// call produces at <paramref name="zoom"/>, built directly from Core (never TileManager/MapView).
        /// </summary>
        private static Color GroundTruthColorAtZoom(byte[] mvtBytes, StyleDocument style, TileId id, double zoom)
        {
            using var mvtTile = MvtDecoder.Decode(id, mvtBytes);
            var fillLayer = style.Layers[0];
            var paint     = ((Fill.StyleLayer)fillLayer).Paint;
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "Fixture must contain a resolvable MVT layer.");
            var features  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, zoom);
            Assert.Greater(features.Count, 0, "Fixture must produce >=1 feature (non-vacuous ground truth).");

            var (bMin, _) = id.MercatorBounds();
            var tileOrigin = new double3(bMin.x, 0.0, bMin.y);

            var mda = MeshDataPayload.AllocateTracked(1);
            TileGeometryBuffers geometry = mvtLayer.Geometry; // BORROWED — the tile owns it
            int vc; Bounds b;
            SyncMeshWrite.Fill(mda[0], features, geometry, paint, zoom, tileOrigin, out vc, out b);
            Assert.Greater(vc, 0, "Ground-truth build must produce geometry.");

            var payload = new MeshDataPayload(mda, vc, b, "ground-truth", materialIndex: 0);
            using var bag = new ObjectDisposalBag();
            Mesh mesh = bag.Track(payload.Upload());
            Color c0 = FirstVertexColor(mesh);
            return c0;
        }

        private static Color FirstVertexColor(Mesh mesh)
        {
            var colors = new List<Color>();
            mesh.GetColors(colors);
            Assert.Greater(colors.Count, 0, "Mesh must have vertex colors.");
            return colors[0];
        }

        private static bool ColorsClose(Color a, Color b, float eps)
            => Mathf.Abs(a.r - b.r) < eps && Mathf.Abs(a.g - b.g) < eps && Mathf.Abs(a.b - b.b) < eps;

        private static void AssertColorsClose(Color expected, Color actual, float eps, string message)
        {
            Assert.AreEqual(expected.r, actual.r, eps, $"{message} (R: expected={expected.r:F4} actual={actual.r:F4})");
            Assert.AreEqual(expected.g, actual.g, eps, $"{message} (G: expected={expected.g:F4} actual={actual.g:F4})");
            Assert.AreEqual(expected.b, actual.b, eps, $"{message} (B: expected={expected.b:F4} actual={actual.b:F4})");
        }

        // ── Preparing the next level ahead: loaded hidden, kept through jitter, shown in one step ───────────

        /// <summary>The level-5 tiles the view holds hidden: prepared, and not reported as shown.</summary>
        private static HashSet<TileId> PreparedTiles(MapView view)
        {
            var keys = new List<LoadedTileKey>();
            view.TileManager.CollectLoadedTileKeys(keys);
            var prepared = new HashSet<TileId>();
            foreach (LoadedTileKey key in keys)
                if (!key.Shown && key.Tile.Z == 5) prepared.Add(key.Tile);
            return prepared;
        }

        /// <summary>Asserts the prepared tiles are exactly the tiles a level-5 selection sees now: none
        /// missing, none outside the frustum.</summary>
        private static void AssertPreparedIsTheLevelFiveCover(MapView view, string when)
        {
            var selector = new FrustumTileSelector(5, 5, view.Config.TileSelection.OnScreenTilePx, new FlatLodStrategy(),
                                                   view.Camera.FarPlanePolicy);
            var context = new ViewContext
            {
                Camera     = view.Camera.CurrentProperties,
                ViewportPx = view.Camera.ViewportLogicalPx,
                Projection = view.Camera.Projection,
            };
            var cover = new List<TileId>();
            selector.SelectCover(in context, cover);
            CollectionAssert.AreEquivalent(cover, PreparedTiles(view),
                $"{when}: the prepared tiles are exactly the level-5 cover, with none missing and none outside the frustum.");
        }

        private static void SetZoom(MapView view, double zoom)
            => view.Camera.Apply(new CameraPropertiesUpdate { Zoom = zoom });

        /// <summary>The backend items a shown tile draws: every registered item less the hidden ones.</summary>
        private static int VisibleItems(MapView view) => view.BrgRenderer().DrawItemCount() - view.BrgRenderer().HiddenDrawItemCount();

        /// <summary>The meshes the tiles in the cover hold, which a fully shown cover draws item for item.</summary>
        private static int CoverMeshes(MapView view)
        {
            var ids = new List<TileId>();
            view.CollectLoadedTileIds(ids);
            int meshes = 0;
            foreach (TileId id in ids) meshes += view.GetTileMeshes(id)?.Length ?? 0;
            return meshes;
        }

        /// <summary>
        /// The level after the drawn one prepares before the zoom reaches it (lead 0.3, level 4 to 5): its tiles load
        /// HIDDEN, are not reported as loaded and begin their label build like any other tile, and a build in flight is never cancelled
        /// for leaving the preload set. A finished one stays through jitter across the edge, and a switch shows it in
        /// one batch with no fetch, in flight or rebaking too.
        /// </summary>
        [UnityTest]
        public IEnumerator PreparedLevel_LoadsHidden_SurvivesJitter_AndShowsInOneBatchAtTheSwitch()
        {
            var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go  = Track(new GameObject("MapView_Preload"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 4;
            view.Config.TileSelection.MaxZoom = 6;
            view.Config.TileSelection.ZoomLevelPreload = 0.3;
            view.Config.TileSelection.MaxConcurrentPrepareLoads = 64;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            var spy = new TileSymbolKickTests.SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            using var gate = new System.Threading.ManualResetEventSlim(false);
            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.5), style: TileSymbolKickTests.FillAndSymbolStyle(), symbolsIntentionallyUnwired: true);
                yield return PumpUntilSettledAcrossFrames(view);
                var drawn = new List<TileId>();
                view.CollectLoadedTileIds(drawn);
                Assert.IsTrue(drawn.TrueForAll(t => t.Z == 4), "precondition: level 4 is drawn.");
                Assert.AreEqual(0, view.CaptureTelemetry().PreparingTileCount, "nothing is prepared outside the lead.");

                // ── Inside the lead (4.72 is past 4.7): level 5 prepares, with the builds parked so they stay in flight.
                int midFlightBefore = view.ReleasedMidFlightCount();
                int midFetchBefore  = view.ReleasedMidFetchCount();
                view.TileManager.MeshBuildGateForTest = gate;
                SetZoom(view, 4.72);
                view.LateUpdate();
                int prepared = view.CaptureTelemetry().PreparingTileCount;
                view.CollectLoadedTileIds(drawn);
                AssertPreparedIsTheLevelFiveCover(view, "inside the lead");
                yield return PumpUntilAcrossFrames(view, () => spy.BeginBuildCalls.FindAll(c => c.Tile.Z == 5).Count == prepared,
                    "every prepared tile to begin its label build");
                Assert.AreEqual(prepared, spy.BeginBuildCalls.FindAll(c => c.Tile.Z == 5).Count, "every prepared tile begins its label build.");
                Assert.IsFalse(drawn.Exists(t => t.Z == 5), "a prepared tile is not reported as loaded.");
                var reported = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(reported); // what the symbol subsystem reconciles against
                Assert.IsTrue(reported.Exists(k => k.Tile.Z == 5), "a prepared tile is reported to the label subsystem, so its labels stay pinned.");
                Assert.IsFalse(reported.Exists(k => k.Tile.Z == 5 && k.Shown), "but not as shown, so none draws.");

                // ── A build in flight is never cancelled for leaving the preload set, nor for leaving the keep set.
                foreach (double zoom in new[] { 4.68, 4.5 })
                {
                    SetZoom(view, zoom);
                    for (int i = 0; i < 3; i++) { view.LateUpdate(); yield return null; }
                    Assert.AreEqual(midFlightBefore, view.ReleasedMidFlightCount(), $"zoom {zoom}: no prepared build cancelled mid-flight");
                    Assert.AreEqual(midFetchBefore, view.ReleasedMidFetchCount(), $"zoom {zoom}: no prepared fetch cancelled");
                    Assert.AreEqual(prepared, view.CaptureTelemetry().PreparingTileCount, $"zoom {zoom}: the prepared records stay");
                }

                // ── With the camera still, finishing releases what has no role left: nothing outlives its sets.
                gate.Set();
                int releasedAfterFinish = 0;
                yield return PumpUntilAcrossFrames(view, () =>
                {
                    releasedAfterFinish += view.TilesReleasedLastTick();
                    return view.CaptureTelemetry().PreparingTileCount == 0;
                }, "the finished prepared records to be released");
                Assert.AreEqual(0, view.CaptureTelemetry().PreparingTileCount, "a prepared record with no role left is released once it finishes.");
                Assert.Greater(releasedAfterFinish, 0, "the release happened, not a silent drop.");

                // ── Somewhere new, with the builds parked again: the level prepares in flight, then the switch shows it as it consumes.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 40.0, Latitude = 10.0 });
                yield return PumpUntilSettledAcrossFrames(view);
                gate.Reset();
                SetZoom(view, 4.72);
                view.LateUpdate();
                view.CollectLoadedTileIds(drawn);
                AssertPreparedIsTheLevelFiveCover(view, "somewhere new, in flight");
                HashSet<TileId> preparedBeforeSwitch = PreparedTiles(view);
                SetZoom(view, 5.06);
                view.LateUpdate();
                gate.Set();
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.Greater(view.LoadedTileCount(), 0, "the switch draws level 5.");
                view.CollectLoadedTileIds(drawn);
                Assert.IsTrue(drawn.TrueForAll(preparedBeforeSwitch.Contains), "every tile the switch draws was prepared before it.");
                Assert.AreEqual(CoverMeshes(view), VisibleItems(view), "every mesh of a drawn tile shows, none of a prepared one does.");

                // ── Jitter across the preload edge of the parents (lead 0.3 above level 5): nothing releases or rebuilds.
                // The cover is settled at the first jitter zoom, so only the prepared records can change.
                SetZoom(view, 5.299);
                yield return PumpUntilAcrossFrames(view, () => view.AllTilesSettled() && view.ReleaseQueueDepth() == 0,
                    "the cover to settle and the release queue to drain");
                Assert.Greater(view.CaptureTelemetry().PreparingTileCount, 0, "precondition: prepared parents exist to jitter.");
                int hits = view.PreparedCacheHits();
                foreach (double zoom in new[] { 5.301, 5.299, 5.301, 5.299 })
                {
                    SetZoom(view, zoom);
                    view.LateUpdate();
                    yield return null;
                    Assert.AreEqual(0, view.TilesReleasedLastTick(), $"zoom {zoom}: a finished prepared record stays through jitter");
                    Assert.AreEqual(0, view.MeshesConsumedLastTick(), $"zoom {zoom}: nothing is re-registered");
                    Assert.AreEqual(0, view.TileBuildsStartedLastTick(), $"zoom {zoom}: nothing starts building");
                }

                Assert.AreEqual(hits, view.PreparedCacheHits(), "jitter takes nothing from the cache.");
                yield return PumpUntilSettledAcrossFrames(view);

                // ── The switch back down: the prepared parents show in one batch, with no fetch and no miss.
                int misses = view.PreparedCacheMisses();
                SetZoom(view, 4.94);
                view.LateUpdate();
                Assert.Greater(view.VisibilityBatchesLastTick(), 0, "the switch shows the prepared tiles.");
                Assert.LessOrEqual(view.VisibilityBatchesLastTick(), 2, "one show batch and one hide batch at most.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "the switch starts no build.");
                Assert.AreEqual(0, view.InFlightCount(), "the switch fetches nothing.");
                Assert.AreEqual(misses, view.PreparedCacheMisses(), "the switch misses no cache.");
                Assert.AreEqual(CoverMeshes(view), VisibleItems(view), "the shown items are the drawn tiles' meshes.");

                // ── Past the keep set the finished records go to the cache.
                SetZoom(view, 4.5);
                yield return PumpUntilAcrossFrames(view, () => view.CaptureTelemetry().PreparingTileCount == 0,
                    "no record to stay prepared outside the keep set");
                Assert.AreEqual(0, view.CaptureTelemetry().PreparingTileCount, "outside the keep set no record stays prepared.");
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0, "a released prepared tile goes to the cache.");

                // ── A flip while rebaking shows the previous geometry: no hole mid-rebake.
                SetZoom(view, 4.72);
                yield return PumpUntilSettledAcrossFrames(view);
                view.CollectLoadedTileIds(drawn);
                var childMeshes = new Dictionary<TileId, int>();
                foreach (TileId t in drawn)
                    for (int c = 0; c < 4; c++)
                    {
                        var child = new TileId { Z = 5, X = t.X * 2 + (c & 1), Y = t.Y * 2 + (c >> 1) };
                        childMeshes[child] = view.GetTileMeshes(child)?.Length ?? 0;
                    }

                gate.Reset();
                view.Config.FillTileBufferClip = 64.0; // a new bake revision: drawn and prepared records rebake, parked by the gate
                view.LateUpdate();
                SetZoom(view, 5.06);
                yield return PumpUntilAcrossFrames(view, () => view.ReleaseQueueDepth() == 0, "the release queue to drain"); // a tile left with no role stays drawn until released
                view.CollectLoadedTileIds(drawn);
                int expectedVisible = 0;
                foreach (TileId t in drawn) expectedVisible += childMeshes.TryGetValue(t, out int m) ? m : 0;
                Assert.Greater(expectedVisible, 0, "precondition: the drawn level-5 tiles held meshes.");
                Assert.AreEqual(expectedVisible, VisibleItems(view), "the flip shows the previous geometry of the rebaking tiles.");
                gate.Set();
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.AreEqual(CoverMeshes(view), VisibleItems(view), "after the rebake only the new meshes of the drawn tiles show.");
            }
            finally
            {
                gate.Set();
                view.TileManager.MeshBuildGateForTest = null;
            }
        }

        // ── EG mesh registrations balance across the prepared-cache round-trip ───────────────────────
        [UnityTest]
        public IEnumerator Stall3_MeshRegistrations_StableAcrossCacheRoundTrips_Entities()
        {
            var style = InterpFillStyle();
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_S82_RegBalance"));
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.Backend               = RenderBackend.Entities; // the ID-route path under test
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // uncapped — synchronous whole-cover eviction each move

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                yield return PumpUntilSettledAcrossFrames(view);
                int rNear = view.RegisteredMeshCount();
                Assert.Greater(rNear, 0, "loading the near cover must register >=1 EG mesh (Entities backend).");

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                    yield return PumpUntilSettledAcrossFrames(view);
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                    yield return PumpUntilSettledAcrossFrames(view);
                    Assert.AreEqual(rNear, view.RegisteredMeshCount(),
                        $"cycle {cycle}: EG mesh registrations must return to the post-load count. A drift means a " +
                        "RegisterMesh on the cache-revisit path is not balanced by an UnregisterMesh on eviction.");
                }
            }
            finally { view.Teardown(); }
        }

        // ── THE decisive revisit ───────────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator Revisit_ServesCached_PixelIdentical()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go = Track(new GameObject("MapView_S82_Revisit"));
            var view     = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be built on first visit.");

                Assert.Greater(view.PreparedCacheMisses(), 0,
                    "Positive control: the first visit must register >=1 PreparedTileCache MISS.");

                Mesh[] beforeEvict = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(beforeEvict);
                Assert.GreaterOrEqual(beforeEvict.Length, 1, "First prepare must produce >=1 mesh.");
                Mesh  originalMesh = beforeEvict[0];
                Color colorBefore  = FirstVertexColor(originalMesh);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                yield return PumpUntilSettledAcrossFrames(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate(); // the recompute (cover diff + probe) runs in THIS tick
                int kicksOnRevisitTick = view.TileBuildsStartedLastTick();
                int hitsOnRevisitTick  = view.PreparedCacheHits();
                yield return PumpUntilSettledAcrossFrames(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be re-built (from cache) on revisit.");

                Assert.AreEqual(0, kicksOnRevisitTick,
                    "DECISIVE: the revisit tick must issue ZERO mesh build kicks.");
                Assert.Greater(hitsOnRevisitTick, 0,
                    "DECISIVE: the revisit tick must register >=1 PreparedCacheHit.");

                Mesh[] afterRevisit = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(afterRevisit);
                Mesh revisitMesh = afterRevisit[0];

                Assert.AreSame(originalMesh, revisitMesh,
                    "A cache hit must hand back the SAME Mesh object the original prepare built.");

                Color colorAfter = FirstVertexColor(revisitMesh);
                AssertColorsClose(colorBefore, colorAfter, 1e-4f,
                    "Revisit content must be pixel-identical to the original fresh prepare.");
            }
            finally { view.Teardown(); }
        }

        // ── Zoom-bake soundness (branch A: bake at id.Z) ───────────────────────────────────────
        [UnityTest]
        public IEnumerator ZoomBake_AtIdZ_NotStaleCamZoom()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillCompositeStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go = Track(new GameObject("MapView_S82_ZoomBake"));
            var view     = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — synchronous whole-cover eviction+transfer

            const double Z1 = 4.2;
            const double Z2 = 4.8;

            try
            {
                Color truthAtIdZ = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: 4.0);
                Color truthAtZ1  = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: Z1);
                Color truthAtZ2  = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: Z2);

                Assert.IsFalse(ColorsClose(truthAtIdZ, truthAtZ1, 1e-3f),
                    "Fixture must be zoom-sensitive enough that the id.Z bake differs from a Z1 bake (non-vacuous).");
                Assert.IsFalse(ColorsClose(truthAtIdZ, truthAtZ2, 1e-3f),
                    "Fixture must be zoom-sensitive enough that the id.Z bake differs from a Z2 bake (non-vacuous).");

                view.LoadTestStyle(src, Cam(10, 10, Z1), style: style);
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));

                Color colorAtZ1Visit = FirstVertexColor(view.GetTileMeshes(TrackedTile)[0]);
                AssertColorsClose(truthAtIdZ, colorAtZ1Visit, 1e-4f,
                    "First prepare must bake at id.Z (4.0), NOT the fractional cam.Zoom (Z1=4.2) — Decision 2 option A.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                yield return PumpUntilSettledAcrossFrames(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0, Zoom = Z2 });
                view.LateUpdate();
                int kicksOnRevisit = view.TileBuildsStartedLastTick();
                yield return PumpUntilSettledAcrossFrames(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Assert.AreEqual(0, kicksOnRevisit,
                    "The revisit at a DIFFERENT fractional camera zoom (Z2=4.8) must still be a cache HIT (no re-kick).");

                Color colorAtZ2Revisit = FirstVertexColor(view.GetTileMeshes(TrackedTile)[0]);
                AssertColorsClose(truthAtIdZ, colorAtZ2Revisit, 1e-4f,
                    "Cached render at Z2 must equal a fresh prepare AT id.Z — proving the bake is sound across a " +
                    "fractional camera-zoom change, not frozen at the stale Z1 the tile happened to first load at.");
            }
            finally { view.Teardown(); }
        }

        // ── NativeArray invariant preserved (DebugLiveAllocCount is a code-maintained counter, not a
        //    Unity-object scan — safe under PlayMode's deferred Object.Destroy) ─────────────────────
        [UnityTest]
        public IEnumerator NativeArrayInvariant_AfterLoadEvictRevisit()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go = Track(new GameObject("MapView_S82_NativeArrayInvariant"));
            var view     = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "After the first (real) prepare, NativeArrays must already be back at baseline.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "The cache stores Mesh, never NativeArrays — must remain at baseline while a tile is cached.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                // Limitation: TileBuildsStartedLastTick counts admitted tiles, so it misses a write kick on a
                // DIFFERENT cover tile in this Update; it still proves the revisit is a hit, not a re-prepare.
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "Revisit must be a hit, not a re-prepare.");
                yield return PumpUntilSettledAcrossFrames(view);
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "After a cache-hit revisit, NativeArray count must remain at baseline.");
            }
            finally { view.Teardown(); }

            Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                "After full Teardown, NativeArray count must return to baseline.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // Stall2ReleaseBudgetTests — the deferred-release queue's per-Update budget
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class Stall2ReleaseBudgetTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => TestStyle.Document(@"{
            ""version"": 8,
            ""name"": ""stall2"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        private static (GameObject go, MapView view) NewView(int releaseBudget)
        {
            var go   = new GameObject("MapView_Stall2");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.Backend               = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 8;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxReleasesPerTick        = releaseBudget;
            view.Config.TileSelection.ZoomLevelPreload = -1.0; // the test counts cover departures, not prepared-ahead records
            return (go, view);
        }

        // ── Budget: no single Update releases more than MaxReleasesPerTick; the backlog drains over frames ──
        [UnityTest]
        public IEnumerator ReleaseQueue_BoundsReleasesPerTick_AndDrainsBacklog()
        {
            var src        = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(releaseBudget: 2);
            Track(go);
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 7.0), style: MinimalStyle());
                yield return PumpUntilSettledAcrossFrames(view);
                int loadedBefore = view.LoadedTileCount();
                Assert.GreaterOrEqual(loadedBefore, 3, "need > budget tiles so the release budget actually defers work.");

                // Zoom far out → the whole z6 cover leaves; the new cover is the coarse (z0) world tile. The old tiles stay
                // drawn until that tile is ready; the Update that swaps them enqueues every departure and the drain frees up to the budget.
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 0.0 });
                for (int frame = 0; frame < 3000 && view.TilesReleasedLastTick() == 0; frame++)
                {
                    view.LateUpdate();
                    yield return null;
                }

                Assert.AreEqual(2, view.TilesReleasedLastTick(),
                    "the zoom-out frees only up to MaxReleasesPerTick (2), NOT the whole departing cover at once.");
                Assert.AreEqual(loadedBefore - 2, view.ReleaseQueueDepth(),
                    "the remaining departures are deferred to later frames.");

                // Static camera: keep ticking; each frame frees at most the budget until the backlog is gone.
                int guard = 0;
                while (view.ReleaseQueueDepth() > 0 && guard++ < 500)
                {
                    view.LateUpdate();
                    Assert.LessOrEqual(view.TilesReleasedLastTick(), 2, "no frame may exceed the release budget.");
                }
                Assert.AreEqual(0, view.ReleaseQueueDepth(), "backlog fully drained.");
                Assert.Less(view.LoadedTileCount(), loadedBefore, "the departed tiles were released; only the new cover remains.");
            }
            finally { view.Teardown(); }
        }

        // ── Re-validation: a tile that leaves cover then returns before its dequeue is KEPT, not churned ──
        [UnityTest]
        public IEnumerator ReleaseQueue_PanOutPanBack_RevalidatesAndKeepsDeferredTiles()
        {
            var src        = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(releaseBudget: 1);
            Track(go);
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 7.0), style: MinimalStyle());
                yield return PumpUntilSettledAcrossFrames(view);
                int loadedBefore = view.LoadedTileCount();
                Assert.GreaterOrEqual(loadedBefore, 4, "need enough tiles that a naive pan-back would free clearly > the transient.");

                // Pan out (budget 1): the old tiles stay drawn until the coarse tile is ready. The Update that swaps them
                // enqueues the departing cover; the drain frees ONE, leaving the rest deferred in the queue.
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 0.0 });
                for (int frame = 0; frame < 3000 && view.ReleaseQueueDepth() == 0; frame++)
                {
                    view.LateUpdate();
                    yield return null;
                }

                Assert.GreaterOrEqual(view.ReleaseQueueDepth(), 3, "most departures are deferred, not released.");

                // Uncap the budget for the RETURN frame — a naive drain (no re-validation) would now free the
                // whole deferred queue. Return to the original view: the deferred tiles re-enter the cover.
                view.Config.MaxReleasesPerTick = 1000;
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 7.0 });
                view.LateUpdate();

                Assert.LessOrEqual(view.TilesReleasedLastTick(), 2,
                    "the deferred tiles came BACK into cover — re-validation must SKIP (keep) them; only a " +
                    "transient (the coarse tile that just left) may release. A naive drain would free all deferred here.");
                Assert.AreEqual(0, view.ReleaseQueueDepth(), "the queue is resolved — kept tiles skipped, transients released.");
            }
            finally { view.Teardown(); }
        }
    }
}
