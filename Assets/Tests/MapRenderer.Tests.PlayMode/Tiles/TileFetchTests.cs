// Tiles/TileFetchTests.cs — fetch-cancellation and symbol-kick teeth, PlayMode half.
//
// Split from Tiles/TileCacheTests.cs by a using collision: both files here import System and always
// qualify Object as UnityEngine.Object.Destroy/DestroyImmediate; TileCacheTests.cs's two files call bare
// Object.Destroy without importing System, so the two groups must not merge.
//
// Contents:
//   TileFetchCancellationTests  — S84 — rapid zoom/cover churn must not flood the log with unobserved fetch exceptions.
//   TileSymbolKickTests         — Epic A / A5b — the symbol kick fires via the normal PumpPending path once the off-main decode lands between ticks.

using System;
using System.Collections;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View.Camera;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using System.Collections.Generic;
using Unity.Profiling;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;


namespace MapRenderer.Tests.PlayMode.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // TileFetchCancellationTests — S84 — rapid zoom/cover churn must not flood the log with fetch exceptions
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileFetchCancellationTests
    {
        // Unique marker so the UnobservedTaskException handler only reacts to OUR faults, never a stray
        // abandoned task from another test sharing the domain (test-isolation footgun).
        private const string FaultMarker = "S84-test-fetch-fault";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""S84"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>
        /// A fetch that stays IN-FLIGHT (spins on the ThreadPool) until its token is cancelled, then faults
        /// with a NON-OCE exception — mimicking an aborted UnityWebRequest's "Unknown Error". The non-OCE
        /// fault is deliberate: UniTask tends to suppress unobserved OCE, so an OCE-based fake would let the
        /// test pass vacuously (with or without the fix). This faults the way the real bug does. Driven
        /// through the shared <see cref="TestDataSource"/> (S81) via its (id, ct) delegate ctor.
        /// A blocking wait on the cancellation token's WaitHandle simulates the fetch latency ON THE
        /// THREADPOOL (not a test-frame wait) — it stays; the settle waits in the test body yield real
        /// frames. Parks (no poll) until cancelled or the ~60s safety cap.
        /// </summary>
        private static async UniTask<TileResponse> SpinThenFault(TileId id, CancellationToken ct)
        {
            await UniTask.SwitchToThreadPool();
            // Park until cancelled or the ~60s safety cap. Guard the WaitHandle access: Release cancels AND
            // disposes the linked CTS, and if that disposal wins the race to here (ThreadPool starvation),
            // `ct.WaitHandle` throws ObjectDisposedException. Swallowing it and falling through to the fault
            // is correct — a disposed source means the fetch WAS released — and it keeps the FaultMarker
            // oracle armed (an escaping ODE would carry no marker and pass the test vacuously).
            try { ct.WaitHandle.WaitOne(60000); }
            catch (ObjectDisposedException) { }
            // Always fault non-OCE (even on the safety-cap path) so a dropped task would be unobserved.
            throw new InvalidOperationException(FaultMarker);
        }

        [UnityTest]
        public IEnumerator RapidRelease_MidFetch_DoesNotPublishUnobservedExceptions()
        {
            bool unobservedFired = false;
            Action<Exception> handler = ex =>
            {
                for (Exception e = ex; e != null; e = e.InnerException)
                    if (e.Message != null && e.Message.Contains(FaultMarker)) { unobservedFired = true; break; }
            };
            UniTaskScheduler.UnobservedTaskException += handler;

            var        src  = new TestDataSource(SpinThenFault);
            GameObject go   = new GameObject("MapView_S84");
            MapView    view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            try
            {
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Tiles enter cover; fetches kick and stay in-flight (SpinThenFault never returns).
                view.LateUpdate();
                for (int i = 0; i < 3; i++) yield return null; // real frames: let the ThreadPool fetches kick
                view.LateUpdate();

                // Churn: pan far so the original tiles are released while their fetch is still in-flight.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate();

                // Positive control (non-vacuous): the cancel-mid-fetch race must actually have happened.
                Assert.Greater(view.ReleasedMidFetchCount(), 0,
                    "Positive control: at least one tile must be released while its FETCH is in-flight. " +
                    "If 0, the race did not occur and the unobserved-exception assertion is vacuous.");

                // Let the cancelled fetches fault on the ThreadPool, and Tick so PendingDisposalQueue.DrainCompleted
                // observes them. Without the fix, the dropped faulted tasks would go unobserved.
                for (int f = 0; f < 300; f++)
                {
                    view.LateUpdate();
                    yield return null;
                }

                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                // Force finalization — this is where an UNOBSERVED faulted task would publish.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Assert.IsFalse(unobservedFired,
                    "A fetch cancelled mid-flight by cover churn must be OBSERVED, not dropped to UniTask's " +
                    "unobserved-exception finalizer. If this fires, the S84 console-flood bug is back: " +
                    "TileManager.ReleaseTile must stash the in-flight fetch task (_pending.StashFetch) and " +
                    "PendingDisposalQueue.DrainCompleted / FlushAll must observe it.");
            }
            finally
            {
                UniTaskScheduler.UnobservedTaskException -= handler;
                if (go != null) { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileSymbolKickTests — A5b — the symbol kick fires via PumpPending once the off-main decode lands between ticks
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileSymbolKickTests : BaseTestFixture
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

            /// <summary>Not under test here: the spy commits no symbol blocks, so it answers "nothing
            /// to lose" and leaves TileManager's prepared-cache probe exactly as it was.</summary>
            public bool SymbolsCachedFor(string sourceId, TileId tile) => true;
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
            Track(go);
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
            finally { view.Teardown(); }
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
            Track(go);
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
            finally { view.Teardown(); }

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
            Track(go);
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
            finally { view.Teardown(); }
        }

        // ── S82 regression: a symbol-only source must STILL fetch+kick with the prepared cache ENABLED ──
        [UnityTest]
        public IEnumerator SymbolOnlySource_FetchesAndKicks_WithPreparedCacheEnabled()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(zoom: 0, preparedCacheEnabled: true); // the DEFAULT — the gap's trigger
            Track(go);
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
            finally { view.Teardown(); }
        }
    }
}
