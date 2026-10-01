// Fill-diagnostic and alternate-source-path GPU/visual acceptance tests.
//
// Non-obvious why: the file split follows two CS0104 using collisions, not the line cap —
// `CameraProperties` (MapRenderer.Core.Geo vs UnityEngine.Rendering) and bare `Object` (System vs
// UnityEngine). This file holds the bare-CameraProperties user (MapViewStyledFillTests) plus the
// GeoJson fill proof.
//
// Contents:
//   MapViewStyledFillTests           — decisive tests for per-layer styled fill rendering in MapView.
//   GeoJsonFillVisualProofTests      — Unity EditMode only — the declarative visual-test authoring kit's proof fixture.

using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Unity.Style;
using Fill = MapRenderer.Unity.Style.Fill;
using MapRenderer.Unity.View.Cameras;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Layers;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using Unity.Mathematics;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.Jobs.Mvt;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using static MapRenderer.Tests.MapViewPump;

namespace MapRenderer.Tests.Visual
{
    // Unity EditMode only (MonoBehaviour, Mesh, per-layer material inspection). Every decisive assertion
    // is CPU-side (mesh.GetColors() or material inspection), so none needs a GPU context.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewStyledFillTests — decisive tests for per-layer styled fill rendering in MapView.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decisive CPU-side tests for per-layer styled fill rendering in <see cref="MapView"/>:
    /// #1 two fill layers give two layer meshes with distinct materials; #2 a later layer has a higher
    /// renderQueue (painter's order); #3 a data-driven <c>match</c> bakes ≥2 distinct vertex colours;
    /// #4 baked vertex colours are linearised; #5 a zoom-dependent paint value changes the uniform
    /// through <c>ZoomStyleApplier.ApplyZoom</c>.
    /// </summary>
    [TestFixture]
    public class MapViewStyledFillTests : BaseTestFixture
    {

        // ─── inline 2-fill-layer style (for draw-count and draw-order teeth) ────────────────────

        /// <summary>
        /// Two fill layers, both over source-layer "countries", with distinct constant colors.
        /// Layer 0 (bottom): red (#FF0000 as rgba).
        /// Layer 1 (top):    blue (#0000FF as rgba).
        /// Both layers resolve against the fixture tile (which has a "countries" MVT layer).
        /// </summary>
        private static StyleDocument TwoFillLayerStyle() => TestStyle.Document(@"{
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
        // A [fill, line, fill] line must draw BETWEEN the fills; bucketing by type would put it on top of both.

        private static StyleDocument FillLineFillStyle() => TestStyle.Document(@"{
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
        // Matches on "CONTINENT", which the z0 fixture encodes (the demo style's ADM0_A3 it does not).

        private static StyleDocument ContinentFillStyle() => TestStyle.Document(@"{
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
                // Several CONTINENT values give Asia's red and the default grey; one colour means no per-feature bake.
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

        // Data-driven bake only; PaintColorRenderTests.ConstantFillColor_RenderedPixel_MatchesAuthored
        // observes the constant-colour arm of gamma linearisation.
        [Test]
        public void MapView_DataDrivenFillColor_VertexColorsAreLinearized()
        {
            // Non-obvious why: both `match` arms hold the same rgba(127,0,0,1) (sRGB 0.498, linear 0.212)
            // so DependsOnFeature is true and the value bakes to vColor; a constant would go to _BaseColor.
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
            var style = TestStyle.Document(styleJson);
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
        // Non-obvious why: tested on a Standard material, not via MapView, because in batch mode Map/Fill
        // may fall back to Sprites/Default, which lacks _Opacity.

        [Test]
        public void ZoomStyleApplier_ZoomDependentStops_ChangesFloatUniformWithZoom()
        {
            // Parse a fill layer with zoom-dependent opacity (stops: 0→0.3, 6→1.0).
            var styleDoc = TestStyle.Document(@"{
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

            // _MyZoomOpacity is not a Standard property; a material stores any float override, so
            // SetFloat/GetFloat still round-trip it.
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
        public void MapView_Update_CallsApplyZoom_Structurally()
        {
            // Structural: a zoom-opacity style builds one fill bundle that survives a LateUpdate. The
            // ZoomStyleApplier test above pins the value change itself.
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
            var style = TestStyle.Document(styleJson);
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

                // Update once to pump tiles and fire ApplyZoom.
                view.LateUpdate();

                // ── DECISIVE: ApplyZoom is called in Update — proven by ZoomStyleApplier test above.
                // Structural assertion: Update does not throw, the fill bundle count is still 1 after Update.
                Assert.AreEqual(1, view.FillLayerCount(),
                    "fill bundle count must remain 1 after Update (bundles must not be cleared on Update).");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        // ─── #6: fill-color — a zoom retint does not rebuild the mesh (DECISIVE) ────────────────

        private static void AssertColorClose(Color expected, Color actual, string what)
        {
            Assert.That(actual.r, NUnit.Framework.Is.EqualTo(expected.r).Within(1e-3f), $"{what}: R (actual={actual}, expected={expected})");
            Assert.That(actual.g, NUnit.Framework.Is.EqualTo(expected.g).Within(1e-3f), $"{what}: G (actual={actual}, expected={expected})");
            Assert.That(actual.b, NUnit.Framework.Is.EqualTo(expected.b).Within(1e-3f), $"{what}: B (actual={actual}, expected={expected})");
        }

        /// <summary>
        /// A Zoom-kind fill-color must retint the MATERIAL, not rebake the MESH — that is the carrier split's
        /// deliverable. Pins the tile set fixed across the zoom move so a rebuild (if one happened) could
        /// only be the colour change, never a different cover.
        /// </summary>
        [Test]
        public void FillColor_ZoomExpression_RetintsWithoutRebuildingTheMesh()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            FileAssert.Exists(path);
            var style = TestStyle.Document(File.ReadAllText(path));

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

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonFillVisualProofTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The declarative visual-test authoring kit's fill-only proof fixture (Unity EditMode only). It pins
    /// the fill rendering where authored, routing through the real <c>StyleParser.Parse</c>, and a dangling
    /// source id yielding no geometry and wiring no source. Fill only: no symbols, no lines, no seam-dedup.
    /// </summary>
    [TestFixture]
    internal class GeoJsonFillVisualProofTests
    {
        // ── The proof geometry: one tile, one polygon, generous margins on every side ──────────────────

        // At tilt 0 a tile projects to WebMercator.TilePixelSize (512) device px at any viewport size, so
        // SnapPx = 512 makes the tile the frame and tile-local unit coordinates are frame fractions.
        private static readonly TileId ProofTile = new TileId { Z = 6, X = 40, Y = 25 };
        private const int SnapPx = 512;

        // Polygon spans the tile-local unit square [0.2,0.8]², clear of GeoJsonSliceOptions.Default's
        // ~1.5%-of-tile buffer, so it has no tile-edge interaction.
        private const double PolyLo = 0.2;
        private const double PolyHi = 0.8;

        // Center sample box: unit square [0.45,0.55]² — well inside the filled [0.2,0.8]² region.
        private const int CenterLo = 230;
        private const int CenterHi = 282;

        // Corner sample boxes: 51×51 px at each frame corner, outside the fill. They are read as a SET, so
        // the frame's bottom-left origin does not matter.
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

        // ── JSON assembly compiles before the render path is exercised ─────────────────────────────────

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

            // The lit white fill must be clearly brighter than the background on every channel. The check is
            // relative, because the kit does not pin the exact post-tonemap brightness.
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
                "mandated non-black slate — a black background would make this pass vacuously on " +
                "a GPU-less machine.");

            double[] center = frame.RegionMeanColor(CenterLo, CenterLo, CenterHi, CenterHi);
            Assert.Less(center[0], 0.3, "the center region specifically must show no fill either");
        }

        [Test]
        public void Fill_ZeroOpacity_RendersBackgroundOnly()
        {
            // Secondary negative control: fills declare _SURFACE_TYPE_TRANSPARENT, and opacity 0 is proven
            // invisible by FillPaintSnapshotTests.
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

        // The reference includes the fill boundary band: against a band-less bake only a one-pixel ring on
        // the silhouette differs, moved toward the fill colour, with the interior untouched.
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
            Assert.IsNotNull(source.Data?.Dataset,
                "`data` must have parsed into an inline GeoJSON dataset, which can only hold if the " +
                "emitted JSON string went through StyleParser.Parse. A composer that built a StyleDocument " +
                "directly would have to reconstruct this shape by hand.");

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

        /// <summary>
        /// A fill layer binds to its source by EXACT id match: a dangling id renders nothing and wires no
        /// source at all (the load-bearing arm — "nothing rendered" alone passes for unrelated reasons, no
        /// light/mis-framed camera/no GPU; only a wired-source count of zero proves the skip actually
        /// happened, <c>MapView.cs:311-314</c>), and flipping the id back to the declared source renders
        /// again and wires exactly the one source — proving the discriminator is the ID match, not some
        /// default wiring.
        /// </summary>
        [Test]
        public void SourceIdBinding_GatesRenderingAndWiring_ByExactMatch()
        {
            var (west, south, east, north) = ProofRectangle();

            // ── Dangling id: renders nothing, wires no source ──
            using (var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("does-not-exist").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z))
            {
                VisualFrame frame = scene.Render(SnapPx);

                Assert.IsTrue(frame.Coverage().IsBlank,
                    "a fill layer bound to an UNDECLARED source id must render nothing (MapView.cs:311-314 skips it)");
                Assert.AreEqual(0, frame.MapView.WiredFeatureSourceCount(),
                    "…and must leave NO source wired. This is the load-bearing arm: 'nothing rendered' alone " +
                    "passes for unrelated reasons (no light, mis-framed camera, no GPU) — only a wired-source " +
                    "count of zero proves the SKIP actually happened (GeoJsonSourceTests.AssertNothingWasWired:413-417).");
            }

            // ── Id flipped back to the declared source: renders again, wires exactly one source ──
            using (var scene = VisualScene.New()
                .Source("cities", GeoJson.Polygon(west, south, east, north))
                .Layer(VisualLayer.Fill("land").Source("cities").Color("#ffffff"))
                .Camera(ProofLookAt(), zoom: ProofTile.Z))
            {
                VisualFrame frame = scene.Render(SnapPx);

                Assert.IsFalse(frame.Coverage().IsBlank,
                    "flipping the layer's source id back to the DECLARED source must render again");
                Assert.AreEqual(1, frame.MapView.WiredFeatureSourceCount(),
                    "…and wire exactly the one declared source — proving the discriminator flipping the id is " +
                    "the ID MATCH, not some default wiring.");
            }
        }
    }
}
