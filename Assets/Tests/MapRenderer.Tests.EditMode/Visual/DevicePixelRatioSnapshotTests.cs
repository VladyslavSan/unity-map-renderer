// Unity EditMode only — real MapCamera + Camera/RenderTexture, off-screen GPU render + CPU readback.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// S107 Stage 2 — the RENDERED teeth for the device-pixel-ratio convention. Every quantity below is
// measured at dpr 1 and dpr 2 and asserted as a RATIO, never as "it changed": a "changed" assertion is
// exactly what lets a dpr² error through, and ratios also absorb the constant AA-straddle offset.
//
// FIXTURE SHAPE IS LOAD-BEARING. The swept variable is a real MapCamera's DevicePixelRatio and the camera
// rendered IS that MapCamera's own UnityEngine.Camera (the SymbolLayerOrderSnapshotTests shape). The two
// nearest precedents would both make these teeth vacuous: LineAaSnapshotTests builds its own orthographic
// camera and never mentions dpr, so a line arm taken from it would only restate the material uniform;
// MapViewSnapshotTests renders a separate SnapCam framed from scene bounds, which is dpr-blind by
// construction, so its ground span would not move at all. Only the pure pixel helpers are shared, via
// PixelCoverage.
//
// THE PHYSICAL FRAMEBUFFER MUST NOT MOVE WITH DPR — if it scaled with the ratio everything would cancel
// and these tests would pass on the broken tree. Two objects hold a size here, and both are Size×Size at
// every dpr: (1) the RenderTexture assigned to the MapCamera's camera, which is what MapCamera.ViewportPx
// reads, and (2) SnapshotRenderer's own _rt, which is what _ScreenParams reads during the draw. This
// fixture deliberately does NOT use MapViewTestExtensions.WithTestCamera, so that class's shared static
// RenderTexture is not involved at all.
//
// What SHOULD move at dpr 2 is the camera: the altitude is framed from the LOGICAL viewport height
// (MapCamera.SyncToCamera), so it halves, the visible ground halves, and every world-anchored quantity
// doubles in device px. That is the control the line family is measured against.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolPlan, TestTileKeys
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class DevicePixelRatioSnapshotTests
    {
        private const int    Size       = 512;
        private const double SweptZoom  = 8.0;
        private const double LookAtLat  = 30.0;
        private const double LookAtLon  = 30.0;

        /// <summary>The two ratios swept. 1 is the only one the rest of the suite runs at; 2 is where every
        /// division in the codebase stops being the identity.</summary>
        private const double Dpr1 = 1.0;
        private const double Dpr2 = 2.0;

        /// <summary>Ratio tolerance. Generous enough for the ±0.5 px the AA fixture allows at these
        /// magnitudes and for whole-pixel quantisation of the glyph bbox, far tighter than the 1.0 (no
        /// conversion) and 4.0 (dpr² applied twice) it has to reject.</summary>
        private const double RatioTolerance = 0.12;

        /// <summary>Styled line width in LOGICAL px. Well clear of both thin-line clamps at both ratios —
        /// the min-width floor binds below a 1 px half-width — so the ratio stays linear. A 1 px line here
        /// would produce a confusing false RED.</summary>
        private const float StyledLineWidthPx = 16f;

        /// <summary>Device-px width the world-metre "ground feature" is sized to at dpr 1.</summary>
        private const float GroundFeatureDevicePx = 40f;

        /// <summary>Symbol size in LOGICAL px — big enough that a ±1 px bbox quantisation is under 2 %.</summary>
        private const float TextSizePx = 80f;

        private static readonly Color BgColor     = new Color(0.05f, 0.05f, 0.08f, 1f);
        private static readonly Color GroundColor = new Color(0.20f, 0.45f, 0.98f, 1f);
        private static readonly float4 TextInk   = new float4(0.1f, 0.85f, 0.1f, 1f);

        private const string LineStyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""road"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""paint"": { ""line-color"": [""rgba"", 242, 153, 38, 1], ""line-width"": 16 } }
            ]
        }";

        // text-color: white — a CONSTANT text-color binds _TextColor (style-transitions epic); leaving it
        // at the spec default (black) would multiply MeasureTextHeightPx's hand-injected TextInk vertex
        // colour (bypassing SymbolFeatureExtractor.EvaluatePaint) down to black.
        private const string SymbolStyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""text-field"": ""{NAME}"" },
                  ""paint"": { ""text-color"": ""#ffffff"" } }
            ]
        }";

        // ── The swept scene ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A real <see cref="MapCamera"/> whose <see cref="MapCamera.DevicePixelRatio"/> is the swept
        /// variable, wrapping a Unity camera bound to a FIXED Size×Size RenderTexture. Everything rendered
        /// through <see cref="UnityCamera"/> therefore sees this fixture's ratio.
        /// </summary>
        private sealed class SweptScene : IDisposable
        {
            public GameObject    CamGo;
            public Camera        UnityCamera;
            public RenderTexture ViewportRt;   // framebuffer size holder #1 — Size×Size at EVERY dpr
            public MapCamera     MapCam;
            public SceneFrame    Frame;

            /// <summary>World metres per DEVICE pixel at the ground plane. Halves at dpr 2 because the
            /// altitude does — this is the whole reason a world-anchored feature's device span doubles.</summary>
            public double MetresPerDevicePx;

            public void Dispose()
            {
                if (CamGo != null) UnityEngine.Object.DestroyImmediate(CamGo);
                if (ViewportRt != null)
                {
                    ViewportRt.Release();
                    UnityEngine.Object.DestroyImmediate(ViewportRt);
                }
            }
        }

        private static SweptScene BuildScene(double devicePixelRatio)
        {
            var camGo = new GameObject("Dpr_TestCamera");
            var uCam  = camGo.AddComponent<Camera>();

            // The framebuffer. Fixed size, independent of the ratio — see the header.
            var rt = new RenderTexture(Size, Size, 0);
            uCam.targetTexture   = rt;
            uCam.clearFlags      = CameraClearFlags.SolidColor;
            uCam.backgroundColor = BgColor;
            uCam.enabled         = false;

            var props = new CameraProperties(
                new GeoCoordinate3D { Latitude = LookAtLat, Longitude = LookAtLon, Altitude = 0.0 },
                zoom: SweptZoom, heading: 0.0, tilt: 0.0);
            // The ctor's 5th argument IS the swept variable (pinned by CameraTransformTests' 2× altitude tooth).
            var mapCam = new MapCamera(uCam, props, 1f, null, devicePixelRatio);

            // The experiment's precondition, asserted rather than assumed: the PHYSICAL viewport must be
            // identical at every ratio. If it scaled with dpr, the altitude change and the framebuffer change
            // would cancel and every tooth below would pass on the broken tree.
            Assert.That(mapCam.ViewportPx.x, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport width must stay {Size} at dpr {devicePixelRatio}.");
            Assert.That(mapCam.ViewportPx.y, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport height must stay {Size} at dpr {devicePixelRatio}.");

            // Tilt 0 ⇒ the camera sits straight above the look-at, which camera-relative rendering places at
            // the world origin. Ground half-height = altitude·tan(fov/2), over Size/2 device pixels.
            double altitude = math.length(mapCam.CameraRelativePosition);
            double halfFov  = math.radians(mapCam.CurrentProperties.VerticalFovDeg) * 0.5;
            double metresPerDevicePx = 2.0 * altitude * math.tan(halfFov) / Size;

            // The frame constant the line shader sizes every px-valued width with. It is PROCESS state, and
            // this fixture used to push it nowhere at all — it built its materials through MaterialFactory +
            // ZoomStyleApplier, never through the seam that pushed it — so the styled arm rendered against
            // whatever ruler an earlier fixture in the batch had left behind (measured: MetersPerPixel(5.0)
            // at a zoom-8 camera, a clean factor of 8, and a 16 px road that rendered 128 px). The
            // MapCamera ctor syncs and therefore pushes, so the constant now arrives by construction —
            // asserted here rather than trusted, because the failure mode is silent and reads as a
            // conversion bug three subsystems away.
            Assert.That((double)Shader.GetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel),
                Is.EqualTo(metresPerDevicePx).Within(0.1).Percent,
                $"at dpr {devicePixelRatio} the pushed frame constant must be this scene's own metres per " +
                $"device px ({metresPerDevicePx:F6}). A stale value here scales every styled line width by " +
                "exactly its own ratio and nothing else in the frame moves with it.");

            return new SweptScene
            {
                CamGo             = camGo,
                UnityCamera       = uCam,
                ViewportRt        = rt,
                MapCam            = mapCam,
                MetresPerDevicePx = metresPerDevicePx,
                Frame = new SceneFrame
                {
                    SceneOriginRender = mapCam.Projection.Project(
                        new GeoCoordinate { Latitude = LookAtLat, Longitude = LookAtLon }),
                    Rebase = float3x3.identity,
                },
            };
        }

        // Ambient/light setup so a real lit Map/Line material reads back strongly — copied from
        // SymbolLayerOrderSnapshotTests, which took it from LayerOrderSnapshotTests.
        private static (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) SetupLitAmbient()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevMode  = RenderSettings.ambientMode;
            var prevLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            return (prevQuality, prevMode, prevLight);
        }

        private static void RestoreAmbient((int quality, UnityEngine.Rendering.AmbientMode mode, Color light) saved)
        {
            QualitySettings.SetQualityLevel(saved.quality, false);
            RenderSettings.ambientMode  = saved.mode;
            RenderSettings.ambientLight = saved.light;
        }

        private static GameObject BuildDirectionalLight()
        {
            var go = new GameObject("Dpr_DirLight");
            go.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = go.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
            return go;
        }

        private static void AssertGpuContext(SnapshotRenderer snap)
        {
            if (!snap.IsAllBlack()) return;
            var blankGo = new GameObject("Dpr_BlankCamera");
            try
            {
                var blankCam = blankGo.AddComponent<Camera>();
                blankCam.clearFlags      = CameraClearFlags.SolidColor;
                blankCam.backgroundColor = BgColor;
                using var blank = new SnapshotRenderer(Size, Size);
                blank.Render(blankCam);
                if (blank.IsAllBlack())
                    Assert.Inconclusive("Scene render and blank control are both all-black: no GPU context " +
                                        "in batch EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
            }
            finally { UnityEngine.Object.DestroyImmediate(blankGo); }
        }

        // ── Line arms ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A short horizontal ribbon centred on the look-at. SHORT is deliberate: the shader measures
        /// <c>pxToWorld</c> per vertex, so stations far out in a perspective frustum would extrude to a
        /// different width than the frame centre and the cut would not measure the styled width.
        /// </summary>
        private static Mesh BuildCentredRibbon(SweptScene scene)
        {
            double halfLength = 40.0 * scene.MetresPerDevicePx; // ±40 device px — spans the cut column
            return SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-halfLength, 0.0), new double2(halfLength, 0.0) },
                JoinType.Miter, CapType.Butt);
        }

        private static GameObject AttachMesh(Mesh mesh, Material mat, string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Σ coverage across a vertical cut through the frame centre — the band's apparent width in
        /// DEVICE pixels. Asserts the band actually rendered first (N9): a ratio over a missing feature reads
        /// as a confusing zero rather than as "the feature is not there".</summary>
        private static float MeasureBandWidthPx(SnapshotRenderer snap, string what)
        {
            byte[] pixels     = snap.RawPixels;
            float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);

            const int column  = Size / 2;
            const int rowFrom = Size / 2 - 80;
            const int rowTo   = Size / 2 + 80;

            float3 plateau = PixelCoverage.PlateauOnColumn(pixels, Size, Size, column, rowFrom, rowTo, background);
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"{what}: the band's plateau {plateau} is indistinguishable from the background {background} — " +
                "it did not render, so no width is measurable and no ratio formed from it would mean anything.");

            float[] profile = PixelCoverage.CoverageProfileOnColumn(
                pixels, Size, Size, column, rowFrom, rowTo, background, plateau);
            float measured = PixelCoverage.CoverageIntegral(profile);

            Assert.That(measured, Is.GreaterThan(1f),
                $"{what}: coverage integral is {measured:F3} px. Profile: " +
                PixelCoverage.FormatProfile(profile, rowFrom));
            return measured;
        }

        /// <summary>
        /// The styled <c>line-width</c> arm — bound through the PRODUCTION seam
        /// (<see cref="MaterialFactory.BindLinePaintToApplier"/> + <see cref="ZoomStyleApplier.ApplyZoom"/>),
        /// which is the only place the ratio can enter a line's width.
        /// </summary>
        private static float MeasureStyledLineWidthPx(SweptScene scene, SnapshotRenderer snap)
        {
            StyleDocument style = StyleParser.Parse(LineStyleJson);
            var lineLayer = (Line.StyleLayer)style.Layers[0];

            Material mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/Line base material must be configured for this fixture.");

            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindLinePaintToApplier(lineLayer.Paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(SweptZoom, scene.MapCam.DevicePixelRatio, 0.0));

            Mesh mesh = BuildCentredRibbon(scene);
            GameObject go = AttachMesh(mesh, mat, "Dpr_StyledLine");
            try
            {
                snap.Render(scene.UnityCamera);
                AssertGpuContext(snap);
                snap.WritePng($"dpr-styled-line-{scene.MapCam.DevicePixelRatio:F1}.png");
                return MeasureBandWidthPx(snap, $"styled line-width {StyledLineWidthPx} px at dpr {scene.MapCam.DevicePixelRatio}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        /// <summary>
        /// The GROUND-FEATURE arm — a ribbon whose width is a fixed number of world METRES
        /// (<c>_WidthIsPixels = 0</c>), so no style seam and no ratio touches it. Its on-screen device span
        /// doubles at dpr 2 purely because the camera altitude halves. This is the control the styled arm has
        /// to match.
        /// </summary>
        private static float MeasureGroundSpanPx(SweptScene scene, SnapshotRenderer snap, double groundWidthMetres)
        {
            Material mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            mat.SetColor("_BaseColor",     GroundColor);
            mat.SetFloat("_Opacity",       1f);
            mat.SetFloat("_Width",         (float)groundWidthMetres);
            mat.SetFloat("_WidthIsPixels", 0f);

            Mesh mesh = BuildCentredRibbon(scene);
            GameObject go = AttachMesh(mesh, mat, "Dpr_GroundFeature");
            try
            {
                snap.Render(scene.UnityCamera);
                AssertGpuContext(snap);
                snap.WritePng($"dpr-ground-span-{scene.MapCam.DevicePixelRatio:F1}.png");
                return MeasureBandWidthPx(snap, $"ground feature ({groundWidthMetres:F1} m) at dpr {scene.MapCam.DevicePixelRatio}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        // ── Symbol arm ────────────────────────────────────────────────────────────────────────────

        private static byte[] LoadGlyphFixture(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;
            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        private static (GlyphAtlasTexture texture, List<SymbolQuad> quads, TextLayoutBounds bounds) BuildGlyphA()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0); // 'A'
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);
            return (texture, quads, bounds);
        }

        /// <summary>
        /// The <c>text-size</c> arm. The glyph quad's px offsets are divided by <c>_ScreenParamsLogical</c>
        /// in the shader, which <see cref="SymbolPlacementSystem"/> fills from
        /// <see cref="MapCamera.ViewportLogicalPx"/> — so at dpr 2 the LOGICAL viewport halves and the glyph's
        /// DEVICE footprint doubles, with nothing multiplied at the style seam. This arm is what makes T3 a
        /// statement about pixels rather than about a uniform.
        /// </summary>
        private static float MeasureTextHeightPx(SweptScene scene, SnapshotRenderer snap)
        {
            var (glyphAtlas, quads, bounds) = BuildGlyphA();
            StyleDocument style = StyleParser.Parse(SymbolStyleJson);
            var settings = MapMaterialSetTestUtil.Load();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[0], settings, SweptZoom, drawIndex: 0);
            Assert.IsNotNull(renderLayer.Material, "MapMaterialSet.SymbolTextWorld must be assigned.");
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;

            var system = new SymbolPlacementSystem(scene.MapCam,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, scene.Frame.SceneOriginRender, quads, bounds.Min, bounds.Max,
                paint: new SymbolPaint { TextColor = TextInk, Opacity = 1f },
                textSizePx: TextSizePx, sortKey: 0f, featureIndex: 0,
                // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 would be
                // ~2e7 m away) — the same note every world-symbol fixture carries.
                tileKey: TestTileKeys.PackedContaining(
                    new GeoCoordinate { Latitude = LookAtLat, Longitude = LookAtLon }, zoom: 14),
                materialIndex: 0, allowOverlap: true);
            var layers = new List<SymbolRenderLayer> { renderLayer };

            using var plan = new TestSymbolPlan(scene.MapCam.Projection);
            try
            {
                // Duplicated Tick — the collision verdict is harvested one Tick late (R3).
                system.Tick(in scene.Frame, plan.Build(buffer), glyphAtlas, float.PositiveInfinity, layers);
                system.Tick(in scene.Frame, plan.Build(buffer), glyphAtlas, float.PositiveInfinity, layers);
                Assert.AreEqual(1, system.LastQuadCount,
                    $"the single 'A' must place at dpr {scene.MapCam.DevicePixelRatio} (precondition, not the tooth).");

                snap.Render(scene.UnityCamera);
                AssertGpuContext(snap);
                snap.WritePng($"dpr-label-{scene.MapCam.DevicePixelRatio:F1}.png");
                return MeasureInkHeightPx(snap, scene.MapCam.DevicePixelRatio);
            }
            finally
            {
                renderLayer.Dispose(); // before the system disposes its meshes
                system.Dispose();
                glyphAtlas.Dispose();
            }
        }

        /// <summary>Bounding-box height, in device px, of the rendered glyph's ink — rows carrying at least
        /// half coverage on the background→ink axis.</summary>
        private static float MeasureInkHeightPx(SnapshotRenderer snap, double dpr)
        {
            byte[] pixels     = snap.RawPixels;
            float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);

            // The ink plateau: the pixel furthest from the background anywhere in frame (deep inside the
            // glyph body, never an AA edge).
            float3 plateau  = background;
            float  bestDist = 0f;
            for (int row = 0; row < Size; row++)
            for (int column = 0; column < Size; column++)
            {
                float3 c    = PixelCoverage.SampleLinear(pixels, Size, Size, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; plateau = c; }
            }
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"dpr {dpr}: no ink distinguishable from the background — the label did not render, so its " +
                "height is not measurable.");

            int minRow = int.MaxValue, maxRow = int.MinValue;
            for (int row = 0; row < Size; row++)
            {
                bool inked = false;
                for (int column = 0; column < Size && !inked; column++)
                    inked = PixelCoverage.CoverageAt(pixels, Size, Size, column, row, background, plateau) >= 0.5f;
                if (!inked) continue;
                if (row < minRow) minRow = row;
                if (row > maxRow) maxRow = row;
            }
            Assert.That(maxRow, Is.GreaterThanOrEqualTo(minRow),
                $"dpr {dpr}: no row reached half ink coverage — nothing to measure.");
            return maxRow - minRow + 1;
        }

        // ── T2 ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>T2.</b> Over a dpr 1 → 2 sweep on a FIXED framebuffer, a styled <c>line-width</c> road's
        /// rendered device width and a known ground feature's on-screen device span scale by the SAME factor,
        /// and that factor is exactly 2.
        ///
        /// <para>RED against the un-fixed tree by arithmetic, not by assertion: <c>_Width</c> is handed to
        /// the shader raw and <c>pxToWorld</c> is metres per DEVICE pixel, so <c>widthWorld·(d/mpp) = W</c>
        /// device px at every ratio — the road keeps its literal screen width (ratio 1.00) while the ground
        /// span doubles. That divergence IS the reported symptom.</para>
        /// </summary>
        [Test]
        public void LineWidth_AndGroundSpan_ScaleTogetherAcrossDpr()
        {
            var saved   = SetupLitAmbient();
            var lightGo = BuildDirectionalLight();
            using var snap = new SnapshotRenderer(Size, Size); // framebuffer size holder #2 — Size×Size, fixed
            try
            {
                // The ground feature's world size is fixed ONCE, from the dpr-1 camera, and reused verbatim at
                // dpr 2 — that is what makes it a fixed GROUND quantity rather than a re-derived screen one.
                double groundWidthMetres;
                float lineAt1, groundAt1;
                using (var scene1 = BuildScene(Dpr1))
                {
                    groundWidthMetres = GroundFeatureDevicePx * scene1.MetresPerDevicePx;
                    lineAt1   = MeasureStyledLineWidthPx(scene1, snap);
                    groundAt1 = MeasureGroundSpanPx(scene1, snap, groundWidthMetres);
                }

                float lineAt2, groundAt2;
                using (var scene2 = BuildScene(Dpr2))
                {
                    double metresPerDevicePxAt1 = groundWidthMetres / GroundFeatureDevicePx;
                    Assert.That(scene2.MetresPerDevicePx,
                        Is.EqualTo(0.5 * metresPerDevicePxAt1).Within(0.1).Percent,
                        "precondition: the camera altitude must HALVE at dpr 2 (MapCamera frames from the " +
                        "logical viewport). If it does not, the ground arm cannot move and the sweep is inert.");
                    lineAt2   = MeasureStyledLineWidthPx(scene2, snap);
                    groundAt2 = MeasureGroundSpanPx(scene2, snap, groundWidthMetres);
                }

                double lineRatio   = lineAt2   / lineAt1;
                double groundRatio = groundAt2 / groundAt1;
                TestContext.WriteLine(
                    $"T2: styled line {lineAt1:F2} → {lineAt2:F2} px (ratio {lineRatio:F3}); " +
                    $"ground feature {groundAt1:F2} → {groundAt2:F2} px (ratio {groundRatio:F3})");

                Assert.That(groundRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a fixed GROUND feature must double in device px at dpr 2 (measured {groundAt1:F2} → " +
                    $"{groundAt2:F2} px, ratio {groundRatio:F3}). This arm does not touch the style seam — if " +
                    "it fails, the camera/framebuffer setup is wrong, not the conversion.");

                Assert.That(lineRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a styled line-width road must double in device px at dpr 2 (measured {lineAt1:F2} → " +
                    $"{lineAt2:F2} px, ratio {lineRatio:F3}). A ratio of 1.00 is the defect: _Width reaching " +
                    "the shader as raw logical px while pxToWorld measures the PHYSICAL framebuffer, so the " +
                    $"road keeps its literal screen width while the ground under it scales by {groundRatio:F3}.");

                Assert.That(lineRatio, Is.EqualTo(groundRatio).Within(RatioTolerance),
                    $"line ratio {lineRatio:F3} and ground ratio {groundRatio:F3} must agree — roads and the " +
                    "ground they sit on cannot drift apart with panel density.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lightGo);
                RestoreAmbient(saved);
            }
        }

        // ── T2b — the ruler the styled arm is sized with ─────────────────────────────────────────

        /// <summary>
        /// <b>T2b (S116).</b> At BOTH ratios the pushed <c>_MapFrameMetersPerDevicePixel</c> equals this
        /// scene's own metres per device pixel, and the pair halves exactly.
        ///
        /// <para>Named separately from the render arms because it is the tooth that would have made the S116
        /// investigation one step long. The symptom was a styled 16 px road rendering 128 px at dpr 1 and
        /// 161 px at dpr 2 — a ratio of 1.258 that looks like a broken conversion and sent the search to the
        /// projection subsystem. The cause was neither: this fixture never pushed the global, so the shader
        /// read <c>MetersPerPixel(5.0) = 2445.985</c> left behind by an earlier fixture while the camera stood
        /// at zoom 8 (<c>305.748113</c>). 2445.985 / 305.748113 = 8.000 = 2³, three whole zoom levels — and a
        /// 16 px band × 8 is exactly the 128 px measured. A globe-vs-Mercator mismatch at latitude 30 would
        /// have been 1.1547, and this fixture is Mercator anyway.</para>
        ///
        /// <para>No render, so it cannot go Inconclusive on a headless GPU.</para>
        /// </summary>
        [Test]
        public void FrameConstant_IsTheScenesOwnMetresPerDevicePixel_AtBothRatios()
        {
            int id = ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel;

            double pushedAt1, sceneAt1, pushedAt2, sceneAt2;
            // BuildScene's own precondition asserts the equality; these read the numbers back out so the
            // RATIO clause below has both halves at once, which is what names the 8.000 rather than a
            // per-ratio "it does not match".
            using (var scene1 = BuildScene(Dpr1))
            {
                pushedAt1 = Shader.GetGlobalFloat(id);
                sceneAt1  = scene1.MetresPerDevicePx;
            }
            using (var scene2 = BuildScene(Dpr2))
            {
                pushedAt2 = Shader.GetGlobalFloat(id);
                sceneAt2  = scene2.MetresPerDevicePx;
            }

            TestContext.WriteLine(
                $"T2b: dpr 1 pushed {pushedAt1:F6} vs scene {sceneAt1:F6}; " +
                $"dpr 2 pushed {pushedAt2:F6} vs scene {sceneAt2:F6}; " +
                $"stale/pushed at dpr 1 = {2445.985 / pushedAt1:F3}");

            Assert.That(pushedAt1, Is.EqualTo(sceneAt1).Within(0.1).Percent,
                $"dpr 1: the shader's ruler is {pushedAt1:F6} m/device px while the camera's is " +
                $"{sceneAt1:F6}. A ratio of 8.000 means a stale MetersPerPixel(5.0) from another fixture.");
            Assert.That(pushedAt2, Is.EqualTo(sceneAt2).Within(0.1).Percent,
                $"dpr 2: the shader's ruler is {pushedAt2:F6} m/device px while the camera's is " +
                $"{sceneAt2:F6}.");

            Assert.That(pushedAt2, Is.EqualTo(0.5 * pushedAt1).Within(0.1).Percent,
                $"the ruler must HALVE at dpr 2 ({pushedAt1:F6} → {pushedAt2:F6}): the altitude is framed " +
                "from the logical viewport, so a device pixel covers half the ground. That halving is what " +
                "makes a styled px width double on screen, matching the ground arm's 2.000.");
        }

        // ── T3 (load-bearing) ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>T3 — the reported symptom, pinned.</b> A rendered line and a rendered symbol must scale by the
        /// SAME factor across the dpr sweep, and that factor must be 2.
        ///
        /// <para>Both are asserted against 2.0 AND against each other, deliberately. "Both moved" would pass
        /// on a build that multiplied the LABEL side at the style seam as well — the symbol path already
        /// divides by the logical viewport, so a second multiply gives <c>textRatio == 4</c>, which the
        /// against-2.0 clause is the only thing that catches.</para>
        ///
        /// <para>Two arms over the SAME sweep with the SAME fixed framebuffer, rendered one at a time: the
        /// symbol is anchored at the look-at and the ribbon crosses it, so a combined frame would put the two
        /// measurements on top of each other. Both render through the swept MapCamera's own camera, which is
        /// the part that matters.</para>
        /// </summary>
        [Test]
        public void LineWidth_AndTextSize_ScaleByTheSameFactorAcrossDpr()
        {
            var saved   = SetupLitAmbient();
            var lightGo = BuildDirectionalLight();
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                float lineAt1, textAt1;
                using (var scene1 = BuildScene(Dpr1))
                {
                    lineAt1  = MeasureStyledLineWidthPx(scene1, snap);
                    textAt1 = MeasureTextHeightPx(scene1, snap);
                }

                float lineAt2, textAt2;
                using (var scene2 = BuildScene(Dpr2))
                {
                    lineAt2  = MeasureStyledLineWidthPx(scene2, snap);
                    textAt2 = MeasureTextHeightPx(scene2, snap);
                }

                double lineRatio  = lineAt2  / lineAt1;
                double textRatio = textAt2 / textAt1;
                TestContext.WriteLine(
                    $"T3: line {lineAt1:F2} → {lineAt2:F2} px (ratio {lineRatio:F3}); " +
                    $"label {textAt1:F2} → {textAt2:F2} px (ratio {textRatio:F3})");

                Assert.That(textRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a label's device footprint must double at dpr 2 — measured {textAt1:F2} → " +
                    $"{textAt2:F2} px, ratio {textRatio:F3}. A ratio near 4 means the label side was ALSO " +
                    "multiplied at the style seam on top of the _ScreenParamsLogical division it already has.");

                Assert.That(lineRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a line's device width must double at dpr 2 — measured {lineAt1:F2} → {lineAt2:F2} px, " +
                    $"ratio {lineRatio:F3}.");

                Assert.That(lineRatio, Is.EqualTo(textRatio).Within(RatioTolerance),
                    $"THE SYMPTOM: line ratio {lineRatio:F3} vs label ratio {textRatio:F3}. Raising the " +
                    "device-pixel ratio must not enlarge the labels while leaving the roads at their literal " +
                    "screen width — that is the drift this epic exists to remove.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lightGo);
                RestoreAmbient(saved);
            }
        }
    }
}
