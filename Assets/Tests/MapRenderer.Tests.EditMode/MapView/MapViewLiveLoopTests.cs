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
            MapView NewView(double preload)
            {
                var view = Track(new GameObject("MapView")).AddComponent<MapView>().WithTestMaterials();
                view.Config.Backend = RenderBackend.Brg;
                view.Config.TileSelection.MinZoom = 14;
                view.Config.TileSelection.MaxZoom = 15;
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
                var gate     = new UniTaskCompletionSource<bool>();
                var gatedSrc = TestDataSource.FromFetch(async tile =>
                {
                    if (tile.Z == 14) await gate.Task;
                    return new TileResponse(SampleTileFixture.Bytes(), TileEncoding.Mvt);
                });
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
                    gate.TrySetResult(true);
                    for (var settle = SettleTimeout.Start(); settle.Running && !gatedView.TryGetBuiltTile(ancestor);)
                    {
                        gatedView.LateUpdate();
                        Thread.Sleep(1);
                    }

                    Assert.IsTrue(gatedView.TryGetBuiltTile(ancestor), "the source's ancestor finished loading.");
                    gatedView.LateUpdate(); // the backend sorts its items at the start of a frame, so one more reads what the last Update showed
                    Assert.AreEqual(1, EmittedFor(gatedView, 1), "the ancestor's fill shows once it registers, though no tile of its own is shown.");
                }
                finally
                {
                    gate.TrySetResult(true);
                    gatedView.Teardown();
                }
            }
            finally
            {
                view.Teardown();
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

                // Prime the reused buffers (_cover, _servedKeys, _toRelease) to steady capacity.
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
                view.Config.TileSelection.MinZoom = 1;
                view.Config.TileSelection.MaxZoom = 3;
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.25, Latitude = 0.0 }); // a moved camera, so the cover recomputes
                PumpUntilSettled(view);
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

                // ── (f) HELD: the z2 tiles wait for z3 tiles that are built but not yet registered (consume blocked). The swap
                // step walks their areas on every Update, and a holding tick must not allocate.
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
                view.Teardown();
            }
        }
    }
}
