// Unity EditMode only — uses MonoBehaviour, NativeArray jobs, the live MapView loop.
// NOT included in Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using NIs = NUnit.Framework.Is;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
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
        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
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


        /// <summary>Deterministically settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick consumes
        /// them. The async-settle behavioural tooth lives in the PlayMode half
        /// (MapRenderer.Tests.PlayMode.MapViews.MapViewLiveLoopTests); this half's GC.Alloc verdicts need
        /// EditMode's alloc isolation, so they warm up with this deterministic drain.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2500)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
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

            try
            {
                // Warm up: load the whole cover and let every tile settle.
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime the reused buffers (_cover, _coverSet, _toRelease) to steady capacity.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.5, Latitude = 0.0 });
                view.LateUpdate();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.LateUpdate();

                // ── (a) THE PAN CASE ──
                // A 1° pan stays inside the loaded z2 cover but dirties it; the full recompute must not allocate.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 1.0, Latitude = 0.0 });
                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "MapView.LateUpdate must not allocate during a within-cover pan (cover recompute path: " +
                    "ApplyZoom loop + TileCover.Cover + set rebuild + request/release scan + rebase). " +
                    "A failure means a per-frame List/Task/closure/LINQ leaked into the hot path.");

                Assert.AreEqual(16, view.LoadedTileCount(),
                    "z2 cover is the whole world (4×4); a within-cover pan loads no new tiles");

                // ── (b) the fully-static frame also early-outs allocation-free. ──
                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "A static frame (cover clean, nothing pending) must early-out with zero allocation.");

                // ── (c) a heading/tilt change still ticks alloc-free. ──
                // Heading dirties the cover, but the whole-world z2 set stays the same, so nothing loads.
                view.Camera.Apply(new CameraPropertiesUpdate { Heading = 45.0, Tilt = 30.0 });
                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "A heading/tilt change must tick alloc-free (cover recompute over an unchanged whole-world set).");

                // ── (d) AT SCALE: zero-alloc must hold over MANY frames, not just one. ──
                // Same N as the Entities verdict below, so a clean BRG shows the churn is Entities-specific.
                const int N = 50;
                Assert.That(() => { for (int i = 0; i < N; i++) view.LateUpdate(); }, Is.Not.AllocatingGCMemory(),
                    $"BRG.Tick must not allocate across {N} steady-state frames — proving the zero-alloc " +
                    "contract holds at the scale where the Entities backend trips the recorder.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── (3b) Entities backend allocation VERDICT (informational, not a hard assertion) ─────────
        // The BRG test on the Entities backend. Its allocation is a trade-off, not a contract: it reports a count.
        [Test]
        public void MapView_SteadyStateTick_Entities_AllocationVerdict()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.Backend = RenderBackend.Entities; // the default backend under measurement
            view.Config.TileSelection.MinZoom = 2; view.Config.TileSelection.MaxZoom = 2;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime reused buffers, identical to the BRG test.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.5, Latitude = 0.0 });
                view.LateUpdate();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.LateUpdate();

                // Same within-cover pan as BRG case (a): full cover recompute, no new tiles loaded.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 1.0, Latitude = 0.0 });
                view.LateUpdate(); // consume the pan; now steady.
                Assert.AreEqual(16, view.LoadedTileCount(),
                    "z2 cover is the whole world (4×4); a within-cover pan loads no new tiles");

                // Measure over MANY frames: a single Entities Tick is alloc-free, but its system groups
                // allocate INTERMITTENTLY, so a 1-frame sample under-reports.
                const int N = 50;
                string verdict;
                try
                {
                    Assert.That(() => { for (int i = 0; i < N; i++) view.LateUpdate(); },
                                Is.Not.AllocatingGCMemory());
                    verdict = $"NO GC allocation across {N} steady-state Ticks";
                }
                catch (AssertionException)
                {
                    // Trips on ≥1 GC.Alloc sampler call over the run; the constraint's byte/count actual
                    // prints blank here, so report the trip (intermittent: a single Tick does not trip).
                    verdict = $"ALLOCATES across {N} Ticks (GC.Alloc recorder tripped; intermittent)";
                }
                TestContext.WriteLine($"[S53b alloc] MapView.LateUpdate (Backend=Entities) steady-state: {verdict}");
            }
            finally
            {
                view.Teardown();
            }
        }
    }
}
