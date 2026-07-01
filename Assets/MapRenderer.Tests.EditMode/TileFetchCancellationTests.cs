// S84 — rapid zoom/cover churn must not flood the log with unobserved fetch exceptions.
//
// Repro of the maintainer-observed bug: load at z, slide the zoom slider chaotically → the console fills
// with "UnityWebRequestException: Unknown Error" published from UniTask's ExceptionHolder.Finalize() —
// i.e. fetch tasks cancelled mid-flight by cover churn were never observed.
//
// Unity-only (MonoBehaviour + MapView + ThreadPool fetch). Excluded from core-tests.csproj.

using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
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
        /// </summary>
        private static async UniTask<TileResponse> SpinThenFault(TileId id, CancellationToken ct)
        {
            await UniTask.SwitchToThreadPool();
            int spins = 0;
            while (!ct.IsCancellationRequested && spins++ < 60000) // ~60s safety cap
                Thread.Sleep(1);
            // Always fault non-OCE (even on the safety-cap path) so a dropped task would be unobserved.
            throw new InvalidOperationException(FaultMarker);
        }

        [Test]
        public void RapidRelease_MidFetch_DoesNotPublishUnobservedExceptions()
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
            MapView    view = go.AddComponent<MapView>().WithTestMaterials();
            try
            {
                view.Config.MinZoom = 5; view.Config.MaxZoom = 5;
                view.Config.PadTiles = 0; view.WithTestCamera();
                view.Config.MaxBuildsPerTick = 64;
                view.Config.MaxTessellationsPerTick = 64;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Tiles enter cover; fetches kick and stay in-flight (CancelFaultingSource never returns).
                view.Tick();
                Thread.Sleep(5);
                view.Tick();

                // Churn: pan far so the original tiles are released while their fetch is still in-flight.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.Tick();

                // Positive control (non-vacuous): the cancel-mid-fetch race must actually have happened.
                Assert.Greater(view.ReleasedMidFetchCount(), 0,
                    "Positive control: at least one tile must be released while its FETCH is in-flight. " +
                    "If 0, the race did not occur and the unobserved-exception assertion is vacuous.");

                // Let the cancelled fetches fault on the ThreadPool, and Tick so DrainPendingFetchDisposal
                // observes them. Without the fix, the dropped faulted tasks would go unobserved.
                for (int f = 0; f < 300; f++)
                {
                    view.Tick();
                    Thread.Sleep(1);
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
                    "TileManager.ReleaseTile must stash the in-flight fetch task (_pendingFetchDisposal) and " +
                    "DrainPendingFetchDisposal / Dispose must observe it.");
            }
            finally
            {
                UniTaskScheduler.UnobservedTaskException -= handler;
                if (go != null) { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
            }
        }
    }
}
