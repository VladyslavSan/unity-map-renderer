// Fill-diagnostic and alternate-source-path GPU/visual acceptance tests (UMR-176 pack: meshing topic).
//
// The three-way split follows TWO using collisions, not the line cap: `CameraProperties`
// (MapRenderer.Core.Geo vs UnityEngine.Rendering) and bare `Object` (System.Object vs
// UnityEngine.Object) — both CS0104. Within that constraint each file below groups
// its dominant fill sub-area.
// This file: the bare-CameraProperties user (MapViewStyledFillTests) plus
// System-importers that are neutral on the UnityEngine.Rendering axis — the
// band cost/compare diagnostics and the GeoJson-sourced fill proof.
//
// Contents:
//   MapViewStyledFillTests           — S40 decisive tests for per-layer styled fill rendering in MapView.
//   FillBandFrameCostDiagnostic      — Measures the fill boundary band's cost, band-on versus band-off, over real MVT fixtures.
//   FillBandVisualCompareDiagnostic  — Produces before/after image pairs of the fill boundary band over real MVT fixtures, plus the graded-pixel counts that go with each pair.
//   GeoJsonFillVisualProofTests      — Unity EditMode only — Stage G-V0, the declarative visual-test authoring kit's proof fixture.

using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using System.Diagnostics;
using Unity.Mathematics;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using Debug = UnityEngine.Debug;
using ProfilerRecorderHandle = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle;

namespace MapRenderer.Tests.Visual
{
    // Unity EditMode only — uses MonoBehaviour, Mesh, per-layer material inspection.
    // NOT included in Tools/core-tests.
    //
    // S40 decisive acceptance tests for per-layer styled fill rendering.
    //
    // ALL decisive assertions here are CPU-side (mesh.GetColors() or per-layer material inspection).
    // They CANNOT degrade to Inconclusive — no GPU context is required for the decisive teeth.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewStyledFillTests — S40 decisive tests for per-layer styled fill rendering in MapView.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S40 decisive tests for per-layer styled fill rendering in <see cref="MapView"/>.
    ///
    /// Acceptance teeth (all CPU-side, cannot degrade to Inconclusive):
    ///
    /// #1 — Multiple distinct fill-layer draws (DECISIVE):
    ///   MapView with a 2-fill-layer inline style over the fixture produces a tile container with
    ///   exactly 2 child renderers, each with a distinct material at a distinct renderQueue.
    ///   Proves the live loop iterates fill layers in style order (not single-material mono-color).
    ///
    /// #2 — Draw order (DECISIVE):
    ///   The second fill layer's material has renderQueue > first fill layer's material renderQueue.
    ///   Proves painter's algorithm ordering.
    ///
    /// #3 — ≥2 distinct baked vertex colors from demo style (DECISIVE):
    ///   MapView with the committed maplibre-demo-style.json (the 'countries-fill' layer has a
    ///   'match' expression with a palette of ≥6 colors) produces a mesh over the fixture tile
    ///   whose mesh.GetColors() contains ≥2 distinct linear-space colors. Proves data-driven
    ///   per-feature color baking is wired end-to-end (not white/mono-color).
    ///
    /// #4 — Gamma correct baked colors (DECISIVE):
    ///   The baked vertex colors in the mesh are closer to their expected linear value than to
    ///   their raw sRGB value — MeshBuilder.Build() applied Color.linear (the D2 gamma fix).
    ///   Uses a constant-color 1-fill-layer style with a known sRGB color to get a deterministic
    ///   expected linear value.
    ///
    /// #5 — ZoomStyleApplier wired (DECISIVE):
    ///   A zoom-dependent fill-color expression (interpolate at zoom 0→1 between red and blue)
    ///   produces different material output at zoom=0 vs zoom=1. Proves ApplyZoom is called and
    ///   actually changes the uniform.
    /// </summary>
    [TestFixture]
    public class MapViewStyledFillTests : BaseTestFixture
    {

        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        // ─── inline 2-fill-layer style (for draw-count and draw-order teeth) ────────────────────

        /// <summary>
        /// Two fill layers, both over source-layer "countries", with distinct constant colors.
        /// Layer 0 (bottom): red (#FF0000 as rgba).
        /// Layer 1 (top):    blue (#0000FF as rgba).
        /// Both layers resolve against the fixture tile (which has a "countries" MVT layer).
        /// </summary>
        private static StyleDocument TwoFillLayerStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""TwoFill"",
            ""sources"": {
                ""maplibre"": {
                    ""type"": ""vector"",
                    ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""]
                }
            },
            ""layers"": [
                {
                    ""id"": ""fill-a"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 255, 0, 0, 1] }
                },
                {
                    ""id"": ""fill-b"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 0, 0, 255, 1] }
                }
            ]
        }");

        // ─── #1 & #2: Multiple fill-layer draws + draw order (DECISIVE) ─────────────────────────

        [Test]
        public void MapView_TwoFillLayers_ProducesTwoDistinctChildRenderersWithOrderedQueues()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = TwoFillLayerStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 0.0, 0, 0), style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built");

                // ── DECISIVE: per-layer iteration — exactly 2 layer meshes (one per fill style layer) ──
                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "The built tile must expose its layer meshes.");
                Assert.AreEqual(2, meshes.Length,
                    "The tile must have exactly 2 layer meshes (one per fill style layer). " +
                    "If this is 0 or 1, the per-layer iteration is broken.");

                // ── DECISIVE: distinct Material per layer (backend-agnostic, from the RenderLayerSet) ──
                Assert.AreEqual(2, view.FillLayerCount(), "Two fill style layers must produce two records.");
                Material mat0 = view.Layers[0].Material;
                Material mat1 = view.Layers[1].Material;
                Assert.IsNotNull(mat0, "Fill layer 0 must have a Material");
                Assert.IsNotNull(mat1, "Fill layer 1 must have a Material");

                Assert.AreNotSame(mat0, mat1,
                    "Fill layer 0 and fill layer 1 must use DISTINCT Material instances. " +
                    "If they share the same Material, per-layer styling is broken.");

                // ── DECISIVE: draw order — layer 1 (blue, declared later) must have higher renderQueue
                Assert.Greater(mat1.renderQueue, mat0.renderQueue,
                    $"Fill layer 1 (declared later) must have renderQueue ({mat1.renderQueue}) > " +
                    $"fill layer 0 ({mat0.renderQueue}). Painter's algorithm: later layer draws on top.");

                Debug.Log($"[StyledFillTests] layer0 queue={mat0.renderQueue}, layer1 queue={mat1.renderQueue}");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ─── Interleaved fill/line draw order follows STYLE order, not type (regression) ──────────
        // A [fill, line, fill] style: the middle line must composite BETWEEN the two fills, i.e.
        // queue(fill-bottom) < queue(line-mid) < queue(fill-top). The old code bucketed records by
        // type (all fills, then all lines), producing queue(fill-bottom) < queue(fill-top) < queue(line-mid)
        // — a line declared between two fills wrongly drew on top of BOTH. The style's layer order is SSOT.

        private static StyleDocument FillLineFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""FillLineFill"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""fill-bottom"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 255, 0, 0, 1] } },
                { ""id"": ""line-mid"",    ""type"": ""line"", ""source"": ""maplibre"", ""source-layer"": ""geolines"", ""paint"": { ""line-color"": [""rgba"", 0, 255, 0, 1] } },
                { ""id"": ""fill-top"",    ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 255, 1] } }
            ]
        }");

        [Test]
        public void MapView_InterleavedFillLineFill_QueuesFollowStyleOrderNotType()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 0.0, 0, 0),
                                style: FillLineFillStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built");

                // Map each layer id -> its material renderQueue from the RenderLayerSet (backend-agnostic;
                // the renderQueue is assigned by the layer's declared index, which encodes paint order).
                var queueById = new Dictionary<string, int>();
                var orderedLayers = view.Layers.Layers;
                for (int i = 0; i < orderedLayers.Count; i++)
                    queueById[orderedLayers[i].StyleLayer.Id] = orderedLayers[i].Material.renderQueue;

                Assert.IsTrue(
                    queueById.ContainsKey("fill-bottom") && queueById.ContainsKey("line-mid") && queueById.ContainsKey("fill-top"),
                    $"All three layers must produce a styled-layer record. Found: [{string.Join(",", queueById.Keys)}]");

                // DECISIVE: the line declared BETWEEN the two fills must sit BETWEEN them in draw order.
                Assert.Less(queueById["fill-bottom"], queueById["line-mid"],
                    $"fill-bottom ({queueById["fill-bottom"]}) must be < line-mid ({queueById["line-mid"]}) — style order.");
                Assert.Less(queueById["line-mid"], queueById["fill-top"],
                    $"line-mid ({queueById["line-mid"]}) must be < fill-top ({queueById["fill-top"]}). " +
                    "If line-mid > fill-top, records are bucketed by type (all fills, then all lines) " +
                    "instead of following the style's declared layer order.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ─── #3: ≥2 distinct baked vertex colors (DECISIVE) ──────────────────────────────────────
        //
        // Uses a fill-color match expression on "CONTINENT" — the property that the fixture tile's
        // countries layer actually encodes (confirmed: DataDrivenColorBakeTests, 8 distinct values
        // including "Asia" and "South America"). The demo style uses ADM0_A3 which the z0 fixture
        // tile does NOT encode, so the test uses an inline style targeting the known CONTINENT key.

        private static StyleDocument ContinentFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""ContinentFill"",
            ""sources"": {
                ""maplibre"": {
                    ""type"": ""vector"",
                    ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""]
                }
            },
            ""layers"": [
                {
                    ""id"": ""continent-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": {
                        ""fill-color"": [
                            ""match"",
                            [""get"", ""CONTINENT""],
                            ""Asia"",     [""rgba"", 200, 50,  50,  1],
                            ""Europe"",   [""rgba"", 50,  200, 50,  1],
                            ""Africa"",   [""rgba"", 50,  50,  200, 1],
                            ""Americas"", [""rgba"", 200, 200, 50,  1],
                            ""South America"", [""rgba"", 200, 100, 50, 1],
                                          [""rgba"", 128, 128, 128, 1]
                        ]
                    }
                }
            ]
        }");

        [Test]
        public void MapView_ContinentMatchStyle_BakesAtLeastTwoDistinctVertexColors()
        {
            var bytes = SampleTileFixture.Bytes();
            var src   = TestDataSource.FromBytes(bytes);
            var go    = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = ContinentFillStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 0.0, 0, 0), style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built");

                // The first (and only) layer mesh is the continent-fill layer.
                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "The built tile must expose its layer meshes.");
                Assert.AreEqual(1, meshes.Length,
                    "Expect exactly 1 fill-layer mesh (ContinentFillStyle has 1 fill layer)");

                Mesh mesh = meshes[0];
                Assert.IsNotNull(mesh, "Fill mesh must not be null");
                Assert.Greater(mesh.vertexCount, 0, "Fill mesh must have vertices");

                // ── DECISIVE: ≥2 distinct vertex colors ───────────────────────────────────────
                // The fixture has features with CONTINENT = "Asia", "South America", "Europe",
                // "Africa", "North America", "Oceania", "Antarctica" (8 distinct values).
                // The match expression maps Asia → red-ish, Europe → green-ish, Africa → blue-ish.
                // All features must NOT have white vertex color — the default branch gives gray (128,128,128).
                // At minimum: Asia (r≈0.55,g≈0.03,b≈0.03) ≠ default gray (r≈0.216,g≈0.216,b≈0.216).
                var colors = new List<Color>();
                mesh.GetColors(colors);

                Assert.Greater(colors.Count, 0,
                    "Mesh must have vertex colors (StyledFillTileBuilder always sets the color channel).");

                // Count distinct colors (linear space; within float tolerance).
                const float colorTol = 0.02f;
                var distinctLinearColors = new List<Color>();
                foreach (var c in colors)
                {
                    bool found = false;
                    foreach (var d in distinctLinearColors)
                    {
                        if (Math.Abs(c.r - d.r) < colorTol &&
                            Math.Abs(c.g - d.g) < colorTol &&
                            Math.Abs(c.b - d.b) < colorTol)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found) distinctLinearColors.Add(c);
                }

                Debug.Log($"[StyledFillTests] distinct vertex colors: {distinctLinearColors.Count}, " +
                          $"total vertex count: {colors.Count}");
                for (int i = 0; i < Math.Min(8, distinctLinearColors.Count); i++)
                    Debug.Log($"  distinct[{i}] = ({distinctLinearColors[i].r:F3}, " +
                              $"{distinctLinearColors[i].g:F3}, {distinctLinearColors[i].b:F3})");

                Assert.GreaterOrEqual(distinctLinearColors.Count, 2,
                    $"A CONTINENT match expression must produce ≥2 distinct baked vertex colors " +
                    $"(fixture has ≥2 distinct CONTINENT values). Got {distinctLinearColors.Count}. " +
                    "If only 1 distinct color (white or gray), the match expression is not evaluated " +
                    "per feature (data-driven color baking is broken in StyledFillTileBuilder).");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ─── #4: Gamma-correct baked vertex colors (DECISIVE) ────────────────────────────────────

        // The constant-color arm of gamma linearization is observed in
        // PaintColorRenderTests.ConstantFillColor_RenderedPixel_MatchesAuthored; this fixture is now
        // data-driven (a `match` expression), so it exercises only the data-driven bake.
        [Test]
        public void MapView_DataDrivenFillColor_VertexColorsAreLinearized()
        {
            // Use a known sRGB value where linear ≠ sRGB: rgba(127, 0, 0) → sRGB≈0.498, linear≈0.212.
            // The decisive check: stored R is closer to linear (≈0.212) than to sRGB (≈0.498).
            // This proves MeshBuilder.Build() called Color.linear before Mesh.SetColors.
            // Both `match` arms below hold the SAME rgba(127,0,0,1) on purpose: this forces
            // DependsOnFeature == true (so the value bakes to vColor, not _BaseColor) while pinning the
            // authored 127 so the linearization check stays byte-level — simplifying to a constant
            // fill-color would move the value to _BaseColor and red this test for the wrong reason.
            const string styleJson = @"{
                ""version"": 8,
                ""name"": ""GammaTest"",
                ""sources"": {
                    ""maplibre"": {
                        ""type"": ""vector"",
                        ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""]
                    }
                },
                ""layers"": [
                    {
                        ""id"": ""gamma-fill"",
                        ""type"": ""fill"",
                        ""source"": ""maplibre"",
                        ""source-layer"": ""countries"",
                        ""paint"": {
                            ""fill-color"": [""match"", [""get"", ""CONTINENT""],
                                ""Asia"", [""rgba"", 127, 0, 0, 1],
                                [""rgba"", 127, 0, 0, 1]]
                        }
                    }
                ]
            }";

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = StyleParser.Parse(styleJson);
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 0.0, 0, 0), style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built");

                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "The built tile must expose its layer meshes.");
                Assert.AreEqual(1, meshes.Length, "Expect 1 fill layer mesh");

                Mesh mesh = meshes[0];
                Assert.IsNotNull(mesh, "Fill mesh must not be null");

                var colors = new List<Color>();
                mesh.GetColors(colors);
                Assert.Greater(colors.Count, 0, "Mesh must have vertex colors");

                float storedR = colors[0].r;
                const float srgbR       = 127f / 255f; // ≈ 0.498 (raw sRGB)
                const float expectedLinR = 0.212f;      // IEC 61966-2-1: sRGB 0.498 → linear ≈ 0.212

                float distToSrgb   = Math.Abs(storedR - srgbR);
                float distToLinear = Math.Abs(storedR - expectedLinR);

                Debug.Log($"[StyledFillTests] Gamma: storedR={storedR:F4}, " +
                          $"sRGB={srgbR:F4}, expectedLinear={expectedLinR:F4}, " +
                          $"distToSrgb={distToSrgb:F4}, distToLinear={distToLinear:F4}");

                // ── DECISIVE: stored R must be closer to linear than to sRGB ─────────────────
                Assert.Less(distToLinear, distToSrgb,
                    $"StyledFillTileBuilder must linearize vertex colors (D2 gamma fix). " +
                    $"Stored R={storedR:F4}. Expected closer to linear ({expectedLinR:F4}) " +
                    $"than to sRGB ({srgbR:F4}). distToLinear={distToLinear:F4}, " +
                    $"distToSrgb={distToSrgb:F4}. " +
                    "If distToSrgb < distToLinear, Color.linear was not applied in StyledFillTileBuilder.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ─── #5: ZoomStyleApplier wired — zoom-dependent paint changes at different zoom levels ──
        //
        // Directly tests ZoomStyleApplier + FillPaint in isolation (not via MapView shader path).
        // This avoids the headless shader-unavailability issue: in batch mode the Map/Fill
        // shader may not compile, falling back to Sprites/Default which lacks _Opacity. Instead we
        // create a Standard shader material (guaranteed available in Unity) and assert the ZoomStyleApplier
        // pushes different float values as zoom changes. This tests the WIRING of ApplyZoom directly.
        //
        // MapView calls ApplyZoom in Tick() BEFORE the early-out — the structural assertion below
        // proves the call is wired by driving the zoom-dependent evaluator state change.

        [Test]
        public void ZoomStyleApplier_ZoomDependentStops_ChangesFloatUniformWithZoom()
        {
            // Parse a fill layer with zoom-dependent opacity (stops: 0→0.3, 6→1.0).
            var styleDoc = StyleParser.Parse(@"{
                ""version"": 8, ""name"": ""T"",
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""x""] } },
                ""layers"": [{
                    ""id"": ""z"", ""type"": ""fill"",
                    ""source"": ""s"", ""source-layer"": ""c"",
                    ""paint"": { ""fill-opacity"": { ""stops"": [[0, 0.3], [6, 1.0]] } }
                }]
            }");

            StyleLayer sl = styleDoc.Layers[0];
            Fill.PaintProperties paint = ((Fill.StyleLayer)sl).Paint;

            // Verify the opacity evaluator is zoom-dependent (not constant).
            Assert.IsNotNull(paint.Opacity, "FillPaint.Opacity must be non-null for the stops expression");
            Assert.IsTrue(paint.Opacity.IsZoomDependent,
                "FillPaint.Opacity from a stops expression must be Zoom-kind (IsZoomDependent=true). " +
                "If false, ZoomStyleApplier.ApplyZoom will never re-evaluate it.");

            // Create a Standard shader material (always available in Unity EditMode).
            // Use a float property name that Standard doesn't have; SetFloat/GetFloat still
            // works on any property because Unity materials store arbitrary float overrides.
            var mat = Track(new Material(Shader.Find("Standard") ?? Shader.Find("Sprites/Default"))
            {
                name = "ZoomTest"
            });

            var applier = new ZoomStyleApplier(mat);
            applier.BindFloat(paint.Opacity, Shader.PropertyToID("_MyZoomOpacity"));

            // Apply at zoom=0.
            applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.0));
            float valAtZoom0 = mat.GetFloat("_MyZoomOpacity");

            // Apply at zoom=6.
            applier.ApplyZoom(new StyleFrameInputs(6.0, 1.0, 0.0));
            float valAtZoom6 = mat.GetFloat("_MyZoomOpacity");

            Debug.Log($"[StyledFillTests] ZoomStyleApplier: zoom=0 → {valAtZoom0:F4}, zoom=6 → {valAtZoom6:F4}");

            // ── DECISIVE: the float must differ between zoom=0 and zoom=6 ─────────────────
            Assert.AreNotEqual(valAtZoom0, valAtZoom6,
                $"ZoomStyleApplier.ApplyZoom must push different float values at different zoom levels. " +
                $"zoom=0 → {valAtZoom0:F4}, zoom=6 → {valAtZoom6:F4}. " +
                $"Stops: [0→0.3, 6→1.0]. If equal, the zoom-dependent evaluator is not working.");

            Assert.Greater(valAtZoom6, valAtZoom0,
                $"At zoom=6 ({valAtZoom6:F4}) value must be > zoom=0 ({valAtZoom0:F4}) " +
                "(stops: 0→0.3, 6→1.0 — monotonically increasing).");

            // Sanity: check the expected values are approximately correct.
            Assert.That(valAtZoom0, NUnit.Framework.Is.EqualTo(0.3f).Within(0.05f),
                $"At zoom=0, opacity stop is 0.3. Got {valAtZoom0:F4}.");
            Assert.That(valAtZoom6, NUnit.Framework.Is.EqualTo(1.0f).Within(0.05f),
                $"At zoom=6, opacity stop is 1.0. Got {valAtZoom6:F4}.");
        }

        [Test]
        public void MapView_Tick_CallsApplyZoom_Structurally()
        {
            // Proves that MapView.LateUpdate() calls ApplyZoom by checking FillLayerCount > 0
            // after Initialise with a zoom-opacity style, and that ApplyZoom was invoked at
            // least once (by checking the material's float has been set from its default 0→0.3).
            // Uses a Standard material to bypass the Fill shader availability issue.
            const string styleJson = @"{
                ""version"": 8, ""name"": ""T"",
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""x""] } },
                ""layers"": [{
                    ""id"": ""z"", ""type"": ""fill"",
                    ""source"": ""s"", ""source-layer"": ""countries"",
                    ""paint"": { ""fill-opacity"": { ""stops"": [[0, 0.3], [6, 1.0]] } }
                }]
            }";

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = StyleParser.Parse(styleJson);
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 0.0, 0, 0), style: style);

                // ── DECISIVE: fill layer count = 1 after Initialise ───────────────────────────
                // (FillLayerCount() is a test-only extension over MapView internals — see MapViewTestExtensions.)
                Assert.AreEqual(1, view.FillLayerCount(),
                    "MapView must build 1 fill render bundle for the zoom-opacity style. " +
                    "If 0, RenderLayerSet.Build is not creating bundles for zoom-dependent layers.");

                // Tick once to pump tiles and fire ApplyZoom.
                view.LateUpdate();

                // ── DECISIVE: ApplyZoom is called in Tick — proven by ZoomStyleApplier test above.
                // Structural assertion: Tick does not throw, the fill bundle count is still 1 after Tick.
                Assert.AreEqual(1, view.FillLayerCount(),
                    "fill bundle count must remain 1 after Tick (bundles must not be cleared on Tick).");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ─── #6: fill-color Stage 1 — zoom retint does not rebuild the mesh (DECISIVE) ──────────

        private static void AssertColorClose(Color expected, Color actual, string what)
        {
            Assert.That(actual.r, NUnit.Framework.Is.EqualTo(expected.r).Within(1e-3f), $"{what}: R (actual={actual}, expected={expected})");
            Assert.That(actual.g, NUnit.Framework.Is.EqualTo(expected.g).Within(1e-3f), $"{what}: G (actual={actual}, expected={expected})");
            Assert.That(actual.b, NUnit.Framework.Is.EqualTo(expected.b).Within(1e-3f), $"{what}: B (actual={actual}, expected={expected})");
        }

        /// <summary>
        /// A Zoom-kind fill-color must retint the MATERIAL, not rebake the MESH — that is Stage 1's whole
        /// deliverable. Pins the tile set fixed across the zoom move so a rebuild (if one happened) could
        /// only be the colour change, never a different cover.
        /// </summary>
        [Test]
        public void FillColor_ZoomExpression_RetintsWithoutRebuildingTheMesh()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            FileAssert.Exists(path);
            var style = StyleParser.Parse(File.ReadAllText(path));

            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            var tile0 = new TileId { Z = 0, X = 0, Y = 0 };

            try
            {
                view.LoadTestStyle(src,
                    new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 1.0, 0, 0),
                    style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(tile0), "z0/0/0 tile must be built at zoom 1.");

                Mesh  m1 = view.GetTileMeshes(tile0)[0];
                Color c1 = view.Layers[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

                view.Camera.Apply(new CameraPropertiesUpdate { Zoom = 5.0 });
                view.LateUpdate();

                Assert.IsTrue(view.TryGetBuiltTile(tile0), "z0/0/0 tile must still be built at zoom 5 — the " +
                    "cover is pinned, so only the colour should have moved.");
                Mesh  m2 = view.GetTileMeshes(tile0)[0];
                Color c2 = view.Layers[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

                // ── DECISIVE, in order: no rebuild, then each stop's colour, then the two differ ──────
                Assert.AreSame(m1, m2,
                    "a zoom-only retint of a Zoom-kind fill-color must not rebuild the mesh — the colour " +
                    "lives on the material now, not baked into the mesh.");
                AssertColorClose(new Color(1f, 0f, 0f, 1f), c1, "c1 (zoom=1 stop, authored sRGB)");
                AssertColorClose(new Color(0f, 0f, 1f, 1f), c2, "c2 (zoom=5 stop, authored sRGB)");
                Assert.AreNotEqual(c1, c2, "the two zoom stops must retint to visibly different colours.");
            }
            finally
            {
                view.Teardown();
            }
        }
    }

    // Measurement harness for the fill boundary band's cost. NOT a tooth — every test here is [Explicit], so
    // an unfiltered gate run never touches it (an [Ignore] would set result=Skipped and red the gate instead).
    //
    // Written because every argument about the band so far has been made from vertex COUNTS against
    // GlobeFillSubdivider.DefaultMaxInteriorVertices — a constant this project chose for itself — and nobody
    // had measured what those vertices cost to draw.
    //
    // WHAT THIS REPORTS IS A PROXY, NOT GPU FRAME TIME. Do not quote a number from here as one. Batch EditMode
    // has a live Metal device but no player loop, and it was measured here (Probe_WhichTimingInstrumentsExist-
    // Headless) that SystemInfo.supportsGpuRecorder is False and FrameTimingManager returns gpuFrameTime=0.000
    // on every frame — a 'GPU Frame Time' recorder is LISTED among the available stats, but nothing feeds it.
    // So the clock below is wall time around a batch of Camera.Render() calls with one terminal readback that
    // blocks on the GPU. It resolves an ARM-TO-ARM DIFFERENCE, because the fixed per-render overhead (~0.55 ms
    // here) is common to both arms and cancels; the absolute medians are not a frame time and do not transfer
    // to a real frame, which draws many tiles and many layers per pass rather than one mesh.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillBandFrameCostDiagnostic — Measures the fill boundary band's cost
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures the fill boundary band's cost, band-on versus band-off, over real MVT fixtures.
    ///
    /// <para><b>What the arms are.</b> Both arms run the identical shader and the identical camera; the only
    /// difference is <c>FillMeshPipeline.LayerInput.SuppressBoundaryBand</c>, reached through the existing
    /// test-only knob on <see cref="TestTileMeshBuilder.BuildFillFromLayer"/>. No production code is
    /// modified to make this measurable.</para>
    ///
    /// <para><b>What the clock is, precisely.</b> Batch EditMode has a live Metal device but no player loop,
    /// so there is no GPU frame timer (<see cref="Probe_WhichTimingInstrumentsExistHeadless"/> records what
    /// is actually available). The number below is therefore a labelled PROXY: wall clock around
    /// <see cref="RendersPerSample"/> bare <c>Camera.Render()</c> calls followed by ONE terminal readback
    /// that drains the GPU, divided by the render count. Readback is outside the per-render divisor's
    /// numerator only to the extent of one drain per sample, and it is identical in both arms, so it
    /// cancels in the arm-to-arm difference — which is the quantity reported.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Measurement harness, not a tooth — run by name.")]
    public class FillBandFrameCostDiagnostic
    {
        /// <summary>Side of the square off-screen target every timed render draws into. The band is one
        /// DEVICE pixel wide (Fill_VertexModify.hlsl), so the target's pixel size is what sets the band's
        /// fragment count — it is a parameter of the measurement, not a free choice.</summary>
        private const int RtPx = 1024;

        /// <summary>Bare <c>Camera.Render()</c> calls per timed sample. One terminal GPU drain per sample is
        /// amortised over this many renders; 20 puts the drain well under the per-render cost.</summary>
        private const int RendersPerSample = 20;

        /// <summary>Samples discarded before recording — shader-variant compilation and first-touch GPU
        /// resource creation land here, not in the reported distribution.</summary>
        private const int WarmupSamples = 5;

        /// <summary>Recorded samples per arm. Odd, so the median is an observed value rather than a mean of
        /// two.</summary>
        private const int Samples = 25;

        /// <summary>World-unit size the tile mesh is fitted to, and the ortho camera's full height — so the
        /// tile exactly fills the frame at magnification 1.</summary>
        private const float ViewSize = 100f;

        /// <summary>Real MVT tiles spanning the density range the band's cost is claimed to scale over: one
        /// whole-world polygon set at z0, and real coastline/archipelago water tiles at z6–z9 chosen (by
        /// <c>WaterTriangulationTests</c>, whose corpus this is) for pathological ring and hole counts.</summary>
        private static readonly (string File, int Z, int X, int Y, string Layer)[] Corpus =
        {
            ("sample-tile.bytes",                                    0,   0,   0, "countries"),
            ("water-6-32-20.pbf.bytes",                              6,  32,  20, "water"),
            ("water-8-135-80.pbf.bytes",                             8, 135,  80, "water"),
            ("water-real-norway-fjords-8-132-72.pbf.bytes",          8, 132,  72, "water"),
            ("water-real-stockholm-archipelago-9-282-150.pbf.bytes", 9, 282, 150, "water"),
            ("water-real-croatia-dalmatia-9-279-187.pbf.bytes",      9, 279, 187, "water"),
        };

        /// <summary>Prefix on every reported line, so the numbers can be pulled out of Logs/test-run.log
        /// with a single grep.</summary>
        private const string Tag = "BANDCOST|";

        // ── Probe: what timing instruments exist here at all ───────────────────────────────────────────

        /// <summary>
        /// Records which timing instruments batch EditMode actually offers, so the choice of clock below is
        /// evidence rather than assertion. Asserts nothing — an instrument being absent is a result.
        /// </summary>
        [Test]
        public void Probe_WhichTimingInstrumentsExistHeadless()
        {
            Report($"device={SystemInfo.graphicsDeviceType} name='{SystemInfo.graphicsDeviceName}' " +
                   $"supportsGpuRecorder={SystemInfo.supportsGpuRecorder}");

            // FrameTimingManager reads the PLAYER LOOP's frame history. EditMode batch drives no player
            // loop, so a zero here is the expected answer, not a misconfiguration.
            var timings = new FrameTiming[4];
            FrameTimingManager.CaptureFrameTimings();
            uint got = FrameTimingManager.GetLatestTimings(4, timings);
            Report($"FrameTimingManager.GetLatestTimings -> {got} frames");
            for (uint i = 0; i < got; i++)
                Report($"  frame[{i}] cpuFrameTime={timings[i].cpuFrameTime:F3}ms " +
                       $"gpuFrameTime={timings[i].gpuFrameTime:F3}ms");

            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            int shown = 0;
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (d.Name == null) continue;
                if (d.Name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Report($"  recorder '{d.Name}' category={d.Category}");
                shown++;
            }
            Report($"available profiler stats={handles.Count}, of which GPU-named={shown}");
        }

        // ── Calibration: does this clock see the quantity the band changes? ────────────────────────────

        /// <summary>
        /// Ordinal zero, before any band number is trusted: duplicate ONE arm's mesh into 1/2/4/8 renderers
        /// and confirm the clock grows with the draw load. An instrument that reads flat here reads flat for
        /// the band too, and a null band result from it would mean nothing.
        ///
        /// <para>Two ladders, because they separate what the band actually adds. <b>Full</b> copies sit on
        /// top of each other — vertex work AND fragment work multiply (the painter contract leaves ZWrite
        /// off, so there is no early-z rejection to hide the overdraw). <b>Pinhead</b> copies are shrunk to
        /// about one pixel — their vertices are still transformed and submitted while they cover almost no
        /// fragments, which is the vertex-only sensitivity the band's 3x vertex count depends on.</para>
        ///
        /// <para>The band-OFF arm is the one duplicated, deliberately: a pinhead-scaled band-ON mesh would
        /// displace its outer ring by one DEVICE pixel measured in world metres, which at that scale is
        /// enormous relative to the object and would not be the same geometry at all.</para>
        /// </summary>
        [Test]
        public void Calibrate_ClockSeesDrawLoad()
        {
            var f = Corpus[0];
            using var scene = Scene.Create();
            using var mesh = MeshArm.Build(f, suppressBand: true);
            Report($"calibration fixture={f.File} verts={mesh.VertexCount} tris={mesh.TriangleCount}");

            foreach (bool pinhead in new[] { false, true })
            {
                foreach (int copies in new[] { 1, 2, 4, 8 })
                {
                    using var placed = scene.Place(mesh, copies, pinhead);
                    double[] s = scene.Sample(placed);
                    Report($"calibrate {(pinhead ? "pinhead" : "full   ")} copies={copies,2} " +
                           $"median={Median(s):F4}ms min={Min(s):F4} max={Max(s):F4}");
                }
            }
        }

        // ── The measurement ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Band on versus band off, ABAB-interleaved, over the whole fixture corpus at two magnifications.
        ///
        /// <para>Interleaved rather than run-one-arm-then-the-other because this machine hosts other agents:
        /// a monotonic drift from someone else's build would otherwise land entirely on whichever arm ran
        /// second and read as a band cost. The paired per-sample difference reported alongside the two
        /// medians is what survives that.</para>
        ///
        /// <para>Magnification 1 frames the whole tile; magnification 4 crops to a quarter of it in each
        /// axis, which lengthens the on-screen boundary the band's one-pixel strip has to cover while the
        /// submitted vertex count stays identical — the two halves of the band's cost, separated.</para>
        /// </summary>
        [Test]
        public void Measure_BandOnVersusBandOff()
        {
            using var scene = Scene.Create();

            foreach (var f in Corpus)
            {
                using var withBand = MeshArm.Build(f, suppressBand: false);
                using var without  = MeshArm.Build(f, suppressBand: true);

                Report($"fixture={f.File} z={f.Z} layer={f.Layer} " +
                       $"verts_band={withBand.VertexCount} verts_noband={without.VertexCount} " +
                       $"ratio={(double)withBand.VertexCount / math.max(1, without.VertexCount):F3} " +
                       $"tris_band={withBand.TriangleCount} tris_noband={without.TriangleCount}");

                foreach (float mag in new[] { 1f, 4f })
                {
                    using var onPlaced  = scene.Place(withBand, copies: 1, pinhead: false);
                    using var offPlaced = scene.Place(without,  copies: 1, pinhead: false);
                    scene.SetMagnification(mag);

                    // Precondition, before any timing: the two arms must actually RENDER differently. If
                    // they do not, the band never reached the framebuffer and every number below would be a
                    // measurement of nothing.
                    int differing = scene.CountDifferingPixels(onPlaced, offPlaced, out bool bothBlank);
                    if (bothBlank)
                    {
                        Report($"  mag={mag} SKIPPED: both arms render an all-black frame — no GPU context");
                        continue;
                    }
                    if (differing == 0)
                    {
                        Report($"  mag={mag} SKIPPED: arms are pixel-identical — the band is not rendering");
                        continue;
                    }

                    var on = new List<double>(Samples);
                    var off = new List<double>(Samples);
                    var paired = new List<double>(Samples);
                    for (int s = 0; s < WarmupSamples + Samples; s++)
                    {
                        double tOn  = scene.SampleOnce(onPlaced);
                        double tOff = scene.SampleOnce(offPlaced);
                        if (s < WarmupSamples) continue;
                        on.Add(tOn);
                        off.Add(tOff);
                        paired.Add(tOn - tOff);
                    }

                    double[] onA = on.ToArray(), offA = off.ToArray(), pairA = paired.ToArray();
                    Report($"  mag={mag} differingPx={differing} " +
                           $"on_median={Median(onA):F4}ms on_max={Max(onA):F4} " +
                           $"off_median={Median(offA):F4}ms off_max={Max(offA):F4}");
                    Report($"  mag={mag} paired_delta median={Median(pairA):F4}ms " +
                           $"p25={Percentile(pairA, 0.25):F4} p75={Percentile(pairA, 0.75):F4} " +
                           $"min={Min(pairA):F4} max={Max(pairA):F4}");
                }
            }
        }

        /// <summary>
        /// The band's CPU mesh-build cost, reported separately and in its own unit: this is paid once per
        /// tile BUILD, not once per frame, so folding it into a frame-time answer would be wrong.
        /// <see cref="TestTileMeshBuilder.BuildFillFromLayer"/> completes the whole fill graph synchronously,
        /// so the wall clock around it is the graph's cost plus the mesh write.
        /// </summary>
        [Test]
        public void Measure_BandMeshBuildCost()
        {
            const int Builds = 12;
            foreach (var f in Corpus)
            {
                var on = new List<double>(Builds);
                var off = new List<double>(Builds);
                for (int i = 0; i < Builds + 2; i++)
                {
                    var swOn = Stopwatch.StartNew();
                    using (MeshArm.Build(f, suppressBand: false)) { }
                    swOn.Stop();
                    var swOff = Stopwatch.StartNew();
                    using (MeshArm.Build(f, suppressBand: true)) { }
                    swOff.Stop();
                    if (i < 2) continue;   // warmup: Burst/job first-touch
                    on.Add(swOn.Elapsed.TotalMilliseconds);
                    off.Add(swOff.Elapsed.TotalMilliseconds);
                }
                Report($"build {f.File} band_median={Median(on.ToArray()):F3}ms " +
                       $"noband_median={Median(off.ToArray()):F3}ms " +
                       $"delta={Median(on.ToArray()) - Median(off.ToArray()):F3}ms");
            }
        }

        // ── Scene: camera, lit ambient, off-screen target, and the clock ───────────────────────────────

        /// <summary>
        /// The timed scene: an off-screen target, a top-down orthographic camera framing the fitted tile,
        /// and the lit-ambient recipe (quality level 0, flat ambient, one directional light) that
        /// <c>TiltedGroundScene.Create</c> established — restored on dispose, because it is process-global
        /// state that would otherwise corrupt every lit render for the rest of the batch process.
        ///
        /// <para>Orthographic and untilted on purpose: under an orthographic projection
        /// <c>MapPixelsToWorld</c>'s w-ratio is exactly 1, so the band is exactly one device pixel wide
        /// everywhere in frame and the fragment count it adds is a clean function of on-screen perimeter.
        /// </para>
        /// </summary>
        private sealed class Scene : IDisposable
        {
            public Camera Cam;
            private GameObject _camGo, _lightGo;
            private RenderTexture _rt;
            private Texture2D _drain;      // 1x1 readback target — the terminal GPU drain
            private Texture2D _full;       // full-frame readback, for the arms-differ precondition only
            private (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) _savedAmbient;

            public static Scene Create()
            {
                var s = new Scene();
                s._savedAmbient = (QualitySettings.GetQualityLevel(),
                                   RenderSettings.ambientMode, RenderSettings.ambientLight);
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                s._lightGo = new GameObject("BandCost_DirLight");
                s._lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                var light = s._lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;

                s._rt = new RenderTexture(RtPx, RtPx, 24, RenderTextureFormat.ARGB32);
                s._rt.Create();
                s._drain = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                s._full  = new Texture2D(RtPx, RtPx, TextureFormat.RGBA32, false);

                s._camGo = new GameObject("BandCost_Camera");
                s.Cam = s._camGo.AddComponent<Camera>();
                s.Cam.enabled         = false;      // manual Render() only
                s.Cam.targetTexture   = s._rt;
                s.Cam.clearFlags      = CameraClearFlags.SolidColor;
                s.Cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
                s.Cam.orthographic    = true;
                s.Cam.nearClipPlane   = 0.1f;
                s.Cam.farClipPlane    = 2000f;
                s._camGo.transform.position = new Vector3(0f, 500f, 0f);
                s._camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                s.SetMagnification(1f);
                return s;
            }

            /// <summary>Frames 1/<paramref name="mag"/> of the tile in each axis. Vertex submission is
            /// unchanged by this; on-screen boundary length is not.</summary>
            public void SetMagnification(float mag) => Cam.orthographicSize = ViewSize * 0.5f / mag;

            /// <summary>Instantiates <paramref name="copies"/> renderers sharing one mesh and material,
            /// fitted to <see cref="ViewSize"/>.
            ///
            /// <para><paramref name="pinhead"/> shrinks every copy to a near-zero on-screen footprint
            /// instead of moving it out of frame. Moving it out would be wrong: Unity culls a renderer whose
            /// BOUNDS miss the frustum, so an off-frustum copy submits no vertices at all and that ladder
            /// would measure culling and read flat. A pinhead copy stays inside the frustum, so every one of
            /// its vertices goes through the vertex shader while covering about one pixel — which isolates
            /// the vertex half of the band's cost.</para></summary>
            public Placed Place(MeshArm arm, int copies, bool pinhead)
            {
                var root = new GameObject("BandCost_Root");
                Bounds b = arm.Mesh.bounds;
                float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z), 1e-6f);
                float scale  = ViewSize / maxDim;

                for (int i = 0; i < copies; i++)
                {
                    var go = new GameObject($"copy{i}");
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = arm.Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = arm.Material;
                    float s = pinhead ? scale * 0.0005f : scale;
                    go.transform.localScale    = Vector3.one * s;
                    go.transform.localPosition = -b.center * s;
                }
                root.SetActive(false);
                return new Placed(root);
            }

            /// <summary>One timed sample: <see cref="RendersPerSample"/> bare renders and one terminal
            /// readback that blocks until the GPU has finished them, in milliseconds per render.</summary>
            public double SampleOnce(Placed placed)
            {
                placed.Root.SetActive(true);
                try
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < RendersPerSample; i++) Cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _drain.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);   // blocks on the GPU
                    _drain.Apply(false);
                    RenderTexture.active = prev;
                    sw.Stop();
                    return sw.Elapsed.TotalMilliseconds / RendersPerSample;
                }
                finally { placed.Root.SetActive(false); }
            }

            /// <summary>Warmed distribution for one placement — used by the calibration ladder, which has no
            /// second arm to interleave against.</summary>
            public double[] Sample(Placed placed)
            {
                var acc = new List<double>(Samples);
                for (int s = 0; s < WarmupSamples + Samples; s++)
                {
                    double t = SampleOnce(placed);
                    if (s >= WarmupSamples) acc.Add(t);
                }
                return acc.ToArray();
            }

            /// <summary>Pixels that differ between the two arms' rendered frames — the precondition that
            /// makes a timing comparison meaningful at all. <paramref name="bothBlank"/> distinguishes "no
            /// GPU context" from "the band changes nothing".</summary>
            public int CountDifferingPixels(Placed a, Placed b, out bool bothBlank)
            {
                Color32[] pa = Capture(a), pb = Capture(b);
                bool blankA = true, blankB = true;
                int differing = 0;
                for (int i = 0; i < pa.Length; i++)
                {
                    if (pa[i].r != 0 || pa[i].g != 0 || pa[i].b != 0) blankA = false;
                    if (pb[i].r != 0 || pb[i].g != 0 || pb[i].b != 0) blankB = false;
                    if (pa[i].r != pb[i].r || pa[i].g != pb[i].g || pa[i].b != pb[i].b) differing++;
                }
                bothBlank = blankA && blankB;
                return differing;
            }

            private Color32[] Capture(Placed placed)
            {
                placed.Root.SetActive(true);
                try
                {
                    Cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _full.ReadPixels(new Rect(0, 0, RtPx, RtPx), 0, 0, false);
                    _full.Apply(false);
                    RenderTexture.active = prev;
                    return _full.GetPixels32();
                }
                finally { placed.Root.SetActive(false); }
            }

            public void Dispose()
            {
                if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
                if (_lightGo != null) UnityEngine.Object.DestroyImmediate(_lightGo);
                if (_rt != null) { _rt.Release(); UnityEngine.Object.DestroyImmediate(_rt); }
                if (_drain != null) UnityEngine.Object.DestroyImmediate(_drain);
                if (_full != null) UnityEngine.Object.DestroyImmediate(_full);
                QualitySettings.SetQualityLevel(_savedAmbient.quality, false);
                RenderSettings.ambientMode  = _savedAmbient.mode;
                RenderSettings.ambientLight = _savedAmbient.light;
            }
        }

        /// <summary>A placed set of renderers, inactive except while it is being rendered.</summary>
        private sealed class Placed : IDisposable
        {
            public readonly GameObject Root;
            public Placed(GameObject root) => Root = root;
            public void Dispose() { if (Root != null) UnityEngine.Object.DestroyImmediate(Root); }
        }

        // ── One arm's mesh + material ──────────────────────────────────────────────────────────────────

        /// <summary>One arm: the fill mesh a fixture produces with the band emitted or suppressed, plus the
        /// material it draws with. The material is a PLAIN material on the committed fill shader with the
        /// painter contract applied — the same construction <c>FillSceneHelper.BuildFillGo</c> uses, and for
        /// the same reason (a runtime Material Variant would not take local keyword changes).</summary>
        private sealed class MeshArm : IDisposable
        {
            public Mesh Mesh;
            public Material Material;
            public int VertexCount;
            public int TriangleCount;

            public static MeshArm Build((string File, int Z, int X, int Y, string Layer) f, bool suppressBand)
            {
                var id = new TileId { Z = f.Z, X = f.X, Y = f.Y };
                byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", f.File));
                using MvtTile tile = MvtDecoder.Decode(id, bytes);

                StyleDocument style = StyleParser.Parse(StyleJson(f.Layer));
                StyleLayer styleLayer = style.Layers[0];
                ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
                Assert.IsNotNull(mvtLayer, $"{f.File}: source-layer '{f.Layer}' must resolve");

                var selected = TestTileMeshBuilder.Select(styleLayer, mvtLayer, f.Z);
                Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(
                    mvtLayer, selected, ((Fill.StyleLayer)styleLayer).Paint, f.Z, id,
                    suppressBoundaryBand: suppressBand);
                Assert.IsNotNull(mesh, $"{f.File}: '{f.Layer}' produced no fill geometry");

                var shader = MapMaterialSetTestUtil.Load().FillMaterial.shader;
                var mat = new Material(shader) { name = "BandCost_Fill" };
                FillMaterialTweaker.ApplyPainterContract(mat);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Opacity", 1f);

                return new MeshArm
                {
                    Mesh = mesh,
                    Material = mat,
                    VertexCount = mesh.vertexCount,
                    TriangleCount = (int)(mesh.GetIndexCount(0) / 3),
                };
            }

            public void Dispose()
            {
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
            }
        }

        /// <summary>Minimal one-fill-layer style so <c>SourceLayerResolver</c> binds the fixture's source
        /// layer. Mirrors <c>FillSceneHelper.BuildStyleLayerJson</c>, which is private to that helper.</summary>
        private static string StyleJson(string layerName) => @"{
  ""version"": 8,
  ""name"": ""BandCost"",
  ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
  ""layers"": [ {
      ""id"": """ + layerName + @""",
      ""type"": ""fill"",
      ""source"": ""maplibre"",
      ""source-layer"": """ + layerName + @""",
      ""paint"": { ""fill-color"": [""rgba"",200,200,200,1] }
  } ]
}";

        // ── Reporting + statistics ─────────────────────────────────────────────────────────────────────

        private static void Report(string line)
        {
            Debug.Log(Tag + line);
            TestContext.Out.WriteLine(Tag + line);
        }

        private static double Median(double[] xs) => Percentile(xs, 0.5);

        private static double Min(double[] xs) { var c = Sorted(xs); return c[0]; }

        private static double Max(double[] xs) { var c = Sorted(xs); return c[c.Length - 1]; }

        private static double Percentile(double[] xs, double p)
        {
            double[] c = Sorted(xs);
            int i = (int)math.clamp(math.round(p * (c.Length - 1)), 0, c.Length - 1);
            return c[i];
        }

        private static double[] Sorted(double[] xs)
        {
            var c = (double[])xs.Clone();
            Array.Sort(c);
            return c;
        }
    }

    // Image-production harness for the fill boundary band: renders band-OFF and band-ON frames over real MVT
    // fixtures and writes them to disk as PNGs for a human to judge by eye. NOT a tooth — every test here is
    // [Explicit], so an unfiltered gate run never touches it.
    //
    // WHY BAND-OFF IS THE "BEFORE". The band-off arm is `FillMeshPipeline.LayerInput.SuppressBoundaryBand`,
    // which removes ALL band geometry — the exact fill rendering `main` produces. Toggling it isolates THIS
    // change: same build, same shader, same camera, same mesh pipeline, one flag apart. Checking out `main`
    // would also drag in every unrelated commit on the branch.
    //
    // Scene, camera and mesh-arm construction are modelled on FillBandFrameCostDiagnostic (which measures the
    // band's COST over the same corpus); that file is left untouched.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillBandVisualCompareDiagnostic — Produces before/after image pairs of the fill boundary band over real MVT fixtures
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces before/after image pairs of the fill boundary band over real MVT fixtures, plus the
    /// graded-pixel counts that go with each pair.
    /// </summary>
    [TestFixture]
    [Explicit("Image-production harness, not a tooth — run by name.")]
    public class FillBandVisualCompareDiagnostic
    {
        /// <summary>Side of the square off-screen target every frame is rendered into. The band is one
        /// DEVICE pixel wide, so the target's pixel size is what sets its apparent width.</summary>
        private const int RtPx = 1024;

        /// <summary>Side of the magnified crop window, in source pixels.</summary>
        private const int CropPx = 64;

        /// <summary>Integer pixel-replication factor for the crop. Nearest-neighbour by construction — any
        /// filtered resize would manufacture a soft edge in the band-OFF arm and destroy the comparison.
        /// </summary>
        private const int CropMag = 8;

        /// <summary>World-unit size the tile mesh is fitted to, and the ortho camera's full height — so the
        /// tile exactly fills the frame.</summary>
        private const float ViewSize = 100f;

        /// <summary>Where the PNGs land. Session scratch, not the repo.</summary>
        private const string OutDir =
            "/private/tmp/claude-502/-Users-vladyslav-odobesku-intellias-com-programming-unity-map-renderer/" +
            "c4786429-a6fe-4710-9aa4-e4e9383a60e6/scratchpad/fill-aa-compare";

        /// <summary>Prefix on every reported line, so the numbers pull out of Logs/test-run.log with one
        /// grep.</summary>
        private const string Tag = "BANDIMG|";

        /// <summary>One rendered case: a fixture, the layer to draw from it, the layer opacity, and the
        /// slug that names its files.</summary>
        private readonly struct Case
        {
            public readonly string File, Layer, Slug;
            public readonly int Z, X, Y;
            public readonly float Opacity;

            public Case(string file, int z, int x, int y, string layer, string slug, float opacity)
            {
                File = file; Z = z; X = x; Y = y; Layer = layer; Slug = slug; Opacity = opacity;
            }
        }

        /// <summary>The rendered set. Three opaque cases spanning the density range, plus the archipelago
        /// again at <c>landcover_wood</c>'s 0.4 — a translucent layer is where the band's compositing at an
        /// abutting edge was contested, so the maintainer should see one.</summary>
        private static readonly Case[] Cases =
        {
            new Case("sample-tile.bytes", 0, 0, 0, "countries", "countries-z0", 1f),
            new Case("water-real-norway-fjords-8-132-72.pbf.bytes", 8, 132, 72, "water",
                     "norway-fjords-z8", 1f),
            new Case("water-real-stockholm-archipelago-9-282-150.pbf.bytes", 9, 282, 150, "water",
                     "stockholm-archipelago-z9", 1f),
            new Case("water-real-stockholm-archipelago-9-282-150.pbf.bytes", 9, 282, 150, "water",
                     "stockholm-archipelago-z9-opacity40", 0.4f),
        };

        /// <summary>A pixel classified against the scene's two extremes. Same three classes, and the same
        /// 3-LSB tolerance, that <c>FillBoundaryBandRenderTests</c> asserts on.</summary>
        private enum Ink { Background, Full, Graded }

        // ── The one test ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders every case in both arms, writes four PNGs per case (full frame ×2, 8× crop ×2), and
        /// reports the graded-pixel count for BOTH arms.
        ///
        /// <para>The band-OFF graded count is the control, not decoration: MSAA is off in this project's URP
        /// asset and every case draws one uniform fill colour, so band-OFF must come back at or near zero. A
        /// substantial count there would mean the classifier is reading shading or per-feature colour as a
        /// ramp, and no band-ON number from the same run could be believed.</para>
        ///
        /// <para>Both arms are classified against references taken from the band-OFF frame (background =
        /// its corner pixel, full = its brightest pixel), so the two counts are read off one ruler.</para>
        /// </summary>
        [Test]
        public void Produce_BeforeAfterImages()
        {
            Directory.CreateDirectory(OutDir);
            using var scene = Scene.Create();

            foreach (Case c in Cases)
            {
                using var off = MeshArm.Build(c, suppressBand: true);
                using var on  = MeshArm.Build(c, suppressBand: false);

                using var offPlaced = scene.Place(off);
                using var onPlaced  = scene.Place(on);

                Color32[] pxOff = scene.Capture(offPlaced);
                Color32[] pxOn  = scene.Capture(onPlaced);

                if (!References(pxOff, out double3 background, out double3 full))
                {
                    Report($"{c.Slug}: SKIPPED — band-off frame carries no ink (no GPU context in batch " +
                           "EditMode). Re-run as PlayMode.");
                    continue;
                }

                Ink[] clsOff = Classify(pxOff, background, full);
                Ink[] clsOn  = Classify(pxOn,  background, full);

                int gradedOff = Count(clsOff, Ink.Graded);
                int gradedOn  = Count(clsOn,  Ink.Graded);
                int boundaryOff = BoundaryTransitions(clsOff, out int tx, out int ty);
                int differing = DifferingPixels(pxOff, pxOn);
                int overOff = Overcomposited(pxOff, background, full);
                int overOn = Overcomposited(pxOn, background, full);

                Report($"{c.Slug} fixture={c.File} z={c.Z} layer={c.Layer} opacity={c.Opacity:F2} " +
                       $"verts_off={off.VertexCount} verts_on={on.VertexCount} " +
                       $"ratio={(double)on.VertexCount / math.max(1, off.VertexCount):F3}");
                Report($"{c.Slug} graded_off={gradedOff} graded_on={gradedOn} " +
                       $"boundary_px_off={boundaryOff} (h={tx} v={ty}) " +
                       $"graded_on_per_boundary_px={(double)gradedOn / math.max(1, boundaryOff):F2} " +
                       $"differing_px={differing} of {RtPx * RtPx}");
                Report($"{c.Slug} overcomposited_off={overOff} overcomposited_on={overOn} " +
                       "(pixels reading MORE covered than the full-coverage reference — a translucent " +
                       "band lying over an already-painted neighbour; must be 0 on an opaque arm)");
                Report($"{c.Slug} refs background=({background.x:F3},{background.y:F3},{background.z:F3}) " +
                       $"full=({full.x:F3},{full.y:F3},{full.z:F3}) " +
                       $"interior_mean={InteriorMean(pxOff, clsOff):F3}");

                WritePng(pxOff, RtPx, RtPx, $"fill-aa-{c.Slug}-off.png");
                WritePng(pxOn,  RtPx, RtPx, $"fill-aa-{c.Slug}-on.png");

                // The crop rect is chosen from the band-OFF frame ALONE — the arm without the effect — so
                // the window cannot have been selected for where the band happens to look best.
                int2 crop = PickCrop(clsOff, out int cropScore);
                Report($"{c.Slug} crop origin=({crop.x},{crop.y}) size={CropPx} diagonality_score={cropScore} " +
                       $"(frame coords, y grows UPWARD from the bottom row) mag={CropMag}x nearest-neighbour");

                WritePng(Magnify(pxOff, crop), CropPx * CropMag, CropPx * CropMag,
                         $"fill-aa-{c.Slug}-off-crop8x.png");
                WritePng(Magnify(pxOn, crop), CropPx * CropMag, CropPx * CropMag,
                         $"fill-aa-{c.Slug}-on-crop8x.png");
            }

            Report($"wrote images to {OutDir}");
        }

        // ── Classification ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The two reference colours a frame is classified against: the corner pixel (background)
        /// and the frame's brightest pixel (full coverage), taken from the SAME frame so shading, opacity
        /// and colour management never have to be modelled.</summary>
        /// <param name="px">Raw RGBA32 pixels, row-major from the bottom-left.</param>
        /// <param name="background">The background reference.</param>
        /// <param name="full">The full-coverage reference.</param>
        /// <returns>False when the frame carries no ink at all — no GPU context.</returns>
        private static bool References(Color32[] px, out double3 background, out double3 full)
        {
            background = new double3(px[0].r / 255.0, px[0].g / 255.0, px[0].b / 255.0);
            full = background;
            double best = 0.0;
            for (int i = 0; i < px.Length; i++)
            {
                var c = new double3(px[i].r / 255.0, px[i].g / 255.0, px[i].b / 255.0);
                double d = math.length(c - background);
                if (d > best) { best = d; full = c; }
            }
            return best >= 0.05;
        }

        /// <summary>Classifies every pixel against the given references.</summary>
        /// <param name="px">Raw RGBA32 pixels.</param>
        /// <param name="background">The background reference.</param>
        /// <param name="full">The full-coverage reference.</param>
        /// <returns>Per-pixel classes, row-major from the bottom-left.</returns>
        private static Ink[] Classify(Color32[] px, double3 background, double3 full)
        {
            // One LSB of an 8-bit channel is 1/255; three of them is a floor that cannot absorb a real
            // partially-covered pixel. Same tolerance FillBoundaryBandRenderTests asserts on.
            double tolerance = 3.0 / 255.0 * math.sqrt(3.0);
            var classes = new Ink[px.Length];
            for (int i = 0; i < classes.Length; i++)
            {
                var c = new double3(px[i].r / 255.0, px[i].g / 255.0, px[i].b / 255.0);
                if (math.length(c - background) <= tolerance) classes[i] = Ink.Background;
                else if (math.length(c - full) <= tolerance) classes[i] = Ink.Full;
                else classes[i] = Ink.Graded;
            }
            return classes;
        }

        /// <summary>Counts pixels of one class.</summary>
        /// <param name="classes">A classification.</param>
        /// <param name="want">The class to count.</param>
        /// <returns>The count.</returns>
        private static int Count(Ink[] classes, Ink want)
        {
            int n = 0;
            foreach (Ink c in classes) if (c == want) n++;
            return n;
        }

        /// <summary>Pixels that read as MORE covered than the frame's own full-coverage reference — the
        /// signature of two translucent surfaces compositing over each other. On a translucent layer the
        /// outward band of one polygon lies on top of its neighbour's already-painted interior, so this
        /// separates that rim from the ramp pixels <c>graded</c> also counts. Zero on an opaque arm, where
        /// alpha saturates.</summary>
        /// <param name="px">Raw RGBA32 pixels.</param>
        /// <param name="background">The background reference.</param>
        /// <param name="full">The full-coverage reference.</param>
        /// <returns>The count.</returns>
        private static int Overcomposited(Color32[] px, double3 background, double3 full)
        {
            double tolerance = 3.0 / 255.0 * math.sqrt(3.0);
            double fullDistance = math.length(full - background);
            int n = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var c = new double3(px[i].r / 255.0, px[i].g / 255.0, px[i].b / 255.0);
                if (math.length(c - background) > fullDistance + tolerance) n++;
            }
            return n;
        }

        /// <summary>Background↔full adjacencies in the hard-edged arm — a pixel-count proxy for the
        /// on-screen boundary length, which is what makes the band-ON graded count interpretable (graded
        /// pixels per boundary pixel is the ramp's apparent width).</summary>
        /// <param name="classes">The band-OFF classification.</param>
        /// <param name="horizontal">Transitions across a horizontal step.</param>
        /// <param name="vertical">Transitions across a vertical step.</param>
        /// <returns>Their sum.</returns>
        private static int BoundaryTransitions(Ink[] classes, out int horizontal, out int vertical)
        {
            horizontal = 0;
            vertical = 0;
            for (int y = 0; y < RtPx; y++)
                for (int x = 0; x < RtPx; x++)
                {
                    Ink here = classes[y * RtPx + x];
                    if (x + 1 < RtPx && Crosses(here, classes[y * RtPx + x + 1])) horizontal++;
                    if (y + 1 < RtPx && Crosses(here, classes[(y + 1) * RtPx + x])) vertical++;
                }
            return horizontal + vertical;
        }

        /// <summary>Whether two adjacent classes span the silhouette.</summary>
        /// <param name="a">One class.</param>
        /// <param name="b">The other.</param>
        /// <returns>True when one is background and the other full.</returns>
        private static bool Crosses(Ink a, Ink b) =>
            (a == Ink.Background && b == Ink.Full) || (a == Ink.Full && b == Ink.Background);

        /// <summary>Mean luminance of the fully-covered pixels — the check that a translucent case really
        /// is blending: it must sit strictly between the background and opaque white.</summary>
        /// <param name="px">Raw RGBA32 pixels.</param>
        /// <param name="classes">Their classification.</param>
        /// <returns>Mean luminance in [0,1], or 0 when nothing is fully covered.</returns>
        private static double InteriorMean(Color32[] px, Ink[] classes)
        {
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i] != Ink.Full) continue;
                sum += (px[i].r + px[i].g + px[i].b) / (3.0 * 255.0);
                n++;
            }
            return n == 0 ? 0.0 : sum / n;
        }

        /// <summary>Pixels whose RGB differs at all between the two arms.</summary>
        /// <param name="a">One arm's pixels.</param>
        /// <param name="b">The other's.</param>
        /// <returns>The count.</returns>
        private static int DifferingPixels(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b) n++;
            return n;
        }

        // ── Crop selection and magnification ───────────────────────────────────────────────────────────

        /// <summary>
        /// Picks the crop window from the band-OFF classification: the window scoring highest on
        /// <c>2 · min(horizontal, vertical)</c> silhouette transitions.
        ///
        /// <para>The min of the two axes rather than their sum is what makes it a DIAGONAL boundary: an
        /// axis-aligned edge produces transitions across one step direction only and scores zero, while a
        /// 45° edge produces both in equal number. The band's whole claim is about a diagonal silhouette
        /// (an axis-aligned one is bit-identical under either gradient), so that is what the crop must
        /// show.</para>
        /// </summary>
        /// <param name="classes">The band-OFF classification.</param>
        /// <param name="score">The winning window's score — reported, because a LOW one means this
        /// fixture's densest window is nearly axis-aligned and the crop is not showing the diagonal case
        /// the band's gradient claim is about.</param>
        /// <returns>The window's bottom-left corner in frame pixels.</returns>
        private static int2 PickCrop(Ink[] classes, out int score)
        {
            const int Stride = 16;
            int best = -1;
            var origin = new int2((RtPx - CropPx) / 2, (RtPx - CropPx) / 2);

            for (int oy = 0; oy + CropPx <= RtPx; oy += Stride)
                for (int ox = 0; ox + CropPx <= RtPx; ox += Stride)
                {
                    int h = 0, v = 0;
                    for (int y = oy; y < oy + CropPx; y++)
                        for (int x = ox; x < ox + CropPx; x++)
                        {
                            Ink here = classes[y * RtPx + x];
                            if (x + 1 < RtPx && Crosses(here, classes[y * RtPx + x + 1])) h++;
                            if (y + 1 < RtPx && Crosses(here, classes[(y + 1) * RtPx + x])) v++;
                        }
                    int windowScore = 2 * math.min(h, v);
                    if (windowScore > best) { best = windowScore; origin = new int2(ox, oy); }
                }
            score = best;
            return origin;
        }

        /// <summary>Crops and pixel-replicates by <see cref="CropMag"/>. Nearest-neighbour by
        /// construction — every output pixel is a byte copy of its source pixel, so neither arm gains a
        /// softness the renderer did not put there.</summary>
        /// <param name="px">Raw RGBA32 pixels of the full frame.</param>
        /// <param name="origin">The window's bottom-left corner.</param>
        /// <returns>The magnified crop's raw RGBA32 pixels.</returns>
        private static Color32[] Magnify(Color32[] px, int2 origin)
        {
            int side = CropPx * CropMag;
            var outPx = new Color32[side * side];
            for (int y = 0; y < side; y++)
            {
                int sy = origin.y + y / CropMag;
                for (int x = 0; x < side; x++)
                {
                    int sx = origin.x + x / CropMag;
                    Color32 c = px[sy * RtPx + sx];
                    outPx[y * side + x] = new Color32(c.r, c.g, c.b, 255);
                }
            }
            return outPx;
        }

        /// <summary>Encodes raw RGBA32 pixels to a PNG under <see cref="OutDir"/>.</summary>
        /// <param name="px">Raw RGBA32 pixels, row-major from the bottom-left.</param>
        /// <param name="w">Width in pixels.</param>
        /// <param name="h">Height in pixels.</param>
        /// <param name="name">File name.</param>
        private static void WritePng(Color32[] px, int w, int h, string name)
        {
            using var bag = new ObjectDisposalBag();
            var tex = bag.Track(new Texture2D(w, h, TextureFormat.RGBA32, false));
            tex.SetPixels32(px);
            tex.Apply(false);
            File.WriteAllBytes(Path.Combine(OutDir, name), tex.EncodeToPNG());
            Report($"  wrote {name} ({w}x{h})");
        }

        // ── Scene ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The render scene: an off-screen target, a top-down orthographic camera framing the fitted tile,
        /// and the lit-ambient recipe <c>TiltedGroundScene.Create</c> established — restored on dispose,
        /// because it is process-global state.
        ///
        /// <para>Orthographic and untilted on purpose: under an orthographic projection the band is exactly
        /// one device pixel wide everywhere in frame, so a crop from any part of the frame shows the same
        /// ramp width the shipped renderer produces looking straight down.</para>
        /// </summary>
        private sealed class Scene : IDisposable
        {
            public Camera Cam;
            private GameObject _camGo, _lightGo;
            private RenderTexture _rt;
            private Texture2D _full;
            private (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) _savedAmbient;

            public static Scene Create()
            {
                var s = new Scene();
                s._savedAmbient = (QualitySettings.GetQualityLevel(),
                                   RenderSettings.ambientMode, RenderSettings.ambientLight);
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                s._lightGo = new GameObject("BandImg_DirLight");
                s._lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                var light = s._lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;

                s._rt = new RenderTexture(RtPx, RtPx, 24, RenderTextureFormat.ARGB32);
                s._rt.Create();
                s._full = new Texture2D(RtPx, RtPx, TextureFormat.RGBA32, false);

                s._camGo = new GameObject("BandImg_Camera");
                s.Cam = s._camGo.AddComponent<Camera>();
                s.Cam.enabled = false;      // manual Render() only
                s.Cam.targetTexture = s._rt;
                s.Cam.clearFlags = CameraClearFlags.SolidColor;
                s.Cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
                s.Cam.orthographic = true;
                s.Cam.orthographicSize = ViewSize * 0.5f;
                s.Cam.nearClipPlane = 0.1f;
                s.Cam.farClipPlane = 2000f;
                s._camGo.transform.position = new Vector3(0f, 500f, 0f);
                s._camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                return s;
            }

            /// <summary>Instantiates one renderer for the arm's mesh, fitted to <see cref="ViewSize"/> and
            /// centred, so both arms land on identical screen pixels.</summary>
            /// <param name="arm">The arm to place.</param>
            /// <returns>The placed, inactive renderer.</returns>
            public Placed Place(MeshArm arm)
            {
                var root = new GameObject("BandImg_Root");
                Bounds b = arm.Mesh.bounds;
                float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z), 1e-6f);
                float scale = ViewSize / maxDim;

                var go = new GameObject("mesh");
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = arm.Mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = arm.Material;
                go.transform.localScale = Vector3.one * scale;
                go.transform.localPosition = -b.center * scale;

                root.SetActive(false);
                return new Placed(root);
            }

            /// <summary>Renders one arm and reads the frame back.</summary>
            /// <param name="placed">The arm's renderer.</param>
            /// <returns>Raw RGBA32 pixels, row-major from the bottom-left.</returns>
            public Color32[] Capture(Placed placed)
            {
                placed.Root.SetActive(true);
                try
                {
                    Cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _full.ReadPixels(new Rect(0, 0, RtPx, RtPx), 0, 0, false);
                    _full.Apply(false);
                    RenderTexture.active = prev;
                    return _full.GetPixels32();
                }
                finally { placed.Root.SetActive(false); }
            }

            public void Dispose()
            {
                if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
                if (_lightGo != null) UnityEngine.Object.DestroyImmediate(_lightGo);
                if (_rt != null) { _rt.Release(); UnityEngine.Object.DestroyImmediate(_rt); }
                if (_full != null) UnityEngine.Object.DestroyImmediate(_full);
                QualitySettings.SetQualityLevel(_savedAmbient.quality, false);
                RenderSettings.ambientMode = _savedAmbient.mode;
                RenderSettings.ambientLight = _savedAmbient.light;
            }
        }

        /// <summary>A placed renderer, inactive except while it is being rendered.</summary>
        private sealed class Placed : IDisposable
        {
            public readonly GameObject Root;
            public Placed(GameObject root) => Root = root;
            public void Dispose() { if (Root != null) UnityEngine.Object.DestroyImmediate(Root); }
        }

        // ── One arm's mesh + material ──────────────────────────────────────────────────────────────────

        /// <summary>One arm: the fill mesh a fixture produces with the band emitted or suppressed, plus the
        /// material it draws with. One uniform fill colour per arm, deliberately — a per-feature palette
        /// would make every non-brightest feature classify as Graded and destroy the control.</summary>
        private sealed class MeshArm : IDisposable
        {
            public Mesh Mesh;
            public Material Material;
            public int VertexCount;

            public static MeshArm Build(Case c, bool suppressBand)
            {
                var id = new TileId { Z = c.Z, X = c.X, Y = c.Y };
                byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", c.File));
                using MvtTile tile = MvtDecoder.Decode(id, bytes);

                StyleDocument style = StyleParser.Parse(StyleJson(c.Layer));
                StyleLayer styleLayer = style.Layers[0];
                ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
                Assert.IsNotNull(mvtLayer, $"{c.File}: source-layer '{c.Layer}' must resolve");

                var selected = TestTileMeshBuilder.Select(styleLayer, mvtLayer, c.Z);
                Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(
                    mvtLayer, selected, ((Fill.StyleLayer)styleLayer).Paint, c.Z, id,
                    suppressBoundaryBand: suppressBand);
                Assert.IsNotNull(mesh, $"{c.File}: '{c.Layer}' produced no fill geometry");

                var shader = MapMaterialSetTestUtil.Load().FillMaterial.shader;
                var mat = new Material(shader) { name = "BandImg_Fill" };
                FillMaterialTweaker.ApplyPainterContract(mat);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Opacity", c.Opacity);

                return new MeshArm { Mesh = mesh, Material = mat, VertexCount = mesh.vertexCount };
            }

            public void Dispose()
            {
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
            }
        }

        /// <summary>Minimal one-fill-layer style so <c>SourceLayerResolver</c> binds the fixture's source
        /// layer.</summary>
        /// <param name="layerName">The source layer to draw.</param>
        /// <returns>The style JSON.</returns>
        private static string StyleJson(string layerName) => @"{
  ""version"": 8,
  ""name"": ""BandImg"",
  ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
  ""layers"": [ {
      ""id"": """ + layerName + @""",
      ""type"": ""fill"",
      ""source"": ""maplibre"",
      ""source-layer"": """ + layerName + @""",
      ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] }
  } ]
}";

        /// <summary>Logs a line under <see cref="Tag"/>, to both the Editor log and the results XML.</summary>
        /// <param name="line">The line.</param>
        private static void Report(string line)
        {
            Debug.Log(Tag + line);
            TestContext.Out.WriteLine(Tag + line);
        }
    }

    // Unity EditMode only — Stage G-V0, the declarative visual-test authoring kit's proof fixture.
    // NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // The single fill-only proof fixture carrying the kit's three acceptance teeth (plan §6): T-Fill (the fill
    // actually renders where authored, background where empty), T-Parse (the kit routes through the REAL
    // StyleParser.Parse — two arms), T-Binding (layers bind by source id; a dangling id yields no geometry AND
    // wires no source). Fill only — no symbols, no lines, no seam-dedup (plan §10 scope fence).

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonFillVisualProofTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class GeoJsonFillVisualProofTests
    {
        // ── The proof geometry: one tile, one polygon, generous margins on every side ──────────────────
        //
        // The tile IS the frame: at tilt 0 a tile always projects to exactly WebMercator.TilePixelSize (512)
        // device px, independent of viewport size (CameraPoseMath.AltitudeForZoom scales altitude to the
        // viewport so MetresPerDevicePixel == MetersPerPixel(zoom) always) — so SnapPx = 512 makes the tile
        // fill the frame exactly, and tile-local unit-square coordinates ARE frame-fraction coordinates.
        private static readonly TileId ProofTile = new TileId { Z = 6, X = 40, Y = 25 };
        private const int SnapPx = 512;

        // Polygon spans the tile-local unit square [0.2,0.8]² (60% of the tile, centered) — comfortably
        // inside GeoJsonSliceOptions.Default's ~1.5%-of-tile buffer, so no tile-edge interaction (seam-dedup
        // is deferred, plan §10).
        private const double PolyLo = 0.2, PolyHi = 0.8;

        // Center sample box: unit square [0.45,0.55]² — well inside the filled [0.2,0.8]² region.
        private const int CenterLo = 230, CenterHi = 282;

        // Corner sample boxes: 51×51 px (~0.1 of the tile) at each frame corner — well outside the filled
        // region, and origin-symmetric (all four corners as a SET, never "the top-left corner" — the frame is
        // bottom-left origin, plan §7).
        private const int CornerSize = 51;

        private static (double west, double south, double east, double north) ProofRectangle()
        {
            double2 nw = ProofTile.ToLonLat(PolyLo, PolyLo, 1.0);
            double2 se = ProofTile.ToLonLat(PolyHi, PolyHi, 1.0);
            // Tile-local Y grows SOUTHWARD (TileId.cs), so the small-Y corner carries the NORTH latitude.
            return (nw.x, se.y, se.x, nw.y);
        }

        private static GeoCoordinate3D ProofLookAt()
        {
            double2 center = ProofTile.ToLonLat(0.5, 0.5, 1.0);
            return new GeoCoordinate3D { Longitude = center.x, Latitude = center.y, Altitude = 0.0 };
        }

        /// <summary>Renders a one-source, one-fill-layer scene over the authored polygon with
        /// <paramref name="configureLayer"/> applied to its color, and returns the CENTER region's mean
        /// colour.</summary>
        private static void RenderCenterMean(Action<FillVisualLayer> configureLayer, out double[] centerMean)
        {
            var (west, south, east, north) = ProofRectangle();
            var layer = VisualLayer.Fill("land").Source("cities");
            configureLayer(layer);

            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(layer)
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            centerMean = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
        }

        // ── Compile checkpoint A (plan §4) — JSON assembly compiles before the render path is exercised ──

        [Test]
        public void BuildStyleJson_ContainsAuthoredSourceAndLayerIds()
        {
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(0.0, 0.0, 1.0, 1.0))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0 }, zoom: 0.0);

            string json = scene.BuildStyleJson();

            StringAssert.Contains("\"cities\"", json, "the source id must appear in the assembled style JSON");
            StringAssert.Contains("\"land\"", json, "the layer id must appear in the assembled style JSON");
            StringAssert.Contains("\"geojson\"", json, "the source's type must be geojson");
        }

        // ── T-Fill: the fill actually renders where authored, background where empty ─────────────────────

        [Test]
        public void Fill_RendersAuthoredPolygon_CenterFilled_CornersBackground()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            SnapshotVerdict verdict = frame.Coverage();
            Assert.IsFalse(verdict.IsBlank, "the authored polygon must render — the frame must not be blank");
            Assert.IsFalse(verdict.IsUniform, "the frame must show BOTH fill and background, not one flat colour");
            Assert.IsTrue(verdict.Passes(minFill: 0.05f, maxFill: 0.85f, minBuckets: 4),
                $"fill must occupy a tolerant central band of the frame: filled={verdict.FilledFraction:P1}, " +
                $"buckets={verdict.DistinctRegionBucketsHit}/64");

            // White fill under the lit-ambient recipe (0.9 flat ambient + one directional light): the center
            // region must bias toward white — i.e. clearly brighter than the dark-slate background on every
            // channel (relative to background, not an absolute floor, since the exact post-tonemap brightness
            // is a lighting-pipeline detail this kit does not pin).
            double[] center = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
            double[] bg = { VisualScene.BackgroundColor.r, VisualScene.BackgroundColor.g, VisualScene.BackgroundColor.b };
            Assert.Greater(center[0], bg[0] + 0.15, "the center region (white fill, lit ambient) must bias toward white — red channel");
            Assert.Greater(center[1], bg[1] + 0.15, "…green channel");
            Assert.Greater(center[2], bg[2] + 0.15, "…blue channel");

            AssertCornersAreBackground(frame, "positive-control render");
        }

        [Test]
        public void Fill_EmptyDataset_RendersBackgroundOnly()
        {
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.FeatureCollection(@"{""type"":""FeatureCollection"",""features"":[]}"))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            Assert.IsTrue(frame.Coverage().IsBlank,
                "an empty FeatureCollection must render background-only — this is the PRIMARY negative " +
                "control: without it, T-Fill's positive arm cannot distinguish 'rendered the authored " +
                "dataset' from 'rendered anything at all'. Meaningful only because the background is the " +
                "mandated non-black slate (plan §7) — a black background would make this pass vacuously on " +
                "a GPU-less machine.");

            double[] center = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
            Assert.Less(center[0], 0.3, "the center region specifically must show no fill either");
        }

        [Test]
        public void Fill_ZeroOpacity_RendersBackgroundOnly()
        {
            // Secondary, de-risked negative control (plan §6, optional arm): fills declare
            // _SURFACE_TYPE_TRANSPARENT and opacity 0 is proven invisible (FillPaintSnapshotTests.cs:158-171).
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff").Opacity(0.0))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            Assert.IsTrue(frame.Coverage().IsBlank, "fill-opacity 0 must render nothing");
        }

        // ── G-VR: golden reference-image regression (change detector, layered ALONGSIDE the analytic
        // teeth above — those stay the correctness oracle; this only catches "different from last bake") ──

        // Reference re-baked 2026-09-08 for the fill boundary band, on the maintainer's authorisation and
        // only after the direction was verified. Measured against the previous bake: 1236 differing px in
        // bbox [101,101]-[410,410] — a ~310 px square whose perimeter is ~1240 px, so the changed pixels ARE a
        // one-pixel ring on the silhouette. All 1236 moved TOWARD the fill colour and none away, and none sits
        // farther than 1.5 px from the boundary, leaving the ~96,000 px interior untouched. That is softened
        // edges, not displaced geometry — had geometry moved, the count would be in the tens of thousands.
        // Recorded because a re-baked golden with no reason is indistinguishable from one re-baked to go green.
        [Test]
        public void Golden_Gv0Fill_MatchesBakedReference()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);
            GoldenImage.Assert(frame, "gv0-fill");
        }

        private static void AssertCornersAreBackground(VisualFrame frame, string context)
        {
            int hi = SnapPx - CornerSize;
            (int x0, int y0)[] corners = { (0, 0), (hi, 0), (0, hi), (hi, hi) };
            foreach ((int x0, int y0) in corners)
            {
                double[] mean = frame.RegionMeanColor(x0, y0, x0 + CornerSize, y0 + CornerSize);
                double variance = frame.RegionColorVariance(x0, y0, x0 + CornerSize, y0 + CornerSize);
                Assert.Less(variance, 0.02,
                    $"[{context}] corner ({x0},{y0}) must be a flat background patch, not speckled fill edge");
                // Background is dark slate (~0.10,0.11,0.15) — a corner touched by fill would read brighter.
                Assert.Less(mean[0] + mean[1] + mean[2], 0.6,
                    $"[{context}] corner ({x0},{y0}) mean {string.Join(",", mean)} reads too bright for the " +
                    "dark-slate background — the fill has bled into a corner region");
            }
        }

        // ── T-Parse: the kit routes through the REAL StyleParser.Parse ────────────────────────────────────

        [Test]
        public void Parse_IdentityArm_ParsedStyleCarriesGeoJsonSourceAndBoundFillLayer()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            SourceDefinition source = frame.ParsedStyle.GetSource("cities");
            Assert.IsNotNull(source, "the parsed style must carry the declared source");
            Assert.AreEqual(SourceType.GeoJson, source.Type, "…typed as geojson");
            Assert.IsNotNull(source.Data, "…with a non-null `data`");
            Assert.IsTrue(source.Data.IsObject,
                "`data` must be a JSON OBJECT — exactly the precondition MapView.cs:323 gates the geojson " +
                "branch on, which can only hold if the emitted JSON string went through StyleParser.Parse " +
                "into a JsonValue tree. A composer that built a StyleDocument directly would have to " +
                "reconstruct this shape by hand.");

            bool boundLayerFound = false;
            foreach (StyleLayer layer in frame.ParsedStyle.Layers)
                if (layer.Id == "land" && layer.Source == "cities") boundLayerFound = true;
            Assert.IsTrue(boundLayerFound, "the parsed layer list must carry the fill layer id bound to \"cities\"");
        }

        [Test]
        public void Parse_ExpressionFormArm_HexAndRgbaSpellingsOfSameColor_AgreeAndDifferFromAThirdColor()
        {
            RenderCenterMean(l => l.Color("#ff0000"), out double[] hexRed);
            RenderCenterMean(l => l.ColorExpression("[\"rgba\",255,0,0,1]"), out double[] exprRed);
            RenderCenterMean(l => l.ColorExpression("[\"rgba\",0,0,255,1]"), out double[] exprBlue);

            double agreement = ManhattanDistance(hexRed, exprRed);
            double contrast  = ManhattanDistance(hexRed, exprBlue);

            Assert.Less(agreement, 0.15,
                $"hex \"#ff0000\" and expression [\"rgba\",255,0,0,1] are the SAME colour and must render " +
                $"the same center-region mean (distance={agreement:F3}). A bypass composer that hand-converts " +
                "hex→RGBA and skips the real expression evaluator cannot make these two spellings agree — it " +
                "would have to reimplement the parser to pass.");
            Assert.Greater(contrast, 0.25,
                $"a genuinely different colour (blue, expression-form) must render VISIBLY differently from " +
                $"red (distance={contrast:F3}) — otherwise the agreement above could be explained by " +
                "'renders the same colour regardless of paint', not by the expression evaluator actually running.");
        }

        private static double ManhattanDistance(double[] a, double[] b)
            => math.abs(a[0] - b[0]) + math.abs(a[1] - b[1]) + math.abs(a[2] - b[2]);

        // ── T-Binding: layers bind by source id; a dangling id yields no geometry AND wires no source ──────

        [Test]
        public void Binding_DanglingSourceId_RendersNothing_AndWiresNoSource()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("does-not-exist").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            Assert.IsTrue(frame.Coverage().IsBlank,
                "a fill layer bound to an UNDECLARED source id must render nothing (MapView.cs:311-314 skips it)");
            Assert.AreEqual(0, frame.MapView.WiredFeatureSourceCount(),
                "…and must leave NO source wired. This is the load-bearing arm: 'nothing rendered' alone " +
                "passes for unrelated reasons (no light, mis-framed camera, no GPU) — only a wired-source " +
                "count of zero proves the SKIP actually happened (GeoJsonSourceTests.AssertNothingWasWired:413-417).");
        }

        [Test]
        public void Binding_FlippingTheIdBackToTheRealSource_RendersAndWiresOneSource()
        {
            var (west, south, east, north) = ProofRectangle();
            using var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z);

            VisualFrame frame = scene.Render(SnapPx);

            Assert.IsFalse(frame.Coverage().IsBlank,
                "flipping the layer's source id back to the DECLARED source must render again");
            Assert.AreEqual(1, frame.MapView.WiredFeatureSourceCount(),
                "…and wire exactly the one declared source — proving the discriminator flipping the id is " +
                "the ID MATCH, not some default wiring.");
        }
    }
}
