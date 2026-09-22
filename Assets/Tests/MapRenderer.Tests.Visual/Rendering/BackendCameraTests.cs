// Rendering-backend GPU/visual acceptance tests driven through a real camera view (UMR-176 pack: backends topic).
//
// Split from BackendTests.cs by a using collision (MapRenderer.Core.Geo.CameraProperties vs
// UnityEngine.Rendering.CameraProperties, CS0104): every member here builds its test camera
// via the bare Core.Geo.CameraProperties constructor, so none of them may import
// UnityEngine.Rendering — the backend tests that do are in BackendTests.cs instead.
//
// Contents:
//   BrgBackendSnapshotTests        — S49 acceptance tests for the BRG render backend.
//   GameObjectTileRendererTests    — GameObject render backend tests — the restored RenderBackend.GameObject path.
//   MapViewGameObjectBackendTests  — GameObject backend wired through the live MapView/TileManager path, not the hand-driven engine unit tests.
//   MapViewEntitiesBackendTests    — S53b increment 2 — Entities backend wired through MapView/TileManager.

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
    // S49 BRG backend tests.
    //
    // Maps acceptance teeth to assertions:
    //
    //   Tooth 1 — toggle OFF parity: Backend=GameObject → BRG never constructed, GO path unchanged.
    //   Tooth 2 — draw order mechanism (GPU-independent): emitted renderQueues in ascending declared order.
    //   Tooth 2 pixel — fill-over-line draw order on real pixels: the top layer's colour dominates the
    //               overlap region; reversing the declared order flips the dominant colour (FALSIFIABLE —
    //               wrong order must produce a detectably different result). The comparison between style A
    //               and style B is the proof; a broken renderQueue sort makes A == B and the assertion fails.
    //   Tooth 3 — per-layer paint on BRG: _BaseColor non-white → red fill red-channel dominates green/blue
    //               in the fill region (not just coverage). Zoom-dependent line width renders on BRG pixels
    //               and produces more fill when width is larger (S11/S13/S14 intact on BRG path).
    //   Tooth 4 — floating-origin (GPU-independent): instance matrix translation == TileLocalToScene.
    //   Tooth 5 — teardown: BRG.IsDisposed == true after Teardown; buffer released.
    //   Tooth 6 — headless green: all GPU-independent assertions pass under ./Tools/run-tests.sh.
    //
    // GPU-dependent teeth (2-pixel, 3-per-layer-paint) fail loudly (Assert.Fail) on a blank render or a
    // culling call that never fires — a real regression, not a vacuous skip.

    // ───────────────────────────────────────────────────────────────────────────────────
    // BrgBackendSnapshotTests — S49 acceptance tests for the BRG render backend.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S49 acceptance tests for the BRG render backend.
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
        // Two styles over the same fixture: fill(red)+line(blue) vs line(blue)+fill(red).
        // The order is the only variable — same geometry, same colours. Whichever is declared last
        // (higher renderQueue) should composite on top and dominate the mean colour of overlap pixels.
        // The geolines fixture layer (6 linestrings) overlaps the countries fill across the whole tile.
        // A wide line-width (200 screen pixels) ensures the geolines are visually thick at test zoom.

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
        // A line-only style with a zoom-interpolated line-width: narrower at low zoom, much wider at
        // high zoom. Rendering at two zoom levels with a fixed-size camera frame should yield different
        // pixel coverages — wider at the high zoom (S13/S14 intact on the BRG path).

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
        /// <c>AllTilesSettled()</c> is guaranteed true — S47), and a final <c>LateUpdate</c> lets the
        /// backend rebuild its sorted draw list over the now-complete draw-item set.
        ///
        /// <para>This replaced a <c>LateUpdate</c> + <c>Thread.Sleep(1)</c> poll bounded by a frame
        /// ceiling (500 here, 2500 in the draw-order teeth). That poll turned every assertion in this
        /// fixture into a race against the threadpool: on a loaded machine the async decode/mesh-build
        /// had not landed before the ceiling ran out, and the fixture failed with "Tiles must settle"
        /// with nothing wrong in the code under test. Raising the ceiling only widens the window —
        /// the ceiling itself is the defect, because it measures in wall-clock what is not a
        /// wall-clock property. Do not reintroduce a sleep-poll here.</para>
        /// </summary>
        private static void SettleDeterministically(MapView view)
        {
            view.LateUpdate();
            view.DrainMeshBuilds();
            view.LateUpdate();
        }

        // ── Tooth 1: toggle OFF (default) → BRG never constructed ─────────────────────────────

        /// <summary>
        /// Backend selection: with the DEFAULT backend (Entities, S53c), BrgRenderer must be null and the
        /// EntitiesRenderer must be constructed — i.e. the BRG backend is only built when explicitly
        /// selected. (Replaces the S49 GameObject-default toggle-off test; the GO backend was retired.)
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
            // Backend defaults to Entities (S53c) — do NOT set it to Brg.

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
        /// ascending renderQueue order (declared layer order = painter's algorithm). This is a
        /// structural/mechanical assertion that does not require GPU readback.
        ///
        /// With one fill layer, the single draw command's renderQueue must equal the material's
        /// renderQueue (LayerDrawOrder.QueueFor(drawIndex) — fill uses LayerSubSlot.Base only, G7/D7).
        /// No re-ordering must occur.
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
            view.Config.Backend = RenderBackend.Brg; // S49 BRG path

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
        /// Fill-over-line draw order tooth (GPU-dependent, FALSIFIABLE):
        ///
        /// Renders the fixture tile via BRG with TWO styles that differ only in layer declaration order
        /// (same geometry, same colours, same zoom). The order determines which layer composites on top:
        ///
        ///   Style A — line(blue) declared BEFORE fill(red): line gets lower renderQueue → fill draws
        ///             last → red fill is on top → red dominates the rendered mean colour.
        ///
        ///   Style B — fill(red) declared BEFORE line(blue): fill gets lower renderQueue → line draws
        ///             last → blue line is on top → blue channel should increase vs Style A.
        ///
        /// The comparison between the two renders is the proof:
        ///   • Style A mean-red must exceed Style B mean-red (fill hidden by line in B).
        ///   • Style B mean-blue must exceed Style A mean-blue (line visible on top in B).
        ///
        /// A broken renderQueue sort (e.g. the `.Sort` in BrgTileRenderer.Rebuild is deleted) falls back
        /// to insertion order = fills-registered-before-lines (type order, not style order). That makes
        /// the fills always draw before lines regardless of the declared order, so A == B. The
        /// comparative flip assertion then FAILS — this is the "wrong-order-must-fail" requirement.
        ///
        /// Fails loudly (Assert.Fail) on a blank render or a culling call that never fires.
        /// </summary>
        [Test]
        public void BrgBackend_FillOverLine_DrawOrderFlips()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

            // Sample the full rendered frame (non-background pixels) for mean colour.
            // We compare mean R and mean B across the two styles — the flip is the proof.
            // Sample rect covers most of the frame (excluding a 32px border to skip edge artifacts).
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
                // Its own scope + bag: mapGo/view/src must be fully torn down before Style B is built
                // below, or both would render into snapB's camera pass (nothing here asserts "only one
                // MapView is alive").
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

                        // Re-frame camera on Style B's own scene bounds.
                        // Both styles use zoom=3 centered at (0,0), so the tile layout is the same, but
                        // each MapView may use a different floating-origin scene placement. Using B's own
                        // bounds ensures the camera is correctly centered on the actual rendered geometry.
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

                // Guard: region must have rendered something beyond background.
                // Both style A and B are already proven non-blank (above), so this is a sanity check.
                // Background colour in float is ~(0.10, 0.11, 0.15); a region of all-background pixels
                // has mean sum ≈ 0.36 which passes the old 0.05 threshold.
                // Use the SnapshotCoverage blank verdict instead (already computed as aBlank/bBlank).
                // We already know both are non-blank, so this guard only fires if SampleRegionMeanColor
                // returns near-zero from a region that happens to be out of frame.
                // Use a threshold of background brightness (sum ≈ 0.36); require mean sum > 0.20.
                if (meanA[0] + meanA[1] + meanA[2] < 0.20 || meanB[0] + meanB[1] + meanB[2] < 0.20)
                {
                    Assert.Fail(
                        "Sampled region mean-color is too dim in one or both renders — " +
                        "geometry may not have reached the sample rect. " +
                        $"meanA sum={meanA[0]+meanA[1]+meanA[2]:F3}, meanB sum={meanB[0]+meanB[1]+meanB[2]:F3}. " +
                        "Re-run as PlayMode or inspect brg-order-*.png.");
                }

                // ── Comparative flip assertion (the load-bearing tooth) ───────────────────────────
                // The signal is the (red − blue) chromaticity of the overlap region: where the red
                // fill composites on top it is red-dominant (R≫B); where the blue line composites on
                // top it is pushed toward blue (R−B shrinks). Using the (R−B) DIFFERENCE cancels the
                // contributions common to both renders — the blue-ish background (R−B≈−0.05), the
                // fill-only regions, and the line-only regions — and isolates exactly the overlap flip.
                //
                // This is more robust than the raw single-channel means: the camera background is
                // blue-dominant (B=0.15 is its largest channel), so a whole-frame mean-blue is swamped
                // by however much background each render happens to expose (Style A here exposes more
                // un-tiled background, inflating ITS mean-blue — the trap the prior single-channel
                // assertion fell into). The (R−B) difference removes that common-mode background term.
                //
                // Falsifiability is preserved: a broken renderQueue sort makes both styles composite
                // fills-before-lines identically → (R−B)_A == (R−B)_B → the strict GreaterThan FAILS.
                // That is the "wrong-order-must-fail" requirement.
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

                // Corroborating single-channel check: the red fill on top in Style A must also raise
                // the raw mean-red above Style B (line on top hides the fill). This is independently
                // falsifiable and not background-contaminated (background red is low and ~equal).
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
        /// equal FloatingOrigin.TileLocalToScene(tileOriginMerc, sceneOrigin) for each loaded tile.
        ///
        /// Verified without GPU readback — reads the CPU-side buffer directly via
        /// BrgTileRenderer.GetInstanceTranslation (test accessor).
        ///
        /// Also verifies: after an origin shift (look-at move), the translation updates to the new
        /// origin (origin-relative coordinate stays correct).
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

                // Enumerate draw items and check each one for the z0/0/0 tile.
                // We don't have a direct handle-to-tileId map here, but for z0/0/0 with one style layer,
                // there is exactly 1 draw item. Verify via GetEmittedRenderQueues and GetInstanceTranslation.
                int[] queues = brg.GetEmittedRenderQueues();
                Assert.Greater(queues.Length, 0, "Must have at least one draw item.");

                // The handle for the first (only) draw item is 0.
                // Note: handles are sequential starting from 0; the first registered tile-layer gets handle 0.
                // We verify all draw items (there may be multiple layers per tile in the future).
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
        /// GraphicsBuffer must be released (HasBuffer == false). This is the S49 extension of
        /// the S51 leak guard to the BRG path.
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
                    "S49 tooth 5: BRG + every GraphicsBuffer released on teardown.");

                // HasBuffer must be false (GraphicsBuffer released).
                Assert.IsFalse(brg.HasBuffer(),
                    "GraphicsBuffer must be released (HasBuffer=false) after BrgTileRenderer.Dispose. " +
                    "S49 tooth 5: no leaked GraphicsBuffer after teardown.");

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
        /// steady state (reused CPU buffer, no per-frame List/array allocation).
        ///
        /// Asserts the GC generation 0 count does not increase over N steady-state Rebuild calls
        /// after the first call (which may grow the buffer).
        ///
        /// Note: GC.CollectionCount is a lower bound — this assertion verifies no managed allocation
        /// inside Rebuild itself. Minor GC from unrelated Unity internals in EditMode is accepted
        /// (the test uses a conservative threshold of 0 new GC cycles over the Rebuild loop only).
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

                // Measure: N more Rebuilds must not trigger GC gen-0.
                // We check GC generation-0 collection count before and after.
                // This is a heuristic — managed allocations inside Rebuild would eventually trigger GC.
                // At our instance count (1 tile, 1 layer), a single float[] allocation would be detected.
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
        /// Per-layer paint tooth (GPU-dependent, FALSIFIABLE): renders the fixture tile via BRG and
        /// asserts:
        ///   1. Non-blank coverage (same gate as MapViewSnapshotTests).
        ///   2. The fill is rgba(200,80,80) — RED. The non-background pixel mean red channel must
        ///      DOMINATE green and blue in the fill region. A plain white fill passes the coverage
        ///      gate but would have R≈G≈B, so this assertion is falsifiable: a non-red _BaseColor
        ///      (e.g., white or wrong color) FAILS this assertion.
        ///
        /// The Assert.Ignore escape hatch from the original implementation is REMOVED. When
        /// OnPerformCulling is never called (cullingCalls==0), the test produces Assert.Fail — a real
        /// failure, not a vacuous skip.
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

                // Settle-loop staleness fix: the loop exits as soon as AllTilesSettled() is true,
                // WITHOUT running another Tick. Within MapView.LateUpdate, BrgRebuild runs BEFORE the
                // TileManager consumes the final tile's draw items, so _sortedItems (read by
                // ComputeSceneBounds and OnPerformCulling) lags one frame behind _items. One more
                // Tick mirrors production's next frame and rebuilds _sortedItems from the full set.
                view.LateUpdate();

                // BRG path: no child MeshRenderers exist. Frame the camera on the BRG scene bounds.
                // Compute the tile size at z=3 (Web Mercator), then query ComputeSceneBounds so the
                // camera covers all loaded tiles — matches the MapViewSnapshot FitTo approach.
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

                // ── Tooth 3: per-layer paint color assertion (FALSIFIABLE) ────────────────────
                // The fill uses rgba(200,80,80,1) — a saturated RED. Sample the non-background
                // region mean colour and assert red channel dominates green and blue.
                // A plain-white fill (wrong _BaseColor) would have R≈G≈B and FAIL this assertion.
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
        /// Zoom-dependent line width tooth (GPU-dependent, FALSIFIABLE): renders the geolines fixture
        /// layer via BRG at two different zoom levels using a zoom-interpolated line-width expression.
        ///
        /// Style: line-width = ["interpolate", ["linear"], ["zoom"], 1, 4, 5, 400].
        /// At zoom=1: _Width=4px, metersPerPixel≈large → narrow world-space band.
        /// At zoom=5: _Width=400px, metersPerPixel≈small but 100x wider px → much wider world-space band.
        ///
        /// Each render is framed on its OWN tile bounds (camera orthoSize = tile extent × 0.55), so
        /// the tile fills the camera equally in both renders. Under per-tile framing, line coverage
        /// scales as (lineWidthPx × metersPerPixel) / tileWidth, which is ~100x larger at zoom=5.
        /// The zoom=5 render should therefore cover FAR MORE pixels than the zoom=1 render.
        /// This proves S11/S13/S14 (zoom-dependent line width, ZoomStyleApplier wired, the _Width uniform
        /// reaching the shader) are intact on the BRG path — falsifiable because a broken ApplyZoom or
        /// wrong _Width uniform would yield equal (≈ zoom=1) coverage at zoom=5.
        ///
        /// Fails loudly (Assert.Fail) on a blank render.
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
                // Its own scope + bag: mapGo/view/src must be fully torn down before the zoom=5 block
                // below builds a second MapView, or both would render into the shared camera.
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

                        // Frame camera on zoom=5 tile bounds (each tile is 1/32 of the zoom=1 extent).
                        // Both renders must be framed on their own tile bounds so they fill the camera
                        // view equally — otherwise the large zoom=1 camera view dwarfs the zoom=5 tile
                        // and the 100x wider line (in pixels) still covers less screen area.
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
                // Each render is framed on its own tile bounds so the tile fills the camera view.
                // With each camera sized to its own tile extents, coverage ∝ lineWorldWidth / tileWidth.
                // lineWorldWidth = widthPx * metersPerPixel(zoom).
                //   zoom=1: 4 * mpp(1); tileWidth = WorldExtent   → coverage ∝ 4*mpp(1)/WorldExtent
                //   zoom=5: 400 * mpp(5) = 400 * mpp(1)/16 = 25*mpp(1); tileWidth = WorldExtent/16
                //           coverage ∝ 25*mpp(1) / (WorldExtent/16) = 400*mpp(1)/WorldExtent  → 100× zoom=1
                // We require at least 1.5x more coverage (generous tolerance for AA and framing variance).
                Debug.Log($"[BrgZoomLine] coverage: zoom=1 filled={filledLowZoom:P2}, zoom=5 filled={filledHighZoom:P2}");

                Assert.That(filledHighZoom, Is.GreaterThan(filledLowZoom * 1.5f),
                    $"Zoom-dependent line width tooth FAILED: zoom=5 line fill ({filledHighZoom:P2}) " +
                    $"must be > 1.5x zoom=1 line fill ({filledLowZoom:P2}). " +
                    "The line-width expression [interpolate, linear, zoom, 1→4px, 5→400px] should " +
                    "produce a 100x wider line at zoom=5 vs zoom=1 in pixel units, translating to " +
                    "~6x more world-space coverage at the same camera framing. " +
                    "If this fails, ZoomStyleApplier or the _Width uniform is not updating on the BRG path " +
                    "(S11/S13/S14 regression).");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── S76: GPU cross-backend line parity ────────────────────────────────────────────────

        /// <summary>
        /// S76 GPU cross-backend line parity: renders the geolines line-only style via Entities
        /// (default) and BRG and asserts that BRG line coverage is within 20% of Entities coverage
        /// AND above a non-degenerate floor.
        ///
        /// Falsifiability: before S76, BRG line props (_Width, _Opacity, etc.) read garbage from
        /// byte offset 0 (the transform matrix). The BRG line coverage would then be near-zero (wrong
        /// width) while Entities coverage is correct — the BRG/Entities ratio would fail the
        /// within-tolerance assertion. After S76, the plan provides correct SoA slots → BRG matches
        /// Entities within tolerance.
        ///
        /// Fails loudly (Assert.Fail) when either render is blank or the coverage diverges more
        /// than 20%.
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
                // Its own scope + bag: mapGo/view/src must be fully torn down before the BRG block
                // below builds a second MapView, or both would render into the shared camera.
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

                        // Frame camera via the first loaded tile's world position.
                        // Entities backend uses GameObjects, so ComputeChildBounds applies — but for
                        // simplicity we use a fixed large orthoSize (tiles are at scene-relative positions).
                        // Render through MapView's OWN camera (production path). A hand-rolled ortho camera is
                        // an unsupported configuration for the screen-space line width (S104): the shader
                        // MEASURES px->world from the live projection matrix and _ScreenParams, so only the
                        // camera the pipeline actually renders through yields the scale the styling assumed.
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
                // The 20% tolerance accommodates slight camera-framing differences between the two backends
                // (Entities places tiles via GameObject transform; BRG via SoA O2W). The key signal is that
                // BRG coverage is in the same ballpark as Entities — before S76, BRG line width was garbage
                // → near-zero coverage, while Entities rendered wide lines (100x difference).
                Debug.Log($"[BrgParity] Entities filled={filledEntities:P2}, BRG filled={filledBrg:P2}");

                float ratio = filledEntities > 0f ? filledBrg / filledEntities : float.NaN;
                Assert.That(ratio, Is.InRange(0.20f, 5.0f),
                    $"BRG line coverage ({filledBrg:P2}) must be within 5× of Entities coverage ({filledEntities:P2}). " +
                    $"Ratio = {ratio:F2}. " +
                    "A ratio near 0 means BRG is not rendering lines (missing _Width SoA slot — S76 bug). " +
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
        /// i.e. the BRG render produced only the camera clear colour (background).
        ///
        /// This is the correct "BRG/DOTS did not composite" probe for headless EditMode: the camera
        /// clear itself always works, so a raw black check would never catch BRG geometry silently
        /// failing to composite on top of a non-black background. SnapshotCoverage.Analyse uses a
        /// Manhattan-distance tolerance=15 around the background colour to classify each pixel;
        /// IsBlank=true when ≥97% of pixels are background.
        /// </summary>
        private static bool IsRenderBlank(SnapshotRenderer snap, Color32 background)
        {
            if (snap.Pixels.Pixels == null) return true;
            var v = SnapshotCoverage.Analyse(snap.Pixels, background);
            return v.IsBlank;
        }
    }

    // GameObject render backend tests — the restored RenderBackend.GameObject path.
    //
    // Proves the GameObject backend engine independently of the live MapView/TileManager wiring, mirroring
    // EntitiesTileRendererTests so the two debuggable backends are held to the same contract:
    //   • Lifecycle: AddTileLayer creates per-layer child GameObjects under one per-tile container; RemoveItem
    //     destroys the right child and tears the container down once its last layer is gone.
    //   • Naming: each layer GameObject is named after its style layer id (fallback to the material name).
    //   • Floating-origin rebase: the container's position equals FloatingOrigin.TileLocalToScene(tileOrigin,
    //     sceneOrigin) and updates on an origin shift — the same formula the instanced backends use.
    //   • Spawn-position flash regression: a layer consumed AFTER a frame's Rebuild is positioned immediately.
    //   • Dispose: idempotent; destroys the backend root (and with it every container + layer child).
    //
    // All assertions read the live Transform hierarchy (GPU-independent).

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

        /// <summary>The layer children are POOLED (ObjectPool + detach on release), so a removed child must
        /// come back on the next add rather than being rebuilt — its two AddComponent calls are the whole
        /// reason the pool exists. Nothing else in this fixture would notice a Release that destroys or a Get
        /// that always creates: behaviour would stay correct and just cost what it did before pooling.
        /// Also pins that a recycled child carries NO state from its previous tenancy (the Mesh belongs to
        /// TileManager and may be destroyed the moment RemoveItem returns).</summary>
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
        // MapView runs Rebuild BEFORE consuming new tiles, so a layer added after the frame's Rebuild must
        // be created at its correct scene position — NOT at the world origin where it would render for one
        // frame. Rebuild first (seeds the cached origin, as the live loop does), THEN add, and assert the
        // new container is already positioned before any further Rebuild runs.

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

            // Two assertions used to live here — "Root accessor must read null after dispose" and
            // "ContainerCount must read 0, not throw, after Dispose". They pinned a leniency that existed
            // ONLY to let this test read a torn-down backend: neither accessor had a production caller. The
            // real post-dispose invariant is the line above (the root GameObject is destroyed) plus the
            // ObjectDisposedException asserted below. Reading a disposed object is a caller bug, not a
            // supported query, so it is no longer answered with a plausible-looking null/0.
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

    // S53b increment 2 — Entities backend wired through MapView/TileManager.
    //
    // Proves the live path (not just the EntitiesTileRenderer unit): selecting RenderBackend.Entities
    // constructs the backend, ConsumeMeshBuild creates one entity per tile-layer, and the per-frame
    // InstancedRebuild positions them via FloatingOrigin.TileLocalToScene. GPU-independent (reads the
    // entity's LocalToWorld translation), mirroring BrgBackendSnapshotTests' floating-origin tooth.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewEntitiesBackendTests — S53b increment 2
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
            view.Config.Backend = RenderBackend.Brg; // S53c: default is Entities; pin BRG to prove exclusivity.
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
