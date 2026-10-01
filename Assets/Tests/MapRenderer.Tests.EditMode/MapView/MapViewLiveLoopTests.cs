// Unity EditMode only — uses MonoBehaviour, NativeArray jobs, the live MapView loop.
// NOT included in Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Unity.Style;
using Fill = MapRenderer.Unity.Style.Fill;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Cameras;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.Jobs.Mvt;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile.Processing;
using static MapRenderer.Tests.MapViewPump;
namespace MapRenderer.Tests.MapViews
{
    /// <summary>
    /// Live loop tests for <see cref="MapView"/> with styled fill rendering: view state drives tile selection
    /// and eviction; a 1-fill-layer style gives the same vertex count as StyledFillTileBuilder called directly;
    /// and the steady-state tick allocates no GC memory on a pan, a static frame, or a heading change.
    /// </summary>
    [TestFixture]
    public class MapViewLiveLoopTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>
        /// Minimal 1-fill-layer style for the live loop tests: a single fill layer over the
        /// "countries" MVT source-layer with a constant red fill color (Constant expression kind).
        /// </summary>
        private static StyleDocument MinimalStyle() => TestStyle.Document(@"{
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


        private static StyleDocument BackgroundAndFillStyle() => TestStyle.Document(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#102030"" } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        private static StyleDocument FillOnlyStyle() => TestStyle.Document(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>Draw commands the BRG backend emits for the camera view, per material slot.</summary>
        private static int EmittedFor(MapView view, int materialIndex)
        {
            var emit = new List<int>();
            view.BrgRenderer().ComputeEmitOrder(emit, UnityEngine.Rendering.BatchCullingViewType.Camera);
            int count = 0;
            foreach (int sortedIndex in emit)
                if (view.BrgRenderer().MaterialIndexAtSorted(sortedIndex) == materialIndex) count++;
            return count;
        }

        /// <summary>
        /// A new tile's layers appear group by group, in the configured order. Arms: (1) with the fetch closed the background draws and the fill does
        /// not, and one group draws nothing until the fill is ready; (2) a group appears when its last payload is consumed, in one flush; (3) a lagging
        /// second source holds A's fill and labels, and an absent, undecodable or faulting one never does; (4) a rebaking shared record stays drawn
        /// across a pan; (5) a group list changed at runtime keeps what is shown, shows a layer registered hidden before the change, and orders new
        /// tiles by the new list, also after an in-place edit of a group's kinds.
        /// </summary>
        [Test]
        public void VisibilityGroups_ApplyToNewTilesAcrossSourcesAndRestyle()
        {
            foreach (bool oneGroup in new[] { false, true })
            {
                var gated = GatedTileSource.Closed();
                var src   = gated.Source;
                var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
                view.Config.Backend = RenderBackend.Brg;
                view.Config.TileSelection.MinZoom = 5;
                view.Config.TileSelection.MaxZoom = 5;
                view.Config.TileSelection.ZoomLevelPreload = -1.0;
                view.WithTestCamera(256);
                view.Config.MaxConsumesPerTick    = 64;
                view.Config.MaxMeshBuildsPerTick  = 64;
                view.Config.MaxConcurrentTileLoads = oneGroup ? 1 : 0; // one group: the background of a tile admitted before its source record must wait too
                if (oneGroup)
                    view.Config.VisibilityGroups = new[]
                    {
                        new VisibilityGroup { Kinds = new[] { StyleLayerType.Background, StyleLayerType.Fill } },
                    };

                try
                {
                    view.LoadTestStyle(src, Cam(0, 0, 5.0), style: BackgroundAndFillStyle());
                    for (int i = 0; i < 40; i++)
                    {
                        view.LateUpdate(); // a drain would block on the closed fetch
                        Thread.Sleep(1);
                    }

                    Assert.AreEqual(oneGroup ? 0 : 4, EmittedFor(view, 0),
                        $"oneGroup={oneGroup}: the background is drawn while the fill loads, unless one group holds both.");
                    Assert.AreEqual(0, EmittedFor(view, 1), $"oneGroup={oneGroup}: the fill is not drawn before its payload is consumed.");

                    gated.OpenAll();
                    view.Config.MaxConcurrentTileLoads = 12;
                    PumpUntilSettled(view);
                    view.LateUpdate();
                    Assert.AreEqual(4, EmittedFor(view, 0), $"oneGroup={oneGroup}: the background is drawn once the tile is ready.");
                    Assert.AreEqual(4, EmittedFor(view, 1), $"oneGroup={oneGroup}: the fill is drawn once the tile is ready.");
                }
                finally
                {
                    gated.OpenAll();
                    view.Teardown();
                }
            }

            AssertAGroupShowsWhileALaterGroupOfTheSameTileIsStillConsuming(withLastExtrusion: true);
            AssertAGroupShowsWhileALaterGroupOfTheSameTileIsStillConsuming(withLastExtrusion: false);
            foreach (SecondSourceBehaviour behaviour in Enum.GetValues(typeof(SecondSourceBehaviour)))
                AssertASecondSourceNeverBlocksTheFirstSourcesGroups(behaviour);
            AssertARebakingSharedRecordStaysDrawnAcrossAPan();
            AssertARuntimeChangeOfTheGroupsFollowsOnNewTiles();
            AssertAChangeShowsALayerThatRegisteredHiddenBeforeIt();
            AssertAnInPlaceEditOfAGroupFollowsOnNewTiles();
        }

        /// <summary>A backend that only counts what a flush sends it.</summary>
        private sealed class CountingBackend : ITileRenderBackend
        {
            public int HandlesShownLastFlush;
            public int HandlesHiddenLastFlush;

            public void SetItemsVisible(IReadOnlyList<int> handles, bool visible)
            {
                if (visible) HandlesShownLastFlush = handles.Count;
                else HandlesHiddenLastFlush = handles.Count;
            }

            public int  AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId) => 0;
            public void RemoveItems(ReadOnlySpan<int> handles) { }
            public void Rebuild(in SceneFrame frame) { }
            public void SetLayerVisible(int slot, bool visible) { }
            public void SetLayerMaterials(IReadOnlyList<Material> layerMaterials, IReadOnlyList<UnityEngine.Rendering.ShadowCastingMode> layerShadowModes) { }
            public void Dispose() { }
        }

        private const string BackgroundAndTwoFillsStyleJson = @"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#102030"" } },
                { ""id"": ""fill-1"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""fill-2"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 50, 50, 200, 1] } }
            ]
        }";

        /// <summary>
        /// One fill layer of a tile is registered hidden, because its group still waits for the second fill. The groups then change so the fill group
        /// comes first. The group numbering moves, and the layer registered hidden still shows once its group is ready: the reveal state was remapped.
        /// </summary>
        private void AssertAChangeShowsALayerThatRegisteredHiddenBeforeIt()
        {
            var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.TileSelection.ZoomLevelPreload = -1.0;
            view.WithTestCamera(256);
            view.Config.MaxConsumesPerTick   = 1;
            view.Config.MaxMeshBuildsPerTick = 64;
            try
            {
                view.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(0, 0, 0.0), style: TestStyle.Document(BackgroundAndTwoFillsStyleJson));
                for (int i = 0; i < 2500 && view.BrgRenderer().DrawItemCount() < 2; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(2, view.BrgRenderer().DrawItemCount(), "precondition: two layers of the tile are registered");
                Assert.AreEqual(1, view.BrgRenderer().HiddenDrawItemCount(), "precondition: the background shows and the fill waits for the second fill");

                view.Config.VisibilityGroups = new[]
                {
                    new VisibilityGroup { Kinds = new[] { StyleLayerType.Fill, StyleLayerType.Line } },
                    new VisibilityGroup { Kinds = new[] { StyleLayerType.Background } },
                    new VisibilityGroup { Kinds = new[] { StyleLayerType.FillExtrusion } },
                };
                view.Config.MaxConsumesPerTick = 64;
                PumpUntilSettled(view);
                view.LateUpdate();
                Assert.AreEqual(3, view.BrgRenderer().DrawItemCount(), "all three layers are registered");
                Assert.AreEqual(0, view.BrgRenderer().HiddenDrawItemCount(), "every layer of the tile is shown, the one registered before the change too");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>
        /// An Inspector edit changes the kinds of a group in place, with no new list. The fill leaves the first group, so a new tile's background
        /// no longer waits for it.
        /// </summary>
        private void AssertAnInPlaceEditOfAGroupFollowsOnNewTiles()
        {
            var gated = GatedTileSource.Closed();
            var src   = gated.Source;
            var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.TileSelection.ZoomLevelPreload = -1.0;
            view.WithTestCamera(256);
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.VisibilityGroups = new[]
            {
                new VisibilityGroup { Kinds = new[] { StyleLayerType.Background, StyleLayerType.Fill } },
                new VisibilityGroup { Kinds = new[] { StyleLayerType.FillExtrusion } },
            };
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: BackgroundAndFillStyle());
                for (int i = 0; i < 40; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(0, EmittedFor(view, 0), "precondition: the first group holds the fill, so the background waits");

                view.Config.VisibilityGroups[0].Kinds[1] = StyleLayerType.Background; // in place: the fill is in no group now
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 90.0 });
                for (int i = 0; i < 40; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(4, EmittedFor(view, 0), "a new tile's background shows first, with the fill still loading");
            }
            finally
            {
                gated.OpenAll();
                view.Teardown();
            }
        }

        /// <summary>
        /// The group list changes at runtime, with no restyle, while a tile's background shows and its fill still loads. The shown background
        /// stays drawn. A tile that appears afterwards follows the new order: one group holds its background and fill, so the background
        /// waits for the fill.
        /// </summary>
        private void AssertARuntimeChangeOfTheGroupsFollowsOnNewTiles()
        {
            var gated = GatedTileSource.Closed();
            var src   = gated.Source;
            var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.TileSelection.ZoomLevelPreload = -1.0;
            view.WithTestCamera(256);
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: BackgroundAndFillStyle());
                for (int i = 0; i < 40; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(4, EmittedFor(view, 0), "precondition: the background shows while the fill loads");

                view.Config.VisibilityGroups = new[]
                {
                    new VisibilityGroup { Kinds = new[] { StyleLayerType.Background, StyleLayerType.Fill } },
                };
                for (int i = 0; i < 5; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(4, EmittedFor(view, 0), "the background that already shows stays drawn");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 90.0 });
                for (int i = 0; i < 40; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(0, EmittedFor(view, 0), "a new tile's background waits for its fill: one group holds both");

                gated.OpenAll();
                PumpUntilSettled(view);
                view.LateUpdate();
                Assert.AreEqual(4, EmittedFor(view, 0), "the new tiles' background is drawn once the tiles are ready");
                Assert.AreEqual(4, EmittedFor(view, 1), "the new tiles' fill is drawn once the tiles are ready");
            }
            finally
            {
                gated.OpenAll();
                view.Teardown();
            }
        }

        /// <summary>
        /// Deep overzoom: the source's one fill record serves four cover tiles. Its rebake is held before its consume, so it draws its previous
        /// geometry. A pan inside the same source tile conceals the old cover tiles and reveals the new ones in one Update, and the previous
        /// fill stays drawn: its handles keep their group, so the carried reveal shows them.
        /// </summary>
        private void AssertARebakingSharedRecordStaysDrawnAcrossAPan()
        {
            var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 14;
            view.Config.TileSelection.MaxZoom = 18;
            view.Config.TileSelection.ZoomLevelPreload = -1.0;
            view.WithTestCamera(256);
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            var ancestor = new TileId { Z = 14, X = 8192, Y = 8192 };
            double2 centre = ancestor.ToLonLat(0.5, 0.5, 1.0);
            try
            {
                view.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(centre.x, centre.y, 17.5),
                    style: BackgroundAndFillStyle(), sourceMaxZoom: 14);
                PumpUntilSettled(view);
                view.LateUpdate();
                Assert.AreEqual(1, EmittedFor(view, 1), "precondition: the z14 fill draws once");

                view.Config.MaxConsumesPerTick = 0; // the rebake builds but never registers
                view.Config.FillTileBufferClip = 64.0;
                for (int i = 0; i < 5; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.IsFalse(view.TryGetBuiltTile(ancestor), "precondition: the fill record is rebaking");
                Assert.AreEqual(1, EmittedFor(view, 1), "precondition: it draws its previous geometry");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = centre.x + 0.0046 }); // inside the same z14 tile
                for (int i = 0; i < 3; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(1, EmittedFor(view, 1), "the previous fill stays drawn across the pan");
            }
            finally
            {
                view.Teardown();
            }
        }

        private enum SecondSourceBehaviour { Lags, Absent, Corrupt, FaultsOnce }

        /// <summary>
        /// Two sources over one tile: the background (slot 0), the fills of source A (slot 1) and of source B (slot 2) share group two. A lagging B holds
        /// group two back for both. An absent tile or an undecodable body in B is ready at once, so A's fill shows and B's has nothing. A network fault in
        /// B is not pending: A's fill shows while B waits to retry, and B's fill shows in the Update that consumes its retry.
        /// </summary>
        private void AssertASecondSourceNeverBlocksTheFirstSourcesGroups(SecondSourceBehaviour behaviour)
        {
            var gate       = new UniTaskCompletionSource<bool>();
            var failedOnce = new System.Collections.Concurrent.ConcurrentDictionary<TileId, bool>();
            var sourceA    = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var sourceB    = TestDataSource.FromFetch(async tile =>
            {
                await UniTask.SwitchToThreadPool();
                switch (behaviour)
                {
                    case SecondSourceBehaviour.Lags:
                        await gate.Task;
                        break;
                    case SecondSourceBehaviour.Absent:
                        return TileResponse.Absent(TileEncoding.Mvt);
                    case SecondSourceBehaviour.Corrupt:
                        return new TileResponse(new byte[] { 0x1A, 0x64 }, TileEncoding.Mvt);
                    case SecondSourceBehaviour.FaultsOnce:
                        if (failedOnce.TryAdd(tile, true)) throw new InvalidOperationException("scripted network failure");
                        break;
                }

                return new TileResponse(SampleTileFixture.Bytes(), TileEncoding.Mvt);
            });

            var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.TileSelection.ZoomLevelPreload = -1.0;
            view.WithTestCamera(256);
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            double now = 100.0;
            view.View.NowSecondsOverride = () => now;
            try
            {
                var style = TestStyle.Document(@"{
                    ""version"": 8, ""name"": ""Test"",
                    ""sources"": {
                        ""a"": { ""type"": ""vector"", ""tiles"": [""https://example.com/a/{z}/{x}/{y}.pbf""] },
                        ""b"": { ""type"": ""vector"", ""tiles"": [""https://example.com/b/{z}/{x}/{y}.pbf""] }
                    },
                    ""layers"": [
                        { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#102030"" } },
                        { ""id"": ""fill-a"", ""type"": ""fill"", ""source"": ""a"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                        { ""id"": ""fill-b"", ""type"": ""fill"", ""source"": ""b"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 50, 50, 200, 1] } }
                    ]
                }");
                var mv = view.View;
                mv.Camera.SetProperties(Cam(0, 0, 5.0));
                mv.Camera.SyncToCamera();
                mv.Layers.Build(style, mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);
                mv.ApplyVisibilityGroups();
                mv.TileManager.SetSources(new List<TileManager.SourceSpec>
                {
                    new TileManager.SourceSpec("a", default, 0, int.MaxValue, () => new MvtTileFeatureSource(sourceA, new InlineWorkScheduler())),
                    new TileManager.SourceSpec("b", default, 0, int.MaxValue, () => new MvtTileFeatureSource(sourceB, new InlineWorkScheduler())),
                }, view.Config.Backend);

                void Pump(Func<bool> done, string what) => PumpUntil(view, done, $"{behaviour}: {what}");

                bool LabelsOfSourceAreShown(string sourceId)
                {
                    var keys = new List<LoadedTileKey>();
                    view.TileManager.CollectLoadedTileKeys(keys);
                    return keys.Exists(k => k.SourceId == sourceId && k.Shown);
                }

                Pump(() => EmittedFor(view, 0) == 4, "the background of the four tiles");
                bool blocks = behaviour == SecondSourceBehaviour.Lags;
                if (blocks)
                {
                    for (int i = 0; i < 40; i++) { view.LateUpdate(); Thread.Sleep(1); }
                    Assert.AreEqual(0, EmittedFor(view, 1), $"{behaviour}: A's fill waits while B still loads.");
                    Assert.IsFalse(LabelsOfSourceAreShown("a"), $"{behaviour}: A's labels wait for the first group A fills, though the tile is shown.");
                    gate.TrySetResult(true);
                }
                else
                {
                    Pump(() => EmittedFor(view, 1) == 4, "A's fill, with B absent, undecodable or waiting to retry");
                }

                if (behaviour == SecondSourceBehaviour.FaultsOnce)
                {
                    for (int i = 0; i < 10; i++) { view.LateUpdate(); Thread.Sleep(1); }
                    Assert.AreEqual(0, EmittedFor(view, 2), $"{behaviour}: B has nothing to show before its retry.");
                    now = 200.0;
                }

                bool bShows = behaviour == SecondSourceBehaviour.Lags || behaviour == SecondSourceBehaviour.FaultsOnce;
                Pump(() => EmittedFor(view, 1) == 4 && EmittedFor(view, 2) == (bShows ? 4 : 0), "the settled fills");
                Assert.IsTrue(LabelsOfSourceAreShown("a"), $"{behaviour}: A's labels are shown once its fill group is revealed.");
                for (int i = 0; i < 5; i++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(bShows ? 4 : 0, EmittedFor(view, 2), $"{behaviour}: B's fill ends {(bShows ? "shown" : "empty")}.");
            }
            finally
            {
                gate.TrySetResult(true);
                view.Teardown();
            }
        }

        /// <summary>
        /// One mesh is consumed per Update, in the order extrusion, fill, fill, extrusion. The two fills (one group) appear in the same flush, once
        /// both are consumed and while the last extrusion still waits. An extrusion consumed early stays hidden until the fills show. Without the
        /// last extrusion the extrusion group is ready while the fills are still pending, so only the group order holds it back.
        /// </summary>
        private void AssertAGroupShowsWhileALaterGroupOfTheSameTileIsStillConsuming(bool withLastExtrusion)
        {
            var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.TileSelection.ZoomLevelPreload = -1.0;
            view.WithTestCamera(256);
            view.Config.MaxConsumesPerTick   = 1;
            view.Config.MaxMeshBuildsPerTick = 64;
            try
            {
                view.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(0, 0, 5.0), style: FillAndExtrusionStyle(withLastExtrusion));
                bool fillBeforeTileIsBuilt = false;
                var  ids = new List<TileId>();
                for (int i = 0; i < 2500 && !(i > 2 && view.AllTilesSettled()); i++)
                {
                    view.LateUpdate(); // a drain would consume every mesh at once
                    Thread.Sleep(1);
                    int fills = EmittedFor(view, 1);
                    Assert.AreEqual(fills, EmittedFor(view, 2), $"update {i}: both fills of a group appear in the same flush.");
                    Assert.LessOrEqual(EmittedFor(view, 0), fills, $"update {i}: an extrusion consumed first stays hidden until the fills show.");
                    if (withLastExtrusion)
                        Assert.LessOrEqual(EmittedFor(view, 3), fills, $"update {i}: the last extrusion never appears before the fills.");
                    ids.Clear();
                    view.CollectLoadedTileIds(ids);
                    int built = ids.FindAll(view.TryGetBuiltTile).Count;
                    if (fills > built) fillBeforeTileIsBuilt = true;
                }

                view.LateUpdate(); // the backend sorts at the start of a frame, so one more reads what the last Update showed
                if (withLastExtrusion)
                {
                    Assert.IsTrue(fillBeforeTileIsBuilt, "a fill was drawn while its tile's extrusion was still unconsumed.");
                    Assert.AreEqual(4, EmittedFor(view, 3), "settled: every last extrusion is drawn.");
                }

                Assert.AreEqual(4, EmittedFor(view, 0), "settled: every first extrusion is drawn.");
            }
            finally
            {
                view.Teardown();
            }
        }

        private static StyleDocument FillAndExtrusionStyle(bool withLastExtrusion) => TestStyle.Document(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""countries-extrusion-1"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-extrusion-color"": [""rgba"", 50, 50, 200, 1], ""fill-extrusion-height"": 10 } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""countries-fill-2"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": [""rgba"", 50, 200, 50, 1] } }" + (withLastExtrusion ? @",
                { ""id"": ""countries-extrusion-2"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-extrusion-color"": [""rgba"", 50, 200, 200, 1], ""fill-extrusion-height"": 20 } }" : "") + @"
            ]
        }");

        /// <summary>The fill slot's mesh for <paramref name="tile"/>: the tile's meshes hold one per slot, told apart by material index.</summary>
        private static Mesh FillMeshOf(MapView view, TileId tile)
        {
            Mesh[] meshes  = view.GetTileMeshes(tile);
            int[]  indices = view.GetTileMaterialIndices(tile);
            for (int i = 0; meshes != null && i < meshes.Length; i++)
                if (indices[i] == 1) return meshes[i];
            return null;
        }

        /// <summary>
        /// Above a source's <c>maxzoom</c> its maxzoom tile serves, and the background, which has no source, stays at the
        /// cover's zoom. At a point where one z14 tile holds the whole view, z15 covers it with four tiles: the source keeps
        /// ONE record (one fetch, though four cover tiles ask for it) and draws it once; the background holds four z15
        /// records. Starting at 15.1 and crossing to 14.9 and back fetches nothing more and never replaces the mesh.
        /// </summary>
        [Test]
        public void Overzoom_ServesFromTheMaxZoomAncestor_AcrossTheMaxZoomBoundary()
        {
            MapView NewView(double preload, RenderBackend backend = RenderBackend.Brg, int minZoom = 14, int maxZoom = 15)
            {
                var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
                view.Config.Backend = backend;
                view.Config.TileSelection.MinZoom = minZoom;
                view.Config.TileSelection.MaxZoom = maxZoom;
                view.Config.TileSelection.ZoomLevelPreload = preload;
                view.WithTestCamera(256);
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;
                return view;
            }

            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var view = NewView(-1.0); // no prepared tiles: the records are exactly the cover's

            var ancestor = new TileId { Z = 14, X = 8192, Y = 8192 };
            double2 centre = ancestor.ToLonLat(0.5, 0.5, 1.0); // the corner of its four z15 children
            try
            {
                view.LoadTestStyle(src, Cam(centre.x, centre.y, 15.1), style: BackgroundAndFillStyle(), sourceMaxZoom: 14);

                // One load slot: of the five distinct keys (four z15 background tiles, one z14 source ancestor) one is
                // admitted and four wait, so the source ancestor was queued once, not once per cover tile that uses it.
                view.Config.MaxConcurrentTileLoads = 1;
                view.LateUpdate();
                Assert.AreEqual(4, view.DesiredCount(), "five distinct keys, one admitted.");
                view.Config.MaxConcurrentTileLoads = 12;

                PumpUntilSettled(view);
                view.LateUpdate();
                Mesh before = FillMeshOf(view, ancestor);
                Assert.IsNotNull(before, "precondition: the z14 ancestor is built, with a fill mesh.");

                var ids = new List<TileId>();
                void Check(double zoom)
                {
                    ids.Clear();
                    view.CollectLoadedTileIds(ids);
                    int z14 = ids.FindAll(t => t.Z == 14).Count;
                    int z15 = ids.FindAll(t => t.Z == 15).Count;
                    bool overzoom = zoom > 15.0;
                    Assert.AreEqual(overzoom ? 4 : 0, z15, $"zoom {zoom}: only the background has z15 records.");
                    Assert.AreEqual(overzoom ? 1 : 2, z14,
                        $"zoom {zoom}: the source's z14 record, plus the background's own z14 record below z15.");
                    Assert.AreEqual(1, src.FetchCount, $"zoom {zoom}: one fetch serves every cover tile of the z14 ancestor.");
                    Assert.AreSame(before, FillMeshOf(view, ancestor), $"zoom {zoom}: the source record is never rebuilt.");
                    Assert.AreEqual(1, EmittedFor(view, 1), $"zoom {zoom}: the ancestor's fill is drawn once, not per z15 tile.");
                    Assert.AreEqual(overzoom ? 4 : 1, EmittedFor(view, 0), $"zoom {zoom}: the background draws the cover's own tiles.");
                }

                Check(15.1);
                foreach (double zoom in new[] { 14.9, 15.1 })
                {
                    view.Camera.Apply(new CameraPropertiesUpdate { Zoom = zoom });
                    PumpUntilSettled(view);
                    view.LateUpdate();
                    Check(zoom);

                    view.LateUpdate();
                    view.LateUpdate();
                    Assert.AreEqual(0, view.VisibilityBatchesLastTick(), $"zoom {zoom}: a settled view shows and hides nothing, so the serving record is not swapped every frame.");
                }

                // Preload at its default, from a cold start just below the level switch: the z15 children are prepared, but the
                // source's ancestor already serves the cover, so only the background prepares them and the source fetches once.
                var   preloadSrc  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                MapView preloadView = NewView(0.05);
                try
                {
                    preloadView.LoadTestStyle(preloadSrc, Cam(centre.x, centre.y, 14.96), style: BackgroundAndFillStyle(), sourceMaxZoom: 14);
                    PumpUntilSettled(preloadView);
                    preloadView.LateUpdate();
                    Assert.AreEqual(1, preloadSrc.FetchCount, "the source's ancestor serves the cover, so preparing its children fetches nothing.");
                    Assert.AreEqual(1, EmittedFor(preloadView, 1), "the ancestor's fill stays a drawn cover record, not a hidden prepared one.");
                    Assert.AreEqual(4, preloadView.CaptureTelemetry().PreparingTileCount, "the four z15 children are prepared, by the background only.");
                }
                finally
                {
                    preloadView.Teardown();
                }

                // The source's ancestor is admitted and finishes AFTER a z15 background tile is shown (one load slot, camera on that tile): its
                // fill shows once it registers, although it has no record marked shown yet.
                var gated = GatedTileSource.Open();
                gated.CloseZoom(14);
                var gatedSrc = gated.Source;
                MapView gatedView = NewView(-1.0);
                gatedView.Config.MaxConcurrentTileLoads = 1;
                try
                {
                    var nearest = new TileId { Z = 15, X = ancestor.X * 2 + 1, Y = ancestor.Y * 2 };
                    double2 nearestCentre = nearest.ToLonLat(0.5, 0.5, 1.0);
                    gatedView.LoadTestStyle(gatedSrc, Cam(nearestCentre.x, nearestCentre.y, 15.1), style: BackgroundAndFillStyle(), sourceMaxZoom: 14);
                    for (var settle = SettleTimeout.Start(); settle.Running && EmittedFor(gatedView, 0) < 1;)
                    {
                        gatedView.LateUpdate();
                        Thread.Sleep(1);
                    }

                    Assert.AreEqual(1, EmittedFor(gatedView, 0), "precondition: one z15 background tile is shown while the source's ancestor loads.");
                    Assert.AreEqual(0, EmittedFor(gatedView, 1), "precondition: the source's ancestor has not registered.");
                    gated.OpenZoom(14);
                    for (var settle = SettleTimeout.Start(); settle.Running && !gatedView.TryGetBuiltTile(ancestor);)
                    {
                        gatedView.LateUpdate();
                        Thread.Sleep(1);
                    }

                    Assert.IsTrue(gatedView.TryGetBuiltTile(ancestor), "the source's ancestor finished loading.");
                    Assert.AreEqual(1, EmittedFor(gatedView, 1), "the ancestor's fill shows once it registers, though no tile of its own is shown.");
                }
                finally
                {
                    gated.OpenZoom(14);
                    gatedView.Teardown();
                }
            }
            finally
            {
                view.Teardown();
            }

            // Every Update, with the Entities backend read back: the watcher keeps the source's z14 fill drawn across 14.9 -> 15.1 -> 14.9,
            // while the background swaps its tiles with no hole and no overlap.
            MapView swapView = NewView(-1.0, RenderBackend.Entities);
            try
            {
                swapView.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(centre.x, centre.y, 14.9),
                    style: BackgroundAndFillStyle(), sourceMaxZoom: 14);
                var watcher = new DrawnTileWatcher(swapView, backgroundSlot: 0, fillSlot: 1);
                void Tick()
                {
                    // Only Update resets VisibilityBatchesLastTick, so the watcher's "<= 2" sees this LateUpdate and the drain together.
                    swapView.LateUpdate();
                    swapView.DrainMeshBuilds();
                    watcher.Check();
                }

                void TickUntilSettled()
                {
                    for (int i = 0; i < 2500 && !(i > 2 && swapView.LoadedTileCount() > 0 && swapView.AllTilesSettled()); i++) Tick();
                }

                TickUntilSettled();
                Assert.IsTrue(watcher.ShownFill.Contains(ancestor), "precondition: the z14 fill is drawn");
                foreach (double zoom in new[] { 15.1, 14.9, 15.1 })
                {
                    swapView.Camera.Apply(new CameraPropertiesUpdate { Zoom = zoom });
                    TickUntilSettled();
                }

                Assert.AreEqual(4, watcher.ShownBackground.Count, "the background ends on the four z15 tiles");
            }
            finally
            {
                swapView.Teardown();
            }

            // A maxzoom of 16, zooming in from 14.9 across the boundary: the z15 tiles that bridge the view never flicker over the z16 cover, and
            // none lingers as a Bridge.
            MapView deepView = NewView(-1.0, maxZoom: 16);
            try
            {
                deepView.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(centre.x, centre.y, 14.9),
                    style: BackgroundAndFillStyle(), sourceMaxZoom: 14);
                PumpUntilSettled(deepView);
                deepView.LateUpdate();
                int tilesBefore = EmittedFor(deepView, 0);
                deepView.Camera.Apply(new CameraPropertiesUpdate { Zoom = 16.1 });
                for (int i = 0; i < 2500; i++)
                {
                    deepView.AwaitInFlightMeshBuilds();
                    deepView.LateUpdate();
                    int drawn = EmittedFor(deepView, 0); // the Update's own result, with no further Update to hide a one-frame hole
                    Assert.IsTrue(drawn == tilesBefore || drawn == 4, $"update {i}: the background draws the old tiles or the four new ones, never a hole or both ({drawn})");
                    Assert.AreEqual(0, deepView.CaptureTelemetry().BridgeTileCount, $"update {i}: no Bridge stays up");
                    if (i > 2 && deepView.AllTilesSettled()) break;
                }

                deepView.LateUpdate();
                deepView.LateUpdate();
                Assert.AreEqual(0, deepView.VisibilityBatchesLastTick(), "settled: nothing is shown or hidden again");
                Assert.AreEqual(4, EmittedFor(deepView, 0), "settled: the background draws the four z16 cover tiles");
                Assert.AreEqual(1, EmittedFor(deepView, 1), "settled: the z14 fill is drawn once");
            }
            finally
            {
                deepView.Teardown();
            }

            // A style with no background: a tile served only from above is still a shown tile. Zooming out to z13 settles on the z13 tiles
            // and draws nothing of z14 or z15 beside them.
            MapView plainView = NewView(-1.0, minZoom: 13);
            try
            {
                plainView.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(centre.x, centre.y, 15.1),
                    style: FillOnlyStyle(), sourceMaxZoom: 14);
                PumpUntilSettled(plainView);
                plainView.LateUpdate();
                Assert.AreEqual(1, EmittedFor(plainView, 0), "precondition: the z14 fill draws once above maxzoom");
                plainView.Camera.Apply(new CameraPropertiesUpdate { Zoom = 13.1 });
                PumpUntilSettled(plainView);
                plainView.LateUpdate();
                plainView.LateUpdate();
                var settledIds = new List<TileId>();
                plainView.CollectLoadedTileIds(settledIds);
                Assert.IsTrue(settledIds.TrueForAll(t => t.Z == 13), "settled: every cover record is z13");
                Assert.AreEqual(settledIds.Count, EmittedFor(plainView, 0), "settled: the fill draws each z13 tile once, with no z14 or z15 beside it");
                Assert.AreEqual(0, plainView.VisibilityBatchesLastTick(), "settled: nothing is shown or hidden again");
            }
            finally
            {
                plainView.Teardown();
            }

            // Deep overzoom: one Update pans over a screen inside one z14 tile, concealing the old cover tiles and revealing the new ones.
            // The source's one fill record is concealed and revealed in that Update, and ends shown.
            MapView panView = NewView(-1.0, maxZoom: 18);
            try
            {
                panView.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(centre.x, centre.y, 17.5),
                    style: BackgroundAndFillStyle(), sourceMaxZoom: 14);
                PumpUntilSettled(panView);
                panView.LateUpdate();
                Assert.AreEqual(1, EmittedFor(panView, 1), "precondition: the z14 fill draws once");

                panView.Camera.Apply(new CameraPropertiesUpdate { Longitude = centre.x + 0.0046 }); // about 600 px, inside the same z14 tile
                panView.LateUpdate();
                panView.LateUpdate();
                Assert.AreEqual(1, EmittedFor(panView, 1), "the pan leaves the z14 fill drawn");
                PumpUntilSettled(panView);
                panView.LateUpdate();
                Assert.AreEqual(1, EmittedFor(panView, 1), "settled: the z14 fill draws once");
            }
            finally
            {
                panView.Teardown();
            }
        }

        // ── (3) NO per-frame GC in steady state ────────────────────────────────────────────────
        // Zero per-frame allocation is the BRG backend's contract; the Entities backend allocates at times.

        [Test]
        public void MapView_SteadyStateTick_DoesNotAllocateGCMemory()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.Backend = RenderBackend.Brg; // zero-alloc path under test
            view.Config.TileSelection.MinZoom = 2; view.Config.TileSelection.MaxZoom = 2;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            // The state arms (e) and (f) each start from, so neither depends on the other: levels 1 to 3, the z2 cover settled.
            void SettleAtLevelsOneToThree(MapView settledView)
            {
                settledView.Config.TileSelection.MinZoom = 1;
                settledView.Config.TileSelection.MaxZoom = 3;
                settledView.Camera.Apply(new CameraPropertiesUpdate { Zoom = 2.0, Heading = 0.0, Tilt = 0.0, Longitude = 0.25, Latitude = 0.0 });
                PumpUntilSettled(settledView);
            }

            try
            {
                // Warm up: load the whole cover and let every tile settle. A real (full-world) bounds gate
                // — HasBounds=true, not the default — so the steady-state tick below actually measures
                // AdmitsTile's overlap branch, not the HasBounds-false early-out.
                double[] d = StyleParser.DefaultBounds;
                var fullWorldBounds = new GeoBounds { West = d[0], South = d[1], East = d[2], North = d[3], HasBounds = true };
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: style, bounds: fullWorldBounds);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime the reused buffers (_cover, _coverIndex, _toRelease) to steady capacity.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.5, Latitude = 0.0 });
                view.LateUpdate();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.LateUpdate();

                // ── (a) THE PAN CASE ──
                // A 1° pan stays inside the loaded z2 cover but dirties it; the full recompute must not allocate.
                double panLon = 0.0;
                AllocationDiagnostics.AssertNotAllocating(() =>
                {
                    // Alternate so every call — warm-up and measured — genuinely dirties the cover, not just the first.
                    panLon = panLon == 0.0 ? 1.0 : 0.0;
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = panLon, Latitude = 0.0 });
                    view.LateUpdate();
                },
                    "MapView.LateUpdate must not allocate during a within-cover pan (cover recompute path: " +
                    "ApplyZoom loop + TileCover.Cover + set rebuild + request/release scan + rebase). " +
                    "A failure means a per-frame List/Task/closure/LINQ leaked into the hot path.");

                Assert.AreEqual(16, view.LoadedTileCount(),
                    "z2 cover is the whole world (4×4); a within-cover pan loads no new tiles");

                // ── (b) the fully-static frame also early-outs allocation-free. ──
                AllocationDiagnostics.AssertNotAllocating(() => view.LateUpdate(),
                    "A static frame (cover clean, nothing pending) must early-out with zero allocation.");

                // ── (c) a heading/tilt change still ticks alloc-free. ──
                // Heading/tilt dirty the cover; a heading of 45° or a tilt of 30° each measurably drop
                // LoadedTileCount below 16 (the frustum genuinely excludes tiles there) — a SMALL nudge
                // around the proven-safe heading=0/tilt=0 baseline (see (a)/(b) above) keeps the whole-world
                // z2 set unchanged, so nothing loads.
                Assert.AreEqual(16, view.LoadedTileCount(),
                    "precondition: the whole-world z2 set must already be all 16 tiles before the heading/tilt nudge.");
                double nudgeHeading = 0.0;
                double nudgeTilt = 0.0;
                AllocationDiagnostics.AssertNotAllocating(() =>
                {
                    // Alternate small nudges so every call — warm-up and measured — genuinely dirties the
                    // cover via CoverKeyGate's exact-equality check, without leaving the whole-world set.
                    nudgeHeading = nudgeHeading == 0.0 ? 0.2 : 0.0;
                    nudgeTilt    = nudgeTilt    == 0.0 ? 0.5 : 0.0;
                    view.Camera.Apply(new CameraPropertiesUpdate { Heading = nudgeHeading, Tilt = nudgeTilt });
                    view.LateUpdate();
                },
                    "A heading/tilt change must tick alloc-free (cover recompute over an unchanged whole-world set).");
                Assert.AreEqual(16, view.LoadedTileCount(),
                    "postcondition: the whole-world z2 set must stay unchanged across the heading/tilt nudge, or " +
                    "this measured a real load/release diff instead of the alloc-free early set-rebuild claim.");

                // ── (d) AT SCALE: zero-alloc must hold over MANY frames, not just one. ──
                // The Entities backend allocates intermittently at this same scale (docs/gc-and-allocation-
                // design.md § 2); BRG staying clean over N=50 shows the churn is Entities-specific.
                const int N = 50;
                AllocationDiagnostics.AssertNotAllocating(() => { for (int i = 0; i < N; i++) view.LateUpdate(); },
                    $"BRG.Update must not allocate across {N} steady-state frames — proving the zero-alloc " +
                    "contract holds at the scale where the Entities backend trips the recorder.");

                // ── (e) PREPARED AHEAD: with level 1 in range, the parents of the z2 cover are prepared, so each cover
                // recompute derives the preload set, merges its keys and re-roles the records. That must not allocate.
                try
                {
                    SettleAtLevelsOneToThree(view);
                    Assert.Greater(view.CaptureTelemetry().PreparingTileCount, 0,
                        "precondition: the parent level is prepared, so the preload set is not empty while measuring.");
                    double preparedPanLon = 0.0;
                    AllocationDiagnostics.AssertNotAllocating(() =>
                    {
                        preparedPanLon = preparedPanLon == 0.0 ? 1.0 : 0.0;
                        view.Camera.Apply(new CameraPropertiesUpdate { Longitude = preparedPanLon, Latitude = 0.0 });
                        view.LateUpdate();
                    },
                        "A cover recompute with a prepared level in play must not allocate (preload set, prepare keys, roles).");
                }
                finally
                {
                    view.Config.TileSelection.MinZoom = 2;
                    view.Config.TileSelection.MaxZoom = 2;
                }

                // ── (f) HELD: the z2 tiles wait for z3 tiles that are built but not yet registered (consume blocked). The swap
                // step walks their areas on every Update, and a holding tick must not allocate.
                try
                {
                    SettleAtLevelsOneToThree(view);
                    view.Config.MaxConsumesPerTick = 0;
                    view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 3.5 });
                    for (int frame = 0; frame < 3000; frame++)
                    {
                        view.LateUpdate();
                        view.AwaitInFlightMeshBuilds();
                        TileTelemetrySnapshot waiting = view.CaptureTelemetry();
                        if (waiting.PendingTileCount > 0 && waiting.ConsumeBacklog == waiting.PendingTileCount) break;
                    }

                    Assert.Greater(view.CaptureTelemetry().HeldTileCount, 0, "precondition: the z2 tiles are held, so the swap step has areas to walk.");
                    AllocationDiagnostics.AssertNotAllocating(() => view.LateUpdate(),
                        "A tick that holds a tile for unregistered finer tiles must not allocate (the swap step's area walk).");
                }
                finally
                {
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.TileSelection.MinZoom = 2;
                    view.Config.TileSelection.MaxZoom = 2;
                }

                // ── (g) A FLUSH THAT HAS WORK: shows and hides queued in one round, then one hide call and one show call. ──
                // The arms above flush an empty queue. This one fills the map, partitions it and calls the backend twice.
                var batch   = new VisibilityBatch();
                var backend = new CountingBackend();
                int[] hide  = { 1, 2, 3, 4, 5, 6 };
                AllocationDiagnostics.AssertNotAllocating(() =>
                {
                    for (int i = 0; i < hide.Length; i++) batch.QueueShow(hide[i]);
                    batch.QueueHide(hide);       // the last call wins: all six end hidden
                    batch.QueueShow(100);
                    batch.Flush(backend);
                },
                    "A flush that hides and shows must not allocate (the handle map, the two lists and the two backend calls).");
                Assert.AreEqual(1, backend.HandlesShownLastFlush, "only handle 100 is shown");
                Assert.AreEqual(hide.Length, backend.HandlesHiddenLastFlush, "the six handles hidden last are hidden");
            }
            finally
            {
                view.Teardown();
            }
        }
    }
}
