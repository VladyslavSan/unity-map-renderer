// Unity EditMode only — drives a real TileManager.Dispose() (UMR-112 §6.8a T9).

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Data;
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
    public class TeardownPenOrderTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, 0.0, 0.0);

        /// <summary>UMR-112 §6.8a T9: <c>DoDispose</c> must call <c>_pending.FlushAll()</c> AFTER the
        /// <c>_loaded</c> teardown pass stashes into the pens, not before — an inverted call flushes
        /// pens that are then refilled and never emptied. Exercises the graph arm (a write step complete
        /// but unconsumed) and the fetch arm (a fetch task completed off-model but not yet observed by
        /// a Tick) in the SAME Dispose call.
        /// <para><b>Scope.</b> The prologue arm is not included: reaching it deterministically needs a
        /// camera pan to admit a fresh tile under an armed <c>MeshBuildGateForTest</c> (the mechanism
        /// <c>SourceTileGraphBuildTests</c>'s Case 1 uses for its Tick-based drain), which would make
        /// this fixture racy for no new coverage — the prologue arm's <c>Dispose</c> path shares the
        /// exact same <c>_pending.FlushAll()</c> call and ordering this test already exercises.</para>
        /// <para><b>RED recipe:</b> hoist <c>_pending.FlushAll()</c> above the <c>_loaded</c> teardown
        /// loop in <c>DoDispose</c>.</para></summary>
        [Test]
        public void Dispose_FlushesPensAfterRecordTeardown()
        {
            var mainSrc   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var stuckGate = new UniTaskCompletionSource<TileResponse>();
            var stuckSrc  = new TestDataSource((id, ct) => stuckGate.Task);

            var go   = new GameObject("T9_TeardownFlushOrder");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom  = 5;
            view.Config.TileSelection.MaxZoom  = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick     = 0; // leaves the graph arm's write complete but unconsumed
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64;

            try
            {
                long graphBaseline = TileBuildGraph.DebugLiveCount;

                var style = StyleParser.Parse(@"{
                    ""version"": 8, ""name"": ""T9"",
                    ""sources"": {
                        ""main"":  { ""type"": ""vector"", ""tiles"": [""https://example.com/main/{z}/{x}/{y}.pbf""] },
                        ""stuck"": { ""type"": ""vector"", ""tiles"": [""https://example.com/stuck/{z}/{x}/{y}.pbf""] }
                    },
                    ""layers"": [
                        { ""id"": ""main-fill"",  ""type"": ""fill"", ""source"": ""main"",  ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",200,50,50,1]} },
                        { ""id"": ""stuck-fill"", ""type"": ""fill"", ""source"": ""stuck"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,200,50,1]} }
                    ]
                }");

                var mv = view.View;
                mv.Camera.SetProperties(Cam(0, 0, 5.0));
                mv.Camera.SyncToCamera();
                mv.Layers.Build(style, mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);

                var specs = new List<TileManager.SourceSpec>
                {
                    new TileManager.SourceSpec("main",  default, 0, int.MaxValue,
                        () => new MvtTileFeatureSource(mainSrc,  new InlineWorkScheduler())),
                    new TileManager.SourceSpec("stuck", default, 0, int.MaxValue,
                        () => new MvtTileFeatureSource(stuckSrc, new InlineWorkScheduler())),
                };
                mv.TileManager.SetSources(specs, view.Config.Backend);

                // "main" fetches + builds inline, parking at write-complete-unconsumed (consume capped to
                // 0) — fetch/prologue/measure/write each advance on their own tick, so pump until
                // ConsumeBacklog sees it (SourceTileGraphBuildTests' Case 3 shape). "stuck" admits on the
                // first tick and its fetch never resolves, so further ticks leave it untouched.
                for (int f = 0; f < 20 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                    view.LateUpdate();

                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the main-source tile's write step must be complete but unconsumed.");
                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "precondition: the main-source tile's graph must be LIVE (write-complete, unconsumed).");
                Assert.Greater(view.InFlightCount(), 0,
                    "precondition: the stuck-source tile's fetch must still be in flight.");

                // Complete the underlying task WITHOUT ticking again — WaitOffPlayerLoop inside FlushAll
                // then resolves immediately, but the record's own FetchCompleted flag is still false.
                stuckGate.TrySetResult(new TileResponse(SampleTileFixture.Bytes(), TileEncoding.Mvt));

                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "the graph pen must be flushed AFTER the teardown loop stashes into it — an inverted " +
                    "FlushAll leaves this elevated.");
            }
            finally
            {
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
            }
        }
    }
}
