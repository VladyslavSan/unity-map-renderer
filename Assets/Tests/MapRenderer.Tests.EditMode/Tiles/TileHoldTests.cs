using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Cameras;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using static MapRenderer.Tests.MapViewPump;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// A tile that leaves the cover stays shown until ready tiles cover its area, and is replaced in one flush: no frame has a hole
    /// where it was, and no frame draws it together with a relative. The rig reads what the Entities backend draws after every Update.
    /// </summary>
    [TestFixture]
    public class TileHoldTests : BaseTestFixture
    {
        private const string StyleJson = @"{
            ""version"": 8,
            ""name"": ""HoldTest"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#102030"" } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }";

        private const int BackgroundSlot = 0;
        private const int FillSlot       = 1;

        /// <summary>One MapView over a gated source, checked after every Update.</summary>
        private sealed class Rig
        {
            public readonly MapView View;
            public readonly DrawnTileWatcher Watcher;
            private double _now;

            public Rig(MapView view)
            {
                View    = view;
                Watcher = new DrawnTileWatcher(view, BackgroundSlot, FillSlot);
            }

            /// <summary>The source's fetch gates: a zoom or a tile is held back until the test opens it.</summary>
            public GatedTileSource Gates { get; } = GatedTileSource.Open();

            public TileManagerFacade Manager => new TileManagerFacade(View);

            public void Move(double zoom, double lon = 0.0)
                => View.Camera.Apply(new CameraPropertiesUpdate { Zoom = zoom, Longitude = lon, Latitude = 0.0 });

            public void Clock(double seconds)
            {
                _now = seconds;
                View.View.NowSecondsOverride = () => _now;
            }

            public double Now => _now;

            /// <summary>One Update, then the invariants.</summary>
            public void Tick()
            {
                View.LateUpdate();
                Thread.Sleep(1); // builds run on worker threads; a pump loop never depends on which tick one finishes
                Watcher.Check();
            }

            public bool Settled => View.LoadedTileCount() > 0 && View.AllTilesSettled();

            /// <summary>Ticks until <paramref name="done"/> is true, checking every tick.</summary>
            public void Pump(Func<bool> done, string what) => PumpUntil(View, done, what, Watcher.Check);

            public void PumpUntilSettled() => Pump(() => Settled, "the cover to settle");

            public HashSet<TileId> ShownBackground => Watcher.ShownBackground;

            public List<TileId> Cover => Watcher.Cover;

            public string Describe(List<TileId> cover) => Watcher.Describe(cover);

            public bool SawDeeperThanCover => Watcher.SawDeeperThanCover;

            public void AssertShowsExactlyTheCover() => Watcher.AssertShowsExactlyTheCover();
        }

        /// <summary>A thin reader over the manager's counters, so a scenario reads like prose.</summary>
        private readonly struct TileManagerFacade
        {
            private readonly MapView _view;
            public TileManagerFacade(MapView view) => _view = view;
            public int Held   => _view.TileManager.Telemetry.HeldTileCount;
            public int Bridge => _view.TileManager.Telemetry.BridgeTileCount;
        }

        private Rig NewRig(string projection)
        {
            var go   = Track(new GameObject("TileHold"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 3;
            view.Config.TileSelection.MaxZoom = 9;
            view.Config.TileSelection.ZoomLevelPreload = -1.0; // nothing prepared ahead: every level switch leans on the hold
            view.Config.Backend                = RenderBackend.Entities;
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxConcurrentTileLoads = 0; // uncapped, so admission never decides which tiles are ready
            view.Config.MaxReleasesPerTick     = 1;
            view.WithTestCamera(px: 2400, projection: projection == "globe" ? new SphericalProjection() : null);
            var rig = new Rig(view);
            view.LoadTestStyle(rig.Gates.Source, Cam(5.5), TestStyle.Document(StyleJson));
            rig.Clock(0.0);
            return rig;
        }

        private static CameraProperties Cam(double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0, Altitude = 0 }, zoom, 0, 0);

        /// <summary>
        /// Across zoom in, zoom out, a many-tile swap, a swing-back, an intermediate level that finishes first, and a pan that takes a
        /// held tile out of view: every Update keeps the view closed, nested-free, and within one show and one hide call.
        /// </summary>
        [TestCase("mercator")]
        [TestCase("globe")]
        public void Hold_KeepsTheViewClosedAndNestedFree_AcrossEveryZoomPanAndSwingBack(string projection)
        {
            Rig rig = NewRig(projection);
            MapView view = rig.View;
            try
            {
                rig.PumpUntilSettled();
                AssertSettled(rig);
                int firstLevelTiles = rig.ShownBackground.Count;

                // Zoom in with the finer level's fetch closed: the coarse tiles stay up, for as long as it takes (no time limit).
                rig.Gates.CloseZoom(6);
                rig.Move(6.5);
                for (int i = 0; i < 60; i++)
                {
                    rig.Clock(rig.Now + 1.0); // 60 simulated seconds of hold
                    rig.Tick();
                }

                Assert.Greater(rig.Manager.Held, 0, "the coarse tiles are held while the finer ones load");
                Assert.IsTrue(rig.SawDeeperThanCover, "the drawn tiles are the held coarse level, not the cover");
                AssertReportedToSymbols(view, rig.ShownBackground, strict: false);

                // One child of a complete family is still downloading: the family's coarse tile stays up, although its three other children
                // are ready, and every other family swaps.
                TileId slow = FindInteriorChild(rig);
                TileId slowParent = TileAncestry.Parent(slow);
                rig.Gates.CloseTile(slow);
                rig.Gates.OpenZoom(6);
                rig.Pump(() => view.TileManager.Telemetry.PendingTileCount == 1, "every finer tile but one to be ready");
                Assert.IsTrue(rig.ShownBackground.Contains(slowParent), "a coarse tile stays up while one child of its family is not ready");
                foreach (TileId shown in rig.ShownBackground)
                    Assert.IsFalse(TileAncestry.IsStrictAncestor(slowParent, shown), "and none of its ready children is drawn beside it");
                Assert.Less(rig.ShownBackground.Count, firstLevelTiles, "the families that are ready have swapped");

                rig.Gates.OpenTile(slow);
                rig.PumpUntilSettled();
                AssertSettled(rig);
                Assert.AreEqual(0, rig.Manager.Held, "a settled view holds nothing");

                // Zoom out: the coarser level comes back as a whole, and the finer tiles go in the same flush.
                view.Config.MaxConsumesPerTick = 64;
                rig.Move(5.5);
                rig.PumpUntilSettled();
                AssertSettled(rig);
                Assert.AreEqual(firstLevelTiles, rig.ShownBackground.Count);

                SwingBack(rig);
                IntermediateLevelStepsIn(rig);
                PanTakesAHeldTileOutOfView(rig);
                OutgoingTilesInThePreloadSetStayUp(rig);
            }
            finally
            {
                rig.Gates.OpenEverything();
                view.Teardown();
            }

            RecordOfARevealedAreaShowsWhileTheTileIsHeld(projection);
        }

        /// <summary>A tile revealed through its background alone, while its source is still loading, is held when the camera zooms in: the source
        /// record, created after the reveal and finished after the zoom, serves a revealed area and so keeps its Hold role and shows.</summary>
        private void RecordOfARevealedAreaShowsWhileTheTileIsHeld(string projection)
        {
            Rig rig = NewRig(projection);
            try
            {
                rig.View.Config.MaxConcurrentTileLoads = 1; // the background of a tile loads first, and its source record only after it shows
                rig.Gates.CloseZoom(5);
                rig.Pump(() => rig.ShownBackground.Count > 0, "the background to show while the source loads");
                for (int i = 0; i < 5; i++) rig.Tick(); // the source record is admitted, and stays in flight behind the gate
                Assert.AreEqual(0, rig.Watcher.ShownFill.Count, "precondition: no fill shows before its source finishes");

                rig.Gates.CloseZoom(6);
                rig.Move(6.5);
                for (int i = 0; i < 5; i++) rig.Tick();
                Assert.Greater(rig.Manager.Held, 0, "precondition: the revealed level-5 tiles are held");

                rig.Gates.OpenZoom(5);
                rig.Pump(() => HasLevel(rig.Watcher.ShownFill, 5), "the held tiles' source records to show");
            }
            finally
            {
                rig.Gates.OpenEverything();
                rig.View.Teardown();
            }
        }

        /// <summary>Assumes the camera was left settled at 5.5. Every finer tile is built but unregistered, then all register in one Update: every tile swaps in that one Update, and
        /// teardown stays at one record per Update. Returns how many records the teardown drained in that Update.</summary>
        private static int SwapEveryTileInOneUpdate(Rig rig)
        {
            MapView view = rig.View;
            view.Config.MaxConsumesPerTick = 0; // children build, but nothing registers
            rig.Move(6.5);
            rig.Pump(() =>
                {
                    TileTelemetrySnapshot t = view.CaptureTelemetry();
                    return t.PendingTileCount > 0 && t.ConsumeBacklog == t.PendingTileCount;
                },
                "every finer tile's write step to finish, unconsumed");
            int coarseTiles = rig.ShownBackground.Count;
            Assert.GreaterOrEqual(rig.Cover.Count, 25, "a many-tile swap, or 'in one Update' proves little");

            view.Config.MaxConsumesPerTick = int.MaxValue;
            rig.Tick();
            Assert.AreEqual(0, rig.Manager.Held, "every tile swapped in the one Update that registered the finer level");
            AssertSettled(rig);
            Assert.LessOrEqual(view.TilesReleasedLastTick(), 1, "teardown stays on its own budget");
            Assert.GreaterOrEqual(view.ReleaseQueueDepth(), coarseTiles, "the swapped-out records wait, hidden, for their teardown");
            return view.TilesReleasedLastTick();
        }

        /// <summary>Assumes the camera was left settled at 5.5. A zoom back before the teardown drains shows the coarse tiles again without registering them again, and so does a
        /// return after a pan that hid the finer tiles: then no tile above or below is shown, and only the cover tile's own record remains.</summary>
        private static void SwingBack(Rig rig)
        {
            MapView view = rig.View;
            int drained  = SwapEveryTileInOneUpdate(rig);
            int hitsBefore = view.TileManager.PreparedCacheHits;
            rig.Move(5.5);
            rig.PumpUntilSettled();
            AssertSettled(rig);
            Assert.LessOrEqual(view.TileManager.PreparedCacheHits - hitsBefore, drained + 2,
                "a record that was still waiting for teardown is shown again, not registered again");

            drained    = SwapEveryTileInOneUpdate(rig);
            hitsBefore = view.TileManager.PreparedCacheHits;
            rig.Move(6.5, lon: 60.0); // the finer tiles leave the view, and are hidden
            rig.Tick();
            Assert.AreEqual(0, rig.ShownBackground.Count, "nothing is drawn where the view left");
            rig.Move(5.5);
            rig.PumpUntilSettled();
            AssertSettled(rig);
            Assert.LessOrEqual(view.TileManager.PreparedCacheHits - hitsBefore, drained + 3,
                "the coarse tiles that were still waiting are shown by their own records");
        }

        /// <summary>Assumes the camera was left settled at 5.5. A level the view never drew is still loading when the camera passes it: its in-flight records are kept as Bridges,
        /// the level finishes first, steps in while the target's fetch is closed, and the target replaces it later.</summary>
        private static void IntermediateLevelStepsIn(Rig rig)
        {
            MapView view = rig.View;
            view.Config.MaxConsumesPerTick = 0;
            rig.Gates.CloseZoom(8);
            rig.Move(7.5);
            for (int i = 0; i < 5; i++) rig.Tick();
            rig.Move(8.5); // the level-7 records are in flight and leave the cover
            rig.Tick();
            Assert.Greater(rig.Manager.Bridge, 0, "an in-flight record between the held tile and the cover is kept, not cancelled. " + rig.Describe(rig.Cover));

            view.Config.MaxConsumesPerTick = 64;
            rig.Pump(() => HasLevel(rig.ShownBackground, 7), "the intermediate level to step in");
            Assert.Greater(rig.Manager.Held, 0, "the intermediate level is now the held one");

            rig.Gates.OpenZoom(8);
            rig.PumpUntilSettled();
            AssertSettled(rig);
            Assert.AreEqual(0, rig.Manager.Held);
        }

        /// <summary>Assumes the camera was left settled at 8.5. A held tile whose finer tiles are still pending leaves the view: it is hidden at once, however long it was held.</summary>
        private static void PanTakesAHeldTileOutOfView(Rig rig)
        {
            MapView view = rig.View;
            view.Config.MaxConsumesPerTick = 0;
            rig.Move(9.5);
            for (int i = 0; i < 60; i++)
            {
                rig.Clock(rig.Now + 1.0);
                rig.Tick();
            }

            Assert.Greater(rig.ShownBackground.Count, 0, "the held tiles are what the view draws");
            Assert.Greater(rig.Manager.Held, 0);

            rig.Move(9.5, lon: 60.0); // a pan that leaves every old tile behind
            rig.Tick();
            Assert.AreEqual(0, rig.Manager.Held, "a tile whose area left the view is not held");
            Assert.AreEqual(0, rig.ShownBackground.Count, "and is not drawn");

            view.Config.MaxConsumesPerTick = 64;
            rig.PumpUntilSettled();
            AssertSettled(rig);
        }

        /// <summary>A cover tile whose three siblings are also in the cover, so its family is complete.</summary>
        private static TileId FindInteriorChild(Rig rig)
        {
            var cover = new HashSet<TileId>(rig.Cover);
            foreach (TileId tile in cover)
            {
                TileId parent = TileAncestry.Parent(tile);
                int siblings = 0;
                foreach (TileId other in cover)
                    if (other.Z == tile.Z && TileAncestry.Parent(other).Equals(parent)) siblings++;
                if (siblings == 4) return tile;
            }

            Assert.Fail("no complete family in the cover");
            return default;
        }

        /// <summary>A shown tile that left the cover for the preload set is held, not hidden as a prepared tile: a fast zoom out, with the
        /// coarser level not prepared, leaves no hole, and the held tiles stay reported to the symbol subsystem.</summary>
        private static void OutgoingTilesInThePreloadSetStayUp(Rig rig)
        {
            MapView view = rig.View;
            view.Config.TileSelection.ZoomLevelPreload = 0.3;
            view.Config.TileSelection.MaxConcurrentPrepareLoads = 64;
            rig.Gates.CloseZoom(8);
            rig.Move(8.85, lon: 60.0); // level 8 is new here, and the level-9 tiles are its preload set
            for (int i = 0; i < 10; i++) rig.Tick();
            Assert.IsTrue(HasLevel(rig.ShownBackground, 9), "the level-9 tiles stay drawn while level 8 loads");
            Assert.Greater(rig.Manager.Held, 0, "they are held, not prepared");
            AssertReportedToSymbols(view, rig.ShownBackground, strict: false);

            rig.Gates.OpenZoom(8);
            rig.PumpUntilSettled();
            AssertSettled(rig);
        }

        /// <summary>The drawn tiles are exactly the cover, and the symbol subsystem is told so.</summary>
        private static void AssertSettled(Rig rig)
        {
            rig.AssertShowsExactlyTheCover();
            AssertReportedToSymbols(rig.View, rig.ShownBackground, strict: true);
        }

        /// <summary>A record is shown to the symbol subsystem only when its tile is drawn, and a drawn tile has a shown record. Strict: a record is shown
        /// exactly when its tile is drawn. Mid-load a tile can be drawn by its background alone while a source's own groups are not revealed yet, so the
        /// non-strict form drops the "exactly".</summary>
        private static void AssertReportedToSymbols(MapView view, HashSet<TileId> drawn, bool strict)
        {
            var keys = new List<LoadedTileKey>();
            view.TileManager.CollectLoadedTileKeys(keys);
            foreach (LoadedTileKey key in keys)
            {
                if (strict) Assert.AreEqual(drawn.Contains(key.Tile), key.Shown, $"record of tile {key.Tile} is reported shown iff its tile is drawn");
                else if (key.Shown) Assert.IsTrue(drawn.Contains(key.Tile), $"record of tile {key.Tile} is reported shown only if its tile is drawn");
            }

            foreach (TileId tile in drawn)
                Assert.IsTrue(keys.Exists(k => k.Tile.Equals(tile) && k.Shown), $"drawn tile {tile} is reported shown");
        }

        private static bool HasLevel(HashSet<TileId> tiles, int zoom)
        {
            foreach (TileId t in tiles)
                if (t.Z == zoom) return true;
            return false;
        }
    }
}
