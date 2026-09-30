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
using MapRenderer.Unity.View.Cameras;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.Jobs.Mvt;
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
                // Warm up: load the whole cover and let every tile settle. A real (full-world) bounds gate
                // — HasBounds=true, not the default — so the steady-state tick below actually measures
                // AdmitsTile's overlap branch, not the HasBounds-false early-out.
                double[] d = StyleParser.DefaultBounds;
                var fullWorldBounds = new GeoBounds { West = d[0], South = d[1], East = d[2], North = d[3], HasBounds = true };
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: style, bounds: fullWorldBounds);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime the reused buffers (_cover, _coverSet, _toRelease) to steady capacity.
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
            }
            finally
            {
                view.Teardown();
            }
        }
    }
}
