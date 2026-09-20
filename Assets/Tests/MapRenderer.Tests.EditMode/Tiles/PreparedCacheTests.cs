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
// Uses interp-fill-style.json (a zoom-`interpolate` fill-color) as its style fixture. Post-Stage-1, a
// zoom-`interpolate` fill-color bakes vColor white and rides `_BaseColor` — same as a constant style —
// so the fixture no longer distinguishes the two; none of these teeth read vertex color, so nothing here
// is lost.

using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Tile;
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

        // ── UMR-95 stage 1: the prepared cache survives a restyle ──────────────────────────────
        // These drive the REAL production entry point, MapView.SetStyle — LoadTestStyle never sets
        // CurrentStyle, so the purge it fires is unconditionally unreachable from that helper.

        private static StyleDocument TwoLayerStyleA() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                  ""source-layer"": ""geolines"",
                  ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }
            ]
        }");

        /// <summary>A style entirely unrelated to A — the "B" arm of an A→B→A drive.</summary>
        private static StyleDocument SingleLayerStyleB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""other"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""other-fill"", ""type"": ""fill"", ""source"": ""other"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 5, 5, 5, 1] } }
            ]
        }");

        /// <summary>A single FILL layer at id "shape-layer" — the closed-hole drive's first arm.</summary>
        private static StyleDocument SingleFillLayerStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""shape-layer"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>The SAME id "shape-layer", now a LINE layer — same ordered layer id, a different TYPE at
        /// the same index. The closed-hole drive's returning arm.</summary>
        private static StyleDocument SingleLineLayerStyle_SameId() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""shape-layer"", ""type"": ""line"", ""source"": ""maplibre"",
                  ""source-layer"": ""geolines"",
                  ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }
            ]
        }");

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/> (mirrors
        /// <c>GeoJsonSourceTests.SpinToCompleted</c>).</summary>
        private static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        /// <summary>Wires a view for the real <see cref="MapView.SetStyle"/> path (never LoadTestStyle) — a
        /// tiles[]-only vector source needs no TileJSON fetch, so SetStyle never actually awaits network.</summary>
        private static MapView NewRestyleView(byte[] bytes, out GameObject go)
        {
            go = new GameObject("MapView_UMR95_Restyle");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(bytes);
            // Seed the camera BEFORE any SetStyle: SetStyle builds the render layers at the CURRENT zoom.
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        /// <summary>T1 — a style round-trip re-shows without a rebuild. RED against the unmodified tree
        /// (DERIVED, not run — SetSources purged unconditionally): the pan-back would be a MISS.</summary>
        [Test]
        public void StyleRoundTrip_AfterPanOut_ReShowsWithoutRebuild()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                // Pan out of cover — transfers A's meshes into the cache.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must leave cover.");
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");

                SpinToCompleted(view.SetStyle(SingleLayerStyleB(), "B"));
                PumpUntilSettled(view);

                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                int hitsBefore = view.PreparedCacheHits();

                // Pan back — one LateUpdate: a hit is synchronous admission-time work.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "the returning style must re-show without a rebuild.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "a cache hit must not start a build.");
                Assert.Greater(view.PreparedCacheHits(), hitsBefore, "the revisit must register a cache hit.");
                Assert.AreEqual(2, view.GetTileMeshes(TrackedTile)?.Length ?? 0, "both layers' meshes must be restored.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>The hole a review found in the id-based design: with the cache token derived from
        /// style CONTENT (not the caller-asserted <c>styleId</c>, a file path), a same-styleId return with a
        /// layer-TYPE swap at the same index now mints a DIFFERENT token, so the probe misses instead of
        /// serving a fill mesh under a line-typed layer. RED: derive the token from <c>StyleId</c> alone.</summary>
        [Test]
        public void SameIdReturn_WithLayerTypeSwapAtSameIndex_NeverServesTheStaleBake()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(SingleFillLayerStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred the fill mesh into the cache.");

                SpinToCompleted(view.SetStyle(SingleLayerStyleB(), "B"));
                PumpUntilSettled(view);

                // Same styleId "A" again — same one layer id ("shape-layer"), now a LINE layer. The content
                // digest differs (the type changed), so this mints a fresh token, never seen before.
                SpinToCompleted(view.SetStyle(SingleLineLayerStyle_SameId(), "A"));

                int hitsBeforeRevisit   = view.PreparedCacheHits();
                int missesBeforeRevisit = view.PreparedCacheMisses();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a layer-type swap at the same index under a repeated styleId must never register a hit " +
                    "— the fill mesh baked under the OLD content must not be handed to the new line layer.");
                // Positive companion: the negative assertion above passes just as well if the pan-back admits
                // NOTHING at all. Pin that it genuinely re-admits under a MISS (a real rebuild), not silence.
                Assert.Greater(view.PreparedCacheMisses(), missesBeforeRevisit,
                    "the revisit must register a miss — the line layer must actually rebuild, not merely fail to hit.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>A fill-extrusion layer BEFORE a fill layer, same source/source-layer. With
        /// <c>MapMaterialSet.FillExtrusionMaterial</c> null, <c>RenderLayerSet.Build</c> skips the extrusion
        /// layer, so the fill layer bakes at dense index 0 — see the field-mutation teeth below.</summary>
        private static StyleDocument ExtrusionThenFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""extrusion-layer"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-extrusion-height"": 50 } },
                { ""id"": ""shape-layer"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>The fix this stage lands: a MaterialSet FIELD mutated IN PLACE (a swap of REFERENCE is
        /// out of scope — both committed sets configure every material, so the only reference swap this repo
        /// can perform moves no layer) can still shift the dense layer numbering under UNCHANGED style
        /// content and id. RED: fold only content into the token, not the built numbering.</summary>
        [Test]
        public void FillExtrusionMaterialAssignedInPlace_BetweenStyleLoads_NeverServesAStaleHit()
        {
            var litSet = MapMaterialSetTestUtil.Load();
            var matSet = ScriptableObject.CreateInstance<MapMaterialSet>();
            matSet.FillMaterial    = litSet.FillMaterial;
            matSet.LineMaterial    = litSet.LineMaterial;
            matSet.SymbolTextWorld = litSet.SymbolTextWorld;
            // FillExtrusionMaterial left null — the extrusion layer is skipped on the first load below.

            var go   = new GameObject("MapView_UMR95_MaterialFieldMutation");
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = matSet;
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(SampleTileFixture.Bytes());
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();

            try
            {
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    "drive precondition: the fill layer (extrusion skipped) must build on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred the fill mesh into the cache.");

                // Same reference, a field mutated IN PLACE — the pin does not fire, but the extrusion layer
                // now builds too, shifting the fill layer from dense index 0 to dense index 1.
                matSet.FillExtrusionMaterial = litSet.FillMaterial;
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A")); // SAME content, SAME id

                int hitsBeforeRevisit = view.PreparedCacheHits();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a MaterialSet field mutation that shifts the dense layer numbering, under unchanged " +
                    "style content and id, must never register a hit — a shifted fill mesh would otherwise " +
                    "be served under what is now the extrusion layer's slot.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(matSet);
            }
        }

        /// <summary>The DECREASE-direction counterpart to
        /// <see cref="FillExtrusionMaterialAssignedInPlace_BetweenStyleLoads_NeverServesAStaleHit"/>: starts
        /// with BOTH layers built (dense ids extrusion=0, fill=1), then NULLS
        /// <c>FillExtrusionMaterial</c> so the extrusion layer drops out and the fill layer's dense id shifts
        /// DOWN to 0. RED: drop <c>LayerNumbering(Layers)</c> from the digest fold (<c>MapView.cs</c>).</summary>
        [Test]
        public void FillExtrusionMaterialNulledInPlace_BetweenStyleLoads_NeverServesAStaleHit()
        {
            var litSet = MapMaterialSetTestUtil.Load();
            var matSet = ScriptableObject.CreateInstance<MapMaterialSet>();
            matSet.FillMaterial          = litSet.FillMaterial;
            matSet.LineMaterial          = litSet.LineMaterial;
            matSet.SymbolTextWorld       = litSet.SymbolTextWorld;
            matSet.FillExtrusionMaterial = litSet.FillMaterial; // assigned — both layers build on first load.

            var go   = new GameObject("MapView_UMR95_MaterialFieldMutation_Decrease");
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = matSet;
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(SampleTileFixture.Bytes());
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();

            try
            {
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    "drive precondition: both the extrusion and fill layers must build on first visit.");
                Assert.AreEqual(2, view.Layers.Count,
                    "drive precondition: extrusion+fill must both be present, at dense ids [0,1].");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.GreaterOrEqual(view.CaptureTelemetry().PreparedCacheEntryCount, 2,
                    "drive precondition: the pan-out must have transferred BOTH dense ids' meshes " +
                    "(extrusion=0, fill=1) into the cache.");

                // Same reference, a field mutated IN PLACE — the pin does not fire, but the extrusion layer
                // now drops out, shifting the fill layer from dense index 1 DOWN to dense index 0 — a PREFIX
                // of the still-cached ids.
                matSet.FillExtrusionMaterial = null;
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A")); // SAME content, SAME id
                Assert.AreEqual(1, view.Layers.Count,
                    "drive precondition: the extrusion layer must actually drop out after nulling its material.");

                int hitsBeforeRevisit   = view.PreparedCacheHits();
                int missesBeforeRevisit = view.PreparedCacheMisses();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a MaterialSet field mutation that shifts the dense layer numbering DOWN, under " +
                    "unchanged style content and id, must never register a hit — the surviving id is a " +
                    "PREFIX of the still-cached ids, so the fill layer must not be served the extrusion " +
                    "layer's stale mesh.");
                Assert.Greater(view.PreparedCacheMisses(), missesBeforeRevisit,
                    "the revisit must register a miss — the fill layer must actually rebuild, not merely " +
                    "fail to hit.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(matSet);
            }
        }

        /// <summary>UMR-95 R1: <c>MapViewConfig.FillAntialiasing</c> bakes into VERTICES
        /// (<c>StyledFillTileBuilder</c>'s boundary band), not a uniform — a toggle changes neither the
        /// style's Root bytes nor the built layer numbering, so it must be folded into the token explicitly.
        /// RED: drop the <c>|aa=</c> component from the digest fold.</summary>
        [Test]
        public void FillAntialiasingToggle_BetweenStyleLoads_NeverServesAStaleHit()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                view.Config.FillAntialiasing = true;
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");

                // Same content, same id — only the config knob differs.
                view.Config.FillAntialiasing = false;
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));

                int hitsBeforeRevisit = view.PreparedCacheHits();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a FillAntialiasing toggle, under unchanged style content and id, must never register a " +
                    "hit — the AA-banded mesh baked before the toggle must not be served after it.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Style-transitions epic, Stage 2: teeth 1 and 19 — the MapView-level teeth ────────────
        // The only teeth of the 22 that need real loaded tiles and a real backend: everything else in
        // the stage's plan is exercised at the RenderLayerSet level in Style/StyleTransitionBindingTests.cs
        // and Style/RestyleSurvivorGateTests.cs, which is cheaper and does not need this fixture's tile
        // pipeline. This is the harness the plan names for the two that genuinely do.

        private static StyleDocument TwoLayerStyleARecolored() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } },
                { ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                  ""source-layer"": ""geolines"",
                  ""paint"": { ""line-color"": [""rgba"", 10, 200, 10, 1], ""line-width"": 10 } }
            ]
        }");

        /// <summary>Tooth 1 (A#1, crit. 7): a paint-only restyle keeps every layer's material, the drawn
        /// tile meshes, and the backend instance — the in-place path never calls Layers.Build/SetSources.
        /// Anti-vacuity: the drawn set is non-empty (both layers' meshes are present before AND after).</summary>
        [Test]
        public void PaintOnlyRestyle_KeepsMaterialsMeshesAndBackend()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                int layerCount = view.Layers.Count;
                Assert.AreEqual(2, layerCount, "drive precondition: both layers must have taken a slot.");
                var materialsBefore = new Material[layerCount];
                for (int i = 0; i < layerCount; i++) materialsBefore[i] = view.Layers[i].Material;

                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must have drawn meshes.");
                Assert.AreEqual(2, meshesBefore.Length, "drive precondition: dense layers x tiles = 2 x 1.");

                // NewRestyleView never sets Config.Backend, so the default (RenderBackend.Entities) applies.
                var backendBefore = view.EntitiesRenderer();
                Assert.IsNotNull(backendBefore, "drive precondition: a backend must be active.");

                SpinToCompleted(view.SetStyle(TwoLayerStyleARecolored(), "A")); // paint-only: same id, in place

                for (int i = 0; i < layerCount; i++)
                    Assert.AreSame(materialsBefore[i], view.Layers[i].Material,
                        $"layer {i}'s material must survive a paint-only restyle.");

                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesAfter, "the tracked tile must still have drawn meshes.");
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length, "the drawn set must not shrink or grow.");
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i], $"mesh {i} must be the SAME instance.");

                Assert.AreSame(backendBefore, view.EntitiesRenderer(),
                    "the backend instance must survive a paint-only restyle.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Tooth 19: after an in-place restyle, GetTileMeshes returns the SAME objects (not merely
        /// equal-content ones) and TileManager.CurrentStyle is unchanged — moving the token would invalidate
        /// prepared-cache entries the gate has just proven are still exactly right.</summary>
        [Test]
        public void PaintOnlyRestyle_KeepsLoadedTileMeshes()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                var tokenBefore = view.TileManager.CurrentStyle;

                SpinToCompleted(view.SetStyle(TwoLayerStyleARecolored(), "A"));

                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesAfter);
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length);
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i], $"mesh {i} must be the SAME instance.");
                Assert.AreEqual(tokenBefore, view.TileManager.CurrentStyle,
                    "CurrentStyle must not move on an in-place restyle — it partitions meshes by " +
                    "mesh-affecting content, which the gate has just proven byte-identical.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // UMR-151, the fence pair. Both need a BACKGROUND layer: without one, SourceRegistry's
        // background-identity reuse has no observer, and a fresh SourcePipeline per call would still pass.

        private static StyleDocument BackgroundAndFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": [""rgba"", 20, 20, 20, 1] } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>Same layers, background RECOLORED (still transitionable — an in-place restyle).</summary>
        private static StyleDocument BackgroundRecoloredAndFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": [""rgba"", 90, 90, 90, 1] } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } }
            ]
        }");

        /// <summary>Same fill layer, background REMOVED — a partial-survival restyle whose diff drops the
        /// synthetic source-less pipeline (SourcesUnchanged's pre-diff "true" goes stale exactly here).</summary>
        private static StyleDocument FillOnlyStyle_BackgroundRemoved() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } }
            ]
        }");

        /// <summary>T4. A restyle whose SOURCES are unchanged (paint-only, background included) must keep
        /// every loaded record — both the fill layer's AND the background's own per-tile quad — by Mesh
        /// IDENTITY. Anti-vacuity: the drawn set is non-empty both before and after (A's T1 tooth 1's own
        /// guard).</summary>
        [Test]
        public void UnchangedSources_KeepEveryRecord()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(BackgroundAndFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");

                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must have drawn meshes.");
                Assert.AreEqual(2, meshesBefore.Length, "drive precondition: background + fill = 2 dense layers.");
                var backendBefore = view.EntitiesRenderer();

                SpinToCompleted(view.SetStyle(BackgroundRecoloredAndFillStyle(), "A"));

                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesAfter, "the tracked tile must still have drawn meshes — not blanked.");
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length, "the drawn set must not shrink or grow.");
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i], $"mesh {i} (incl. the background's) must be the SAME instance.");
                Assert.AreSame(backendBefore, view.EntitiesRenderer(), "the backend instance must survive too.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>T5. Removing the BACKGROUND layer (sources otherwise untouched) departs the synthetic
        /// source-less pipeline — its record must be torn down, while the surviving fill layer's own record
        /// keeps its Mesh identity. This is the pairing point with T4: an implementation that keeps every
        /// record unconditionally (ignoring the departed-pipeline case) passes T4 but fails here.</summary>
        [Test]
        public void BackgroundRemovedRestyle_TearsDownOnlyTheBackgroundRecord()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                SpinToCompleted(view.SetStyle(BackgroundAndFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");

                // Background is declared FIRST, so it takes slot 0 and the fill slot 1 — but GetTileMeshes
                // returns CONSUME order, so find each mesh by material index, never by position.
                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                int[]  indicesBefore = view.GetTileMaterialIndices(TrackedTile);
                Assert.AreEqual(2, meshesBefore.Length, "drive precondition: background + fill = 2 dense layers.");
                int fillSlotIndexBefore = System.Array.IndexOf(indicesBefore, 1);
                int bgSlotIndexBefore   = System.Array.IndexOf(indicesBefore, 0);
                Assert.GreaterOrEqual(fillSlotIndexBefore, 0, "drive precondition: slot 1 (the fill layer) must have a mesh.");
                Assert.GreaterOrEqual(bgSlotIndexBefore, 0, "drive precondition: slot 0 (the background) must have a mesh.");
                Mesh fillMeshBefore = meshesBefore[fillSlotIndexBefore];
                Mesh bgMeshBefore   = meshesBefore[bgSlotIndexBefore];

                SpinToCompleted(view.SetStyle(FillOnlyStyle_BackgroundRemoved(), "A"));

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "the fill layer must still be drawn — not evicted wholesale.");
                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                int[]  indicesAfter = view.GetTileMaterialIndices(TrackedTile);
                Assert.AreEqual(1, meshesAfter.Length,
                    "GetTileMeshes must show only the surviving fill layer — NOTE this alone would also read " +
                    "'1' if the background's record were merely orphaned (its OLD pipeline slot number falls " +
                    "outside the NEW, shrunk _sources.Count either way), so it is not decisive by itself.");
                Assert.AreEqual(1, indicesAfter[0], "the surviving mesh must still be at slot 1 — the fill layer's slot never moved.");
                Assert.AreSame(fillMeshBefore, meshesAfter[0],
                    "the surviving fill layer's Mesh must be the SAME instance — its pipeline never departed.");
                Assert.IsTrue(bgMeshBefore == null, // Unity fake-null: decisive where the count above is not —
                    "the background's Mesh must be ACTUALLY DESTROYED, not merely orphaned in _loaded and " +
                    "hidden from GetTileMeshes by its old pipeline slot falling outside the new source count.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── UMR-151: T3 — the BRG re-stamp is mandatory (§3's deviation is void without it) ────────

        private static StyleDocument ThreeFillLayersAbc() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        private static StyleDocument ThreeFillLayersReorderedCab() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } },
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } }
            ]
        }");

        /// <summary>T3 — MANDATORY (§3's per-frame-cost deviation is void without it): under
        /// <c>RenderBackend.Brg</c>, a reorder restyle must move the emitted draw order too, not just the
        /// Material.renderQueue values — BRG caches its own copy (<c>DrawItem.LayerRenderQueue</c>) and only
        /// <c>SetLayerMaterials</c>'s explicit re-stamp updates it. Slots never move on a reorder (a=0, b=1,
        /// c=2 throughout); only which slot draws FIRST changes, to match the NEW declared order (c,a,b).</summary>
        [Test]
        public void ReorderRestyle_BrgEmitOrder_MatchesTheNewDeclaredOrder()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            view.Config.Backend = RenderBackend.Brg;
            try
            {
                SpinToCompleted(view.SetStyle(ThreeFillLayersAbc(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");
                Assert.AreEqual(3, view.Layers.Count, "drive precondition: all three fill layers must have taken a slot.");
                int itemsBefore = view.TileManager.BrgRenderer.DrawItemCount();
                Assert.Greater(itemsBefore, 0, "drive precondition: the settled cover must have registered draw items.");

                SpinToCompleted(view.SetStyle(ThreeFillLayersReorderedCab(), "A"));
                view.LateUpdate(); // drives Rebuild, which re-sorts _sortedItems from the (re-stamped) queues

                var brg = view.TileManager.BrgRenderer;
                Assert.IsNotNull(brg, "drive precondition: the BRG backend must be active.");
                Assert.AreEqual(itemsBefore, brg.DrawItemCount(),
                    "a reorder must tear NOTHING down — every draw item registered before it must still be registered.");

                // Rebuild sorts by renderQueue ALONE and a layer's draw items all share its queue, so each
                // layer is one contiguous RUN at ANY cover size — read run order, not the first 3 positions.
                var runOrder = new List<int>(3);
                for (int i = 0; i < brg.DrawItemCount(); i++)
                {
                    int materialIndex = brg.MaterialIndexAtSorted(i);
                    if (runOrder.Count == 0 || runOrder[runOrder.Count - 1] != materialIndex)
                        runOrder.Add(materialIndex);
                }
                CollectionAssert.AreEqual(new[] { 2, 0, 1 }, runOrder,
                    "the emitted runs must be (c, a, b) — the NEW declared order — one contiguous run per layer.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Same three fill layers, "b" REMOVED — a partial-survival restyle that tombstones slot 1
        /// while leaving slots 0 and 2 alive.</summary>
        private static StyleDocument ThreeFillLayersAcRemovedB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        /// <summary>UMR-151: a mesh build KICKED before a removal restyle must not register its payload at
        /// the slot the restyle retired. Tombstoning preserves slot WIDTH, so the consume guard's count check
        /// cannot see the retirement. Drives the build to "complete but unconsumed" (MaxConsumesPerTick=0 +
        /// AwaitInFlightMeshBuilds, never PumpUntilSettled), restyles, and only then consumes.</summary>
        /// <remarks>Runs on the DEFAULT (Entities) backend on purpose. BRG dereferences the retired slot's
        /// null material and throws inside <c>AddTileLayer</c>, which would abort this test BEFORE its own
        /// assertion — a crash detector, not a detector of the property named. Entities registers silently
        /// from a <c>default</c> material id (its <c>_layerNames</c> is not refreshed by
        /// <c>SetLayerMaterials</c>, so the <c>mat.name</c> fallback that would NRE is unreachable), so the
        /// bad state is observable and the assertion below is what fires.</remarks>
        [Test]
        public void MidFlightBuild_ConsumedAfterARemovalRestyle_NeverRegistersTheRetiredSlot()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            view.Config.MaxConsumesPerTick = 0; // hold every built payload UNCONSUMED across the restyle
            try
            {
                SpinToCompleted(view.SetStyle(ThreeFillLayersAbc(), "A"));
                for (int f = 0; f < 3000; f++)
                {
                    view.LateUpdate();
                    view.AwaitInFlightMeshBuilds(); // neither kicks nor consumes — the backlog survives it
                    if (view.LoadedTileCount() > 0 &&
                        view.CaptureTelemetry().ConsumeBacklog >= view.LoadedTileCount()) break;
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, view.LoadedTileCount(),
                    "drive precondition: every cover tile must be BUILT but UNCONSUMED — that mid-flight " +
                    "state is the whole tooth; a settled cover cannot express it.");
                Assert.AreEqual(3, view.Layers.Count, "drive precondition: all three fill layers must have taken a slot.");

                SpinToCompleted(view.SetStyle(ThreeFillLayersAcRemovedB(), "A"));
                Assert.AreEqual(3, view.Layers.Count, "the slot WIDTH must not shrink — that is why a count check cannot catch this.");
                Assert.IsNull(view.Layers[1].StyleLayer, "drive precondition: slot 1 must hold a tombstone (in-place retirement, not a rebuild).");

                view.Config.MaxConsumesPerTick = 64;
                PumpUntilSettled(view);

                int[] indices = view.GetTileMaterialIndices(TrackedTile);
                Assert.IsNotNull(indices, "the tracked tile must still register its SURVIVING layers — non-vacuity.");
                CollectionAssert.Contains(indices, 0, "slot 0 (\"a\") survived the restyle and must still register.");
                CollectionAssert.Contains(indices, 2, "slot 2 (\"c\") survived the restyle and must still register.");
                Assert.IsNotNull(view.EntitiesRenderer(), "drive precondition: the Entities backend must be active.");
                CollectionAssert.DoesNotContain(indices, 1,
                    "a payload built for the RETIRED slot 1 must be freed without registering — registering it " +
                    "points a live draw item at an unregistered material id (and NREs outright on BRG).");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
