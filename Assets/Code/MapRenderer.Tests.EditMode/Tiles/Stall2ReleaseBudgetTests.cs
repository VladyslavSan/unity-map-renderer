// Stall #2 (release storm) — the deferred-release queue budgets how many (tile, source) records are freed
// per Tick (backend removal + mesh destroy + scheduler release), so a zoom-out/fast-pan no longer frees the
// whole departing cover in one frame. These teeth drive the real MapView tile loop (BRG backend, to avoid
// the EG backend's intermittent alloc noise). Unity-only; excluded from core-tests.csproj.

using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class Stall2ReleaseBudgetTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
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

        private static void PumpUntilSettled(MapView view, int maxFrames = 3000)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
        }

        private static (GameObject go, MapView view) NewView(int releaseBudget)
        {
            var go   = new GameObject("MapView_Stall2");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend               = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 8;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxReleasesPerTick        = releaseBudget;
            return (go, view);
        }

        // ── Budget: no single Tick releases more than MaxReleasesPerTick; the backlog drains over frames ──
        [Test]
        public void ReleaseQueue_BoundsReleasesPerTick_AndDrainsBacklog()
        {
            var src        = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(releaseBudget: 2);
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 7.0), style: MinimalStyle());
                PumpUntilSettled(view);
                int loadedBefore = view.LoadedTileCount();
                Assert.GreaterOrEqual(loadedBefore, 3, "need > budget tiles so the release budget actually defers work.");

                // Zoom far out → the whole z6 cover leaves; the new cover is the coarse (z0) world tile.
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 0.0 });
                view.LateUpdate(); // recompute enqueues every departure, then the drain frees up to the budget
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
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Re-validation: a tile that leaves cover then returns before its dequeue is KEPT, not churned ──
        [Test]
        public void ReleaseQueue_PanOutPanBack_RevalidatesAndKeepsDeferredTiles()
        {
            var src        = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(releaseBudget: 1);
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 7.0), style: MinimalStyle());
                PumpUntilSettled(view);
                int loadedBefore = view.LoadedTileCount();
                Assert.GreaterOrEqual(loadedBefore, 4, "need enough tiles that a naive pan-back would free clearly > the transient.");

                // Pan out (budget 1): the recompute enqueues the departing cover; the drain frees ONE, leaving
                // the rest deferred in the queue.
                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 0.0 });
                view.LateUpdate();
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
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
