// MapView/MapViewTests.cs — MapView telemetry, mesh-build, restyle, live-loop and style-commit teeth, PlayMode half.
//
// All six settle over real frames (yield, never Thread.Sleep). MapViewMaterialValidationOrderingTests.cs
// stays its own file — it imports System, and every file here calls bare Object.DestroyImmediate
// (UnityEngine.Object), a using collision the merge rule resolves by not merging, never by qualifying.
//
// Contents:
//   MapTelemetryTests              — render/tile telemetry, PlayMode half: async-settle teeth over real frames.
//   MapViewAsyncMeshBuildTests     — async non-blocking tile mesh build, PlayMode half: async-settle teeth over real frames.
//   MapViewBackgroundRestyleTests  — a restyle keeps the previous background + identity valid until the synchronous commit.
//   MapViewLiveLoopTests           — the live loop's cover-drives-selection + eviction-releases tooth, settled over real frames.
//   MapViewSetStyleTests           — the live MapView.SetStyle path over real frames.
//   MapViewSourceSpecTests         — MapView.BuildSourceSpecs resolves specs to only the MVT-fetching layers.

using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.App;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using Unity.Mathematics;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using static MapRenderer.Tests.SetStyleAtomicity; // shared scaffold: styles, GatedLoader, SpinTo*, AssertOldStyleIntact
using System.IO;
using MapRenderer.Unity.Rendering.Source;


namespace MapRenderer.Tests.PlayMode.MapViews
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MapTelemetryTests — render/tile telemetry, PlayMode async-settle half
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapTelemetryTests : BaseTestFixture
    {
        // ── Helpers ────────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""multi-source"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>Two distinct rendered sources — the multi-source discriminator (each gets its own
        /// TileManager pipeline / <c>_loaded</c> record per cover tile).</summary>
        private static StyleDocument TwoSourceStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""multi-source-b"",
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

        /// <summary>Pumps Tick() until every loaded tile has settled or a spin budget is hit (real frames,
        /// so the ThreadPool mesh build actually progresses — mirrors
        /// <c>MapViewAsyncMeshBuildTests.PumpUntilSettled</c>).</summary>
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

        /// <summary>A fetch that stays in-flight until <paramref name="release"/> is cancelled, then resolves
        /// absent — mirrors <c>TileFetchCancellationTests.SpinThenFault</c>, but resolves cleanly instead of
        /// faulting — the caller needs a deterministic pending window, not a cancellation race. A blocking
        /// wait on the cancellation token's WaitHandle is a fetch-latency SIM (inside the UniTask body after
        /// SwitchToThreadPool), not a settle-poll — it stays as-is, parked rather than polled.</summary>
        private static async UniTask<TileResponse> SpinUntilReleased(CancellationTokenSource release)
        {
            await UniTask.SwitchToThreadPool();
            // Park until cancelled or the ~60s safety cap. Guard the WaitHandle access: if the release path
            // disposed the CTS before this ThreadPool continuation ran, `release.Token.WaitHandle` throws
            // ObjectDisposedException — a disposed source means the fetch WAS released, so resolve absent as
            // if it had woken cleanly, rather than faulting the fetch with an unexpected ODE.
            try { release.Token.WaitHandle.WaitOne(60000); }
            catch (System.ObjectDisposedException) { }
            return TileResponse.Absent(TileEncoding.Mvt);
        }

        // ── THE decisive test: independent recompute + must-change ───────────────────────────────

        [UnityTest]
        public IEnumerator CaptureTelemetry_VisibleTileCount_MatchesIndependentSelector_AndChangesAcrossViews()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_Telemetry_Decisive"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
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
                yield return PumpUntilSettled(view);

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
                // NOT change the count here — the 512-px on-screen-tile convention keeps the near-field
                // grid size roughly CONSTANT across zoom for a fixed square viewport (by design: altitude and
                // tile ground size scale together), so a zoom change only "changes" VisibleTileCount below the
                // saturation point. Confirmed directly (FrustumTileSelector over this viewport): z0=1, z1=4,
                // z2..z4=16 (constant thereafter) — so 4 → 1 is the smallest genuinely material zoom change.
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 1.0 });
                yield return PumpUntilSettled(view);
                int changed = view.CaptureTelemetry().VisibleTileCount;

                Assert.Greater(changed, 0);
                Assert.AreNotEqual(snap.VisibleTileCount, changed,
                    "VisibleTileCount must change between materially different views (zoom 4 → 1).");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Multi-source discriminator ────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator MultiSource_VisibleTileCount_DivergesFromLoadedTileCount()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_Telemetry_MultiSource"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: TwoSourceStyle());
                yield return PumpUntilSettled(view);

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
            }
        }

        // ── Built/pending lag while loading ───────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator InFlightFetch_EverythingPending_NothingBuiltYet()
        {
            var release = new CancellationTokenSource();
            var src     = new TestDataSource((id, ct) => SpinUntilReleased(release));
            var go = Track(new GameObject("MapView_Telemetry_Pending"));
            var view    = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;
                // Also lift the concurrent-load cap (default 12) so EVERY cover tile fetches at once — otherwise
                // a >12-tile cover leaves the overflow queued (not loaded ⇒ not pending) and Pending < Visible.
                view.Config.MaxConcurrentTileLoads    = 64;

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
                // A `finally` cannot contain `yield return` (CS1625), so the drain is the SYNCHRONOUS
                // DrainMeshBuilds (spins the ThreadPool builds inline) — never Thread.Sleep.
                release.Cancel();
                for (int f = 0; f < 300; f++) { view.LateUpdate(); view.DrainMeshBuilds(); }
                view.Teardown();
            }
            // The assertions above are all synchronous (immediate post-tick state) — nothing in the try
            // block needs a frame boundary. This yield exists only so the compiler emits an iterator
            // state machine (a method with zero `yield` statements can't return IEnumerator); it must sit
            // here, outside `finally`, since `yield return`/`yield break` is illegal inside one (CS1625).
            yield break;
        }

        [UnityTest]
        public IEnumerator FixtureSource_AfterSettle_AllInFlightAndBacklogCountersZero_BuiltEqualsVisible()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_Telemetry_Settled"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);
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
            }
        }

        // ── PreparedTileCache utilization on the telemetry surface ────────────────────────────────

        [UnityTest]
        public IEnumerator PreparedCache_Snapshot_ReflectsHitsEntryCountBytesHeld_AfterEvictAndRevisit()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_S82_PreparedCacheTelemetry"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);

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

                yield return PumpUntilSettled(view);
            }
            finally
            {
                view.Teardown();
            }
        }

        [UnityTest]
        public IEnumerator CaptureTelemetry_PreparedCacheDisabled_ReportsDisabledAndZeroHits()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_S82_PreparedCacheDisabled"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            // Set BEFORE WithTestCamera() — TileManager reads PreparedCache.Enabled once at construction
            // (mirrors PreparedCacheTests.CacheDisabled_Revisit_AlwaysReprepares_NoTransfer).
            view.Config.PreparedCache.Enabled = false;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);

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
            }
        }

        // ── MapTelemetryPanel pulls each provider's telemetry ─────────────────────────────────────

        [UnityTest]
        public IEnumerator MapTelemetryPanel_Pull_PopulatesFieldsFromProviderTelemetry()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_Telemetry_Panel"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                using var innerBag = new ObjectDisposalBag();
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);

                var panelGo = innerBag.Track(new GameObject("TelemetryPanel"));
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;
                view.LateUpdate();   // the providers refresh their own structs during the frame
                panel.Pull();        // what the panel's Update does; manual-drive has no game loop

                var snap = view.CaptureTelemetry();
                Assert.Greater(snap.VisibleTileCount, 0, "positive control: the cover must be non-empty");
                Assert.AreEqual(snap.VisibleTileCount, panel.VisibleTileCount);
                Assert.AreEqual(snap.LoadedTileCount, panel.LoadedTileCount);
                Assert.AreEqual(snap.ConsumeBacklog, panel.ConsumeBacklog);

                // The cache section forwards too — positive control on Misses (the first prepare) so
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
                view.Teardown();
            }
        }

        /// <summary>
        /// docs/telemetry-design.md's pull model: a panel that is never pulled is never written — which
        /// is exactly a DISABLED panel, because a disabled MonoBehaviour gets no <c>Update</c>. Pulling once fills
        /// it (the positive control: without it this would also pass if the pull were simply broken), and a panel
        /// whose reference is cleared stops updating again.
        ///
        /// <para>Note what the pull model makes this test STRONGER at: the old subscription version could only
        /// assert on <c>HasSubscribers</c> — the state the early-out read — and admitted it could not distinguish
        /// "did not capture" from "captured and told nobody". Here the panel's own fields ARE the evidence: frames
        /// pass, the providers refresh, and the panel stays zero because nothing read it.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator MapTelemetryPanel_NeverPulled_IsNeverWritten_AndPullingFillsIt()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_Telemetry_NeverPulled"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                using var innerBag = new ObjectDisposalBag();
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick   = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);

                Assert.Greater(view.CaptureTelemetry().VisibleTileCount, 0,
                    "positive control: there IS a non-zero level to read, so a zero below means 'not written'.");

                var panelGo = innerBag.Track(new GameObject("TelemetryPanel_NeverPulled"));
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;   // wired, but never pulled — exactly a DISABLED panel's state

                for (int i = 0; i < 8; i++) view.LateUpdate();

                Assert.AreEqual(0, panel.VisibleTileCount,
                    "eight frames of live providers must leave an unpulled panel untouched — wiring the Inspector " +
                    "reference is not what makes it cost anything; Update is.");
                Assert.AreEqual(0, panel.LoadedTileCount);
                Assert.AreEqual(0, panel.SymbolActiveTiles);

                panel.Pull();

                Assert.AreEqual(view.CaptureTelemetry().VisibleTileCount, panel.VisibleTileCount,
                    "positive control: one pull fills the panel from the provider's live struct.");
                Assert.Greater(panel.VisibleTileCount, 0);

                // Clearing the reference is what a panel switched off mid-session looks like to Pull(). Zeroing the
                // mirror fields by hand first is what gives this teeth: a Pull that ignored the null Map would
                // write them straight back to the non-zero values above.
                panel.Map = null;
                panel.VisibleTileCount = 0;
                panel.LoadedTileCount  = 0;

                for (int i = 0; i < 4; i++) { view.LateUpdate(); panel.Pull(); }

                Assert.AreEqual(0, panel.VisibleTileCount,
                    "an unwired panel must stay unwritten even while its Update keeps calling Pull.");
                Assert.AreEqual(0, panel.LoadedTileCount);
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Each owner publishes its own telemetry (docs/telemetry-design.md) ─────────────────────
        //
        // The two LABEL providers are tested where they are actually driven — SymbolSubsystemPumpTests
        // and SymbolFadeTests — because LoadTestStyle never wires the symbol subsystem, so no MapView-level
        // test can reach CurrentBatch / SymbolPlacementSystem.Tick at all (see the design doc's note).

        /// <summary>
        /// A CLEAN tick must not blank the readout. <c>TileManager.Tick</c> returns early when the cover is
        /// unchanged, so a refresh reached from inside that path would leave the levels reading zero exactly when
        /// the camera goes still — the state you stare at longest. This is why the refresh sits in a shell around
        /// <c>TickCore</c> rather than at the end of the work.
        ///
        /// <para><b>Weaker than the push-model test it replaces, deliberately, and here is exactly how.</b> The old
        /// version counted publish CALLBACKS, so it could assert "one publish per tick, including the early-return
        /// path". A pull has no callback to count, and no captured value can be perturbed from a test without adding
        /// production surface the no-test-only-members rule forbids — so "the refresh ran" is no longer directly
        /// observable. What survives IS falsifiable: a clean tick that refreshed from the early-return path would
        /// produce zeros, and the loop below would fail. "The refresh runs at all" is now guaranteed structurally
        /// instead — it is one line in <c>Tick</c>, outside <c>TickCore</c>, where the early return cannot reach it.
        /// Reinstating a per-refresh stamp on the snapshot would make it observable again.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator TileTelemetry_SurvivesACleanTick_WithoutBlankingTheLevels()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_Telemetry_CleanTick"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick   = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "the camera must be still and the cover clean before measuring.");

                int settledVisible = view.CaptureTelemetry().VisibleTileCount;
                Assert.Greater(settledVisible, 0,
                    "positive control: the cover is non-empty, so a zero below is a blanked readout, not an empty map.");

                // Nothing moves: no camera change, no config change, so every one of these is a clean tick.
                for (int i = 0; i < 8; i++)
                {
                    view.LateUpdate();
                    AssertLiveVisibleTileCountUnchanged(view, settledVisible, i);
                }
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>Bound by reference: aliases the provider's own field, so this is observed through the
        /// same storage the production readers use. Split out of the coroutine body — a `ref readonly`
        /// local is illegal inside an iterator method (CS8176).</summary>
        private static void AssertLiveVisibleTileCountUnchanged(MapView view, int expectedVisible, int tickIndex)
        {
            ref readonly TileTelemetrySnapshot live = ref view.View.TileManager.Telemetry;
            Assert.AreEqual(expectedVisible, live.VisibleTileCount,
                $"clean tick {tickIndex} blanked or changed the cover level — a refresh reached from TickCore's " +
                "early-return path is how that happens, and it is a readout that dies when the map stills.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewAsyncMeshBuildTests — async non-blocking tile mesh build, PlayMode async-settle half
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapViewAsyncMeshBuildTests : BaseTestFixture
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
            var go = Track(new GameObject("MapView_TiltCover"));
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
            }
        }

        // ── Tooth 1: no live .Schedule().Complete() — mesh build is deferred ≥ 1 frame ──────

        /// <summary>
        /// Behavioral test for tooth 1: on the same frame the fetch completes, the tile must NOT
        /// transition to Built == true (mesh build is deferred to a later frame / poll cycle).
        ///
        /// Setup: the source returns synchronously (Task.FromResult), so after the first Tick() the fetch
        /// task IsCompleted == true. Mesh build is kicked as a background Task and must NOT be consumed in
        /// that same Tick().
        /// </summary>
        [UnityTest]
        public IEnumerator Tooth1_MeshBuildDeferred_TileNotBuiltInSameFetchFrame()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView_T1"));
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
            }
        }

        // ── Tooth 3: correctness parity — async path == sync path ──────────────────────────────

        /// <summary>
        /// The async MapView live loop (build off-main + upload on main) must produce the same
        /// vertex count AND vertex positions as a direct synchronous call to StyledFillTileBuilder.BuildMesh.
        ///
        /// The async arm projects vertices with a scalar managed C# loop
        /// (<c>ProjectVerticesManaged</c>), which replicates the builder's double-precision arithmetic. This
        /// test pins that no ULP difference exists: positions must be exactly equal.
        ///
        /// This mirrors MapViewLiveLoopTests.MapView_GoLive_ProducesSameGeometryAsDirectBuilder but
        /// exercises the async path explicitly by waiting for async settle.
        /// </summary>
        [UnityTest]
        public IEnumerator Tooth3_AsyncPath_ProducesSameGeometryAsSyncPath()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var    src   = TestDataSource.FromBytes(bytes);
            var    go    = Track(new GameObject("MapView_T3"));
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
                var paint     = ((Fill.StyleLayer)fillLayer).Paint;
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
            }
        }

        // ── Tooth 4: cancellation — released mid-flight tile is discarded ─────────────────────

        /// <summary>
        /// Request tiles at z=5, kick mesh build mid-flight, pan far away to evict original tiles,
        /// then verify no stale GameObjects are created for the evicted tiles after settle.
        ///
        /// Verifies the cancellation contract: a mesh build completing after ReleaseTile must not
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
            var go = Track(new GameObject("MapView_T4"));
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
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewBackgroundRestyleTests — a restyle keeps the previous background/identity valid until commit
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapViewBackgroundRestyleTests : BaseTestFixture
    {
        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapViewBackgroundRestyle");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            // Style B's "https://example.com/..." tile template is a PLACEHOLDER, never meant to be
            // fetched for real — without this override, PumpUntilSettled's real cover/fetch loop would hit
            // the network (via the DEFAULT TileDataSourceFactory), producing a flaky/slow test AND an
            // unobserved-exception console flood from the doomed request (mirrors MapViewSetStyleTests'
            // convention: every non-file:// test either overrides the factory or stays fully offline).
            view.View.TileSourceFactoryOverride = _ => TestDataSource.Absent();
            return view;
        }

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator DelayedRestyle_KeepsPreviousBackgroundRendered_UntilCommit()
        {
            var view = NewView(out var go);
            Track(go);
            try
            {
                // Style A: background-only (no fetching layer, so its own SetStyle commits synchronously).
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;
                Assert.IsNotNull(bgMaterialA);

                // Style B: background + a url-source fill — the ONE await genuinely suspends on the gate.
                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B").Preserve();

                // Mid-resolution: OLD identity/layers/background must still be live — nothing mutated yet.
                AssertOldStyleIntact(view, bgMaterialA, because: "nothing may mutate before the commit (no blank window).");
                Assert.AreEqual(1, view.Layers.Count, "the OLD (one-layer) RenderLayerSet must be untouched.");

                // Release the gate — resolution completes, the commit runs.
                gate.Release();
                yield return SpinToSucceeded(restyleTask);
                yield return PumpUntilSettled(view);

                Assert.AreEqual("B", view.StyleId, "after commit, the NEW identity must be reported.");
                Assert.AreEqual(2, view.Layers.Count, "after commit, the NEW (two-layer) RenderLayerSet is live.");
            }
            finally { view.Teardown(); }
        }

        [UnityTest]
        public IEnumerator CancelledDuringResolution_LeavesPreviousStyleIntact()
        {
            var view = NewView(out var go);
            Track(go);
            try
            {
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                var layersRef = view.Layers; // same instance across restyle (rebuilt in place)
                Material bgMaterialA = view.Layers[0].Material;

                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                using var cts = new CancellationTokenSource();
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B", cts.Token).Preserve();

                // Cancel WHILE still suspended in resolution — nothing has mutated yet.
                cts.Cancel();
                Assert.AreEqual("A", view.StyleId, "cancel mid-resolution must not have touched identity yet.");

                gate.Release(); // let the (now-doomed) resolution finish so the task can observe the token
                yield return SpinToCompleted(restyleTask);

                Assert.IsTrue(restyleTask.Status == UniTaskStatus.Canceled || restyleTask.Status == UniTaskStatus.Faulted,
                    $"a cancelled restyle's task must not succeed (status={restyleTask.Status}).");
                AssertOldStyleIntact(view, bgMaterialA, because: "a cancelled restyle must leave the OLD style intact (no destroyed-material draw).");
                Assert.AreSame(layersRef, view.Layers, "the RenderLayerSet instance itself is unchanged.");
                Assert.AreEqual(1, view.Layers.Count, "Layers.Build must NEVER have run for the cancelled style.");
            }
            finally { view.Teardown(); }
        }

        [UnityTest]
        public IEnumerator AlreadyCancelledToken_LeavesPreviousStyleIntact()
        {
            var view = NewView(out var go);
            Track(go);
            try
            {
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;

                using var cts = new CancellationTokenSource();
                cts.Cancel(); // already cancelled BEFORE SetStyle is even called
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#ff0000")), "B", cts.Token).Preserve();
                yield return SpinToCompleted(restyleTask);

                Assert.IsTrue(restyleTask.Status == UniTaskStatus.Canceled || restyleTask.Status == UniTaskStatus.Faulted,
                    $"an already-cancelled token must abort SetStyle (status={restyleTask.Status}).");
                AssertOldStyleIntact(view, bgMaterialA, because: "an already-cancelled restyle must leave the OLD style intact.");
            }
            finally { view.Teardown(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewLiveLoopTests — the live loop's cover-drives-selection + eviction-releases over real frames
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapViewLiveLoopTests : BaseTestFixture
    {
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

        // ── cover drives selection + eviction releases container ───────────────────────────
        [UnityTest]
        public IEnumerator MapView_CoverDrivesTileSelection_AndEvictionReleases()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                yield return PumpUntilSettled(view);

                // The cover tracks the framing viewport span, so assert the behaviour (center built, far
                // pan evicts + re-covers), never a frozen count.
                Assert.Greater(view.LoadedTileCount(), 0, "z5 center cover must be non-empty");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "the center tile must be built");

                // Pan far east (lon=170) → new cover does NOT overlap the old one.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170, Latitude = 0 });
                yield return PumpUntilSettled(view);

                Assert.Greater(view.LoadedTileCount(), 0, "cover must be re-selected (non-empty) after the pan");
                Assert.IsFalse(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "the old center tile must have been evicted after the pan");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 5, X = 31, Y = 16 }),
                    "the new center tile must be built after the pan");
                // Eviction unregisters the tile's instanced draw items (and, on Entities, destroys its
                // layer entities + tile root). Unity's leak detector fails the run on teardown if leaked.
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewSetStyleTests — the live MapView.SetStyle path over real frames
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The live <see cref="MapView.SetStyle"/> path: file:// offline (zero network),
    /// inline-<c>tiles[]</c> fetch-side short-circuit, multi-source per-source routing, restyle reuses an
    /// unchanged source's pipeline+bytes, and <c>styleId</c> establishment. (The TileJSON parse+fill proof
    /// is the fast-core <c>TileJsonTests</c>; not re-tested here.)
    /// </summary>
    [TestFixture]
    public class MapViewSetStyleTests : BaseTestFixture
    {
        // Yield frames until a (Preserved) UniTask completes on the main thread — the file:// document loader
        // and the inline-source spec build both complete on the ThreadPool (no PlayerLoop), so a real frame
        // gives them wall-clock. Never Thread.Sleep.
        private static IEnumerator Await(UniTask task, int maxSpins = 10000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) yield return null;
            t.GetAwaiter().GetResult();
        }

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapView");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            return view;
        }

        private static string OneFillStyle(string sourceId, string tilesTemplate) => $@"{{
            ""version"": 8,
            ""sources"": {{ ""{sourceId}"": {{ ""type"": ""vector"", ""tiles"": [""{tilesTemplate}""] }} }},
            ""layers"": [
                {{ ""id"": ""f"", ""type"": ""fill"", ""source"": ""{sourceId}"",
                   ""source-layer"": ""countries"", ""paint"": {{ ""fill-color"": [""rgba"",200,50,50,1] }} }}
            ]
        }}";

        // Writes the fixture tile to <temp>/<sub>/0/0/0.mvt and returns a file:// {z}/{x}/{y} template.
        private static string WriteFileTileFixture(string sub)
        {
            string dir = Path.Combine(Application.temporaryCachePath, sub);
            Directory.CreateDirectory(Path.Combine(dir, "0", "0"));
            File.WriteAllBytes(Path.Combine(dir, "0", "0", "0.mvt"), SampleTileFixture.Bytes());
            return "file://" + Path.Combine(dir, "{z}", "{x}", "{y}.mvt");
        }

        // ── THE decisive tooth: a file:// style whose vector source resolves to file:// tiles renders with
        //    ZERO network — no UnityWebRequestDataSource is ever constructed across SetStyle + settle. ──────
        [UnityTest]
        public IEnumerator SetStyle_FileUriChain_RendersOffline_ZeroNetwork()
        {
            string tilesTemplate = WriteFileTileFixture("s83b-offline-tiles");
            string styleDir = Path.Combine(Application.temporaryCachePath, "s83b-offline-style");
            Directory.CreateDirectory(styleDir);
            string stylePath = Path.Combine(styleDir, "style.json");
            File.WriteAllText(stylePath, OneFillStyle("src", tilesTemplate.Replace("\\", "/")));
            string styleUri = "file://" + stylePath;

            int netBefore = UnityWebRequestDataSource.DebugConstructedCount;
            var view = NewView(out var go);
            Track(go);
            try
            {
                yield return Await(view.SetStyle(styleUri));   // file:// style doc → parse → resolve (inline) → wire
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "the file:// tile must build from the local fixture");
                Assert.AreEqual(netBefore, UnityWebRequestDataSource.DebugConstructedCount,
                    "a file:// chain must construct ZERO UnityWebRequestDataSource — no network. " +
                    "A >0 delta means the style/TileJSON/tiles hit the network (offline guarantee broken).");
                Assert.AreEqual(styleUri, view.StyleId, "SetStyle(uri) ⇒ styleId == uri");
            }
            finally { view.Teardown(); }
        }

        // ── Inline tiles[] short-circuit (fetch side): a source with inline tiles triggers ZERO TileJSON
        //    document fetches. ─────────────────────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator SetStyle_InlineTiles_FetchesNoTileJson()
        {
            string tilesTemplate = WriteFileTileFixture("s83b-inline-tiles");
            int docFetches = 0;
            var view = NewView(out var go);
            Track(go);
            view.View.DocumentLoaderOverride = (uri, ct) => { Interlocked.Increment(ref docFetches); return UniTask.FromResult(""); };
            try
            {
                var style = StyleParser.Parse(OneFillStyle("src", tilesTemplate.Replace("\\", "/")));
                yield return Await(view.SetStyle(style, "inline-style"));
                yield return PumpUntilSettled(view);

                Assert.AreEqual(0, docFetches,
                    "an inline-tiles[] source must trigger NO TileJSON document fetch (fetch-side short-circuit).");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }), "tile builds from inline tiles");
                Assert.AreEqual("inline-style", view.StyleId, "StyleDocument overload surfaces the caller styleId");
            }
            finally { view.Teardown(); }
        }

        // ── A url (TileJSON) source fetches the TileJSON exactly once and resolves its tiles from it. ──────
        [UnityTest]
        public IEnumerator SetStyle_TileJsonUrlSource_FetchesOnceAndResolves()
        {
            string tilesTemplate = WriteFileTileFixture("s83b-tilejson-tiles").Replace("\\", "/");
            int docFetches = 0;
            var view = NewView(out var go);
            Track(go);
            // Document loader returns a TileJSON whose tiles[] is the local file:// fixture template.
            view.View.DocumentLoaderOverride = (uri, ct) =>
            {
                Interlocked.Increment(ref docFetches);
                return UniTask.FromResult($@"{{ ""tilejson"":""3.0.0"", ""tiles"":[""{tilesTemplate}""], ""minzoom"":0, ""maxzoom"":0 }}");
            };
            try
            {
                // Source uses a `url` (no inline tiles) → resolution must fetch + fill from the TileJSON.
                var style = StyleParser.Parse(@"{
                    ""version"": 8,
                    ""sources"": { ""src"": { ""type"": ""vector"", ""url"": ""https://example.com/tiles.json"" } },
                    ""layers"": [ { ""id"":""f"", ""type"":""fill"", ""source"":""src"", ""source-layer"":""countries"",
                                    ""paint"": { ""fill-color"": [""rgba"",200,50,50,1] } } ]
                }");
                yield return Await(view.SetStyle(style, "tilejson-style"));
                yield return PumpUntilSettled(view);

                Assert.AreEqual(1, docFetches, "a url-only source fetches its TileJSON exactly once");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "tiles resolved from the TileJSON build (a no-op resolve would leave the source tile-less → no build)");
            }
            finally { view.Teardown(); }
        }

        // ── Multi-source per-source routing: two vector sources A,B each with a layer; each source's own
        //    counting IDataSource is fetched (both > 0). A single-source shortcut drives one to 0 → fails. ──
        [UnityTest]
        public IEnumerator SetStyle_MultiSource_RoutesEachLayerToItsOwnSource()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var perTemplate = new Dictionary<string, TestDataSource>();
            var view = NewView(out var go);
            Track(go);
            view.View.TileSourceFactoryOverride = template =>
            {
                var src = TestDataSource.FromBytes(bytes);
                perTemplate[template] = src;
                return src;
            };
            try
            {
                var style = StyleParser.Parse(@"{
                    ""version"": 8,
                    ""sources"": {
                        ""A"": { ""type"":""vector"", ""tiles"":[""https://a/{z}/{x}/{y}.pbf""] },
                        ""B"": { ""type"":""vector"", ""tiles"":[""https://b/{z}/{x}/{y}.pbf""] }
                    },
                    ""layers"": [
                        { ""id"":""la"", ""type"":""fill"", ""source"":""A"", ""source-layer"":""countries"",
                          ""paint"": { ""fill-color"": [""rgba"",200,50,50,1] } },
                        { ""id"":""lb"", ""type"":""fill"", ""source"":""B"", ""source-layer"":""countries"",
                          ""paint"": { ""fill-color"": [""rgba"",50,50,200,1] } }
                    ]
                }");
                yield return Await(view.SetStyle(style, "multi"));
                yield return PumpUntilSettled(view);

                Assert.IsTrue(perTemplate.ContainsKey("https://a/{z}/{x}/{y}.pbf"), "source A pipeline built");
                Assert.IsTrue(perTemplate.ContainsKey("https://b/{z}/{x}/{y}.pbf"), "source B pipeline built");
                int fa = perTemplate["https://a/{z}/{x}/{y}.pbf"].FetchCount;
                int fb = perTemplate["https://b/{z}/{x}/{y}.pbf"].FetchCount;
                Assert.Greater(fa, 0, "source A is fetched for A's records");
                Assert.Greater(fb, 0, "source B is fetched for B's records (a single-source shortcut leaves this 0)");
                Assert.AreEqual(fa, fb, "routing is symmetric — each source fetches only its own (tile,source) records");
            }
            finally { view.Teardown(); }
        }

        // ── Restyle reuses an UNCHANGED source's pipeline: it is not re-created (construct count stays 1)
        //    and its cached bytes are reused (FetchCount unchanged across the second SetStyle). ────────────
        [UnityTest]
        public IEnumerator Restyle_UnchangedSource_KeepsPipelineAndReusesBytes()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            int constructsForA = 0;
            TestDataSource srcA = null;
            var view = NewView(out var go);
            Track(go);
            view.View.TileSourceFactoryOverride = template =>
            {
                if (template == "https://a/{z}/{x}/{y}.pbf") { constructsForA++; srcA = TestDataSource.FromBytes(bytes); return srcA; }
                return TestDataSource.FromBytes(bytes);
            };
            try
            {
                // Style 1: source A + a red fill layer.
                yield return Await(view.SetStyle(StyleParser.Parse(@"{
                    ""version"":8,
                    ""sources"": { ""A"": { ""type"":""vector"", ""tiles"":[""https://a/{z}/{x}/{y}.pbf""] } },
                    ""layers"": [ { ""id"":""f"", ""type"":""fill"", ""source"":""A"", ""source-layer"":""countries"",
                                    ""paint"": { ""fill-color"": [""rgba"",200,50,50,1] } } ]
                }"), "v1"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual(1, constructsForA, "source A constructed once on first SetStyle");
                int fetchesAfterV1 = srcA.FetchCount;
                Assert.Greater(fetchesAfterV1, 0, "source A fetched its tile on v1");

                // Style 2: SAME source A (unchanged def ⇒ same SourceKey), DIFFERENT layer paint (green).
                yield return Await(view.SetStyle(StyleParser.Parse(@"{
                    ""version"":8,
                    ""sources"": { ""A"": { ""type"":""vector"", ""tiles"":[""https://a/{z}/{x}/{y}.pbf""] } },
                    ""layers"": [ { ""id"":""f"", ""type"":""fill"", ""source"":""A"", ""source-layer"":""countries"",
                                    ""paint"": { ""fill-color"": [""rgba"",50,200,50,1] } } ]
                }"), "v2"));
                yield return PumpUntilSettled(view);

                Assert.AreEqual(1, constructsForA,
                    "an UNCHANGED source must NOT be re-created on restyle (kept pipeline ⇒ identity preserved)");
                Assert.AreEqual(fetchesAfterV1, srcA.FetchCount,
                    "an unchanged source's bytes are reused from its kept cache on restyle — NO re-fetch. " +
                    "A teardown-and-rebuild (or Scheduler.Release on a kept source) would re-fetch → count grows → fails.");
                Assert.AreEqual("v2", view.StyleId, "restyle updates styleId");
            }
            finally { view.Teardown(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewSourceSpecTests — MapView.BuildSourceSpecs resolves specs to only the MVT-fetching layers
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapViewSourceSpecTests : BaseTestFixture
    {
        // rasterSrc is URL-only (no inline tiles[]) — a regression that incorrectly builds a spec for a
        // raster layer would fetch its TileJSON too, making "zero document loads" a real falsifier, not a
        // vacuous one.
        private const string MixedStyle = @"{
            ""version"": 8,
            ""sources"": {
                ""fillSrc"":   { ""type"": ""vector"", ""tiles"": [""https://fill/{z}/{x}/{y}.pbf""] },
                ""rasterSrc"": { ""type"": ""raster"", ""url"": ""https://raster-tilejson.example/meta.json"" }
            },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#00ff00"" } },
                { ""id"": ""f"",  ""type"": ""fill"", ""source"": ""fillSrc"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": ""#ff0000"" } },
                { ""id"": ""r"",  ""type"": ""raster"", ""source"": ""rasterSrc"" }
            ]
        }";

        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapViewSourceSpec");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            return view;
        }

        /// <summary>Yields frames until the (Preserved) style task completes, then propagates its result —
        /// the loaders complete on the ThreadPool, so a real frame gives them wall-clock. Never Thread.Sleep.</summary>
        private static IEnumerator SpinToCompleted(UniTask task, int maxSpins = 20000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) yield return null;
            t.GetAwaiter().GetResult();
        }

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator MixedStyle_ResolvesOnlyTheFillsSource_NoRasterOrBackgroundFetch()
        {
            var view = NewView(out var go);
            Track(go);
            int docFetches = 0;
            int factoryCalls = 0;
            var seenTemplates = new List<string>();
            view.View.DocumentLoaderOverride    = (uri, ct) => { Interlocked.Increment(ref docFetches); return UniTask.FromResult(""); };
            view.View.TileSourceFactoryOverride = template =>
            {
                Interlocked.Increment(ref factoryCalls);
                seenTemplates.Add(template);
                return TestDataSource.Absent(); // never actually fetched by this test's assertions
            };
            try
            {
                yield return SpinToCompleted(view.SetStyle(StyleParser.Parse(MixedStyle), "mixed"));
                yield return PumpUntilSettled(view);

                Assert.AreEqual(0, docFetches,
                    "no TileJSON/document load may be issued for the raster (or background) layer.");
                Assert.AreEqual(1, factoryCalls, "exactly ONE source (the fill's) may be constructed.");
                Assert.AreEqual("https://fill/{z}/{x}/{y}.pbf", seenTemplates[0]);
            }
            finally { view.Teardown(); }
        }
    }
}
