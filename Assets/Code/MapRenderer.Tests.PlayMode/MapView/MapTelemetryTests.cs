// S85 acceptance tests — render/tile telemetry (live plumbing, multi-source, load-progress, backlog,
// panel forwarding). PlayMode half: the async-settle teeth, which settle over real frames (yield, never
// Thread.Sleep in the test body). The allocation-free teeth (D2 GC.Alloc) and the trivial unwired-panel
// no-op stay in the EditMode half (MapRenderer.Tests.MapViews.MapTelemetryTests) — PlayMode's per-frame
// engine allocations would pollute the measured region. NOT included in Tools/core-tests.

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
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.PlayMode.MapViews
{
    [TestFixture]
    public class MapTelemetryTests
    {
        // ── Helpers ────────────────────────────────────────────────────────────────────────────
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
        /// faulting (this stage wants a deterministic pending window, not a cancellation race). A blocking
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
            var go   = new GameObject("MapView_S85_Decisive");
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
                // NOT change the count here — the S88 512-px on-screen-tile convention keeps the near-field
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
                Object.DestroyImmediate(go);
            }
        }

        // ── Multi-source discriminator ────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator MultiSource_VisibleTileCount_DivergesFromLoadedTileCount()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S85_MultiSource");
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
                Object.DestroyImmediate(go);
            }
        }

        // ── Built/pending lag while loading ───────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator InFlightFetch_EverythingPending_NothingBuiltYet()
        {
            var release = new CancellationTokenSource();
            var src     = new TestDataSource((id, ct) => SpinUntilReleased(release));
            var go      = new GameObject("MapView_S85_Pending");
            var view    = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
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
                // A `finally` cannot contain `yield return` (CS1625), so the drain is the SYNCHRONOUS
                // DrainMeshBuilds (spins the ThreadPool builds inline) — never Thread.Sleep.
                release.Cancel();
                for (int f = 0; f < 300; f++) { view.LateUpdate(); view.DrainMeshBuilds(); }
                view.Teardown();
                Object.DestroyImmediate(go);
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
            var go   = new GameObject("MapView_S85_Settled");
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
                Object.DestroyImmediate(go);
            }
        }

        // ── S82: PreparedTileCache utilization on the telemetry surface ───────────────────────────

        [UnityTest]
        public IEnumerator PreparedCache_Snapshot_ReflectsHitsEntryCountBytesHeld_AfterEvictAndRevisit()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S82_PreparedCacheTelemetry");
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
                Object.DestroyImmediate(go);
            }
        }

        [UnityTest]
        public IEnumerator CaptureTelemetry_PreparedCacheDisabled_ReportsDisabledAndZeroHits()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S82_PreparedCacheDisabled");
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
                Object.DestroyImmediate(go);
            }
        }

        // ── MapTelemetryPanel pulls each provider's telemetry ─────────────────────────────────────

        [UnityTest]
        public IEnumerator MapTelemetryPanel_Pull_PopulatesFieldsFromProviderTelemetry()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S85_Panel");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            GameObject panelGo = null;
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);

                panelGo = new GameObject("S85_TelemetryPanel");
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;
                view.LateUpdate();   // the providers refresh their own structs during the frame
                panel.Pull();        // what the panel's Update does; manual-drive has no game loop

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

        /// <summary>
        /// docs/telemetry-design.md §2 under the pull model: a panel that is never pulled is never written — which
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
            var go   = new GameObject("MapView_Telemetry_NeverPulled");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            GameObject panelGo = null;
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick   = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                yield return PumpUntilSettled(view);

                Assert.Greater(view.CaptureTelemetry().VisibleTileCount, 0,
                    "positive control: there IS a non-zero level to read, so a zero below means 'not written'.");

                panelGo = new GameObject("TelemetryPanel_NeverPulled");
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;   // wired, but never pulled — exactly a DISABLED panel's state

                for (int i = 0; i < 8; i++) view.LateUpdate();

                Assert.AreEqual(0, panel.VisibleTileCount,
                    "eight frames of live providers must leave an unpulled panel untouched — wiring the Inspector " +
                    "reference is not what makes it cost anything; Update is.");
                Assert.AreEqual(0, panel.LoadedTileCount);
                Assert.AreEqual(0, panel.SymbolActiveLabelTiles);

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
                if (panelGo != null) Object.DestroyImmediate(panelGo);
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Each owner publishes its own telemetry (docs/telemetry-design.md §3) ──────────────────
        //
        // The two LABEL providers are tested where they are actually driven — SymbolLabelSubsystemPumpTests
        // and LabelFadeTests — because LoadTestStyle never wires the symbol subsystem, so no MapView-level
        // test can reach CurrentBatch / Labels.Tick at all (see the design doc's §6 note).

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
            var go   = new GameObject("MapView_Telemetry_CleanTick");
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
                Object.DestroyImmediate(go);
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
}
