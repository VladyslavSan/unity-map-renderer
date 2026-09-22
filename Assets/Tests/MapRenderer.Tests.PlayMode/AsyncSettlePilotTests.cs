using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Async
{
    /// <summary>
    /// Settle-mechanism proof for the PlayMode test assembly. A real fill-style tile build hops to the
    /// ThreadPool (<c>UniTask.RunOnThreadPool</c>, <c>configureAwait: false</c>), so it completes on
    /// wall-clock time on a background thread — NOT via the PlayerLoop. In an EditMode <c>[UnityTest]</c>
    /// a <c>yield return null</c> frame is instantaneous and starves that worker, so the build never lands;
    /// in PlayMode the frame has real duration, the worker finishes, and the next <c>LateUpdate</c> consumes
    /// it. This asymmetry is the whole reason the async settle tests belong here, not in EditMode.
    /// </summary>
    [TestFixture]
    public class AsyncSettlePilotTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>A minimal one-fill-layer style over the committed fixture tile — enough to force the
        /// real (non-source-less) mesh-build path that hops to the ThreadPool.</summary>
        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>Kicks a real ThreadPool mesh build, proves it is NOT settled synchronously, then pumps
        /// real frames (each yielding the worker its wall-clock) until it settles. Fails in EditMode by
        /// construction; must pass in PlayMode.</summary>
        [UnityTest]
        public IEnumerator RealFillBuild_SettlesUnderYieldFramePump()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("AsyncSettlePilotView"));
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials().WithTestCamera();
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), FillStyle());

                // One tick dispatches the mesh build to the ThreadPool; it cannot have settled this frame.
                view.LateUpdate();
                Assert.Greater(view.LoadedTileCount(), 0, "the fill cover must load at least one tile.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the mesh build is dispatched to the ThreadPool — it must be in-flight, not settled synchronously.");

                int f = 0;
                while (!view.AllTilesSettled() && f++ < 600)
                {
                    view.LateUpdate();   // consume any completed builds
                    yield return null;   // real frame: give the ThreadPool worker wall-clock to finish
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "the ThreadPool mesh build must settle under a real-frame yield pump (the PlayMode premise).");
            }
            finally { view.Teardown(); }
        }
    }
}
