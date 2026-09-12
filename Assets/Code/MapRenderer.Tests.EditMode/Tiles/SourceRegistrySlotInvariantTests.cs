// Unity EditMode only — drives the real MapView/TileManager restyle path (UMR-112 §4.3/§6.1). The
// ordering half of T7 is pinned behaviourally below (Rebuild_CallerFactoryObservesLoadedClearedFirst)
// and structurally by TileProcessingStructureTests's
// TileManagerSetSources_ClearsSlotKeyedStateUnconditionally_ThenRebuildsSources.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class SourceRegistrySlotInvariantTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, 0.0, 0.0);

        /// <summary>Deterministic settle (mirrors <c>Tiles/PreparedCacheTests.PumpUntilSettled</c>):
        /// <c>DrainMeshBuilds</c> spins each tick's kicked builds to completion so the next tick consumes
        /// them. The <c>LoadedTileCount() &gt; 0</c> guard is load-bearing — <c>AllTilesSettled()</c> is
        /// vacuously true on an empty cover, before anything has ever been admitted.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 200)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        private static StyleDocument ThreeSourceStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""T7ThreeSources"",
            ""sources"": {
                ""a"": { ""type"": ""vector"", ""tiles"": [""https://example.com/a/{z}/{x}/{y}.pbf""] },
                ""b"": { ""type"": ""vector"", ""tiles"": [""https://example.com/b/{z}/{x}/{y}.pbf""] },
                ""c"": { ""type"": ""vector"", ""tiles"": [""https://example.com/c/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""a-fill"", ""type"": ""fill"", ""source"": ""a"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",200,50,50,1]} },
                { ""id"": ""b-fill"", ""type"": ""fill"", ""source"": ""b"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,200,50,1]} },
                { ""id"": ""c-fill"", ""type"": ""fill"", ""source"": ""c"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,50,200,1]} }
            ]
        }");

        // Removes "b" — the MIDDLE slot — so "c" shifts from slot 2 to slot 1.
        private static StyleDocument TwoSourceStyle_BRemoved() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""T7TwoSources"",
            ""sources"": {
                ""a"": { ""type"": ""vector"", ""tiles"": [""https://example.com/a/{z}/{x}/{y}.pbf""] },
                ""c"": { ""type"": ""vector"", ""tiles"": [""https://example.com/c/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                { ""id"": ""a-fill"", ""type"": ""fill"", ""source"": ""a"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",200,50,50,1]} },
                { ""id"": ""c-fill"", ""type"": ""fill"", ""source"": ""c"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,50,200,1]} }
            ]
        }");

        /// <summary>UMR-112 §6.1: restyling away a source must not leave its tiles behind. Removing the
        /// MIDDLE source (b, slot 1) reassigns the survivor (c) from slot 2 to slot 1 — a stale entry keyed
        /// to the OLD slot would resolve against the wrong (or an out-of-range) pipeline. Pins that
        /// <c>_loaded</c> empties immediately on restyle and that the restyled cover settles with no tile
        /// reporting the removed source. The ORDER this depends on is pinned by
        /// <see cref="Rebuild_CallerFactoryObservesLoadedClearedFirst"/> instead — that test reaches the
        /// window directly through <c>Rebuild</c>'s own caller-supplied-code hook, so it, not this
        /// end-to-end settle, is the one that RED-verifies an ordering inversion.</summary>
        [Test]
        public void RemovedSource_TilesDoNotSurviveARestyle()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("T7_SlotInvariant");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: ThreeSourceStyle(),
                    decodeScheduler: new InlineWorkScheduler());
                Assert.AreEqual(3, view.WiredFeatureSourceCount(), "precondition: three real sources wired.");

                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "precondition: the initial three-source cover must settle.");
                Assert.Greater(view.LoadedTileCount(), 0, "sanity: something must actually be loaded.");

                // Restyle, removing the MIDDLE source.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: TwoSourceStyle_BRemoved(),
                    decodeScheduler: new InlineWorkScheduler());

                Assert.AreEqual(2, view.WiredFeatureSourceCount(), "the registry must now hold exactly the two surviving sources.");
                Assert.AreEqual(0, view.LoadedTileCount(), "SetSources step 1 must clear _loaded immediately — before any tick.");

                // Drive several ticks against the NEW registry — an ordering bug throws (out-of-range slot)
                // or silently aliases a stale entry to the wrong pipeline.
                PumpUntilSettled(view);
                Assert.IsTrue(view.LoadedTileCount() > 0 && view.AllTilesSettled(),
                    "the restyled two-source cover must settle without throwing.");

                var loaded = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(loaded);
                Assert.Greater(loaded.Count, 0, "sanity: the restyled cover must have loaded something.");
                foreach (var key in loaded)
                    Assert.AreNotEqual("b", key.SourceId,
                        "no loaded record may report the REMOVED source 'b' — a stale slot-keyed entry " +
                        "would alias the wrong pipeline after the restyle re-slotted the survivors.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>UMR-112 §6.1: <c>SourceRegistry.Rebuild</c> is not a black box mid-call — it invokes a
        /// caller-supplied <c>SourceSpec.CreateSource</c> factory for every new pipeline while the registry
        /// is still rebuilding. Wires a factory that reads the loaded-tile count from inside that call and
        /// pins <c>TileManager.SetSources</c>' load-bearing order: it clears <c>_loaded</c> before calling
        /// <c>Rebuild</c>, so the factory observes zero, never the pre-restyle count.</summary>
        [Test]
        public void Rebuild_CallerFactoryObservesLoadedClearedFirst()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("T7_RebuildReentrancy");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: ThreeSourceStyle(),
                    decodeScheduler: new InlineWorkScheduler());
                PumpUntilSettled(view);
                Assert.Greater(view.LoadedTileCount(), 0, "precondition: something must be loaded before the restyle.");

                int observedDuringRebuild = -1;
                var specs = new List<TileManager.SourceSpec>
                {
                    new TileManager.SourceSpec("only-new", default, 0, int.MaxValue, () =>
                    {
                        observedDuringRebuild = view.LoadedTileCount();
                        return new MvtTileFeatureSource(src, new InlineWorkScheduler());
                    }),
                };

                view.TileManager.SetSources(specs, view.Config.Backend);

                Assert.AreEqual(0, observedDuringRebuild,
                    "a CreateSource factory invoked from mid-Rebuild must see _loaded already cleared — " +
                    "SetSources must clear slot-keyed state BEFORE rebuilding the registry.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
