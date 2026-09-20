// S85 acceptance tests — render/tile telemetry (live plumbing, panel forwarding, allocation). Unity-only
// (MonoBehaviour + MapView + ThreadPool fetch); the TileCoverStats + FrustumTileSelector aspect/Flat teeth
// live in the engine-free TileCoverStatsTests.cs (shared verbatim with the fast core-tests project).
//
// EditMode half: the D2 GC.Alloc teeth (PlayMode's per-frame engine allocations would pollute the measured
// region) and the trivial unwired-panel no-op. The async-settle + multi-source/backlog/prepared-cache
// teeth live in the PlayMode half (MapRenderer.Tests.PlayMode.MapViews.MapTelemetryTests). Settle is
// deterministic here via DrainMeshBuilds (no Thread.Sleep).

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;
using MapRenderer.App;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.MapViews
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

        /// <summary>Deterministically settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick consumes
        /// them. No frame yielding — mirrors <c>PreparedCacheTests.PumpUntilSettled</c>.</summary>
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

        [Test]
        public void MapTelemetryPanel_Pull_NoOpsCleanly_WhenUnwired()
        {
            var panelGo = new GameObject("S85_TelemetryPanel_Unwired");
            try
            {
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = null;
                Assert.DoesNotThrow(() => panel.Pull());
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
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
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

        /// <summary>
        /// The PUBLISH path must be allocation-free too — and this is the boxing tooth of
        /// docs/telemetry-design.md §6: the provider→reader path must stay copy-free and unboxed. Returning a
        /// snapshot by value, or erasing one to <c>object</c> / a non-generic interface anywhere on the path,
        /// shows up here as a per-frame allocation.
        /// </summary>
        [Test]
        public void PullTelemetry_IntoAPanel_IsAllocationFree_AcrossNFrames()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_Telemetry_PublishAlloc");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg; // zero-alloc is the BRG backend's contract
            GameObject panelGo = null;
            try
            {
                view.Config.TileSelection.MinZoom = 2; view.Config.TileSelection.MaxZoom = 2;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick   = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must settle before measuring steady state.");

                panelGo = new GameObject("TelemetryPanel_PublishAlloc");
                var panel = panelGo.AddComponent<MapTelemetryPanel>();
                panel.Map = view;

                // Prime the reused capture scratch, and prove the pull actually lands before measuring — otherwise
                // this would be an allocation test over a read that never happens.
                view.LateUpdate();
                panel.Pull();
                Assert.Greater(panel.VisibleTileCount, 0, "positive control: the panel must be reading real levels.");

                Assert.That(() =>
                {
                    for (int i = 0; i < 64; i++) { view.LateUpdate(); panel.Pull(); }
                },
                Is.Not.AllocatingGCMemory(),
                "each provider's refresh + the ref-return read + the panel's field writes must not allocate per " +
                "frame. A by-value accessor, a boxed snapshot (erased to object / a non-generic interface), or a " +
                "per-frame closure fails this.");
            }
            finally
            {
                if (panelGo != null) Object.DestroyImmediate(panelGo);
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── ConsumeBacklog: the S95 "measure first" signal ────────────────────────────────────────
        // EditMode-only: this test blocks CONSUME (MaxConsumesPerTick=0) so completed mesh builds pile up
        // as an unconsumed backlog. It needs the ThreadPool builds to COMPLETE (wall-clock) WITHOUT being
        // consumed — DrainMeshBuilds would consume them to Built (backlog→0, defeating the scenario), and a
        // PlayMode yield-pump stalls the pipeline under consume=0 backpressure. AwaitInFlightMeshBuilds gives
        // each iteration the ThreadPool wall-clock a completed build needs WITHOUT consuming — the only
        // mechanism that fits.
        [Test]
        public void ConsumeBacklog_TracksTheThrottledBuildBacklog_ThenDrainsToZero()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S85_Backlog");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 0;  // blocks consume entirely (the S87 backlog-builder)
                view.Config.MaxMeshBuildsPerTick = 64; // don't cap mesh build kicks

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Mesh builds complete one at a time on the ThreadPool, so ConsumeBacklog trickles up across
                // several Ticks. Wait for it to STABILIZE at PendingTileCount (⇒ no record still in-flight);
                // AwaitInFlightMeshBuilds gives each iteration the wall-clock a completed build needs (see
                // note above) WITHOUT consuming, so the backlog it measures stays intact.
                TileTelemetrySnapshot snap = default;
                for (int f = 0; f < 3000; f++)
                {
                    view.LateUpdate();
                    snap = view.CaptureTelemetry();
                    if (snap.PendingTileCount > 0 && snap.ConsumeBacklog == snap.PendingTileCount) break;
                    view.AwaitInFlightMeshBuilds();
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
                {
                    view.LateUpdate();
                    view.DrainMeshBuilds();
                }

                Assert.IsTrue(view.AllTilesSettled(), "raising the budget must let the tiles finish settling");
                Assert.AreEqual(0, view.CaptureTelemetry().ConsumeBacklog, "the backlog must drain to 0");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
