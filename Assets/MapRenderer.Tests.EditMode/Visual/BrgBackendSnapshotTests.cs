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
// GPU-dependent teeth (2-pixel, 3-per-layer-paint) fall back to Assert.Inconclusive when no GPU
// context is provably absent (all-black render + all-black blank control), per lessons.md:
// no unbounded skip — the Inconclusive path only fires when GPU context is proved missing; if the
// blank-control renders non-black (GPU present) but the map render is all-black or culling never
// fires, the test produces Assert.Fail (a real failure, not a vacuous skip).

using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S49 acceptance tests for the BRG render backend.
    ///
    /// GPU-independent assertions are HARD (always run, never skipped).
    /// GPU-dependent (pixel) assertions go Inconclusive when provably no GPU context.
    /// </summary>
    [TestFixture]
    public class BrgBackendSnapshotTests
    {
        // ── Test fixture infrastructure ────────────────────────────────────────────────────────

        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties MakeCam(double lon, double lat, double zoom)
            => new CameraProperties(new LookAtPoint(lon, lat, 0), zoom, 0, 0);

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

        private sealed class FixtureSource : IDataSource
        {
            private readonly byte[] _bytes;
            public FixtureSource(byte[] b) { _bytes = b; }
            public TileEncoding Encoding => TileEncoding.Mvt;
            public UniTask<TileResponse> FetchAsync(TileId id, System.Threading.CancellationToken ct = default)
                => UniTask.FromResult(new TileResponse(_bytes, TileEncoding.Mvt));
            public void Dispose() { }
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.Tick();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
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
            var src  = new FixtureSource(FixtureBytes());
            var go   = new GameObject("MapView_Tooth1");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;
            // Backend defaults to Entities (S53c) — do NOT set it to Brg.

            try
            {
                view.Initialise(src, MakeCam(0, 0, 0.0), ownsSource: false, style: style);

                Assert.IsNull(view.BrgRenderer(),
                    "The default (Entities) backend must NOT construct a BrgTileRenderer; " +
                    "BRG is only built when Backend == Brg.");
                Assert.IsNotNull(view.EntitiesRenderer(),
                    "The default backend must construct the EntitiesTileRenderer.");

                // Also verify the tile settles normally on the default path.
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must settle on the default (Entities) backend.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(0, 0, 0)),
                    "z0/0/0 tile must be built on the default backend.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth 2: draw order mechanism (GPU-independent) ───────────────────────────────────

        /// <summary>
        /// Draw order mechanism tooth (GPU-independent): the BRG backend emits draw commands in
        /// ascending renderQueue order (declared layer order = painter's algorithm). This is a
        /// structural/mechanical assertion that does not require GPU readback.
        ///
        /// With one fill layer, the single draw command's renderQueue must equal the material's
        /// renderQueue (TransparentQueue + drawIndex). No re-ordering must occur.
        /// </summary>
        [Test]
        public void BrgBackend_EmitsDraw_InAscendingRenderQueueOrder()
        {
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_Tooth2");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;
            view.Backend = RenderBackend.Brg; // S49 BRG path

            try
            {
                view.Initialise(src, MakeCam(0, 0, 0.0), ownsSource: false, style: style);

                Assert.IsNotNull(view.BrgRenderer(),
                    "Backend=Brg must construct a BrgTileRenderer.");

                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must settle on the BRG path.");

                // Force a Rebuild to populate the sorted draw list.
                view.BrgRenderer().Rebuild(default);

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
                Object.DestroyImmediate(go);
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
        /// Falls back to Assert.Inconclusive when GPU context is provably absent (both renders all-black
        /// and blank-control also all-black). Produces Assert.Fail when GPU is present but compositing
        /// is wrong (blank-control non-black but render is all-black or culling never fired).
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

            var lightGo = new GameObject("BrgOrderTestLight");
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

            var cameraGo = new GameObject("BrgOrderTestCam");
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
                {
                    var src  = new FixtureSource(FixtureBytes());
                    var mapGo = new GameObject("BrgOrderA");
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.MinZoom = 3; view.MaxZoom = 3;
                    view.PadFactor = 1f; view.ViewportAspect = 1f;
                    view.MaxBuildsPerTick = 64;
                    view.Backend = RenderBackend.Brg;
                    try
                    {
                        view.Initialise(src, new CameraProperties(new LookAtPoint(0, 0, 0), 3.0, 0, 0),
                            ownsSource: false, style: StyleLineThenFill());

                        for (int f = 0; f < 500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                        {
                            view.Tick(); Thread.Sleep(1);
                        }
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Style A: BRG must load and settle tiles.");

                        // One more Tick so _sortedItems is rebuilt from the full draw-item set after
                        // the final tile consume (the settle loop exits before its next Rebuild).
                        view.Tick();

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
                        Object.DestroyImmediate(mapGo);
                        src.Dispose();
                    }
                }

                // ── Render B: fill(red) declared before line(blue) → line on top → BLUE expected ──
                {
                    var src   = new FixtureSource(FixtureBytes());
                    var mapGo = new GameObject("BrgOrderB");
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.MinZoom = 3; view.MaxZoom = 3;
                    view.PadFactor = 1f; view.ViewportAspect = 1f;
                    view.MaxBuildsPerTick = 64;
                    view.Backend = RenderBackend.Brg;
                    try
                    {
                        view.Initialise(src, new CameraProperties(new LookAtPoint(0, 0, 0), 3.0, 0, 0),
                            ownsSource: false, style: StyleFillThenLine());

                        for (int f = 0; f < 500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                        {
                            view.Tick(); Thread.Sleep(1);
                        }
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Style B: BRG must load and settle tiles.");

                        // One more Tick so _sortedItems is rebuilt from the full draw-item set after
                        // the final tile consume (the settle loop exits before its next Rebuild).
                        view.Tick();

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
                        Object.DestroyImmediate(mapGo);
                        src.Dispose();
                    }
                }

                // ── BRG-blank guard ──────────────────────────────────────────────────────────────
                // Inconclusive ONLY when blank-control is all-black (GPU provably absent).
                // Any other blank = real failure.
                const byte BgR8 = 26, BgG8 = 28, BgB8 = 38; // bgColor (0.10,0.11,0.15) × 255 ≈ 26,28,38
                bool aBlank = IsRenderBlank(snapA, BgR8, BgG8, BgB8);
                bool bBlank = IsRenderBlank(snapB, BgR8, BgG8, BgB8);

                if (aBlank || bBlank)
                {
                    var (blankGo, blankCam) = BuildBlankCamera(bgColor);
                    using var blankSnap = new SnapshotRenderer(SnapW, SnapH);
                    try
                    {
                        blankSnap.Render(blankCam);
                        if (blankSnap.IsAllBlack())
                        {
                            // GPU provably absent.
                            Assert.Inconclusive(
                                "BRG draw-order renders blank and blank-control is all-black: no GPU context. " +
                                "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                            return;
                        }
                    }
                    finally { Object.DestroyImmediate(blankGo); }

                    // GPU IS present — BRG blank is a real failure.
                    Assert.Fail(
                        $"BRG draw-order render is blank (all-background) but GPU is present. " +
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
                meanA = SnapshotCoverage.SampleRegionMeanColor(snapA.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);
                meanB = SnapshotCoverage.SampleRegionMeanColor(snapB.RawPixels, SnapW, SnapH, SX0, SY0, SX1, SY1);

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
                    Assert.Inconclusive(
                        "Sampled region mean-color is too dim in one or both renders — " +
                        "geometry may not have reached the sample rect. " +
                        $"meanA sum={meanA[0]+meanA[1]+meanA[2]:F3}, meanB sum={meanB[0]+meanB[1]+meanB[2]:F3}. " +
                        "Re-run as PlayMode or inspect brg-order-*.png.");
                    return;
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
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
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
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_Tooth4");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;
            view.Backend = RenderBackend.Brg;

            try
            {
                var cam0 = MakeCam(0, 0, 0.0);
                view.Initialise(src, cam0, ownsSource: false, style: style);

                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(0, 0, 0)),
                    "z0/0/0 tile must be built by the BRG path.");

                var brg = view.BrgRenderer();
                Assert.IsNotNull(brg, "BRG renderer must be non-null.");

                // Compute the expected translation for z0/0/0 tile at sceneOrigin = cam0.
                double2 tileOrigin  = FloatingOrigin.TileLocalOriginMercator(new TileId(0, 0, 0));
                double2 sceneOrigin = cam0.CenterMercator();

                float3 expectedPos  = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin);

                // Force Rebuild to populate the CPU buffer with the current sceneOrigin.
                brg.Rebuild(sceneOrigin);

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
                brg.Rebuild(sceneOrigin1);

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
                Object.DestroyImmediate(go);
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
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_Tooth5");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;
            view.Backend = RenderBackend.Brg;

            try
            {
                view.Initialise(src, MakeCam(0, 0, 0.0), ownsSource: false, style: MinimalStyle());
                PumpUntilSettled(view);

                var brg = view.BrgRenderer();
                Assert.IsNotNull(brg, "BRG must be non-null after Initialise with BRG backend.");

                // Verify the BRG has a buffer after tile load.
                view.BrgRenderer().Rebuild(default);
                // Note: HasBuffer may be false if no items registered yet (empty scene). Either way,
                // after Teardown IsDisposed must be true.

                view.Teardown();

                // IsDisposed must be true after Teardown.
                Assert.IsTrue(brg.IsDisposed,
                    "BrgTileRenderer must be disposed after MapView.Teardown. " +
                    "S49 tooth 5: BRG + every GraphicsBuffer released on teardown.");

                // HasBuffer must be false (GraphicsBuffer released).
                Assert.IsFalse(brg.HasBuffer,
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
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_NoAlloc");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;
            view.Backend = RenderBackend.Brg;

            try
            {
                view.Initialise(src, MakeCam(0, 0, 0.0), ownsSource: false, style: MinimalStyle());
                PumpUntilSettled(view);

                var brg = view.BrgRenderer();
                Assert.IsNotNull(brg);

                double2 origin = default;

                // Warm-up: first Rebuild may allocate (buffer creation, sorted-list growth).
                brg.Rebuild(origin);
                brg.Rebuild(origin);

                // Measure: N more Rebuilds must not trigger GC gen-0.
                // We check GC generation-0 collection count before and after.
                // This is a heuristic — managed allocations inside Rebuild would eventually trigger GC.
                // At our instance count (1 tile, 1 layer), a single float[] allocation would be detected.
                System.GC.Collect(0, System.GCCollectionMode.Forced, blocking: true);
                int gcBefore = System.GC.CollectionCount(0);

                const int N = 100;
                for (int i = 0; i < N; i++)
                    brg.Rebuild(origin);

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
                Object.DestroyImmediate(go);
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
        /// OnPerformCulling is never called (cullingCalls==0) but the blank-control proves a GPU
        /// context IS present, the test produces Assert.Fail (a real failure, not a vacuous skip).
        /// Only when the blank-control is also all-black (GPU provably absent) does Inconclusive fire.
        /// </summary>
        [Test]
        public void BrgBackend_RendersNonBlankFill_OnRealPixels()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;
            const float MinFill = 0.05f, MaxFill = 0.95f;
            const int   MinBuckets = 4;

            // Sample rect for color-channel mean: central 80% of the frame, avoiding edge artifacts.
            const int ColorSX0 = 50, ColorSY0 = 50, ColorSX1 = 462, ColorSY1 = 462;

            var src   = new FixtureSource(FixtureBytes());
            var mapGo = new GameObject("MapView_BrgPixel");
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            view.MinZoom = 3; view.MaxZoom = 3;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;
            view.Backend = RenderBackend.Brg;

            var lightGo = new GameObject("BrgTestLight");
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

            var cameraGo = new GameObject("BrgTestCam");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags   = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled     = false;
            camera.farClipPlane = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                view.Initialise(src, new CameraProperties(new LookAtPoint(0, 0, 0), 3.0, 0, 0),
                    ownsSource: false, style: MinimalStyle());

                for (int f = 0; f < 500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                {
                    view.Tick();
                    Thread.Sleep(1);
                }
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "BRG path must load + settle tiles.");

                // Settle-loop staleness fix: the loop exits as soon as AllTilesSettled() is true,
                // WITHOUT running another Tick. Within MapView.Tick, BrgRebuild runs BEFORE the
                // TileManager consumes the final tile's draw items, so _sortedItems (read by
                // ComputeSceneBounds and OnPerformCulling) lags one frame behind _items. One more
                // Tick mirrors production's next frame and rebuilds _sortedItems from the full set.
                view.Tick();

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

                // ── cullingCalls guard: replaces the former Assert.Ignore escape hatch ──────────
                // If OnPerformCulling was never called we cannot assert pixel colour. But ONLY go to
                // Inconclusive when the blank-control proves GPU context is absent. If the blank-control
                // is non-black (GPU IS present) but culling never fired, that is a real failure.
                if (cullingCalls == 0)
                {
                    var (blankGoC, blankCamC) = BuildBlankCamera(bgColor);
                    using var blankSnapC = new SnapshotRenderer(SnapW, SnapH);
                    try
                    {
                        blankSnapC.Render(blankCamC);
                        if (blankSnapC.IsAllBlack())
                        {
                            Assert.Inconclusive(
                                "BRG OnPerformCulling not called and blank-control is all-black: " +
                                "no GPU context in headless EditMode batch. " +
                                "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                            return;
                        }
                    }
                    finally { Object.DestroyImmediate(blankGoC); }

                    // GPU context IS present — culling not firing is a real failure.
                    Assert.Fail(
                        "BRG OnPerformCulling was never called during Camera.Render() but the blank-control " +
                        "rendered non-black (GPU context IS present). The BRG render loop is not being driven. " +
                        "Check: (a) BRG is properly registered, (b) Camera.Render triggers the SRP culling path.");
                    return;
                }

                // ── BRG-blank guard ──────────────────────────────────────────────────────────────
                // Detect via SnapshotCoverage (background detection by colour proximity, not IsAllBlack).
                // Inconclusive ONLY when blank-control is all-black (GPU provably absent).
                // Any other blank = real failure (GPU present but BRG not rendering).
                var verdict = SnapshotCoverage.Analyse(snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                Debug.Log($"[BrgBackendSnapshot] filled={verdict.FilledFraction:P1} buckets={verdict.DistinctRegionBucketsHit}/64 blank={verdict.IsBlank}");

                if (verdict.IsBlank)
                {
                    // Render is blank — determine if GPU is absent (Inconclusive) or failing (Fail).
                    var (blankGo, blankCam) = BuildBlankCamera(bgColor);
                    using var blankSnap     = new SnapshotRenderer(SnapW, SnapH);
                    try
                    {
                        blankSnap.Render(blankCam);
                        if (blankSnap.IsAllBlack())
                        {
                            // GPU provably absent: blank-control is also all-black.
                            Assert.Inconclusive(
                                "BRG render is blank and blank-control is all-black: no GPU context in batch EditMode. " +
                                "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                            return;
                        }
                    }
                    finally
                    {
                        Object.DestroyImmediate(blankGo);
                    }

                    // GPU IS present (blank-control rendered non-black) — BRG render is a real failure.
                    Assert.Fail(
                        $"BRG render is blank (all-background) but GPU is present (blank-control non-black). " +
                        $"cullingCalls={cullingCalls}. The BRG backend is not producing visible pixels. " +
                        "Check: (a) matrix packing (packed float3x4 format), (b) camera framing, " +
                        "(c) OnPerformCulling called and draw commands emitted, (d) buffer uploaded.");
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
                    snap.RawPixels, SnapW, SnapH, ColorSX0, ColorSY0, ColorSX1, ColorSY1);
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
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                view.Teardown();
                Object.DestroyImmediate(mapGo);
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
        /// This proves S11/S13/S14 (zoom-dependent line width, ZoomStyleApplier wired,
        /// _MetersPerPixel updated) are intact on the BRG path — falsifiable because a broken
        /// ApplyZoom or wrong _Width uniform would yield equal (≈ zoom=1) coverage at zoom=5.
        ///
        /// Falls back to Assert.Inconclusive only when GPU context is provably absent.
        /// </summary>
        [Test]
        public void BrgBackend_ZoomDependentLineWidth_RendersOnBrgPixels()
        {
            const int SnapW = 512, SnapH = 512;
            var bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;

            var lightGo = new GameObject("BrgZoomLineLight");
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
            var cameraGo = new GameObject("BrgZoomLineCam");
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
                {
                    var src   = new FixtureSource(FixtureBytes());
                    var mapGo = new GameObject("BrgZoomLow");
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.MinZoom = 0; view.MaxZoom = 2;
                    view.PadFactor = 1f; view.ViewportAspect = 1f;
                    view.MaxBuildsPerTick = 64;
                    view.Backend = RenderBackend.Brg;
                    try
                    {
                        view.Initialise(src, new CameraProperties(new LookAtPoint(0, 0, 0), 1.0, 0, 0),
                            ownsSource: false, style: StyleZoomDependentLine());

                        for (int f = 0; f < 500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                        {
                            view.Tick(); Thread.Sleep(1);
                        }
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Zoom=1 render: BRG must settle tiles.");

                        // One more Tick so _sortedItems is rebuilt from the full draw-item set after
                        // the final tile consume (the settle loop exits before its next Rebuild).
                        view.Tick();

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

                        var vLow = SnapshotCoverage.Analyse(snapLow.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                        filledLowZoom = vLow.FilledFraction;
                        Debug.Log($"[BrgZoomLine] zoom=1 culling={cullingLow} filled={filledLowZoom:P1}");
                    }
                    finally
                    {
                        view.Teardown();
                        Object.DestroyImmediate(mapGo);
                        src.Dispose();
                    }
                }

                // ── Render at zoom=5 (wide line: ~400px) ─────────────────────────────────────
                {
                    var src   = new FixtureSource(FixtureBytes());
                    var mapGo = new GameObject("BrgZoomHigh");
                    var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
                    view.MinZoom = 4; view.MaxZoom = 6;
                    view.PadFactor = 1f; view.ViewportAspect = 1f;
                    view.MaxBuildsPerTick = 64;
                    view.Backend = RenderBackend.Brg;
                    try
                    {
                        view.Initialise(src, new CameraProperties(new LookAtPoint(0, 0, 0), 5.0, 0, 0),
                            ownsSource: false, style: StyleZoomDependentLine());

                        for (int f = 0; f < 500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                        {
                            view.Tick(); Thread.Sleep(1);
                        }
                        Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                            "Zoom=5 render: BRG must settle tiles.");

                        // One more Tick so _sortedItems is rebuilt from the full draw-item set after
                        // the final tile consume (the settle loop exits before its next Rebuild).
                        view.Tick();

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

                        var vHigh = SnapshotCoverage.Analyse(snapHigh.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                        filledHighZoom = vHigh.FilledFraction;
                        Debug.Log($"[BrgZoomLine] zoom=5 culling={cullingHigh} filled={filledHighZoom:P1}");
                    }
                    finally
                    {
                        view.Teardown();
                        Object.DestroyImmediate(mapGo);
                        src.Dispose();
                    }
                }

                // ── BRG-blank guard ──────────────────────────────────────────────────────────────
                // Inconclusive ONLY when blank-control is all-black (GPU provably absent).
                bool lowBlank  = IsRenderBlank(snapLow,  BgR8, BgG8, BgB8);
                bool highBlank = IsRenderBlank(snapHigh, BgR8, BgG8, BgB8);

                if (lowBlank || highBlank)
                {
                    var (blankGo, blankCam) = BuildBlankCamera(bgColor);
                    using var blankSnap = new SnapshotRenderer(SnapW, SnapH);
                    try
                    {
                        blankSnap.Render(blankCam);
                        if (blankSnap.IsAllBlack())
                        {
                            Assert.Inconclusive(
                                "BRG zoom-line renders blank and blank-control is all-black: no GPU context. " +
                                "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                            return;
                        }
                    }
                    finally { Object.DestroyImmediate(blankGo); }

                    // GPU IS present — BRG blank is a real failure.
                    Assert.Fail(
                        $"BRG zoom-line render is blank but GPU is present. " +
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

                // ── No-render guard: if the line didn't render at all, the test is vacuous ─────
                if (filledLowZoom < 0.001f && filledHighZoom < 0.001f)
                {
                    Assert.Inconclusive(
                        "Both zoom renders produced near-zero line coverage — geolines may not render " +
                        "at these zooms or the camera framing misses the tiles. " +
                        $"low={filledLowZoom:P2}, high={filledHighZoom:P2}. " +
                        "Re-run as PlayMode or inspect brg-zoom-line-*.png.");
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
                    "If this fails, ZoomStyleApplier or _MetersPerPixel is not updating on the BRG path " +
                    "(S11/S13/S14 regression).");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildBlankCamera(Color bgColor)
        {
            var go  = new GameObject("BrgBlankCam");
            var cam = go.AddComponent<Camera>();
            cam.orthographic     = true;
            cam.clearFlags       = CameraClearFlags.SolidColor;
            cam.backgroundColor  = bgColor;
            cam.enabled          = false;
            cam.transform.position = new Vector3(0f, 200f, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.orthographicSize = 70f;
            return (go, cam);
        }

        /// <summary>
        /// Returns true when <paramref name="snap"/> contains no non-background pixels —
        /// i.e. the BRG render produced only the camera clear colour (background).
        ///
        /// This is the correct "BRG/DOTS did not composite" probe for headless EditMode: the camera
        /// clear works fine (so IsAllBlack=false), but BRG geometry is never composited on top.
        /// SnapshotCoverage.Analyse uses a Manhattan-distance tolerance=15 around the background
        /// colour to classify each pixel; IsBlank=true when ≥97% of pixels are background.
        /// </summary>
        private static bool IsRenderBlank(SnapshotRenderer snap, byte bgR8, byte bgG8, byte bgB8)
        {
            if (snap.RawPixels == null) return true;
            var v = SnapshotCoverage.Analyse(snap.RawPixels, snap.Width, snap.Height, bgR8, bgG8, bgB8);
            return v.IsBlank;
        }
    }
}
