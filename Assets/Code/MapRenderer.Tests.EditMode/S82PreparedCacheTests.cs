// S82 acceptance — PreparedTileCache MapView-integration teeth (real cover→fetch→build→consume→evict
// cycles through TileManager). Unity-only (Mesh, MonoBehaviour) — not included in the fast dotnet
// core-tests project.
//
// Vacuity trap avoided: every test here uses interp-fill-style.json (a zoom-`interpolate` fill-color), NOT
// a constant-color style — a constant fixture would make the id.Z-vs-cam.Zoom bake and the revisit-content
// check pass vacuously (the S82 stage's explicit warning).
//
// Teeth covered:
//   - THE decisive revisit: prepare, evict (transfers to cache), revisit the SAME tile — assert (1) zero
//     mesh build kicks + a cache hit on revisit, (2) a positive-control miss on the first visit, (3) the
//     revisit content is pixel-identical (same Mesh object, same baked vertex color) to the original prepare.
//   - Zoom-bake soundness: prepare at cam.Zoom=z1, revisit at cam.Zoom=z2 (same tile, id.Z unchanged) — the
//     cached render must equal a fresh prepare AT id.Z, not at either fractional camera zoom.
//   - NativeArray invariant preserved across load→evict→revisit→teardown.
//   - Cached meshes are live while held (positive control) and destroyed exactly once (at Teardown).
//   - Backend reuse on a hit, across all three backends (Entities/BRG/GameObject).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class S82PreparedCacheTests
    {
        // z=4 tile containing (lon=10, lat=10) — verified (via the standard slippy-map formula) to sit
        // comfortably inside a tile, away from any tile-boundary floating-point edge case (unlike lon=0/
        // lat=0, which sits exactly on a grid corner at every even-n zoom).
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        // ── Fixtures ────────────────────────────────────────────────────────────────────────────

        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static StyleDocument InterpFillStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return StyleParser.Parse(File.ReadAllText(path));
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static void PumpUntilSettled(MapView view, int maxFrames = 2000)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);
            }
        }

        private static int CountMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Length;

        /// <summary>
        /// Independent ground truth: the uniform baked vertex color a FRESH <see cref="StyledFillTileBuilder.WriteMeshData"/>
        /// call produces at <paramref name="zoom"/>, for the interp-fill style's single fill layer over the
        /// sample-tile fixture — built directly from Core, never touching TileManager/MapView. The
        /// fill-color expression is a pure function of zoom (no per-feature "get"), so every vertex shares
        /// one color; returning just the first is robust to vertex order/count (unlike a full array compare).
        /// </summary>
        private static Color GroundTruthColorAtZoom(byte[] mvtBytes, StyleDocument style, TileId id, double zoom)
        {
            var mvtTile   = MvtDecoder.Decode(mvtBytes);
            var fillLayer = style.Layers[0];
            var paint     = new Fill.PaintProperties(fillLayer);
            var features  = FeatureSelector.SelectFeatures(fillLayer, mvtTile, zoom);
            var mvtLayer  = SourceLayerResolver.ResolveMvtLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "Fixture must contain a resolvable MVT layer.");
            Assert.Greater(features.Count, 0, "Fixture must produce >=1 feature (non-vacuous ground truth).");

            var (bMin, _) = id.MercatorBounds();
            var tileOrigin = new double3(bMin.x, 0.0, bMin.y);

            var mda = MeshDataPayload.AllocateTracked(1);
            StyledFillTileBuilder.WriteMeshData(
                mda[0], features, paint, zoom, mvtLayer.Extent, id, tileOrigin, out int vc, out Bounds b);
            Assert.Greater(vc, 0, "Ground-truth build must produce geometry.");

            var payload = new MeshDataPayload(mda, vc, b, "ground-truth", materialIndex: 0);
            Mesh mesh = payload.Upload();
            Color c0 = FirstVertexColor(mesh);
            Object.DestroyImmediate(mesh);
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

        // ── Stall #3: EG mesh registrations balance across the prepared-cache round-trip ─────────────
        // The ID route (RegisterMesh per AddTileLayer, UnregisterMesh per removal) leaks if the cache path is
        // asymmetric — BuildTileFromCache→AddTileLayer is a SECOND RegisterMesh site, eviction-to-cache is the
        // matching UnregisterMesh site. A plain load→release wouldn't catch that; repeated round trips do — a
        // missing unregister makes the near-cover registration count drift up each cycle.
        [Test]
        public void Stall3_MeshRegistrations_StableAcrossCacheRoundTrips_Entities()
        {
            var style = InterpFillStyle();
            var src   = TestDataSource.FromBytes(FixtureBytes());
            var go    = new GameObject("MapView_S82_RegBalance");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
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
                PumpUntilSettled(view);
                int rNear = view.RegisteredMeshCount();
                Assert.Greater(rNear, 0, "loading the near cover must register >=1 EG mesh (Entities backend).");

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    // Evict to a far cover (transfers the near tiles to the prepared cache, unregistering them).
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                    PumpUntilSettled(view);
                    // Revisit the SAME near view — a cache HIT re-registers via BuildTileFromCache→AddTileLayer.
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                    PumpUntilSettled(view);
                    Assert.AreEqual(rNear, view.RegisteredMeshCount(),
                        $"cycle {cycle}: EG mesh registrations must return to the post-load count. A drift means a " +
                        "RegisterMesh on the cache-revisit path is not balanced by an UnregisterMesh on eviction " +
                        "(the ID route's leak trap).");
                }
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── THE decisive revisit ───────────────────────────────────────────────────────────────

        [Test]
        public void Revisit_ServesCached_PixelIdentical()
        {
            byte[] bytes = FixtureBytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_Revisit");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be built on first visit.");

                // Positive control (2): the FIRST visit is a genuine cache MISS.
                Assert.Greater(view.PreparedCacheMisses(), 0,
                    "Positive control: the first visit must register >=1 PreparedTileCache MISS.");

                Mesh[] beforeEvict = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(beforeEvict);
                Assert.GreaterOrEqual(beforeEvict.Length, 1, "First prepare must produce >=1 mesh.");
                Mesh  originalMesh = beforeEvict[0];
                Color colorBefore  = FirstVertexColor(originalMesh);

                // Evict: pan far away — the whole cover (including TrackedTile) leaves; Built tiles transfer
                // to the PreparedTileCache.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                // Revisit: pan back to the SAME (lon,lat) — the SAME tile re-enters cover.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate(); // the recompute (cover diff + probe) runs in THIS tick
                int kicksOnRevisitTick = view.MeshBuildsKickedLastTick();
                int hitsOnRevisitTick  = view.PreparedCacheHits();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be re-built (from cache) on revisit.");

                // DECISIVE (1): zero mesh build kicks + a registered hit on the revisit tick.
                Assert.AreEqual(0, kicksOnRevisitTick,
                    "DECISIVE: the revisit tick must issue ZERO mesh build kicks. A shallow (re-building) " +
                    "cache implementation would bump this.");
                Assert.Greater(hitsOnRevisitTick, 0,
                    "DECISIVE: the revisit tick must register >=1 PreparedCacheHit.");

                Mesh[] afterRevisit = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(afterRevisit);
                Mesh revisitMesh = afterRevisit[0];

                // DECISIVE (3a): Model B hands back the SAME Mesh object — the strongest possible
                // "pixel-identical" proof (not a re-built lookalike that merely happens to match).
                Assert.AreSame(originalMesh, revisitMesh,
                    "A cache hit must hand back the SAME Mesh object the original prepare built.");

                // DECISIVE (3b): content check (belt-and-suspenders — redundant with AreSame, but verifies
                // the baked color itself, using the zoom-interpolated fixture so this is non-vacuous).
                Color colorAfter = FirstVertexColor(revisitMesh);
                AssertColorsClose(colorBefore, colorAfter, 1e-4f,
                    "Revisit content must be pixel-identical to the original fresh prepare.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Zoom-bake soundness (branch A: bake at id.Z) ───────────────────────────────────────

        [Test]
        public void ZoomBake_AtIdZ_NotStaleCamZoom()
        {
            byte[] bytes = FixtureBytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_ZoomBake");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            const double Z1 = 4.2, Z2 = 4.8;

            try
            {
                // Ground-truth references, computed independently of MapView/TileManager.
                Color truthAtIdZ = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: 4.0);
                Color truthAtZ1  = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: Z1);
                Color truthAtZ2  = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: Z2);

                // Non-vacuity guard: the fixture must actually be zoom-sensitive enough that an id.Z bake is
                // DISTINGUISHABLE from a stale fractional-cam.Zoom bake, or this test would pass for the wrong
                // reason (the S82 stage's "Vacuity trap").
                Assert.IsFalse(ColorsClose(truthAtIdZ, truthAtZ1, 1e-3f),
                    "Fixture must be zoom-sensitive enough that the id.Z bake differs from a Z1 bake (non-vacuous).");
                Assert.IsFalse(ColorsClose(truthAtIdZ, truthAtZ2, 1e-3f),
                    "Fixture must be zoom-sensitive enough that the id.Z bake differs from a Z2 bake (non-vacuous).");

                // Prepare at cam.Zoom = Z1 (fractional; MinZoom==MaxZoom==4 clamps tile selection to z=4).
                view.LoadTestStyle(src, Cam(10, 10, Z1), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));

                Color colorAtZ1Visit = FirstVertexColor(view.GetTileMeshes(TrackedTile)[0]);
                AssertColorsClose(truthAtIdZ, colorAtZ1Visit, 1e-4f,
                    "First prepare must bake at id.Z (4.0), NOT the fractional cam.Zoom (Z1=4.2) — Decision 2 option A.");

                // Evict, then revisit at cam.Zoom = Z2 — same tile (same lon/lat, same clamped z=4).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0, Zoom = Z2 });
                view.LateUpdate();
                int kicksOnRevisit = view.MeshBuildsKickedLastTick();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Assert.AreEqual(0, kicksOnRevisit,
                    "The revisit at a DIFFERENT fractional camera zoom (Z2=4.8) must still be a cache HIT (no " +
                    "re-kick) — the bake is keyed to id.Z, invariant to the camera's fractional zoom.");

                Color colorAtZ2Revisit = FirstVertexColor(view.GetTileMeshes(TrackedTile)[0]);
                AssertColorsClose(truthAtIdZ, colorAtZ2Revisit, 1e-4f,
                    "Cached render at Z2 must equal a fresh prepare AT id.Z (both Z1's and Z2's fresh prepares " +
                    "bake at the SAME id.Z under Decision 2A) — proving the bake is sound across a fractional " +
                    "camera-zoom change, not frozen at the stale Z1 the tile happened to first load at.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── NativeArray invariant preserved ─────────────────────────────────────────────────────

        [Test]
        public void NativeArrayInvariant_AfterLoadEvictRevisit()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            byte[] bytes = FixtureBytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_NativeArrayInvariant");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "After the first (real) prepare, NativeArrays must already be back at baseline (disposed " +
                    "immediately after upload in ConsumeMeshBuild — unchanged by S82).");

                // Evict → cache transfer (Mesh only; no NativeArrays are ever cached).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "The cache stores Mesh, never NativeArrays — must remain at baseline while a tile is cached.");

                // Revisit → cache hit (no mesh build at all).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                Assert.AreEqual(0, view.MeshBuildsKickedLastTick(), "Revisit must be a hit, not a re-prepare.");
                PumpUntilSettled(view);
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "After a cache-hit revisit, NativeArray count must remain at baseline (falsifier: caching " +
                    "NativeArrays instead of Mesh would leave this non-zero).");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }

            Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                "After full Teardown, NativeArray count must return to baseline.");
        }

        // ── Cached meshes: live while held, destroyed exactly once ────────────────────────────

        [Test]
        public void CachedMeshes_LiveWhileHeld_DestroyedOnce()
        {
            byte[] bytes = FixtureBytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_LiveThenDestroyed");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            int meshBefore = CountMeshObjects();

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Mesh trackedMesh = view.GetTileMeshes(TrackedTile)[0];
                Assert.Greater(CountMeshObjects() - meshBefore, 0, "Real load must create Mesh objects (non-vacuous).");

                // Evict — transfers to the cache (well within the default byte budget / count cap; nothing
                // else competes for eviction here).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                // POSITIVE CONTROL: the cache holds the mesh — it must be STILL ALIVE, not destroyed by
                // release. (Bounded/forced-LRU eviction destroying a mesh is covered deterministically, with
                // full control over byte sizes, at the cache-unit level — PreparedTileCacheTests.
                // Bounded_EvictsLru_FreesMesh — rather than here, where real fixture mesh byte sizes and the
                // real cover's tile count are not test-controlled and would make an in-session forced-budget
                // eviction non-deterministic to target.)
                Assert.IsTrue(trackedMesh != null,
                    "POSITIVE CONTROL: an evicted-but-cached mesh must remain ALIVE (the cache holds it — " +
                    "proving retention, not that release already destroyed it).");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }

            // DECISIVE: Teardown (TileManager.Dispose -> _prepared.Dispose()) must destroy the still-cached
            // mesh — exactly once (the POSITIVE CONTROL above already rules out an earlier premature destroy).
            int meshAfterTeardown = CountMeshObjects();
            Assert.LessOrEqual(meshAfterTeardown, meshBefore,
                "After Teardown, mesh count must return to baseline — the PreparedTileCache's held mesh must " +
                "be destroyed exactly once, not leaked and not double-freed.");
        }

        // ── Backend reuse on a hit, across all three backends ──────────────────────────────────

        [TestCase(RenderBackend.Entities)]
        [TestCase(RenderBackend.Brg)]
        [TestCase(RenderBackend.GameObject)]
        public void BackendReuse_OnHit_SnapshotStaysPopulated(RenderBackend backend)
        {
            byte[] bytes = FixtureBytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject($"MapView_S82_BackendReuse_{backend}");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.Config.Backend = backend;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), $"[{backend}] TrackedTile must be built on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), $"[{backend}] TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                int kicks = view.MeshBuildsKickedLastTick();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    $"[{backend}] TrackedTile must be re-built (from cache) on revisit — falsifier: a backend " +
                    "that assumed it owned/destroyed the mesh on RemoveItem would draw nothing here.");
                Assert.AreEqual(0, kicks, $"[{backend}] the revisit must be a cache hit (no re-mesh build).");

                Mesh[] meshes = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshes);
                Assert.GreaterOrEqual(meshes.Length, 1);
                Assert.IsTrue(meshes[0] != null, $"[{backend}] the re-added mesh must be alive.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Enabled = false: exact pre-S82 revert (no probe, no transfer) ──────────────────────

        /// <summary>
        /// With <see cref="MapRenderer.Unity.Rendering.Map.PreparedTileCacheConfig.Enabled"/> = false, the
        /// cache must be entirely bypassed: a revisit is ALWAYS a miss/re-prepare (never a hit), and a
        /// released tile's meshes are DESTROYED immediately rather than transferred/kept alive — the exact
        /// pre-S82 behaviour. Falsifier: a probe/transfer that ignores the toggle would register a hit
        /// and/or leave the evicted mesh alive (as the ENABLED teeth above prove it does when true).
        /// </summary>
        [Test]
        public void CacheDisabled_Revisit_AlwaysReprepares_NoTransfer()
        {
            byte[] bytes = FixtureBytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_CacheDisabled");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            // Set BEFORE WithTestCamera() — that call constructs MapView/TileManager, which reads
            // PreparedCache.Enabled once at construction (mirrors how MinZoom/MaxZoom above must also
            // precede WithTestCamera() in every other test in this file).
            view.Config.PreparedCache.Enabled = false;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be built on first visit.");
                Assert.Greater(view.PreparedCacheMisses(), 0,
                    "The probe still counts a miss when disabled (it never finds anything cached).");
                Assert.AreEqual(0, view.PreparedCacheHits(), "Disabled must never register a hit.");

                Mesh originalMesh = view.GetTileMeshes(TrackedTile)[0];
                Assert.IsNotNull(originalMesh);

                // Evict: pan far away — the tile leaves the cover.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");

                // DECISIVE (no transfer): disabled must destroy the released mesh immediately, exactly like
                // pre-S82 DestroyTrackedMeshes — NOT keep it alive in the cache (contrast
                // CachedMeshes_LiveWhileHeld_DestroyedOnce's positive control when Enabled=true).
                Assert.IsTrue(originalMesh == null,
                    "DECISIVE: with the cache disabled, a released tile's mesh must be destroyed immediately " +
                    "(no transfer-to-cache) — a live mesh here would mean Enabled=false failed to gate the " +
                    "transfer site in ReleaseTile.");

                PumpUntilSettled(view);

                // Revisit: pan back to the same tile.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be rebuilt on revisit.");
                // DECISIVE (always re-prepares): a fresh Mesh object was produced (the destroyed original
                // could never be handed back), the hit counter never moved, and the miss counter advanced
                // again (a second genuine miss/prepare, not a served-from-cache hit).
                Mesh revisitMesh = view.GetTileMeshes(TrackedTile)[0];
                Assert.IsNotNull(revisitMesh);
                Assert.AreNotSame(originalMesh, revisitMesh,
                    "A disabled cache can never hand back the original mesh (it was destroyed on release) — " +
                    "the revisit must be a genuinely fresh prepare.");
                Assert.AreEqual(0, view.PreparedCacheHits(), "Disabled must never register a hit, ever.");
                Assert.Greater(view.PreparedCacheMisses(), 1,
                    "The revisit must register a SECOND miss (re-prepared again), not a hit.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
