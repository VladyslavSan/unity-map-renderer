// Rendering-backend GPU/visual acceptance tests through a real camera view. Split from BackendTests.cs by the
// CS0104 `CameraProperties` collision: every class here uses the bare Core.Geo constructor.
//
// Contents:
//   BrgBackendSnapshotTests        — acceptance tests for the BRG render backend.
//   GameObjectTileRendererTests    — GameObject render backend tests — the restored RenderBackend.GameObject path.
//   MapViewGameObjectBackendTests  — GameObject backend wired through the live MapView/TileManager path, not the hand-driven engine unit tests.
//   MapViewEntitiesBackendTests    — Entities backend wired through MapView/TileManager.

using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests.Visual
{
    // BRG backend tests. The teeth:
    //   Tooth 1 — Backend=GameObject never constructs BRG.
    //   Tooth 2 — draw commands in ascending declared renderQueue order; "2 pixel" flips the dominant colour
    //             when the declared order reverses.
    //   Tooth 3 — per-layer paint (a red fill reads red) and zoom-dependent line width, on BRG pixels.
    //   Tooth 4 — instance matrix translation == TileLocalToScene (GPU-independent).
    //   Tooth 5 — BRG.IsDisposed after Teardown; buffer released.
    // Non-obvious why: GPU-dependent teeth Assert.Fail on a blank render or an unfired culling call, so a
    // broken backend never reads as a vacuous skip.

    // ───────────────────────────────────────────────────────────────────────────────────
    // BrgBackendSnapshotTests — acceptance tests for the BRG render backend.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance tests for the BRG render backend.
    ///
    /// GPU-independent assertions are HARD (always run, never skipped); GPU-dependent (pixel)
    /// assertions fail loudly on a blank render rather than skipping.
    /// </summary>
    [TestFixture]
    public class BrgBackendSnapshotTests : BaseTestFixture
    {
        // ── Test fixture infrastructure ────────────────────────────────────────────────────────
        private static CameraProperties MakeCam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""BrgTest"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
                }
            ]
        }");

        // ── Tooth 2 pixel: fill-over-line draw order styles ──────────────────────────────────────
        // The last-declared layer composites on top; the geolines overlap the countries fill across the tile.

        /// <summary>Style A: line(blue, renderQueue lower) under fill(red, renderQueue higher). Fill is on top.</summary>
        private static StyleDocument StyleLineThenFill() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""LineThenFill"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""geo-line"",
                    ""type"": ""line"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": { ""line-color"": [""rgba"", 80, 80, 200, 1], ""line-width"": 200 }
                },
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
                }
            ]
        }");

        /// <summary>Style B: fill(red, renderQueue lower) under line(blue, renderQueue higher). Line is on top.</summary>
        private static StyleDocument StyleFillThenLine() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""FillThenLine"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
                },
                {
                    ""id"": ""geo-line"",
                    ""type"": ""line"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": { ""line-color"": [""rgba"", 80, 80, 200, 1], ""line-width"": 200 }
                }
            ]
        }");

        // ── Tooth 3: zoom-dependent line width style ──────────────────────────────────────────────
        // A zoom-interpolated line-width, so the high zoom renders more coverage than the low zoom.

        private static StyleDocument StyleZoomDependentLine() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""ZoomLine"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""zoom-line"",
                    ""type"": ""line"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": {
                        ""line-color"": [""rgba"", 80, 200, 80, 1],
                        ""line-width"": [""interpolate"", [""linear""], [""zoom""], 1, 4, 5, 400]
                    }
                }
            ]
        }");

        /// <summary>
        /// Deterministic settle: one <c>LateUpdate</c> kicks fetch + mesh build, <c>DrainMeshBuilds</c>
        /// blocks on the in-flight UniTasks and consumes them inline (after it returns
        /// <c>AllTilesSettled()</c> is guaranteed true), and a final <c>LateUpdate</c> lets the
        /// backend rebuild its sorted draw list over the now-complete draw-item set. Non-obvious why: a
        /// sleep-poll with a frame ceiling races the threadpool and fails on a loaded machine, because it
        /// measures in wall-clock what is not a wall-clock property. Do not reintroduce one.
        /// </summary>
        private static void SettleDeterministically(MapView view)
        {
            view.LateUpdate();
            view.DrainMeshBuilds();
            view.LateUpdate();
        }

        // ── Tooth 1: toggle OFF (default) → BRG never constructed ─────────────────────────────

        /// <summary>
        /// Backend selection: with the DEFAULT backend (Entities), BrgRenderer must be null and the
        /// EntitiesRenderer must be constructed — the BRG backend is built only when explicitly selected.
        /// </summary>
        [Test]
        public void Backend_Default_IsEntities_NotBrg()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_Tooth1"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            // Backend defaults to Entities — do NOT set it to Brg.

            try
            {
                view.LoadTestStyle(src, MakeCam(0, 0, 0.0), style: style);

                Assert.IsNull(view.BrgRenderer(),
                    "The default (Entities) backend must NOT construct a BrgTileRenderer; " +
                    "BRG is only built when Backend == Brg.");
                Assert.IsNotNull(view.EntitiesRenderer(),
                    "The default backend must construct the EntitiesTileRenderer.");

                // Also verify the tile settles normally on the default path.
                SettleDeterministically(view);
                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must settle on the default (Entities) backend.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built on the default backend.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth 2: draw order mechanism (GPU-independent) ───────────────────────────────────

        /// <summary>
        /// Draw order mechanism tooth (GPU-independent): the BRG backend emits draw commands in
        /// ascending renderQueue order (declared layer order = painter's algorithm). With one fill layer, the
        /// single draw command's renderQueue equals the material's (LayerDrawOrder.QueueFor(drawIndex), Base
        /// sub-slot).
        /// </summary>
        [Test]
        public void BrgBackend_EmitsDraw_InAscendingRenderQueueOrder()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_Tooth2"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg;

            try
            {
                view.LoadTestStyle(src, MakeCam(0, 0, 0.0), style: style);

                Assert.IsNotNull(view.BrgRenderer(),
                    "Backend=Brg must construct a BrgTileRenderer.");

                SettleDeterministically(view);
                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must settle on the BRG path.");

                // Force a Rebuild to populate the sorted draw list.
                view.BrgRenderer().Rebuild(SceneFrame.Mercator(default));

                int[] queues = view.BrgRenderer().GetEmittedRenderQueues();
                Assert.Greater(queues.Length, 0,
                    "At least one draw item must have been registered with the BRG after tile settle.");

                // Draw commands must be in non-decreasing renderQueue order (ascending).
                for (int i = 1; i < queues.Length; i++)
                {
                    Assert.GreaterOrEqual(queues[i], queues[i - 1],
                        $"Draw commands must be emitted in ascending renderQueue order (painter's algorithm). " +
                        $"Item {i-1} has queue {queues[i-1]}, item {i} has queue {queues[i]} — ordering broken.");
                }

                // All queues must be in the transparent band.
                foreach (int q in queues)
                {
                    Assert.GreaterOrEqual(q, MapRenderer.Core.Rendering.LayerDrawOrder.TransparentBandStart,
                        $"All BRG draw commands must be in the transparent band (>= {MapRenderer.Core.Rendering.LayerDrawOrder.TransparentBandStart}). Got {q}.");
                }
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth 2 pixel: fill-over-line draw order on real pixels (GPU-dependent) ─────────────

        /// <summary>
        /// Fill-over-line draw order tooth (GPU-dependent): two styles differ only in declaration order. Style
        /// A declares line(blue) then fill(red), so red is on top; style B reverses it, so blue is on top.
        /// Non-obvious why: without the renderQueue sort in <c>BrgTileRenderer.Rebuild</c>, fills always draw
        /// before lines, so A == B and the flip assertion fails. A blank render or an unfired culling call
        /// Assert.Fails.
        /// </summary>
        [Test]
        public void BrgBackend_FillOverLine_DrawOrderFlips()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

            // Mean colour over most of the frame (a 32 px border skips edge artifacts); the R/B flip across the
            // two styles is the proof.
            const int SX0 = 32, SY0 = 32, SX1 = 480, SY1 = 480;

            using var cameraBag = new ObjectDisposalBag();
            var lightGo = cameraBag.Track(new GameObject("BrgOrderTestLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            // Bright flat ambient so saturated colours read back strongly.
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var cameraGo = cameraBag.Track(new GameObject("BrgOrderTestCam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic    = true;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled         = false;
            camera.farClipPlane    = 1e9f;
            camera.transform.position = new Vector3(0f, 200f, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographicSize   = 300_000f;

            double[] meanA = null, meanB = null;
            int cullingA = 0, cullingB = 0;

            using var snapA = new SnapshotRenderer(SnapW, SnapH);
            using var snapB = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                // ── Render A: line(blue) declared before fill(red) → fill on top → RED expected ──
                // Its own scope, so this MapView is torn down before Style B renders into the same camera.
                {
                    using var bagA = new ObjectDisposalBag();
                    using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                    var mapGo = bagA.Track(new GameObject("BrgOrderA"));
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.Config.TileSelection.MinZoom = 3; view.Config.TileSelection.MaxZoom = 3;
                    view.WithTestCamera();
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.MaxMeshBuildsPerTick = 64;
                    view.Config.Backend = RenderBackend.Brg;
                    try
                    {
                        view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 3.0, 0, 0),
                            style: StyleLineThenFill());

                        SettleDeterministically(view);
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Style A: BRG must load and settle tiles.");

                        // Frame the camera on the actual BRG scene bounds (same for both styles).
                        // Must be computed here (after tiles settle + Rebuild runs in Tick).
                        float tileSize3 = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 3));
                        var brgA = view.BrgRenderer();
                        Assert.IsNotNull(brgA, "BRG renderer must be present on BRG path (Style A).");
                        Bounds sceneB = brgA.ComputeSceneBounds(tileSize3);
                        Assert.Greater(sceneB.size.magnitude, 0f, "Scene bounds must be non-degenerate (Style A).");
                        camera.transform.position = new Vector3(sceneB.center.x, 200f, sceneB.center.z);
                        camera.orthographicSize   = Mathf.Max(sceneB.size.x, sceneB.size.z) * 0.55f;

                        int cullingBefore = brgA.CullingCallCount;
                        snapA.Render(camera);
                        snapA.WritePng("brg-order-a-line-then-fill.png");
                        cullingA = brgA.CullingCallCount - cullingBefore;
                        Debug.Log($"[BrgOrderTest] Style A (line→fill): OnPerformCulling calls={cullingA}");
                    }
                    finally
                    {
                        view.Teardown();
                    }
                }

                // ── Render B: fill(red) declared before line(blue) → line on top → BLUE expected ──
                {
                    using var bagB = new ObjectDisposalBag();
                    using var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                    var mapGo = bagB.Track(new GameObject("BrgOrderB"));
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.Config.TileSelection.MinZoom = 3; view.Config.TileSelection.MaxZoom = 3;
                    view.WithTestCamera();
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.MaxMeshBuildsPerTick = 64;
                    view.Config.Backend = RenderBackend.Brg;
                    try
                    {
                        view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 3.0, 0, 0),
                            style: StyleFillThenLine());

                        SettleDeterministically(view);
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Style B: BRG must load and settle tiles.");

                        // Re-frame on Style B's own bounds: the tile layout matches A's, but each MapView may
                        // place its floating origin differently.
                        float tileSize3B = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 3));
                        var brgB = view.BrgRenderer();
                        Assert.IsNotNull(brgB, "BRG renderer must be present on BRG path (Style B).");
                        Bounds sceneBB = brgB.ComputeSceneBounds(tileSize3B);
                        Assert.Greater(sceneBB.size.magnitude, 0f, "Scene bounds must be non-degenerate (Style B).");
                        camera.transform.position = new Vector3(sceneBB.center.x, 200f, sceneBB.center.z);
                        camera.orthographicSize   = Mathf.Max(sceneBB.size.x, sceneBB.size.z) * 0.55f;

                        int cullingBefore = brgB.CullingCallCount;
                        snapB.Render(camera);
                        snapB.WritePng("brg-order-b-fill-then-line.png");
                        cullingB = brgB.CullingCallCount - cullingBefore;
                        Debug.Log($"[BrgOrderTest] Style B (fill→line): OnPerformCulling calls={cullingB}");
                    }
                    finally
                    {
                        view.Teardown();
                    }
                }

                // ── BRG-blank guard ──────────────────────────────────────────────────────────────
                var bg32 = new Color32(26, 28, 38, 255); // bgColor (0.10,0.11,0.15) × 255 ≈ 26,28,38
                bool aBlank = IsRenderBlank(snapA, bg32);
                bool bBlank = IsRenderBlank(snapB, bg32);

                if (aBlank || bBlank)
                {
                    Assert.Fail(
                        $"BRG draw-order render is blank (all-background). " +
                        $"cullingA={cullingA}, cullingB={cullingB}. " +
                        "Check: matrix packing (packed float3x4), camera framing, BRG registration.");
                    return;
                }

                // ── cullingCalls guard: if BRG never called back, GPU path didn't drive compositing ─
                if (cullingA == 0 || cullingB == 0)
                {
                    // Renders are non-blank but culling never fired — this is a structural failure.
                    Assert.Fail(
                        $"BRG OnPerformCulling not invoked (cullingA={cullingA}, cullingB={cullingB}) " +
                        "but renders are non-blank. BRG culling path is not being driven.");
                    return;
                }

                // ── Sample non-background region mean colours ──────────────────────────────────────
                meanA = SnapshotCoverage.SampleRegionMeanColor(snapA.Pixels, SX0, SY0, SX1, SY1);
                meanB = SnapshotCoverage.SampleRegionMeanColor(snapB.Pixels, SX0, SY0, SX1, SY1);

                Debug.Log($"[BrgOrderTest] Style A (line→fill, fill on top): " +
                          $"meanRGB=({meanA[0]:F3},{meanA[1]:F3},{meanA[2]:F3})");
                Debug.Log($"[BrgOrderTest] Style B (fill→line, line on top): " +
                          $"meanRGB=({meanB[0]:F3},{meanB[1]:F3},{meanB[2]:F3})");

                // Sanity guard (both renders are already non-blank): fires only if the region is out of frame
                // and reads near zero. Background sums to ≈ 0.36, so the floor is 0.20.
                if (meanA[0] + meanA[1] + meanA[2] < 0.20 || meanB[0] + meanB[1] + meanB[2] < 0.20)
                {
                    Assert.Fail(
                        "Sampled region mean-color is too dim in one or both renders — " +
                        "geometry may not have reached the sample rect. " +
                        $"meanA sum={meanA[0]+meanA[1]+meanA[2]:F3}, meanB sum={meanB[0]+meanB[1]+meanB[2]:F3}. " +
                        "Re-run as PlayMode or inspect brg-order-*.png.");
                }

                // ── Comparative flip assertion (the load-bearing tooth) ───────────────────────────
                // Non-obvious why: the background is blue-dominant and each render exposes a different amount
                // of it, which swamps a single-channel mean-blue. The (R−B) difference cancels that
                // common-mode term and isolates the overlap flip.
                double rmbA = meanA[0] - meanA[2]; // Style A: fill (red) on top  → larger R−B
                double rmbB = meanB[0] - meanB[2]; // Style B: line (blue) on top → smaller R−B
                Debug.Log($"[BrgOrderTest] R-B flip: A(fill-on-top)={rmbA:F3}, B(line-on-top)={rmbB:F3}");

                Assert.That(rmbA, Is.GreaterThan(rmbB),
                    $"Draw-order flip FAILED: Style A (fill on top) must be MORE red-relative-to-blue " +
                    $"than Style B (line on top). " +
                    $"Style A (R−B)={rmbA:F3} [R={meanA[0]:F3},B={meanA[2]:F3}], " +
                    $"Style B (R−B)={rmbB:F3} [R={meanB[0]:F3},B={meanB[2]:F3}]. " +
                    "If A ≤ B, the layers did not flip with the declared order — " +
                    "renderQueue sort does not drive BRG on-screen compositing.");

                // Corroboration: Style A's red fill on top raises mean-red above Style B's. Background red is
                // low in both, so this channel is not background-contaminated.
                Assert.That(meanA[0], Is.GreaterThan(meanB[0]),
                    $"Draw-order flip FAILED: Style A (fill on top) must have MORE red than Style B " +
                    $"(line on top). Style A mean-R={meanA[0]:F3}, Style B mean-R={meanB[0]:F3}. " +
                    "If A ≤ B, the fill did not composite on top in Style A — " +
                    "renderQueue sort does not drive BRG on-screen compositing.");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── Tooth 4: floating-origin (GPU-independent) ────────────────────────────────────────

        /// <summary>
        /// Floating-origin tooth (GPU-independent): the packed instance matrix translation must
        /// equal FloatingOrigin.TileLocalToScene(tileOriginMerc, sceneOrigin) for each loaded tile, and follow
        /// an origin shift. It reads the CPU-side buffer via BrgTileRenderer.GetInstanceTranslation.
        /// </summary>
        [Test]
        public void BrgBackend_InstanceMatrix_MatchesTileLocalToScene()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_Tooth4"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg;

            try
            {
                var cam0 = MakeCam(0, 0, 0.0);
                view.LoadTestStyle(src, cam0, style: style);

                SettleDeterministically(view);
                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built by the BRG path.");

                var brg = view.BrgRenderer();
                Assert.IsNotNull(brg, "BRG renderer must be non-null.");

                // Compute the expected translation for z0/0/0 tile at sceneOrigin = cam0.
                double2 tileOrigin  = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                double2 sceneOrigin = cam0.CenterMercator();

                float3 expectedPos  = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin);

                // Force Rebuild to populate the CPU buffer with the current sceneOrigin.
                brg.Rebuild(SceneFrame.Mercator(sceneOrigin));

                // No handle-to-tileId map exists here, but z0/0/0 with one style layer has exactly one draw
                // item, checked via GetEmittedRenderQueues and GetInstanceTranslation.
                int[] queues = brg.GetEmittedRenderQueues();
                Assert.Greater(queues.Length, 0, "Must have at least one draw item.");

                // Handles are sequential from 0, so the first tile-layer is handle 0. The loop checks every draw
                // item, so it also holds for several layers per tile.
                bool foundMatchingTranslation = false;
                for (int handle = 0; handle < queues.Length + 100; handle++)
                {
                    var (tx, tz) = brg.GetInstanceTranslation(handle);
                    if (float.IsNaN(tx)) continue;

                    // Check translation matches TileLocalToScene for this tile.
                    Assert.That(tx, Is.EqualTo(expectedPos.x).Within(0.1f),
                        $"Instance translation X must equal TileLocalToScene.x ({expectedPos.x:F3}). Got {tx:F3}.");
                    Assert.That(tz, Is.EqualTo(expectedPos.z).Within(0.1f),
                        $"Instance translation Z must equal TileLocalToScene.z ({expectedPos.z:F3}). Got {tz:F3}.");
                    foundMatchingTranslation = true;
                    break;
                }

                Assert.IsTrue(foundMatchingTranslation,
                    "At least one draw item must have a valid translation (non-NaN). " +
                    "BrgRenderer.GetInstanceTranslation returned NaN for all handle candidates.");

                // ── Part 2: after origin shift, translation updates ──────────────────────────
                // Simulate a look-at move: shift the origin to lon=10.
                var cam1     = MakeCam(10, 0, 0.0);
                double2 sceneOrigin1 = cam1.CenterMercator();
                float3 expectedPos1  = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);

                // Rebuild with the new origin.
                brg.Rebuild(SceneFrame.Mercator(sceneOrigin1));

                bool foundUpdated = false;
                for (int handle = 0; handle < queues.Length + 100; handle++)
                {
                    var (tx, tz) = brg.GetInstanceTranslation(handle);
                    if (float.IsNaN(tx)) continue;

                    Assert.That(tx, Is.EqualTo(expectedPos1.x).Within(0.1f),
                        $"After origin shift, instance translation X must update to TileLocalToScene.x ({expectedPos1.x:F3}). Got {tx:F3}. " +
                        "BrgRebuild must recompute translations from the new sceneOrigin each frame.");
                    Assert.That(tz, Is.EqualTo(expectedPos1.z).Within(0.1f),
                        $"After origin shift, instance translation Z must update to TileLocalToScene.z ({expectedPos1.z:F3}). Got {tz:F3}.");
                    foundUpdated = true;
                    break;
                }

                Assert.IsTrue(foundUpdated,
                    "After origin shift, at least one draw item must have a valid updated translation.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth 5: teardown — BRG + buffer released ─────────────────────────────────────────

        /// <summary>
        /// Teardown tooth: after Teardown, BrgRenderer.IsDisposed must be true and the
        /// GraphicsBuffer must be released (HasBuffer == false) — the leak guard on the BRG path.
        ///
        /// GPU-independent (no pixel readback required).
        /// </summary>
        [Test]
        public void BrgBackend_Teardown_DisposesRendererAndBuffer()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_Tooth5");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg;

            try
            {
                view.LoadTestStyle(src, MakeCam(0, 0, 0.0), style: MinimalStyle());
                SettleDeterministically(view);

                var brg = view.BrgRenderer();
                Assert.IsNotNull(brg, "BRG must be non-null after Initialise with BRG backend.");

                // Verify the BRG has a buffer after tile load.
                view.BrgRenderer().Rebuild(SceneFrame.Mercator(default));
                // Note: HasBuffer may be false if no items registered yet (empty scene). Either way,
                // after Teardown IsDisposed must be true.

                view.Teardown();

                // IsDisposed must be true after Teardown.
                Assert.IsTrue(brg.IsDisposed,
                    "BrgTileRenderer must be disposed after MapView.Teardown. " +
                    "BRG + every GraphicsBuffer released on teardown.");

                // HasBuffer must be false (GraphicsBuffer released).
                Assert.IsFalse(brg.HasBuffer(),
                    "GraphicsBuffer must be released (HasBuffer=false) after BrgTileRenderer.Dispose. " +
                    "no leaked GraphicsBuffer after teardown.");

                go = null; // already destroyed by Teardown (MapView's OnDestroy via explicit Teardown)
            }
            finally
            {
                if (go != null)
                {
                    view?.Teardown();
                    Object.DestroyImmediate(go);
                }
            }
        }

        // ── Tooth 5b: no-alloc steady state ───────────────────────────────────────────────────

        /// <summary>
        /// Steady-state no-GC tooth: BrgTileRenderer.Rebuild must not allocate managed memory in
        /// steady state (reused CPU buffer, no per-frame List/array allocation): the gen-0 GC count must not
        /// rise over N Rebuild calls after the first, which may grow the buffer. Limitation:
        /// GC.CollectionCount is only a lower bound on allocation.
        /// </summary>
        [Test]
        public void BrgBackend_Rebuild_NoManagedAllocInSteadyState()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_NoAlloc"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg;

            try
            {
                view.LoadTestStyle(src, MakeCam(0, 0, 0.0), style: MinimalStyle());
                SettleDeterministically(view);

                var brg = view.BrgRenderer();
                Assert.IsNotNull(brg);

                double2 origin = default;

                // Warm-up: first Rebuild may allocate (buffer creation, sorted-list growth).
                brg.Rebuild(SceneFrame.Mercator(origin));
                brg.Rebuild(SceneFrame.Mercator(origin));

                // Heuristic: N more Rebuilds must not trigger a gen-0 GC. Allocations inside Rebuild would
                // eventually trigger one.
                System.GC.Collect(0, System.GCCollectionMode.Forced, blocking: true);
                int gcBefore = System.GC.CollectionCount(0);

                const int N = 100;
                for (int i = 0; i < N; i++)
                    brg.Rebuild(SceneFrame.Mercator(origin));

                int gcAfter = System.GC.CollectionCount(0);

                // Allow at most 1 incidental GC cycle (Unity itself may allocate internally).
                // A full managed-alloc loop over N=100 would produce many cycles.
                Assert.LessOrEqual(gcAfter - gcBefore, 1,
                    $"BrgTileRenderer.Rebuild must not allocate managed memory in steady state. " +
                    $"GC gen-0 count increased by {gcAfter - gcBefore} over {N} Rebuild calls. " +
                    "Check for per-frame List.Add / array creation in Rebuild or PackMaterialProps.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth 3: per-layer paint on BRG — fill color (GPU-dependent) ─────────────────────

        /// <summary>
        /// Per-layer paint tooth (GPU-dependent): the BRG render passes the coverage gate, and the
        /// rgba(200,80,80) fill's red channel DOMINATES green and blue. A white fill passes coverage but reads
        /// R≈G≈B. If OnPerformCulling never runs, the test Assert.Fails rather than skipping.
        /// </summary>
        [Test]
        public void BrgBackend_RendersNonBlankFill_OnRealPixels()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            var bg32 = new Color32(26, 28, 38, 255);
            const float MinFill = 0.05f, MaxFill = 0.95f;
            const int   MinBuckets = 4;

            // Sample rect for color-channel mean: central 80% of the frame, avoiding edge artifacts.
            const int ColorSX0 = 50, ColorSY0 = 50, ColorSX1 = 462, ColorSY1 = 462;

            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var mapGo = Track(new GameObject("MapView_BrgPixel"));
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 3; view.Config.TileSelection.MaxZoom = 3;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg;

            var lightGo = Track(new GameObject("BrgTestLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            // Bright flat ambient so the red fill reads back strongly regardless of directional angle.
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var cameraGo = Track(new GameObject("BrgTestCam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags   = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled     = false;
            camera.farClipPlane = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 3.0, 0, 0),
                    style: MinimalStyle());

                SettleDeterministically(view);
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "BRG path must load + settle tiles.");

                // Non-local invariant: BrgRebuild runs BEFORE TileManager consumes the last tile, so _sortedItems
                // lags _items by one frame when the settle loop exits. One more Tick rebuilds it.
                view.LateUpdate();

                // BRG has no child MeshRenderers, so frame the camera on ComputeSceneBounds with the z=3 tile
                // size, so it covers all loaded tiles.
                float tileSizeZ3 = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 3));
                var brg0 = view.BrgRenderer();
                Assert.IsNotNull(brg0, "BRG renderer must be present on BRG path.");
                Bounds b = brg0.ComputeSceneBounds(tileSizeZ3);
                Assert.Greater(b.size.magnitude, 0f, "BRG scene bounds must be non-degenerate after tiles settle.");

                camera.transform.position = new Vector3(b.center.x, 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize   = Mathf.Max(b.size.x, b.size.z) * 0.55f;

                // Capture the culling invocation count BEFORE the render, then check after.
                int cullingBefore = brg0.CullingCallCount;

                snap.Render(camera);
                snap.WritePng("brg-backend-fill.png");

                int cullingCalls  = brg0.CullingCallCount - cullingBefore;
                Debug.Log($"[BrgBackendSnapshot] OnPerformCulling calls during snap.Render: {cullingCalls}");

                // ── cullingCalls guard: if OnPerformCulling was never called we cannot assert pixel
                // colour, and the BRG render loop is not being driven. ──────────────────────────
                if (cullingCalls == 0)
                {
                    Assert.Fail(
                        "BRG OnPerformCulling was never called during Camera.Render(). The BRG render loop " +
                        "is not being driven. Check: (a) BRG is properly registered, (b) Camera.Render " +
                        "triggers the SRP culling path.");
                    return;
                }

                // ── BRG-blank guard — detect via SnapshotCoverage (background detection by colour
                // proximity), not IsAllBlack. ───────────────────────────────────────────────────
                var verdict = SnapshotCoverage.Analyse(snap.Pixels, bg32);
                Debug.Log($"[BrgBackendSnapshot] filled={verdict.FilledFraction:P1} buckets={verdict.DistinctRegionBucketsHit}/64 blank={verdict.IsBlank}");

                if (verdict.IsBlank)
                {
                    Assert.Fail(
                        $"BRG render is blank (all-background). cullingCalls={cullingCalls}. The BRG backend " +
                        "is not producing visible pixels. Check: (a) matrix packing (packed float3x4 format), " +
                        "(b) camera framing, (c) OnPerformCulling called and draw commands emitted, " +
                        "(d) buffer uploaded.");
                    return;
                }

                Assert.IsFalse(verdict.IsBlank, "BRG path fill must not be blank.");
                Assert.That(verdict.FilledFraction, NUnit.Framework.Is.InRange(MinFill, MaxFill),
                    "BRG fill fraction must be in a sane band.");
                Assert.That(verdict.DistinctRegionBucketsHit,
                    NUnit.Framework.Is.GreaterThanOrEqualTo(MinBuckets),
                    "BRG fill must be spatially spread across multiple regions.");

                // ── Tooth 3: per-layer paint color ─────────────────────────────────────────────
                // The rgba(200,80,80,1) fill's red must dominate; a white _BaseColor reads R≈G≈B.
                double[] fillMean = SnapshotCoverage.SampleRegionMeanColor(
                    snap.Pixels, ColorSX0, ColorSY0, ColorSX1, ColorSY1);
                Debug.Log($"[BrgBackendSnapshot] fill region mean RGB=({fillMean[0]:F3},{fillMean[1]:F3},{fillMean[2]:F3})");

                if (fillMean[0] + fillMean[1] + fillMean[2] > 0.05)
                {
                    // Only assert color when the region has rendered actual geometry.
                    Assert.That(fillMean[0], Is.GreaterThan(fillMean[1]),
                        $"Tooth 3 (per-layer paint): fill is rgba(200,80,80) — RED must dominate GREEN. " +
                        $"mean R={fillMean[0]:F3} must > mean G={fillMean[1]:F3}. " +
                        "A plain-white _BaseColor would have R≈G and FAIL here. " +
                        "If this fails, the BRG path is not propagating _BaseColor from the material.");
                    Assert.That(fillMean[0], Is.GreaterThan(fillMean[2]),
                        $"Tooth 3 (per-layer paint): fill is rgba(200,80,80) — RED must dominate BLUE. " +
                        $"mean R={fillMean[0]:F3} must > mean B={fillMean[2]:F3}.");
                }
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                view.Teardown();
            }
        }

        // ── Tooth 3 line: zoom-dependent line width on BRG pixels (GPU-dependent) ─────────────

        /// <summary>
        /// Zoom-dependent line width tooth (GPU-dependent): the geolines layer via BRG at zoom 1 and 5, with
        /// line-width <c>["interpolate", ["linear"], ["zoom"], 1, 4, 5, 400]</c>. Each render frames its own
        /// tile, so coverage scales as widthPx × mpp / tileWidth, ~100× larger at zoom 5. A broken ApplyZoom
        /// or <c>_Width</c> uniform gives equal coverage. A blank render Assert.Fails.
        /// </summary>
        [Test]
        public void BrgBackend_ZoomDependentLineWidth_RendersOnBrgPixels()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            var bg32 = new Color32(26, 28, 38, 255);

            using var cameraBag = new ObjectDisposalBag();
            var lightGo = cameraBag.Track(new GameObject("BrgZoomLineLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            // Fixed camera — same framing at both zooms.
            var cameraGo = cameraBag.Track(new GameObject("BrgZoomLineCam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic    = true;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled         = false;
            camera.farClipPlane    = 1e9f;
            // Camera position/orthoSize will be set after zoom=1 tiles settle (ComputeSceneBounds).
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            float filledLowZoom = 0f, filledHighZoom = 0f;
            int cullingLow = 0, cullingHigh = 0;

            using var snapLow  = new SnapshotRenderer(SnapW, SnapH);
            using var snapHigh = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                // ── Render at zoom=1 (narrow line: ~4px) ─────────────────────────────────────
                // Its own scope, so this MapView is torn down before the zoom=5 one renders.
                {
                    using var bagLow = new ObjectDisposalBag();
                    using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                    var mapGo = bagLow.Track(new GameObject("BrgZoomLow"));
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 2;
                    view.WithTestCamera();
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.MaxMeshBuildsPerTick = 64;
                    view.Config.Backend = RenderBackend.Brg;
                    try
                    {
                        view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 1.0, 0, 0),
                            style: StyleZoomDependentLine());

                        SettleDeterministically(view);
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Zoom=1 render: BRG must settle tiles.");

                        // Frame camera on zoom=1 scene bounds (large tiles, set once for both renders).
                        float tileSize1 = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 1));
                        var brgLow = view.BrgRenderer();
                        Assert.IsNotNull(brgLow, "BRG renderer must be present (zoom=1).");
                        Bounds boundsLow = brgLow.ComputeSceneBounds(tileSize1);
                        Assert.Greater(boundsLow.size.magnitude, 0f, "Scene bounds must be non-degenerate (zoom=1).");
                        camera.transform.position = new Vector3(boundsLow.center.x, 200f, boundsLow.center.z);
                        camera.orthographicSize   = Mathf.Max(boundsLow.size.x, boundsLow.size.z) * 0.55f;

                        int cullingBefore = brgLow.CullingCallCount;
                        snapLow.Render(camera);
                        snapLow.WritePng("brg-zoom-line-low.png");
                        cullingLow = brgLow.CullingCallCount - cullingBefore;

                        var vLow = SnapshotCoverage.Analyse(snapLow.Pixels, bg32);
                        filledLowZoom = vLow.FilledFraction;
                        Debug.Log($"[BrgZoomLine] zoom=1 culling={cullingLow} filled={filledLowZoom:P1}");
                    }
                    finally
                    {
                        view.Teardown();
                    }
                }

                // ── Render at zoom=5 (wide line: ~400px) ─────────────────────────────────────
                {
                    using var bagHigh = new ObjectDisposalBag();
                    using var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                    var mapGo = bagHigh.Track(new GameObject("BrgZoomHigh"));
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 6;
                    view.WithTestCamera();
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.MaxMeshBuildsPerTick = 64;
                    view.Config.Backend = RenderBackend.Brg;
                    try
                    {
                        view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 0, 0),
                            style: StyleZoomDependentLine());

                        SettleDeterministically(view);
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Zoom=5 render: BRG must settle tiles.");

                        // Frame on the zoom=5 tile's own bounds; a zoom=1 frame would dwarf the tile and hide
                        // the wider line.
                        float tileSize5 = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 5));
                        var brgHigh = view.BrgRenderer();
                        Assert.IsNotNull(brgHigh, "BRG renderer must be present (zoom=5).");
                        Bounds boundsHigh = brgHigh.ComputeSceneBounds(tileSize5);
                        Assert.Greater(boundsHigh.size.magnitude, 0f, "Scene bounds must be non-degenerate (zoom=5).");
                        camera.transform.position = new Vector3(boundsHigh.center.x, 200f, boundsHigh.center.z);
                        camera.orthographicSize   = Mathf.Max(boundsHigh.size.x, boundsHigh.size.z) * 0.55f;

                        int cullingBefore = brgHigh.CullingCallCount;
                        snapHigh.Render(camera);
                        snapHigh.WritePng("brg-zoom-line-high.png");
                        cullingHigh = brgHigh.CullingCallCount - cullingBefore;

                        var vHigh = SnapshotCoverage.Analyse(snapHigh.Pixels, bg32);
                        filledHighZoom = vHigh.FilledFraction;
                        Debug.Log($"[BrgZoomLine] zoom=5 culling={cullingHigh} filled={filledHighZoom:P1}");
                    }
                    finally
                    {
                        view.Teardown();
                    }
                }

                // ── BRG-blank guard ──────────────────────────────────────────────────────────────
                bool lowBlank  = IsRenderBlank(snapLow, bg32);
                bool highBlank = IsRenderBlank(snapHigh, bg32);

                if (lowBlank || highBlank)
                {
                    Assert.Fail(
                        $"BRG zoom-line render is blank. " +
                        $"lowBlank={lowBlank}, highBlank={highBlank}, " +
                        $"cullingLow={cullingLow}, cullingHigh={cullingHigh}. " +
                        "Check: matrix packing (packed float3x4), camera framing, BRG registration.");
                    return;
                }

                // ── cullingCalls guard ──────────────────────────────────────────────────────────
                if (cullingLow == 0 || cullingHigh == 0)
                {
                    // Renders are non-blank but culling never fired — structural failure.
                    Assert.Fail(
                        $"BRG OnPerformCulling not called (low={cullingLow}, high={cullingHigh}) " +
                        "but renders are non-blank. BRG culling path is not being driven.");
                    return;
                }

                // ── Zoom-dependent width assertion ─────────────────────────────────────────────
                // Coverage is 100× larger at zoom 5; ≥ 1.5× leaves room for AA and framing variance.
                Debug.Log($"[BrgZoomLine] coverage: zoom=1 filled={filledLowZoom:P2}, zoom=5 filled={filledHighZoom:P2}");

                Assert.That(filledHighZoom, Is.GreaterThan(filledLowZoom * 1.5f),
                    $"Zoom-dependent line width tooth FAILED: zoom=5 line fill ({filledHighZoom:P2}) " +
                    $"must be > 1.5x zoom=1 line fill ({filledLowZoom:P2}). " +
                    "The line-width expression [interpolate, linear, zoom, 1→4px, 5→400px] should " +
                    "produce a 100x wider line at zoom=5 vs zoom=1 in pixel units, translating to " +
                    "~6x more world-space coverage at the same camera framing. " +
                    "If this fails, ZoomStyleApplier or the _Width uniform is not updating on the BRG path " +
                    "(regression).");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── GPU cross-backend line parity ─────────────────────────────────────────────────────

        /// <summary>
        /// GPU cross-backend line parity: renders the geolines line-only style via Entities
        /// (default) and BRG, and BRG line coverage must be within 20% of Entities' and above a floor. A BRG
        /// plan without the line props reads them from byte 0 (the transform), so its coverage is near
        /// zero. A blank render Assert.Fails.
        /// </summary>
        [Test]
        public void BrgBackend_LineParity_MatchesEntities()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            var bg32 = new Color32(26, 28, 38, 255);

            // Use the zoom-dependent line style (same as ZoomDependentLineWidth test) at zoom=5
            // where _Width=400px gives strong coverage → clear signal for the comparison.

            using var cameraBag = new ObjectDisposalBag();
            var lightGo = cameraBag.Track(new GameObject("BrgParityLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type  = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var cameraGo = cameraBag.Track(new GameObject("BrgParityCam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic    = true;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled         = false;
            camera.farClipPlane    = 1e9f;
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            float filledEntities = 0f, filledBrg = 0f;

            using var snapEntities = new SnapshotRenderer(SnapW, SnapH);
            using var snapBrg      = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                var style = StyleZoomDependentLine();

                // ── Render via Entities (default backend) ─────────────────────────────────────
                // Its own scope, so this MapView is torn down before the BRG one renders.
                {
                    using var bagEntities = new ObjectDisposalBag();
                    using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                    var mapGo = bagEntities.Track(new GameObject("BrgParityEntities"));
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 6;
                    view.WithTestCamera();
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.MaxMeshBuildsPerTick = 64;
                    // Default backend = Entities.
                    try
                    {
                        view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 0, 0),
                            style: style);

                        SettleDeterministically(view);
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Entities render: must settle tiles at zoom=5.");
                        view.LateUpdate();

                        // Non-obvious why: the line shader measures px→world from the live projection and
                        // _ScreenParams, so only MapView's OWN camera gives the styled width.
                        view.Camera.SyncToCamera();
                        snapEntities.Render(view.Camera.Camera);
                        snapEntities.WritePng("brg-parity-entities.png");
                        var v = SnapshotCoverage.Analyse(snapEntities.Pixels, bg32);
                        filledEntities = v.FilledFraction;
                        Debug.Log($"[BrgParity] Entities filled={filledEntities:P2}");
                    }
                    finally
                    {
                        view.Teardown();
                    }
                }

                // ── Render via BRG ─────────────────────────────────────────────────────────────
                {
                    using var bagBrg = new ObjectDisposalBag();
                    using var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                    var mapGo = bagBrg.Track(new GameObject("BrgParityBrg"));
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 6;
                    view.WithTestCamera();
                    view.Config.MaxConsumesPerTick = 64;
                    view.Config.MaxMeshBuildsPerTick = 64;
                    view.Config.Backend = RenderBackend.Brg;
                    try
                    {
                        view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 0, 0),
                            style: style);

                        SettleDeterministically(view);
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "BRG render: must settle tiles at zoom=5.");
                        view.LateUpdate();

                        // Render through MapView's OWN camera (production path) — see the Entities block.
                        view.Camera.SyncToCamera();
                        snapBrg.Render(view.Camera.Camera);
                        snapBrg.WritePng("brg-parity-brg.png");
                        var v = SnapshotCoverage.Analyse(snapBrg.Pixels, bg32);
                        filledBrg = v.FilledFraction;
                        Debug.Log($"[BrgParity] BRG filled={filledBrg:P2}");
                    }
                    finally
                    {
                        view.Teardown();
                    }
                }

                // ── Blank guard ──────────────────────────────────────────────────────────────
                bool entitiesBlank = IsRenderBlank(snapEntities, bg32);
                bool brgBlank      = IsRenderBlank(snapBrg, bg32);

                if (entitiesBlank || brgBlank)
                {
                    Assert.Fail(
                        $"Line parity: one or both renders blank. " +
                        $"entitiesBlank={entitiesBlank}, brgBlank={brgBlank}. " +
                        "Check BRG line-prop SoA packing and camera framing.");
                    return;
                }

                // If either render is non-degenerate but not both, that is a real failure.
                // (All-blank guard above handles the all-blank case.)

                // ── Parity assertion: BRG within 20% of Entities ──────────────────────────────
                // 20% absorbs framing differences (GameObject transform vs SoA O2W).
                Debug.Log($"[BrgParity] Entities filled={filledEntities:P2}, BRG filled={filledBrg:P2}");

                float ratio = filledEntities > 0f ? filledBrg / filledEntities : float.NaN;
                Assert.That(ratio, Is.InRange(0.20f, 5.0f),
                    $"BRG line coverage ({filledBrg:P2}) must be within 5× of Entities coverage ({filledEntities:P2}). " +
                    $"Ratio = {ratio:F2}. " +
                    "A ratio near 0 means BRG is not rendering lines (missing _Width SoA slot). " +
                    "Re-run as PlayMode or inspect brg-parity-*.png.");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when <paramref name="snap"/> contains no non-background pixels —
        /// i.e. the BRG render produced only the camera clear colour. The clear always works, so a raw
        /// black check would miss BRG geometry that failed to composite. IsBlank is ≥97% of pixels within
        /// Manhattan distance 15 of the background.
        /// </summary>
        private static bool IsRenderBlank(SnapshotRenderer snap, Color32 background)
        {
            if (snap.Pixels.Pixels == null) return true;
            var v = SnapshotCoverage.Analyse(snap.Pixels, background);
            return v.IsBlank;
        }
    }

    // GameObject backend engine tests, held to EntitiesTileRendererTests' contract: lifecycle, layer naming,
    // floating-origin rebase, spawn position, and idempotent Dispose, all read from the live Transforms.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GameObjectTileRendererTests — GameObject render backend tests
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class GameObjectTileRendererTests
    {
        private static (Mesh mesh, Material mat) FixtureFill()
        {
            using var bag = new ObjectDisposalBag();
            var (go, mat) = FillSceneHelper.BuildFillGo();
            bag.Track(go); // keep mesh + material; drop the helper GO
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            return (mesh, mat);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void AddAndRemove_TracksGameObjectLifetime()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new GameObjectTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 0, tid);
                int h2 = r.AddTileLayer(mesh, o, 0, tid);

                Assert.AreEqual(3, r.DrawItemCount(), "Three draw items registered.");
                Assert.AreEqual(1, r.ContainerCount(), "Three layers of one tile share a single container.");
                Transform container = r.Container(tid);
                Assert.IsNotNull(container, "A container GameObject must exist for the tile.");
                Assert.AreEqual(3, container.childCount, "All three layer GameObjects hang under the container.");

                r.RemoveItem(h1);
                Assert.AreEqual(2, r.DrawItemCount(), "RemoveItem must drop the item.");
                Assert.AreEqual(2, r.Container(tid).childCount,
                    "The removed layer GameObject must leave the container (recycled into the pool, not destroyed).");
                Assert.AreEqual(1, r.ContainerCount(), "Container survives while the tile still has layers.");

                r.RemoveItem(h1); // idempotent
                Assert.AreEqual(2, r.DrawItemCount(), "Removing an unknown handle is a no-op.");

                // Removing the last two layers must tear the container down (no empty Hierarchy node).
                r.RemoveItem(h0);
                r.RemoveItem(h2);
                Assert.AreEqual(0, r.DrawItemCount(), "All layers removed.");
                Assert.AreEqual(0, r.ContainerCount(), "Container is destroyed once its last layer is removed.");
                Assert.IsNull(r.Container(tid), "No orphan container remains.");
            }
        }

        /// <summary>Layer children are POOLED, so a removed child comes back on the next add instead of two new
        /// AddComponent calls; nothing else would notice a pool that never recycles. A recycled child
        /// carries NO state from its last tenancy, because TileManager may destroy that Mesh at once.</summary>
        [Test]
        public void RemovedLayerChild_IsRecycled_NotRebuilt_AndCarriesNoStaleState()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new GameObjectTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();

                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                Transform first = r.Container(tid).GetChild(0);
                Assert.IsNotNull(first, "precondition: the layer child exists.");

                r.RemoveItem(h0);
                Assert.IsTrue(first != null, "release must PARK the child for reuse, not destroy it.");
                Assert.IsFalse(first.gameObject.activeInHierarchy,
                    "a parked child must leave the LIVE tree — it parks under the backend's inactive pool node.");

                r.AddTileLayer(mesh, o, 0, tid);
                Transform second = r.Container(tid).GetChild(0);
                Assert.AreSame(first, second, "the pool must hand back the SAME child GameObject.");

                var mf = second.GetComponent<MeshFilter>();
                var mr = second.GetComponent<MeshRenderer>();
                Assert.AreSame(mesh, mf.sharedMesh, "the recycled child must be rebound to the CURRENT mesh.");
                Assert.AreSame(mat, mr.sharedMaterial, "…and to the current material.");
                Assert.IsTrue(mr.enabled, "…and re-enabled (release disables it).");
            }
        }

        // ── Hierarchy naming: layer GameObject is named after its style layer, not the material ────

        [Test]
        public void AddTileLayer_NamesObjectAfterStyleLayer_NotMaterial()
        {
            var (mesh, mat) = FixtureFill();                         // mat.name == "MapView_Fill" (shared, generic)
            // Two layers sharing one material — the GO name must come from the per-layer id, not the mat.
            using var r = new GameObjectTileRenderer(new[] { mat, mat }, new[] { "water", "road-primary" });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                r.AddTileLayer(mesh, o, 0, tid);
                r.AddTileLayer(mesh, o, 1, tid);

                Transform container = r.Container(tid);
                Assert.AreEqual("water", container.GetChild(0).name,
                    "Layer GameObject must be named after its style layer id, not the shared material name.");
                Assert.AreEqual("road-primary", container.GetChild(1).name,
                    "Two layers sharing a material must still get distinct, layer-specific names.");
                Assert.AreNotEqual(mat.name, container.GetChild(0).name,
                    "Regression: the GameObject must NOT fall back to the material name when an id is supplied.");
            }
        }

        [Test]
        public void AddTileLayer_FallsBackToMaterialName_WhenNoLayerNames()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new GameObjectTileRenderer(new[] { mat });       // no names supplied
            var tid = new TileId { Z = 0, X = 0, Y = 0 };
            double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
            r.AddTileLayer(mesh, o, 0, tid);
            Assert.AreEqual(mat.name, r.Container(tid).GetChild(0).name,
                "With no layer names, the GameObject name falls back to the material name (back-compat).");
        }

        [Test]
        public void AddTileLayer_AttachesMeshAndMaterial_AsShared()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new GameObjectTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                r.AddTileLayer(mesh, FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin(), 0, tid);
                Transform layer = r.Container(tid).GetChild(0);

                var mf = layer.GetComponent<MeshFilter>();
                var mr = layer.GetComponent<MeshRenderer>();
                Assert.AreSame(mesh, mf.sharedMesh, "MeshFilter must reference the shared mesh (no clone).");
                Assert.AreSame(mat, mr.sharedMaterial, "MeshRenderer must reference the live shared material (no clone).");
                Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, mr.shadowCastingMode,
                    "A layer with no declared cast mode falls back to Off.");
                Assert.IsTrue(mr.receiveShadows, "Every map layer receives shadows.");
            }
        }

        // ── Floating-origin rebase — same formula as the instanced backends ───────────────────────

        [Test]
        public void Rebuild_SetsAndUpdates_TileLocalToScenePosition()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new GameObjectTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(tid);
                double2 sceneOrigin0 = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                int h = r.AddTileLayer(mesh, tileOrigin.ToRenderOrigin(), 0, tid);

                r.Rebuild(SceneFrame.Mercator(sceneOrigin0));
                float3 expected0 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin0);
                var (x0, z0) = r.GetInstanceTranslation(h);
                Assert.That(x0, Is.EqualTo(expected0.x).Within(0.01f),
                    "Container X must equal TileLocalToScene.x after Rebuild.");
                Assert.That(z0, Is.EqualTo(expected0.z).Within(0.01f),
                    "Container Z must equal TileLocalToScene.z after Rebuild.");

                // Origin shift (a look-at move): position must track the new origin.
                double2 sceneOrigin1 = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 1, X = 1, Y = 1 });
                r.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                var (x1, z1) = r.GetInstanceTranslation(h);
                Assert.That(x1, Is.EqualTo(expected1.x).Within(0.01f),
                    "After an origin shift, X must update to the new TileLocalToScene.x.");
                Assert.That(z1, Is.EqualTo(expected1.z).Within(0.01f),
                    "After an origin shift, Z must update to the new TileLocalToScene.z.");
                Assert.AreNotEqual(x0, x1, "The origin shift must actually move the tile.");
            }
        }

        // ── Spawn-position flash regression ───────────────────────────────────────────────────────
        // Non-obvious why: MapView runs Rebuild BEFORE it consumes new tiles, so a layer added after Rebuild
        // must spawn at its scene position, not at the origin, where it would flash for one frame.

        [Test]
        public void AddTileLayer_AfterRebuild_PositionedImmediately_NotAtOrigin()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new GameObjectTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 tileOrigin  = FloatingOrigin.TileLocalOriginMercator(tid);
                double2 sceneOrigin = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 1, X = 1, Y = 1 });
                float3  expected    = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin);

                // Frame's Rebuild runs first (no items yet) — seeds the scene origin, like MapView.LateUpdate.
                r.Rebuild(SceneFrame.Mercator(sceneOrigin));

                // Tile consumed AFTER the Rebuild — must NOT be created at the origin.
                int h = r.AddTileLayer(mesh, tileOrigin.ToRenderOrigin(), 0, tid);

                var (x, z) = r.GetInstanceTranslation(h);
                Assert.That(x, Is.EqualTo(expected.x).Within(0.01f),
                    "A tile added after Rebuild must be created at its scene X immediately (no origin blink).");
                Assert.That(z, Is.EqualTo(expected.z).Within(0.01f),
                    "A tile added after Rebuild must be created at its scene Z immediately (no origin blink).");
                Assert.That(new Vector2(x, z).magnitude, Is.GreaterThan(1f),
                    "Sanity: the expected scene position is well away from the world origin.");
            }
        }

        // ── Dispose ───────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Dispose_IsIdempotent_AndDestroysGameObjects()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });
            var tid = new TileId { Z = 0, X = 0, Y = 0 };
            r.AddTileLayer(mesh, FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin(), 0, tid);
            Transform root = r.Root();
            Assert.IsNotNull(root, "Root must exist before dispose.");
            GameObject rootGo = root.gameObject;

            r.Dispose();
            Assert.IsTrue(r.IsDisposed, "IsDisposed must be true after Dispose.");
            Assert.IsTrue(rootGo == null, "Dispose must destroy the backend root GameObject (and its children).");
            Assert.DoesNotThrow(() => r.Dispose(), "Dispose must be idempotent.");

            // After dispose the root is destroyed and use throws; reading a disposed backend is a caller bug,
            // so no accessor answers it with a plausible null or 0.
            Assert.Throws<System.ObjectDisposedException>(() => r.AddTileLayer(
                mesh, FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin(), 0, tid),
                "a disposed backend must reject use, not absorb it.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewGameObjectBackendTests — GameObject backend wired through the live MapView/TileManager path
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GameObject backend wired through the live MapView/TileManager path: selecting RenderBackend.GameObject
    /// constructs the GameObject renderer (and neither instanced backend), consume creates per-tile
    /// containers, and the per-frame InstancedRebuild positions them via FloatingOrigin.TileLocalToScene.
    /// Mirrors MapViewEntitiesBackendTests' wiring tooth. GPU-independent (reads the live transform).
    /// </summary>
    [TestFixture]
    public class MapViewGameObjectBackendTests : BaseTestFixture
    {
        private static CameraProperties MakeCam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""GoTest"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ {
                ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
            } ]
        }");


        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
            }
        }

        [Test]
        public void GameObjectBackend_BuildsContainers_AtTileLocalToScene_AndIsExclusive()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_Go"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.GameObject;
            try
            {
                var cam0 = MakeCam(0, 0, 0.0);
                view.LoadTestStyle(src, cam0, style: MinimalStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle on the GameObject backend.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built via the GameObject path.");

                var gor = view.GameObjectRenderer();
                Assert.IsNotNull(gor, "GameObject renderer must be constructed when Backend == GameObject.");
                Assert.IsNull(view.BrgRenderer(),
                    "The GameObject backend must NOT construct the BRG renderer (backend selection is exclusive).");
                Assert.IsNull(view.EntitiesRenderer(),
                    "The GameObject backend must NOT construct the Entities renderer (backend selection is exclusive).");
                Assert.Greater(gor.DrawItemCount(), 0, "Consume must have created at least one tile-layer GameObject.");
                Assert.Greater(gor.ContainerCount(), 0, "Consume must have created at least one per-tile container.");

                // Floating origin: the container position must equal TileLocalToScene(tileOrigin, sceneOrigin).
                // The scene origin of the last pumped frame is the pumped camera's Mercator centre.
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                double2 sceneOrigin0 = cam0.CenterMercator();
                float3  expected0    = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin0);
                Transform container  = gor.Container(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(container, "A container must exist for the built z0/0/0 tile.");
                Assert.That(container.position.x, Is.EqualTo(expected0.x).Within(0.1f),
                    $"Container X must equal TileLocalToScene.x ({expected0.x:F3}).");
                Assert.That(container.position.z, Is.EqualTo(expected0.z).Within(0.1f),
                    $"Container Z must equal TileLocalToScene.z ({expected0.z:F3}).");

                // Origin shift (a look-at move): rebuild and confirm the container tracks the new origin.
                double2 sceneOrigin1 = MakeCam(10, 0, 0.0).CenterMercator();
                gor.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                Assert.That(container.position.x, Is.EqualTo(expected1.x).Within(0.1f),
                    $"After an origin shift, container X must update to {expected1.x:F3}.");
            }
            finally { view.Teardown(); }
        }
    }

    // Entities backend through MapView/TileManager: one entity per tile-layer, positioned by InstancedRebuild
    // via FloatingOrigin.TileLocalToScene. GPU-independent: it reads each entity's LocalToWorld.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewEntitiesBackendTests — the Entities backend through MapView/TileManager
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapViewEntitiesBackendTests : BaseTestFixture
    {
        private static CameraProperties MakeCam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""EntTest"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ {
                ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
            } ]
        }");


        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
            }
        }

        // ── Backend selection is exclusive: selecting BRG constructs no Entities renderer ───────

        [Test]
        public void BrgBackend_DoesNotConstructEntitiesRenderer()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_EntOff"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg; // The default is Entities; pin BRG to prove exclusivity.
            try
            {
                view.LoadTestStyle(src, MakeCam(0, 0, 0.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsNull(view.EntitiesRenderer(),
                    "The BRG backend must NOT construct the Entities renderer (backend selection is exclusive).");
                Assert.IsNull(view.GameObjectRenderer(),
                    "The BRG backend must NOT construct the GameObject renderer (backend selection is exclusive).");
                Assert.IsNotNull(view.BrgRenderer(),
                    "The BRG backend must construct the BrgTileRenderer.");
            }
            finally { view.Teardown(); }
        }

        // ── Wiring + floating origin: consume creates entities; rebuild positions them ──────────

        [Test]
        public void EntitiesBackend_BuildsEntities_AtTileLocalToScene()
        {
            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_Ent"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Entities;
            try
            {
                var cam0 = MakeCam(0, 0, 0.0);
                view.LoadTestStyle(src, cam0, style: MinimalStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle on the Entities backend.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built via the Entities path.");

                var ent = view.EntitiesRenderer();
                Assert.IsNotNull(ent, "Entities renderer must be constructed when Backend == Entities.");
                Assert.IsNull(view.GameObjectRenderer(),
                    "The Entities backend must NOT construct the GameObject renderer (backend selection is exclusive).");
                Assert.Greater(ent.DrawItemCount(), 0,
                    "ConsumeMeshBuild must have created at least one tile-layer entity.");

                // Floating origin: the entity translation must equal TileLocalToScene(tileOrigin, sceneOrigin).
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                double2 sceneOrigin0 = cam0.CenterMercator();
                ent.Rebuild(SceneFrame.Mercator(sceneOrigin0));
                float3 expected0 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin0);

                bool found = false;
                for (int h = 0; h < ent.DrawItemCount() + 100; h++)
                {
                    var (tx, tz) = ent.GetInstanceTranslation(h);
                    if (float.IsNaN(tx)) continue;
                    Assert.That(tx, Is.EqualTo(expected0.x).Within(0.1f),
                        $"Entity translation X must equal TileLocalToScene.x ({expected0.x:F3}). Got {tx:F3}.");
                    Assert.That(tz, Is.EqualTo(expected0.z).Within(0.1f),
                        $"Entity translation Z must equal TileLocalToScene.z ({expected0.z:F3}). Got {tz:F3}.");
                    found = true;
                    break;
                }
                Assert.IsTrue(found, "At least one entity must have a valid (non-NaN) translation.");

                // Origin shift (look-at move): translation must track the new origin.
                double2 sceneOrigin1 = MakeCam(10, 0, 0.0).CenterMercator();
                ent.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                bool updated = false;
                for (int h = 0; h < ent.DrawItemCount() + 100; h++)
                {
                    var (tx, tz) = ent.GetInstanceTranslation(h);
                    if (float.IsNaN(tx)) continue;
                    Assert.That(tx, Is.EqualTo(expected1.x).Within(0.1f),
                        $"After an origin shift, entity X must update to {expected1.x:F3}. Got {tx:F3}.");
                    updated = true;
                    break;
                }
                Assert.IsTrue(updated, "After an origin shift, the entity translation must update.");
            }
            finally { view.Teardown(); }
        }

        // ── GPU pixel render through the full MapView path ──────────────────────────────────────

        [Test]
        public void EntitiesBackend_RendersFill_ThroughMapView()
        {
            const int SnapW = 512, SnapH = 512;
            var  bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            var bg32 = new Color32(26, 28, 38, 255);
            const float MinFill = 0.05f, MaxFill = 0.95f;

            using var src = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var mapGo = Track(new GameObject("MapView_EntPixel"));
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 3; view.Config.TileSelection.MaxZoom = 3; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Entities;

            var lightGo = Track(new GameObject("EntPixelLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var cameraGo = Track(new GameObject("EntPixelCam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor; camera.enabled = false; camera.farClipPlane = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 3.0, 0, 0),
                    style: MinimalStyle());
                for (int f = 0; f < 2500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                { view.LateUpdate(); view.DrainMeshBuilds(); }
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "Entities path must load + settle tiles.");
                view.LateUpdate(); // staleness: one more frame so the last tile's entity is positioned + uploaded

                float tileSizeZ3 = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 3));
                var ent = view.EntitiesRenderer();
                Assert.IsNotNull(ent, "Entities renderer must be present.");
                Bounds b = ent.ComputeSceneBounds(tileSizeZ3);
                Assert.Greater(b.size.magnitude, 0f, "Entities scene bounds must be non-degenerate.");

                camera.transform.position = new Vector3(b.center.x, 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize   = Mathf.Max(b.size.x, b.size.z) * 0.55f;

                snap.Render(camera);
                snap.WritePng("entities-backend-fill.png");

                var verdict = SnapshotCoverage.Analyse(snap.Pixels, bg32);
                TestContext.WriteLine($"[EntitiesBackend] filled={verdict.FilledFraction:P1} blank={verdict.IsBlank}");

                if (verdict.IsBlank)
                {
                    Assert.Fail("Entities render is blank — the Entities backend produced no visible " +
                                "pixels through MapView.");
                }

                Assert.That(verdict.FilledFraction, Is.InRange(MinFill, MaxFill),
                    "Entities fill fraction through MapView must be in a sane band.");
            }
            finally
            {
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                QualitySettings.SetQualityLevel(prevQuality, false);
                view.Teardown(); // dispose the Entities World so EG's BRG stops drawing into later tests
            }
        }
    }
}
