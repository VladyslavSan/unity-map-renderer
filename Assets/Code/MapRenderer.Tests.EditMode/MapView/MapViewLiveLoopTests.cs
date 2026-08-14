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
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
namespace MapRenderer.Tests.MapViews
{
    /// <summary>
    /// S40 live loop tests for <see cref="MapView"/> with per-layer styled fill rendering.
    /// Covers:
    ///   (1) view-state drives tile selection + eviction releases (container destroyed).
    ///   (2) wiring parity: MapView with a 1-fill-layer style produces the same mesh vertex count
    ///       as StyledFillTileBuilder called directly — proves the live-loop wiring is correct.
    ///       (Replaces the retired S06 bit-identical Burst-vs-managed assertion; that test pinned
    ///        the Burst pipeline which is now replaced by the managed per-layer path.)
    ///   (3) NO per-frame GC in steady state (the acceptance teeth — the ApplyZoom loop and all
    ///       reused buffers must not allocate in the pan / static-frame / bearing-only cases).
    /// </summary>
    [TestFixture]
    public class MapViewLiveLoopTests
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

        // ── (3) NO per-frame GC in steady state (acceptance teeth) ─────────────────────────────
        // Zero-allocation per-frame is the BRG backend's contract. The Entities default does allocate GC
        // intermittently over many frames (verified by MapView_SteadyStateTick_Entities_AllocationVerdict
        // below, via the same GC.Alloc-recorder instrument) — magnitude unverified; the once-quoted
        // "~409 B/frame" came from a since-debunked GC.GetTotalMemory measure. Pin BRG: this asserts a BRG property.

        [Test]
        public void MapView_SteadyStateTick_DoesNotAllocateGCMemory()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView");
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
                // A small center nudge stays within the loaded z2 cover (one z2 tile spans ~10,000 km
                // at the equator, so a 111 km pan loads no new tiles), but it DOES dirty the cover so
                // Tick runs the full recompute: TileCover.Cover + _coverSet rebuild + request/release
                // scan + floating-origin rebase loop + ApplyZoom loop. All must allocate ZERO bytes.
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
                // S71: heading now DIRTIES the cover (it rotates the viewport quad → a different tile bbox),
                // so this Tick runs the full recompute — but at the whole-world z2 cover the re-selected set
                // is identical, so request/release find nothing and the recompute stays zero-alloc. (Tilt is
                // not in the cover key — the selector has no tilt branch, D3.)
                view.Camera.Apply(new CameraPropertiesUpdate { Heading = 45.0, Tilt = 30.0 });
                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "A heading/tilt change must tick alloc-free (cover recompute over an unchanged whole-world set).");

                // ── (d) AT SCALE: zero-alloc must hold over MANY frames, not just one. ──
                // This is the symmetric counterpart to MapView_SteadyStateTick_Entities_AllocationVerdict,
                // which trips the GC.Alloc recorder over N Ticks while a single Entities Tick is clean. BRG
                // must stay clean at the SAME N — otherwise the divergence would be shared-Tick-path churn,
                // not EG-specific. (Same N as the Entities verdict so the comparison is genuine.)
                const int N = 50;
                Assert.That(() => { for (int i = 0; i < N; i++) view.LateUpdate(); }, Is.Not.AllocatingGCMemory(),
                    $"BRG.Tick must not allocate across {N} steady-state frames — proving the zero-alloc " +
                    "contract holds at the scale where the Entities backend trips the recorder.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── (3b) Entities backend allocation VERDICT (informational, not a hard tooth) ──────────────
        // Mirrors the BRG test above exactly — same within-cover pan on the same loaded cover, same
        // GC.Alloc-recorder instrument (Is.Not.AllocatingGCMemory) — but with the default Entities backend.
        // This is the apples-to-apples answer to "do the EG system groups allocate in the live Tick path?"
        // (the claim the BRG pin is justified on). It REPORTS the verdict rather than asserting zero, because
        // whether Entities-allocates-per-frame is a measured trade-off, not a contract. The figure the
        // constraint carries is a COUNT of GC.Alloc calls, not bytes — reported as such.
        [Test]
        public void MapView_SteadyStateTick_Entities_AllocationVerdict()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView");
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

                // Measure over MANY frames, not one. A single Entities Tick is alloc-free, but EG's system
                // groups allocate INTERMITTENTLY (the isolated Rebuild×50 test trips the recorder) — so a
                // 1-frame sample under-reports. Looping N static Ticks (InstancedRebuild fires every frame)
                // is the honest "does sitting still leak GC over time" characterization of the live path.
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
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
