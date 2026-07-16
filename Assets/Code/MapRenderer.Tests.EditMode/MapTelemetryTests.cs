// S85 acceptance tests — render/tile telemetry (live plumbing, multi-source, load-progress, backlog,
// panel forwarding, allocation). Unity-only (MonoBehaviour + MapView + ThreadPool fetch); the
// TileCoverStats + FrustumTileSelector aspect/Flat teeth live in the engine-free TileCoverStatsTests.cs
// (shared verbatim with the fast core-tests project).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapTelemetryTests
    {
        // ── Helpers ────────────────────────────────────────────────────────────────────────────

        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""S85"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>Two distinct rendered sources — the S83b multi-source discriminator (each gets its own
        /// TileManager pipeline / <c>_loaded</c> record per cover tile).</summary>
        private static StyleDocument TwoSourceStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""S85-multi"",
            ""sources"": {
                ""src-a"": { ""type"": ""vector"", ""tiles"": [""https://example.com/a/{z}/{x}/{y}.pbf""] },
                ""src-b"": { ""type"": ""vector"", ""tiles"": [""https://example.com/b/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""a-fill"", ""type"": ""fill"", ""source"": ""src-a"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""b-fill"", ""type"": ""fill"", ""source"": ""src-b"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 50, 50, 200, 1] } }
            ]
        }");

        /// <summary>Pumps Tick() until every loaded tile has settled or a spin budget is hit (mirrors
        /// <c>MapViewAsyncMeshBuildTests.PumpUntilSettled</c>).</summary>
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

        /// <summary>A fetch that stays in-flight (spins on the ThreadPool) until <paramref name="release"/>
        /// is cancelled, then resolves absent — mirrors <c>TileFetchCancellationTests.SpinThenFault</c>, but
        /// resolves cleanly instead of faulting (this stage wants a deterministic pending window, not a
        /// cancellation race).</summary>
        private static async UniTask<TileResponse> SpinUntilReleased(CancellationTokenSource release)
        {
            await UniTask.SwitchToThreadPool();
            int spins = 0;
            while (!release.IsCancellationRequested && spins++ < 60000) // ~60s safety cap
                Thread.Sleep(1);
            return TileResponse.Absent(TileEncoding.Mvt);
        }

        // ── THE decisive test: independent recompute + must-change ───────────────────────────────

        [Test]
        public void CaptureTelemetry_VisibleTileCount_MatchesIndependentSelector_AndChangesAcrossViews()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S85_Decisive");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                // Pin Flat + the planar far policy so the independent selector can't diverge for reasons
                // unrelated to telemetry plumbing (ScreenSpaceLod / RaySphereFarPlane vs GeometryAwareFarPlane).
                view.Config.TileSelection.LodMode        = TileLodMode.Flat;
                view.Config.TileSelection.MinZoom        = 0;
                view.Config.TileSelection.MaxZoom        = 14;
                view.Config.TileSelection.OnScreenTilePx = 512;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 4.0), style: MinimalStyle());
                PumpUntilSettled(view);

                var snap = view.CaptureTelemetry();
                Assert.Greater(snap.VisibleTileCount, 0, "positive control: the cover must be non-empty");

                // Independently recompute the SAME cover from the SAME view inputs MapView itself feeds the
                // selector (BuildTileSelectionConfig/EnsureSelector) — same min/max zoom, on-screen px, Flat
                // LOD, GeometryAwareFarPlane (planar default), same camera + framing viewport.
                var independent = new FrustumTileSelector(
                    view.Config.TileSelection.MinZoom, view.Config.TileSelection.MaxZoom, view.Config.TileSelection.OnScreenTilePx,
                    new FlatLodStrategy(), new GeometryAwareFarPlane());
                var independentView = new ViewContext
                {
                    Camera     = view.Camera.CurrentProperties,
                    ViewportPx = view.Camera.ViewportPx / view.Config.DevicePixelRatio,
                    Projection = view.Camera.Projection,
                };
                var independentCover = new List<TileId>();
                independent.SelectVisibleTiles(in independentView, independentCover);

                Assert.AreEqual(independentCover.Count, snap.VisibleTileCount,
                    "VisibleTileCount must equal an independently recomputed cover over the same view inputs " +
                    "— a shallow impl returning a constant or 0 diverges here.");

                // Must CHANGE between two materially different views (a zoom change). NOTE: zoom 4 → 8 does
                // NOT change the count here — the S88 512-px on-screen-tile convention keeps the near-field
                // grid size roughly CONSTANT across zoom for a fixed square viewport (by design: altitude and
                // tile ground size scale together), so a zoom change only "changes" VisibleTileCount below the
                // saturation point. Confirmed directly (FrustumTileSelector over this viewport): z0=1, z1=4,
                // z2..z4=16 (constant thereafter) — so 4 → 1 is the smallest genuinely material zoom change.
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 1.0 });
                PumpUntilSettled(view);
                int changed = view.CaptureTelemetry().VisibleTileCount;

                Assert.Greater(changed, 0);
                Assert.AreNotEqual(snap.VisibleTileCount, changed,
                    "VisibleTileCount must change between materially different views (zoom 4 → 1).");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Multi-source discriminator ────────────────────────────────────────────────────────────

        [Test]
        public void MultiSource_VisibleTileCount_DivergesFromLoadedTileCount()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S85_MultiSource");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: TwoSourceStyle());
                PumpUntilSettled(view);

                var snap = view.CaptureTelemetry();
                Assert.Greater(snap.VisibleTileCount, 0, "positive control: the cover must be non-empty");
                Assert.AreEqual(snap.VisibleTileCount * 2, snap.LoadedTileCount,
                    "two rendered sources ⇒ two (tile, source) records per cover tile (_loaded ≈ 2×cover)");
                Assert.AreNotEqual(snap.VisibleTileCount, snap.LoadedTileCount,
                    "the discriminator: VisibleTileCount must NOT be confused with _loaded.Count under multi-source " +
                    "— strictly stronger than the single-source structural grep, where the two are count-equal.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Built/pending lag while loading ───────────────────────────────────────────────────────

        [Test]
        public void InFlightFetch_EverythingPending_NothingBuiltYet()
        {
            var release = new CancellationTokenSource();
            var src     = new TestDataSource((id, ct) => SpinUntilReleased(release));
            var go      = new GameObject("MapView_S85_Pending");
            var view    = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                view.LateUpdate(); // requests the cover; fetches kick and stay in-flight (SpinUntilReleased never returns)

                var snap = view.CaptureTelemetry();
                Assert.Greater(snap.VisibleTileCount, 0, "positive control: the cover must be non-empty");
                Assert.Greater(snap.InFlightFetches, 0,
                    "positive control: fetches must actually be in-flight, else the assertions below are vacuous");
                Assert.AreEqual(snap.VisibleTileCount, snap.PendingTileCount,
                    "every cover tile is pending while its fetch is stuck in-flight (single source ⇒ Loaded==Visible)");
                Assert.AreEqual(0, snap.LoadedTileCount - snap.PendingTileCount, "built == Loaded − Pending == 0");
            }
            finally
            {
                // Release the held fetches and drain so nothing leaks (observe-on-teardown discipline).
                release.Cancel();
                for (int f = 0; f < 300; f++) { view.LateUpdate(); Thread.Sleep(1); }
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void FixtureSource_AfterSettle_AllInFlightAndBacklogCountersZero_BuiltEqualsVisible()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S85_Settled");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must settle before measuring the settled state");

                var snap = view.CaptureTelemetry();
                Assert.Greater(snap.VisibleTileCount, 0, "positive control: the cover must be non-empty");
                Assert.AreEqual(0, snap.InFlightFetches);
                Assert.AreEqual(0, snap.PendingTileCount);
                Assert.AreEqual(0, snap.ConsumeBacklog);
                Assert.AreEqual(snap.VisibleTileCount, snap.LoadedTileCount - snap.PendingTileCount,
                    "built (Loaded − Pending) must equal the cover once settled (single source ⇒ Loaded==Visible)");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── ConsumeBacklog: the S95 "measure first" signal ────────────────────────────────────────

        [Test]
        public void ConsumeBacklog_TracksTheThrottledBuildBacklog_ThenDrainsToZero()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S85_Backlog");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 0;  // blocks consume entirely (the S87 backlog-builder)
                view.Config.MaxMeshBuildsPerTick = 64; // don't cap mesh build kicks

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Mesh builds complete one at a time on the ThreadPool, so ConsumeBacklog trickles up
                // (1, 2, ... ) across several Ticks before every loaded tile has finished building.
                // Wait for it to STABILIZE at PendingTileCount (⇒ no record is still fetch/tess-in-flight) —
                // stopping at the first non-zero backlog would race a partially-arrived batch.
                TileTelemetrySnapshot snap = default;
                for (int f = 0; f < 3000; f++)
                {
                    view.LateUpdate();
                    snap = view.CaptureTelemetry();
                    if (snap.PendingTileCount > 0 && snap.ConsumeBacklog == snap.PendingTileCount) break;
                    Thread.Sleep(1);
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
                    view.LateUpdate();

                Assert.IsTrue(view.AllTilesSettled(), "raising the budget must let the tiles finish settling");
                Assert.AreEqual(0, view.CaptureTelemetry().ConsumeBacklog, "the backlog must drain to 0");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── S82: PreparedTileCache utilization on the telemetry surface ───────────────────────────

        [Test]
        public void PreparedCache_Snapshot_ReflectsHitsEntryCountBytesHeld_AfterEvictAndRevisit()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S82_PreparedCacheTelemetry");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                PumpUntilSettled(view);

                var afterLoad = view.CaptureTelemetry();
                Assert.IsTrue(afterLoad.PreparedCacheEnabled, "positive control: default config has the cache enabled");
                Assert.Greater(afterLoad.PreparedCacheMisses, 0, "the first prepare must register >=1 miss");
                Assert.AreEqual(0, afterLoad.PreparedCacheEntryCount,
                    "nothing has been evicted into the cache yet — the live cover still owns every built mesh");
                Assert.AreEqual(0, afterLoad.PreparedCacheBytesHeld);

                // Evict the whole cover — pan far away; every Built tile transfers into the PreparedTileCache
                // in THIS tick (release is unthrottled — the whole _toRelease diff is processed synchronously
                // inside one Tick, no need to PumpUntilSettled to "finish" the eviction). Deliberately do NOT
                // PumpUntilSettled here: letting the away-location's fresh fetches reach Built before panning
                // back would transfer THEM into the cache too on the very next Tick (a released-but-Built
                // tile always transfers), contaminating the entry-count assertions below with unrelated
                // entries that have nothing to do with the revisit under test.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();

                var afterEvict = view.CaptureTelemetry();
                Assert.Greater(afterEvict.PreparedCacheEntryCount, 0,
                    "evicted Built tiles must land in the PreparedTileCache — EntryCount must reflect reality.");
                Assert.Greater(afterEvict.PreparedCacheBytesHeld, 0,
                    "cached meshes must report non-zero held bytes — BytesHeld must reflect reality.");

                // Revisit — pan back to the original (lon,lat) BEFORE the away-location tiles have had any
                // chance to reach Built (see note above): the cache must serve a hit for every originally-
                // cached tile, handing ownership (and the entry) back OUT (Model B TryTake).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.LateUpdate(); // the recompute (cover diff + probe) runs in THIS tick
                var afterRevisitTick = view.CaptureTelemetry();

                Assert.Greater(afterRevisitTick.PreparedCacheHits, 0,
                    "DECISIVE: the revisit tick must register >=1 PreparedCacheHits — a hit must increment Hits.");
                Assert.Less(afterRevisitTick.PreparedCacheEntryCount, afterEvict.PreparedCacheEntryCount,
                    "TryTake (Model B) removes the entry on a hit — a revisit that hits every originally-cached " +
                    "tile must strictly DECREASE EntryCount (a shallow impl that never drains EntryCount, or " +
                    "that only grows it, fails this).");

                PumpUntilSettled(view);
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void CaptureTelemetry_PreparedCacheDisabled_ReportsDisabledAndZeroHits()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S82_PreparedCacheDisabled");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            // Set BEFORE WithTestCamera() — TileManager reads PreparedCache.Enabled once at construction
            // (mirrors S82PreparedCacheTests.CacheDisabled_Revisit_AlwaysReprepares_NoTransfer).
            view.Config.PreparedCache.Enabled = false;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                PumpUntilSettled(view);

                var snap = view.CaptureTelemetry();
                Assert.IsFalse(snap.PreparedCacheEnabled, "the snapshot must reflect the disabled toggle.");
                Assert.AreEqual(0, snap.PreparedCacheHits, "a disabled cache must never register a hit.");
                Assert.Greater(snap.PreparedCacheMisses, 0,
                    "positive control: the probe still counts a miss when disabled (it never finds anything cached).");
                Assert.AreEqual(0, snap.PreparedCacheEntryCount, "a disabled cache never transfers a Built tile in.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── MapTelemetryPanel forwards the live snapshot ──────────────────────────────────────────

        [Test]
        public void MapTelemetryPanel_Tick_PopulatesFieldsFromLiveTelemetry()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S85_Panel");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            GameObject panelGo = null;
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                PumpUntilSettled(view);

                panelGo = new GameObject("S85_TelemetryPanel");
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;
                panel.Tick();

                var snap = view.CaptureTelemetry();
                Assert.Greater(snap.VisibleTileCount, 0, "positive control: the cover must be non-empty");
                Assert.AreEqual(snap.VisibleTileCount, panel.VisibleTileCount);
                Assert.AreEqual(snap.LoadedTileCount, panel.LoadedTileCount);
                Assert.AreEqual(snap.ConsumeBacklog, panel.ConsumeBacklog);

                // S82: the cache section forwards too — positive control on Misses (the first prepare) so
                // this isn't a vacuous 0==0 comparison.
                Assert.Greater(snap.PreparedCacheMisses, 0, "positive control: the first prepare must register a miss");
                Assert.AreEqual(snap.PreparedCacheEnabled, panel.PreparedCacheEnabled);
                Assert.AreEqual(snap.PreparedCacheHits, panel.PreparedCacheHits);
                Assert.AreEqual(snap.PreparedCacheMisses, panel.PreparedCacheMisses);
                Assert.AreEqual(snap.PreparedCacheEntryCount, panel.PreparedCacheEntryCount);
                Assert.AreEqual(snap.PreparedCacheBytesHeld, panel.PreparedCacheBytesHeld);
                Assert.AreEqual(snap.PreparedCacheByteBudget, panel.PreparedCacheByteBudget);
                Assert.AreEqual(snap.PreparedCacheEvictions, panel.PreparedCacheEvictions);
                double expectedHitRate = (double)snap.PreparedCacheHits / (snap.PreparedCacheHits + snap.PreparedCacheMisses) * 100.0;
                Assert.AreEqual(expectedHitRate, panel.PreparedCacheHitRatePercent, 1e-9,
                    "the panel must derive the hit-rate percent from the same Hits/Misses the snapshot reports.");
            }
            finally
            {
                if (panelGo != null) Object.DestroyImmediate(panelGo);
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MapTelemetryPanel_Tick_NoOpsCleanly_WhenUnwired()
        {
            var panelGo = new GameObject("S85_TelemetryPanel_Unwired");
            try
            {
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = null;
                Assert.DoesNotThrow(() => panel.Tick());
            }
            finally
            {
                Object.DestroyImmediate(panelGo);
            }
        }

        // ── Allocation-free read (N-tick gate) ────────────────────────────────────────────────────

        [Test]
        public void CaptureTelemetry_AllocationFree_AcrossNTicks()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_S85_Alloc");
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
                Object.DestroyImmediate(go);
            }
        }
    }
}
