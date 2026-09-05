// S82 acceptance — PreparedTileCache MapView-integration teeth (real cover→fetch→build→consume→evict
// cycles through TileManager). Unity-only (Mesh, MonoBehaviour) — not included in the fast dotnet
// core-tests project.
//
// EditMode half: the mesh-DESTRUCTION-timing / absolute-mesh-count / backend-registration teeth. These
// depend on EditMode semantics that PlayMode breaks — Object.Destroy is immediate here (deferred to
// end-of-frame in PlayMode, so "destroyed immediately" and process-wide FindObjectsOfTypeAll<Mesh> counts
// would not hold), and the BRG/GameObject backends do not settle a first-visit tile under the manual-drive
// PlayMode loop. Settle is deterministic here via DrainMeshBuilds (no Thread.Sleep). The async-settle +
// cache-hit-counting teeth live in the PlayMode half (MapRenderer.Tests.PlayMode.Tiles.PreparedCacheTests).
//
// Vacuity trap avoided: interp-fill-style.json (a zoom-`interpolate` fill-color), NOT a constant-color style.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class PreparedCacheTests
    {
        // z=4 tile containing (lon=10, lat=10) — verified to sit comfortably inside a tile, away from any
        // tile-boundary floating-point edge case.
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        private static StyleDocument InterpFillStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return StyleParser.Parse(File.ReadAllText(path));
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Deterministically settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick consumes
        /// them. No frame yielding — EditMode needs immediate Object.Destroy semantics for the lifetime teeth.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2000)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        private static int CountMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Length;

        // ── Cached meshes: live while held, destroyed exactly once ────────────────────────────

        [Test]
        public void CachedMeshes_LiveWhileHeld_DestroyedOnce()
        {
            byte[] bytes = SampleTileFixture.Bytes();
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
                // release. (Bounded/forced-LRU eviction destroying a mesh is covered deterministically at the
                // cache-unit level — PreparedTileCacheTests.Bounded_EvictsLru_FreesMesh.)
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
        // Stays in EditMode: the BRG/GameObject backends do not settle a first-visit tile under the
        // manual-drive PlayMode loop (verified — "TrackedTile must be built on first visit" fails alone in
        // PlayMode for Brg), and this asserts on mesh aliveness. Kept as a parametrized [TestCase].

        [TestCase(RenderBackend.Entities)]
        [TestCase(RenderBackend.Brg)]
        [TestCase(RenderBackend.GameObject)]
        public void BackendReuse_OnHit_SnapshotStaysPopulated(RenderBackend backend)
        {
            byte[] bytes = SampleTileFixture.Bytes();
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

                // Captured BEFORE eviction so the revisit can be proven to be the SAME instance, not a
                // structurally-similar re-build — the counter-based checks below read 0 for a hit AND a miss
                // whose kick is deferred a tick (see PreparedCacheHits assertion), so identity is the only
                // discriminator that can't be satisfied by a disguised full re-fetch/re-decode/re-mesh.
                Mesh[] originalMeshes = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(originalMeshes);
                Assert.GreaterOrEqual(originalMeshes.Length, 1);
                Mesh originalMesh = originalMeshes[0];
                int hitsBefore = view.PreparedCacheHits();

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), $"[{backend}] TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                int kicks = view.TileBuildsStartedLastTick();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    $"[{backend}] TrackedTile must be re-built (from cache) on revisit — falsifier: a backend " +
                    "that assumed it owned/destroyed the mesh on RemoveItem would draw nothing here.");
                Assert.AreEqual(0, kicks, $"[{backend}] the revisit must be a cache hit (no re-mesh build).");

                Mesh[] meshes = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshes);
                Assert.GreaterOrEqual(meshes.Length, 1);
                Assert.IsTrue(meshes[0] != null, $"[{backend}] the re-added mesh must be alive.");

                // DECISIVE: the counter above (kicks == 0) reads 0 for a genuine hit AND for a miss whose
                // kick is structurally deferred to the NEXT tick — it cannot alone distinguish "reused the
                // prepared mesh" from "quietly re-fetched/re-decoded/re-meshed and only the kick counter
                // missed it". Mesh IDENTITY across the revisit, plus the cache's own hit counter, can.
                Assert.AreSame(originalMesh, meshes[0],
                    $"[{backend}] the revisit mesh must be the SAME instance as before eviction — a genuine " +
                    "prepared-cache hit reuses the held Mesh, it never rebuilds an equivalent one.");
                Assert.Greater(view.PreparedCacheHits(), hitsBefore,
                    $"[{backend}] the revisit must register on the cache's own hit counter.");
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
        /// and/or leave the evicted mesh alive (as the ENABLED teeth prove it does when true).
        /// </summary>
        [Test]
        public void CacheDisabled_Revisit_AlwaysReprepares_NoTransfer()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_CacheDisabled");
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            // Set BEFORE WithTestCamera() — that call constructs MapView/TileManager, which reads
            // PreparedCache.Enabled once at construction.
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
                // pre-S82 DestroyTrackedMeshes — NOT keep it alive in the cache. (Immediate Object.Destroy is
                // why this tooth is EditMode-only.)
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

        // ── (g)'s revisit clause: a two-mesh tile, cache hit, no re-kick ────────────────────────

        /// <summary>
        /// job-scheduling-design.md §8 stage 3 tooth (f)/(g): a two-mesh (fill + line) tile — both produced
        /// by the graph arm (§8 stage 5 Group B retired the seam arm) — evicted to the cache and revisited must be a PURE cache
        /// hit: <see cref="TileManager.BuildTileFromCache"/> is synchronous admission-time work (no fetch,
        /// no kick, no async pipeline), so <c>TileBuildsStartedLastTick()</c> must read 0 on the tick the
        /// tile becomes built again — the falsifier that catches a shallow cache which silently RE-BUILT
        /// instead of transferring (which the mesh-count/hit-count assertions alone would not catch, since
        /// a re-build produces the same counts).
        /// </summary>
        [Test]
        public void TwoMeshTile_RevisitAfterEviction_IsACacheHit_WithNoReKick()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var src      = TestDataSource.FromBytes(bytes);
            var style    = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
                ""layers"": [
                    { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                      ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                    { ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                      ""source-layer"": ""geolines"",
                      ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }
                ]
            }");
            var go   = new GameObject("MapView_S89_TwoMeshRevisit");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");
                Assert.AreEqual(2, view.GetTileMeshes(TrackedTile)?.Length ?? 0,
                    "drive precondition: both layers must produce a real mesh — the revisit clause needs a " +
                    "genuinely two-mesh tile, not one with an empty line layer.");

                // Evict — transfers both meshes to the cache.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "the tile must leave the cover.");
                PumpUntilSettled(view);

                int hitsBefore = view.PreparedCacheHits();

                // Revisit — one deterministic tick: BuildTileFromCache runs synchronously on admission.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    "a cache hit must build the tile SYNCHRONOUSLY on admission — no fetch, no kick, no " +
                    "async pipeline (BuildTileFromCache's own contract).");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(),
                    "no re-kick on a hit — the falsifier: a shallow cache that silently RE-BUILT instead of " +
                    "transferring would start a build on this exact tick.");
                // Greater-than, not exactly +1: at this zoom the whole cover (not just TrackedTile) re-enters
                // on one pan, so every previously-evicted tile in it registers its own hit on the same tick.
                // TrackedTile's OWN hit is what the assertions above/below (built synchronously, 2 meshes)
                // already pin; this just confirms the cache's hit counter moved at all.
                Assert.Greater(view.PreparedCacheHits(), hitsBefore,
                    "the revisit must register at least one cache hit — including TrackedTile's own.");
                Assert.AreEqual(2, view.GetTileMeshes(TrackedTile)?.Length ?? 0,
                    "the cache hit must restore BOTH meshes — a shallow cache that only remembered one " +
                    "layer would show here.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
