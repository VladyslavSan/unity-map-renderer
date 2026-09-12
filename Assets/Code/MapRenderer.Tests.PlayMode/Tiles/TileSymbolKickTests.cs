// Epic A / A5b acceptance teeth (F-2, F-3, F-5, symbol-only-with-cache) — the PlayMode half of the feed
// swap. These assert the SYMBOL kick fires (TryBeginBuild via the normal PumpPending kick path, over the
// async decode). That path needs the decode to LAND between ticks (off-main wall-clock) so the next tick
// kicks the symbol — which only real PlayMode frames provide. `DrainMeshBuilds` (the EditMode settle) is
// symbol-SILENT by construction (F-4, EditMode), so it cannot host these. The structural/drain-silence/
// departed teeth (F-1, F-4, F-6) stay in the EditMode half.

using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.PlayMode.Tiles
{
    [TestFixture]
    public class TileSymbolKickTests
    {
        // ── Test doubles (duplicated from the EditMode half — private nested spies can't cross the boundary) ──

        /// <summary>Spy <see cref="ISymbolTileWorkerFactory"/> — records every <c>TryBeginBuild</c> call and
        /// every pass it issues. <see cref="ParticipatesFor"/> gates which sources get a non-null pass;
        /// <see cref="PassFactory"/> lets a test substitute the pass (e.g. a throwing spy for F-3).</summary>
        private sealed class SpySymbolTileWorkerFactory : ISymbolTileWorkerFactory
        {
            public readonly List<(string SourceId, TileId Tile)> BeginBuildCalls = new();
            public readonly List<SpySymbolTileWorkerPass> IssuedPasses = new();
            public Func<string, bool> ParticipatesFor = _ => true;
            public Func<string, TileId, SpySymbolTileWorkerPass> PassFactory;

            public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
            {
                BeginBuildCalls.Add((sourceId, tile));
                if (!ParticipatesFor(sourceId)) return null;
                SpySymbolTileWorkerPass pass = PassFactory != null
                    ? PassFactory(sourceId, tile)
                    : new SpySymbolTileWorkerPass(sourceId, tile);
                IssuedPasses.Add(pass);
                return pass;
            }
        }

        private sealed class SpySymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            public readonly string SourceId;
            public readonly TileId Tile;
            public bool Ran;
            public SharedDisposable<IDecodedTile> ReceivedDecode;
            public Action OnRun;

            public SpySymbolTileWorkerPass(string sourceId, TileId tile) { SourceId = sourceId; Tile = tile; }

            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                Ran = true;
                ReceivedDecode = decode;
                OnRun?.Invoke();
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument FillAndSymbolStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""s"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16 } }
            ]
        }");

        private static StyleDocument TwoSourceStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""layers"": [
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""symsrc"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16 } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""meshsrc"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        private static (GameObject go, MapView view) NewView(int zoom, bool preparedCacheEnabled = true)
        {
            var go   = new GameObject("MapView_TileSymbolKick");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.Config.PreparedCache.Enabled = preparedCacheEnabled;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            return (go, view);
        }

        /// <summary>Pumps across real frames until the cover settles, yielding a frame each iteration so the
        /// off-main decode lands and the next LateUpdate's PumpPending fires the symbol kick.</summary>
        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        // ── F-2: symbol rides the kick, sharing ONE decode ──────────────────────────────────────────────
        [UnityTest]
        public IEnumerator F2_SymbolRidesKick_SharingOneDecode()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(zoom: 0); // z0 = one tile — a clean single-decode measurement
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle(), symbolsIntentionallyUnwired: true);

                const int recorderCapacity = 64;
                using var decodeRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "MapRenderer.Tile.Decode", capacity: recorderCapacity,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame);

                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "sanity: the tile must settle.");
                yield return null; yield return null; // let the profiler commit accumulated samples

                Assert.AreEqual(1, spy.BeginBuildCalls.Count, "TryBeginBuild must be called exactly once, at the kick.");
                Assert.AreEqual(1, spy.IssuedPasses.Count, "sanity: the source has a symbol layer — a pass must issue.");
                Assert.IsTrue(spy.IssuedPasses[0].Ran, "RunWorkerAndHandoff must have run.");
                Assert.IsNotNull(spy.IssuedPasses[0].ReceivedDecode, "sanity: the pass received a decode.");

                // Defensive clamp: the recorder is a `recorderCapacity`-frame ring; .Count can momentarily
                // reflect an in-flight write past that bound. The decode happens once, early, and survives.
                long decodeHits = 0;
                int  sampleCount = Math.Min(decodeRecorder.Count, recorderCapacity);
                for (int i = 0; i < sampleCount; i++) decodeHits += decodeRecorder.GetSample(i).Count;
                Assert.AreEqual(1, decodeHits,
                    "F-2 DECISIVE: the tile's MVT bytes must decode exactly ONCE across mesh+symbol in the SAME " +
                    "kick task (a symbol pass fed a fresh/second decode, or not driven at kick, leaves this 0 or 2).");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── F-3: two fault domains — a contract-violating symbol throw never strands a mesh array ───────
        // AllTilesSettled() is NOT decisive (ConsumeMeshBuild's faulted-task guard settles either way via
        // silent-discard). The decisive observable is MeshDataPayload.DebugLiveAllocCount: WITH the wrap the
        // kick task completes with a real TilePrologueOutput (consume runs, array disposed); WITHOUT it, the
        // kick-allocated writable array leaks past settle. RED-verified in the A5b stage report.
        [UnityTest]
        public IEnumerator F3_SymbolFaultNeverStrandsMeshArray_LambdaWrapCatches()
        {
            long allocBefore = MeshDataPayload.DebugLiveAllocCount;

            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(zoom: 0);
            var spy = new SpySymbolTileWorkerFactory
            {
                PassFactory = (sourceId, tile) => new SpySymbolTileWorkerPass(sourceId, tile)
                {
                    OnRun = () => throw new InvalidOperationException("F-3 deliberately contract-violating spy")
                }
            };
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle(), symbolsIntentionallyUnwired: true);
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(),
                    "sanity: the tile reaches Built either way — ConsumeMeshBuild's faulted-task guard settles " +
                    "it via silent-discard even without the wrap. Settling alone does NOT prove the wrap works.");
                Assert.AreEqual(1, spy.IssuedPasses.Count, "sanity: the throwing pass was issued.");
                Assert.IsTrue(spy.IssuedPasses[0].Ran, "sanity: RunWorkerAndHandoff actually ran (and threw).");

                long allocAfterSettle = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(allocBefore, allocAfterSettle,
                    $"F-3 DECISIVE: the kick-allocated writable MeshDataArray must be disposed by settle time " +
                    $"(baseline={allocBefore}, after={allocAfterSettle}). Without the lambda wrap the throw faults " +
                    "the whole RunOnThreadPool body, so ConsumeMeshBuild takes the silent-discard path " +
                    "(Built=true, but no UploadMesh, no dispose) — a genuine native leak this catches.");

                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "F-3: the fill layer's mesh must have been registered with the backend.");
                Assert.GreaterOrEqual(meshes.Length, 1, "sanity: at least one layer slot (the fill layer).");
                Assert.IsNotNull(meshes[0], "the fill mesh itself must be non-null — real geometry reached the backend.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }

            // (The confirmatory managed-side CountMeshObjects delta the EditMode form carried is dropped here:
            // Object.Destroy is deferred to end-of-frame in PlayMode, so an absolute Mesh count is unreliable.
            // The decisive native-leak tooth above — DebugLiveAllocCount, a code-maintained counter — stands.)
        }

        // ── F-5: a symbol-only source kicks; a mesh-only source runs no symbol pass ────────────────────
        [UnityTest]
        public IEnumerator F5_SymbolOnlySourceKicks_MeshOnlySourceRunsNoSymbolPass()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            // Cache DISABLED to isolate this tooth's concern (does the kick fire for a symbol-only source
            // through the normal fetch→kick pipeline). The cache-ON twin is SymbolOnlySource_...Enabled.
            var (go, view) = NewView(zoom: 0, preparedCacheEnabled: false);
            var spy = new SpySymbolTileWorkerFactory { ParticipatesFor = sourceId => sourceId == "symsrc" };
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: TwoSourceStyle(), symbolsIntentionallyUnwired: true);
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "sanity: both sources' tiles must settle.");

                var loadedKeys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(loadedKeys);
                string diag = "loaded=[" + string.Join(", ", loadedKeys.ConvertAll(k => $"{k.SourceId}:{k.Tile}")) +
                    "] beginBuildCalls=[" + string.Join(", ", spy.BeginBuildCalls.ConvertAll(c => $"{c.SourceId}:{c.Tile}")) + "]";
                Assert.AreEqual(2, view.LoadedTileCount(),
                    $"sanity: both sources must each produce exactly one loaded z0 tile. {diag}");

                Assert.IsTrue(spy.BeginBuildCalls.Exists(c => c.SourceId == "symsrc"),
                    $"F-5: TryBeginBuild must fire for the symbol-only source (dense mesh = 0 does not gate the kick). {diag}");
                Assert.IsTrue(spy.BeginBuildCalls.Exists(c => c.SourceId == "meshsrc"),
                    $"sanity: TileManager calls TryBeginBuild for EVERY kicked source. {diag}");

                SpySymbolTileWorkerPass symPass = spy.IssuedPasses.Find(p => p.SourceId == "symsrc");
                Assert.IsNotNull(symPass, "a pass must have been issued for the symbol-only source.");
                Assert.IsTrue(symPass.Ran, "RunWorkerAndHandoff must have run for the symbol-only source's pass.");

                Assert.IsFalse(spy.IssuedPasses.Exists(p => p.SourceId == "meshsrc"),
                    "F-5: no symbol pass may be issued/run for the mesh-only source.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── S82 regression: a symbol-only source must STILL fetch+kick with the prepared cache ENABLED ──
        [UnityTest]
        public IEnumerator SymbolOnlySource_FetchesAndKicks_WithPreparedCacheEnabled()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(zoom: 0, preparedCacheEnabled: true); // the DEFAULT — the gap's trigger
            var spy = new SpySymbolTileWorkerFactory { ParticipatesFor = sourceId => sourceId == "symsrc" };
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: TwoSourceStyle(), symbolsIntentionallyUnwired: true);
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "sanity: both sources' tiles must settle.");

                Assert.IsTrue(spy.BeginBuildCalls.Exists(c => c.SourceId == "symsrc"),
                    "S82 REGRESSION: a symbol-only source (zero dense mesh layers) must fetch+kick even with the " +
                    "prepared cache ENABLED — pre-fix the vacuous `allCached` short-circuit sent it down " +
                    "BuildTileFromCache on every cover entry, so it never fetched and its labels never built.");

                SpySymbolTileWorkerPass symPass = spy.IssuedPasses.Find(p => p.SourceId == "symsrc");
                Assert.IsNotNull(symPass, "a pass must have been issued for the symbol-only source.");
                Assert.IsTrue(symPass.Ran, "RunWorkerAndHandoff must have run for the symbol-only source's pass.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
