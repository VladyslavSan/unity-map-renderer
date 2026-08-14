// Epic A / A2 acceptance — the source-less per-covered-tile background processor's TileManager wiring
// (plan §F teeth 1, 2, 11, 12). Drives the real MapView/TileManager cover→build→consume loop with a
// background-only style (no fill/line/symbol layer, so NO SourceSpec is ever created — LoadTestStyle's
// injected source is unreachable by construction) and reads the GameObject backend's live Transform
// hierarchy (GameObjectTileRendererTests' pattern) so a single assertion set proves per-tile COUNT, the
// correct material SLOT, and a non-empty mesh together.

using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TileManagerBackgroundRegistrationTests
    {
        private static string BackgroundOnlyStyle(string colorHex = "#00ff00") => $@"{{
            ""version"": 8,
            ""layers"": [ {{ ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": {{ ""background-color"": ""{colorHex}"" }} }} ]
        }}";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static (GameObject go, MapView view) NewView(RenderBackend backend, int zoom)
        {
            var go   = new GameObject("BgRegistrationMapView");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.WithTestCamera();
            view.Config.Backend               = backend;
            view.Config.MaxConsumesPerTick    = 64;
            view.Config.MaxMeshBuildsPerTick  = 64;
            return (go, view);
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
        }

        // ── Tooth 1: per-covered-tile registration (primary semantic tooth) ──────────────────────────

        [Test]
        public void BackgroundStyle_RegistersOneQuadPerCoveredTile()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3); // z3: a multi-tile cover (non-vacuous N)
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                PumpUntilSettled(view);

                int loaded = view.LoadedTileCount();
                Assert.Greater(loaded, 1,
                    "non-vacuous: the cover must contain MORE than one tile, or 'one quad per tile' is untested.");
                Assert.IsTrue(view.AllTilesSettled());

                var gor = view.GameObjectRenderer();
                Assert.IsNotNull(gor, "GameObject renderer must be constructed when Backend == GameObject.");

                var keys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(keys);
                Assert.AreEqual(loaded, keys.Count);

                Material backgroundMaterial = view.Layers[0].Material; // the ONLY layer — DrawIndex 0
                Assert.IsNotNull(backgroundMaterial);

                foreach (var key in keys)
                {
                    Transform container = gor.Container(key.Tile);
                    Assert.IsNotNull(container, $"a per-tile container must exist for {key.Tile}.");
                    Assert.AreEqual(1, container.childCount,
                        $"exactly one draw item (the background quad) per covered tile ({key.Tile}).");

                    Transform quad = container.GetChild(0);
                    var mf = quad.GetComponent<MeshFilter>();
                    var mr = quad.GetComponent<MeshRenderer>();
                    Assert.IsNotNull(mf?.sharedMesh, $"the background quad at {key.Tile} must carry a mesh.");
                    Assert.Greater(mf.sharedMesh.vertexCount, 0,
                        $"the background quad's mesh at {key.Tile} must be non-empty.");
                    Assert.AreSame(backgroundMaterial, mr.sharedMaterial,
                        $"the background quad at {key.Tile} must draw with the background layer's material " +
                        "(its declared DrawIndex/material slot).");
                }

                Assert.AreEqual(loaded, gor.ContainerCount(), "exactly one container per covered tile — no extras.");
                Assert.AreEqual(loaded, gor.DrawItemCount(), "exactly one draw item per covered tile — no extras.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth 2: no fetch, no decode for background ───────────────────────────────────────────────

        [Test]
        public void BackgroundStyle_IssuesNoFetch_AndNeverDecodes()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            // A source that FAULTS if ever invoked — background must never route through the fetch path at
            // all (no SourceSpec is derived for a source-less style; RenderLayerFactory.TryGetFetchSource
            // excludes it structurally). Falsifies the rejected "empty-bytes through the real fetch/decode
            // path" design alternative (plan §B Q1).
            var neverCalled = TestDataSource.FromFetch(_ => throw new System.InvalidOperationException(
                "background must never fetch — the source-less pipeline has no Scheduler/Source at all."));
            try
            {
                view.LoadTestStyle(neverCalled, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled());
                Assert.Greater(view.LoadedTileCount(), 0, "background must still load and register.");
                Assert.AreEqual(0, neverCalled.FetchCount, "the injected source must NEVER be consulted.");
                Assert.AreEqual(0, view.InFlightCount(), "no fetch may ever be in flight for a background-only style.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth 11: source-less builds honor the build cap (HIGH 2) ─────────────────────────────────

        [Test]
        public void BackgroundCover_KicksAtMostBuildCapPerTick()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            view.Config.MaxMeshBuildsPerTick = 1; // force per-tick throttling
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));

                // The FIRST LateUpdate both (a) recomputes the cover — creating N pending source-less
                // records — and (b) runs PumpPending BEFORE that recompute in the SAME Tick call, so the
                // just-created records cannot be kicked this same frame (§E step 4: Tick's cover-request loop
                // only CREATES a pending record; PumpPending is the sole kicker).
                view.LateUpdate();
                int loaded = view.LoadedTileCount();
                Assert.Greater(loaded, 1, "non-vacuous: must cover more than one tile.");
                Assert.AreEqual(0, view.MeshBuildsKickedLastTick(),
                    "the cover-recompute tick creates pending records but kicks none this same frame.");

                int guard = 0;
                while (!view.AllTilesSettled() && guard++ < 10000)
                {
                    view.LateUpdate();
                    Assert.LessOrEqual(view.MeshBuildsKickedLastTick(), 1,
                        "at most MaxMeshBuildsPerTick source-less builds may be kicked in a single Tick — " +
                        "never the whole cover synchronously in one burst.");
                    Thread.Sleep(1);
                }
                Assert.IsTrue(view.AllTilesSettled(), "the throttled cover must still fully drain eventually.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Tooth 12: DrainMeshBuilds builds source-less records (HIGH a) ─────────────────────────────

        [Test]
        public void DrainMeshBuilds_BuildsSourcelessBackground()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                // One LateUpdate to run the cover recompute (creates records; PumpPending, running BEFORE
                // the recompute in the same call, sees none of them yet — so nothing is kicked either way).
                view.LateUpdate();
                Assert.Greater(view.LoadedTileCount(), 0, "the cover must have created source-less records.");
                Assert.IsFalse(view.AllTilesSettled(), "records must still be pending before the drain.");

                view.DrainMeshBuilds(); // uncapped — must build every un-kicked source-less record inline

                Assert.IsTrue(view.AllTilesSettled(), "DrainMeshBuilds must settle every source-less record.");
                var gor = view.GameObjectRenderer();
                Assert.Greater(gor.DrawItemCount(), 0,
                    "DrainMeshBuilds must have produced a non-empty background mesh + AddTileLayer for the " +
                    "un-kicked record — the un-fixed drain settles with ZERO draws (falls to the no-mesh else).");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
