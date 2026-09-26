// Render-layer draw-order and draw-gate GPU/visual acceptance tests, part 1 of 2. Split by the CS0104
// `CameraProperties` collision: a bare CameraProperties user goes in LayerOcclusionTests.cs instead.
//
// Contents:
//   LayerOrderSnapshotTests           — multi-layer painter's-algorithm "clean composite" snapshot test.
//   FillExtrusionDrawGateTests        — Unity-only: render tests requiring a GPU context (SnapshotRenderer).
//   SkyGradientRenderTests            — at tilt 60 the Map/Sky skybox fills the strip from the map edge (horizon-color) to the screen top (sky-color).
//   DistanceHazeRenderTests           — at tilt 60 the far ground is fog-color and a far symbol fades; the screen bottom is un-hazed.
//   RenderModeMaterialSelectionTests  — pins that the fill layer's material is cloned from whichever MapMaterialSet the config references, so an unlit set's Map/FillUnlit base reaches the rendered layer (the twin is wired end-to-end), and a lit set's Map/Fill base does under lit.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Style;
using Color = UnityEngine.Color;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // LayerOrderSnapshotTests — multi-layer painter's-algorithm "clean composite" snapshot test.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Multi-layer painter's-algorithm "clean composite" snapshot test: no z-fighting (low region-colour
    /// variance) over the live material path. <c>BrgBackendSnapshotTests</c> covers draw-order dominance.
    /// A synthetic uniform fill quad keeps the sample region one flat colour; a fixture fill would straddle
    /// polygon edges and inflate variance. Camera: top-down ortho (512×512, Y=200, orthoSize=70).
    /// </summary>
    [TestFixture]
    public class LayerOrderSnapshotTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = AmbientMode.Flat,
            AmbientLight = new Color(0.9f, 0.9f, 0.9f, 1f),
        };

        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        // Saturated layer colours whose dominant channel is unambiguous regardless of lighting intensity.
        private static readonly Color FillBottom = new Color(0.10f, 0.85f, 0.10f, 1f); // GREEN
        private static readonly Color LineMid    = new Color(0.10f, 0.10f, 0.90f, 1f); // BLUE
        private static readonly Color FillTop    = new Color(0.90f, 0.10f, 0.10f, 1f); // RED

        // The layers all overlap a central rectangle on the XZ plane. The line is a WIDE ribbon
        // through the centre so it densely covers the sample region.
        private const float OverlapHalf   = 18f;  // overlap rectangle half-extent (world meters)
        private const float LineHalfWidth = 22f;  // line half-width (m) — wider than the overlap

        // Central sample sub-rect (pixels) — well inside the projected overlap, away from edges.
        private const int SX0 = 216, SY0 = 216, SX1 = 296, SY1 = 296;

        // Variance threshold for a clean composite. Calibrated against the measured clean value
        // (logged) with headroom; a coplanar z-fight speckle between two saturated colours far exceeds this.
        private const double CleanVarianceMax = 0.02;

        [Test]
        public void CoplanarLayers_CompositeCleanly_LowVariance()
        {
            using var bag = new ObjectDisposalBag();
            var (cameraGo, camera) = BuildCamera();
            bag.Track(cameraGo);
            var sceneGo = bag.Track(new GameObject("CoplanarLayerScene"));

            var lightGo = new GameObject("DirLight");
            lightGo.transform.SetParent(sceneGo.transform);
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                // Painter's order via renderQueue: [0]=green fill (bottom), [1]=blue line, [2]=red fill (top).
                // Fill on top of a line is the keystone case.
                int[] queues = LayerDrawOrder.ComputeQueues(3);

                BuildFillQuad(sceneGo, FillBottom, OverlapHalf, queues[0], bag);
                BuildWideLine(sceneGo, LineMid, LineHalfWidth, queues[1], bag);
                BuildFillQuad(sceneGo, FillTop, OverlapHalf, queues[2], bag);

                snap.Render(camera);
                snap.WritePng("layer-order-clean-composite.png");

                double[] mean = SnapshotCoverage.SampleRegionMeanColor(
                    snap.Pixels, SX0, SY0, SX1, SY1);
                double variance = SnapshotCoverage.RegionColorVariance(
                    snap.Pixels, SX0, SY0, SX1, SY1);

                Debug.Log($"[LayerOrderSnapshot] clean composite: " +
                          $"meanRGB=({mean[0]:F3},{mean[1]:F3},{mean[2]:F3}), var={variance:F5}");

                // Sanity: the region must actually have rendered something (not background slate).
                if (mean[0] + mean[1] + mean[2] < 0.05)
                {
                    Assert.Fail(
                        "Overlap region is ~background — layers did not render into the sample rect. " +
                        "Likely no GPU context or a framing problem.");
                }

                // ── Non-vacuous guard: the TOP red fill must win. Mis-wound fill quads render nothing, the
                // region reads blue, and the variance tooth below would pass vacuously. ──
                Assert.That(mean[0], Is.GreaterThan(mean[1]).And.GreaterThan(mean[2]),
                    $"Top layer (red fill) must dominate the composite (meanRGB=" +
                    $"({mean[0]:F3},{mean[1]:F3},{mean[2]:F3})). A blue/green-dominant region means the fill " +
                    "quads did not render (a winding bug — docs/coordinates-and-projections.md § \"Handedness, winding & why the ECEF reflection is load-bearing\") or painter order is wrong.");

                // ── No-z-fighting tooth: a clean composite has low colour variance. Coplanar ZWrite-On
                // layers would speckle and inflate it. ──
                Assert.That(variance, Is.LessThan(CleanVarianceMax),
                    $"Coplanar overlap variance ({variance:F5}) must be < {CleanVarianceMax} (clean composite). " +
                    "Coplanar ZWrite-On layers would speckle (z-fight) and inflate this.");
            }
        }

        // ─── Layer builders ──────────────────────────────────────────────────────────

        /// <summary>
        /// Build a uniform-colour flat fill quad on the XZ plane (±half meters), drawn with a live
        /// Map/Fill material at the given renderQueue. The mesh uses the simple managed Mesh
        /// API (Unity lays attributes out canonically — no non-standard-order warning) with a white
        /// COLOR channel (identity) and a flat +Y normal; the layer colour is the _BaseColor uniform.
        /// </summary>
        private static void BuildFillQuad(GameObject parent, Color color, float half, int renderQueue,
            ObjectDisposalBag bag)
        {
            var mesh = new Mesh { name = "LayerOrderFillQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3( half, 0f, -half),
                new Vector3( half, 0f,  half),
                new Vector3(-half, 0f,  half),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.tangents = new[]
            {
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            // Hand-built, so no StyledFillTileBuilder winding reversal: {0,2,1,0,3,2} is Unity-front from above
            // under stock Cull Back; {0,1,2,0,2,3} would be culled and make the composite vacuous.
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();

            var go = new GameObject("FillQuad");
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Opacity", 1f);
            mat.renderQueue = renderQueue;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;

            bag.Track(mesh);
            bag.Track(mat);
        }

        /// <summary>
        /// Build a wide horizontal line ribbon through the centre via SyntheticLineMesh, drawn with a
        /// live Map/Line material at the given renderQueue.
        /// </summary>
        private static void BuildWideLine(GameObject parent, Color color, float halfWidthM, int renderQueue,
            ObjectDisposalBag bag)
        {
            var mesh = SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-OverlapHalf, 0), new double2(OverlapHalf, 0) },
                JoinType.Miter, CapType.Butt);

            var go = new GameObject("WideLine");
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Width", halfWidthM * 2f);   // full width in meters
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetFloat("_Opacity", 1f);
            mat.renderQueue = renderQueue;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;

            bag.Track(mesh);
            bag.Track(mat);
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LayerOrderCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

    }

    // Unity-only: render tests requiring a GPU context (SnapshotRenderer).
    // NOT included in Tools/core-tests/core-tests.csproj.
    //
    // The RENDERED half of the layer draw gate (LayerFadeGateTests and BackendDrawGateTests cover the C# and
    // per-backend halves). It drives the GameObjects backend, whose gated draw item a camera sees in EditMode.
    //
    // Non-obvious why: the probe is fill-extrusion because FillExtrusionTweaker.ApplyElevatedContract blends
    // One/Zero with DepthWrite.On, so alpha is discarded. A submitted draw is always a solid building, so pixels
    // show whether the draw was submitted at all.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillExtrusionDrawGateTests — Unity-only: render tests requiring a GPU context (SnapshotRenderer).
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillExtrusionDrawGateTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = AmbientMode.Flat,
            AmbientLight = new Color(0.6f, 0.6f, 0.6f, 1f),
            Fog          = false,
        };

        private const int    SnapW = 256;
        private const int    SnapH = 256;
        private const double Extent = 4096.0;
        private const double Zoom   = 14.0;
        private const int    SampleHalf = 6;

        private static readonly TileId Tile = new TileId { Z = 14, X = 8192, Y = 8192 };

        private static readonly Color Background   = new Color(0.10f, 0.35f, 0.65f, 1f);
        private const string          BuildingHex  = "#CC6633";

        /// <summary>Mean sampled colour at the image centre, in the snapshot's own sRGB bytes.</summary>
        private static float3 SampleCentre(SnapshotRenderer snap)
        {
            float3 sum = float3.zero;
            int n = 0;
            for (int y = SnapH / 2 - SampleHalf; y <= SnapH / 2 + SampleHalf; y++)
            for (int x = SnapW / 2 - SampleHalf; x <= SnapW / 2 + SampleHalf; x++)
            {
                Color32 px = snap.Pixels[x, y];
                sum += new float3(px.r / 255f, px.g / 255f, px.b / 255f);
                n++;
            }
            return sum / n;
        }

        private static IFeature BuildingFootprint()
        {
            uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));
            return new DictionaryFeature(geometryType: TileGeometryType.Polygon, geometry: new uint[]
            {
                (1u << 3) | 1u, ZigZag(548),  ZigZag(548),
                (3u << 3) | 2u,
                ZigZag(3000),  ZigZag(0),
                ZigZag(0),     ZigZag(3000),
                ZigZag(-3000), ZigZag(0),
            });
        }

        /// <summary>
        /// Renders one fill-extrusion layer registered as a REAL draw item on the GameObjects backend, and
        /// returns the sampled centre pixel. The authored opacity is 1 in both arms; the ONLY variable is
        /// the backend's per-slot draw gate.
        /// </summary>
        /// <param name="drawn">False to gate the layer's slot out before rendering.</param>
        private static float3 RenderGatedExtrusionLayer(bool drawn, string tag)
        {
            var paint = TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-color\":\"{BuildingHex}\",\"fill-extrusion-height\":40," +
                "\"fill-extrusion-opacity\":1}");

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            using var meshMatBag = new ObjectDisposalBag();
            Mesh mesh = meshMatBag.Track(TestTileMeshBuilder.BuildFillExtrusion(
                new[] { BuildingFootprint() }, paint, Zoom, Extent, Tile));
            Assert.IsNotNull(mesh, "the fixture feature must produce fill-extrusion geometry.");

            // LIT, not Unlit: Lit is what routes the draw to FillExtrusion_LitForwardPass.hlsl, the pass a
            // gated slot must never reach.
            Material mat = meshMatBag.Track(MaterialFactory.CreateFillExtrusionMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/FillExtrusion base material must be configured.");
            FillExtrusionTweaker.ApplyElevatedContract(mat);

            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillExtrusionPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Bounds b = mesh.bounds;
            // No Rebuild: the tile container stays at the world origin, where a bare MeshRenderer would have
            // put the mesh, so the camera framing below is the same one every other snapshot fixture uses.
            using var backend = new GameObjectTileRenderer(
                new List<Material> { mat },
                new List<string> { "buildings-3d" },
                new List<ShadowCastingMode> { ShadowCastingMode.On });
            backend.AddTileLayer(mesh, double3.zero, 0, Tile);
            backend.SetLayerVisible(0, drawn);

            using var camBag = new ObjectDisposalBag();
            var camGo  = camBag.Track(new GameObject("FillExtrusionDrawGate_Camera"));
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(b.center.x, 500f, b.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = math.max(b.extents.x, b.extents.z) * 1.2f;
            camera.farClipPlane       = 5000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = Background;
            camera.enabled            = false;

            snap.Render(camera);
            snap.WritePng($"fill-extrusion-draw-gate-{tag}.png");
            return SampleCentre(snap);
        }

        /// <summary>
        /// A gated-out fill-extrusion slot produces NO pixels: the background survives where the building
        /// would be. Arm 1 (ungated) proves the building draws and is framed; arm 2 changes only the gate.
        /// Both arms author opacity 1, and One/Zero blending discards alpha, so no uniform explains arm 2.
        /// </summary>
        [Test]
        public void GatedFillExtrusion_RendersBackground_NotASolidBuilding()
        {
            float3 drawn = RenderGatedExtrusionLayer(true, "ungated");
            var bg = new float3(Background.r, Background.g, Background.b);
            float controlDelta = math.length(drawn - bg);
            Assert.Greater(controlDelta, 0.05f,
                "CONTROL: ungated, the sampled centre must differ from the background — the building " +
                $"has to actually draw there for its absence to mean anything. sampled={drawn} " +
                $"background={bg}.");

            float3 gated = RenderGatedExtrusionLayer(false, "gated");
            float gatedDelta = math.length(gated - bg);
            Assert.Less(gatedDelta, 0.02f,
                $"a gated-out fill-extrusion slot must leave the BACKGROUND at the centre pixel. It " +
                $"came back as {gated} against a background of {bg} (the control drew " +
                $"{drawn}). Both arms author fill-extrusion-opacity 1, so the draw item reached " +
                "the GPU: ITileRenderBackend.SetLayerVisible did not retire it.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DistanceHazeRenderTests — the distance haze through a real fill layer, rendered and read back.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders one dark fill that covers the view at the demo pose (tilt 60°, 60° vertical FOV) twice: haze off,
    /// then haze on with the spec-default white fog. Near the far cut the hazed ground must be the fog colour by
    /// the amount the fog range predicts; at the screen bottom it must equal the clear render.
    /// </summary>
    [TestFixture]
    public class DistanceHazeRenderTests : BaseTestFixture
    {
        private const int    Size    = 512;
        private const double TiltDeg = 60.0;
        private const double FovDeg  = 60.0;

        private static VisualScene NewScene() => VisualScene.New()
            .Source("land", GeoJson.Polygon(-20.0, -10.0, 80.0, 84.0))
            .Layer(VisualLayer.Fill("land").Source("land").Color("#203040"))
            .Camera(new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 6.0, tilt: TiltDeg);

        [Test]
        public void Tilt60_FarCutIsFogColor_ScreenBottomIsClear()
            => AssertFarCutIsFogColorAndScreenBottomIsClear(RenderMode.Lit, "haze-tilt60");

        // Pins the Unlit twin's per-fragment fog contract (view-space z carried to the fragment stage, not a
        // per-vertex factor) with the same acceptance as the Lit twin.
        [Test]
        public void Tilt60_FarCutIsFogColor_ScreenBottomIsClear_Unlit()
            => AssertFarCutIsFogColorAndScreenBottomIsClear(RenderMode.Unlit, "haze-tilt60-unlit");

        private static void AssertFarCutIsFogColorAndScreenBottomIsClear(RenderMode mode, string snapshotPrefix)
        {
            Frame clear;
            using (VisualScene scene = NewScene().RenderMode(mode))
            {
                clear = scene.Render(Size).Pixels.Clone();
            }
            SnapshotRenderer.WritePngFromRgba32(clear, $"{snapshotPrefix}-off.png");

            Frame hazed;
            DistanceHaze.HazeRange range;
            double edgeDeg, height, near;
            using (VisualScene scene = NewScene().RenderMode(mode).Haze())
            {
                VisualFrame frame = scene.Render(Size);
                hazed = frame.Pixels.Clone();
                MapCamera camera = frame.MapView.Camera;
                near    = frame.Camera.nearClipPlane;
                range   = DistanceHaze.Range(camera.CameraRelativePosition, camera.CurrentFarMetres, near, FovDeg);
                edgeDeg = SkyGradient.MapEdgeElevation(
                    camera.CameraRelativePosition, camera.CurrentFarMetres, camera.Projection).Degrees;
                height  = camera.CameraRelativePosition.y;
            }
            SnapshotRenderer.WritePngFromRgba32(hazed, $"{snapshotPrefix}-on.png");

            double pitchDeg = -(90.0 - TiltDeg);
            double RowElevationDeg(int row)
                => pitchDeg + math.degrees(math.atan(((row + 0.5) / Size * 2.0 - 1.0) * math.tan(math.radians(FovDeg * 0.5))));
            double ExpectedFog(int row) // lit passes: linear fog over view depth from the near plane
            {
                double elevation = math.radians(RowElevationDeg(row));
                double depth = height / math.sin(-elevation) * math.cos(elevation - math.radians(pitchDeg));
                return math.saturate((depth - near - range.Start) / (range.End - range.Start));
            }

            int firstAbove = 0;
            while (firstAbove < Size && RowElevationDeg(firstAbove) <= edgeDeg) firstAbove++;
            Assert.That(firstAbove, Is.InRange(16, Size - 16), $"precondition: the far cut ({edgeDeg:F2}°) is on screen.");

            int column = Size / 2;
            int edgeRow = firstAbove - 3; // clear of the far cut's anti-aliased row
            double expected = ExpectedFog(edgeRow);
            Assert.That(expected, Is.GreaterThan(0.8), "precondition: the row near the far cut is deep in the haze.");
            Assert.That(FogFraction(clear[column, edgeRow], hazed[column, edgeRow]), Is.EqualTo(expected).Within(0.1),
                $"row {edgeRow} near the far cut: clear {clear[column, edgeRow]}, hazed {hazed[column, edgeRow]}.");

            Color32 clearBottom = clear[column, 0], hazedBottom = hazed[column, 0];
            Assert.That(math.abs(clearBottom.r - hazedBottom.r) <= 2 && math.abs(clearBottom.g - hazedBottom.g) <= 2
                        && math.abs(clearBottom.b - hazedBottom.b) <= 2,
                $"the screen bottom must be un-hazed: clear {clearBottom}, hazed {hazedBottom}.");
        }

        [Test]
        public void Tilt60_SymbolFadesByTheHazeAtItsAnchor_NearSymbolIsUnchanged()
        {
            // A white ground under white fog keeps the ground colour fixed, so only the black glyph's alpha moves.
            const double Zoom = 6.0;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            double altitude = CameraPoseMath.AltitudeForZoom(Zoom, Size, FovDeg);
            double height = altitude * 0.5, reach = altitude * 0.8660254037844386; // tilt 60
            double near = CameraPoseMath.NearClip(altitude), far = 4.0 * altitude;  // the far-plane cap binds
            double farDepth = 3.25 * altitude, nearDepth = 0.7 * altitude;
            GeoCoordinate farPoint  = PointAtDepth(lookAt, farDepth, height, reach, altitude);
            GeoCoordinate nearPoint = PointAtDepth(lookAt, nearDepth, height, reach, altitude);

            VisualScene NewSymbolScene() => VisualScene.New()
                .Source("land", GeoJson.Polygon(-20.0, -10.0, 80.0, 84.0))
                .Layer(VisualLayer.Fill("land").Source("land").Color("#ffffff"))
                .Source("points", GeoJson.Points((farPoint.Longitude, farPoint.Latitude, "I"),
                                                 (nearPoint.Longitude, nearPoint.Latitude, "I")))
                .Layer(VisualLayer.SymbolText("labels").Source("points").TextField("name")
                    .TextSize(48.0).TextFont(SymbolFont).TextColor("#000000"))
                .Glyphs(SymbolFont, File.ReadAllBytes(Path.Combine(
                    Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes")))
                .Camera(new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                        zoom: Zoom, tilt: TiltDeg)
                .Configure(config => config.SymbolTileCoverageCull = 0.0) // far tiles are thin on screen
                .ExpectSymbolQuads(2);

            double clearFar, clearNear, hazedFar, hazedNear;
            using (VisualScene scene = NewSymbolScene())
            {
                VisualFrame frame = scene.Render(Size);
                Assert.AreEqual(altitude, math.length(frame.MapView.Camera.CameraRelativePosition), altitude * 1e-6,
                    "precondition: the scene camera sits at the altitude the anchors were placed for.");
                clearFar  = InkAround(frame, lookAt, farPoint);
                clearNear = InkAround(frame, lookAt, nearPoint);
                Assert.That(clearNear, Is.GreaterThan(0.5), "precondition: the near glyph draws dark ink without haze.");
                SnapshotRenderer.WritePngFromRgba32(frame.Pixels, "haze-symbols-tilt60-off.png");
            }
            using (VisualScene scene = NewSymbolScene().Haze())
            {
                VisualFrame frame = scene.Render(Size);
                hazedFar  = InkAround(frame, lookAt, farPoint);
                hazedNear = InkAround(frame, lookAt, nearPoint);
                SnapshotRenderer.WritePngFromRgba32(frame.Pixels, "haze-symbols-tilt60-on.png");
            }

            DistanceHaze.HazeRange range = DistanceHaze.Range(
                new double3(0.0, height, -reach), far, near, FovDeg);
            double expectedVisibility = 1.0 - math.saturate((farDepth - near - range.Start) / (range.End - range.Start));
            Assert.That(clearFar, Is.GreaterThan(0.5), "precondition: the far glyph draws dark ink without haze.");
            Assert.That(expectedVisibility, Is.InRange(0.2, 0.6), "precondition: the far anchor is inside the haze band.");
            Assert.That(hazedFar / clearFar, Is.EqualTo(expectedVisibility).Within(0.1),
                $"far glyph ink: clear {clearFar:F3}, hazed {hazedFar:F3}; expected visibility {expectedVisibility:F3}.");
            Assert.That(hazedNear / clearNear, Is.EqualTo(1.0).Within(0.03),
                $"the glyph near the screen bottom is in clear air: clear {clearNear:F3}, hazed {hazedNear:F3}.");
        }

        private const string SymbolFont = "Fixture Haze Font";

        // The point on the ground north of the look-at whose view depth is depth, for heading 0.
        private static GeoCoordinate PointAtDepth(GeoCoordinate lookAt, double depth, double height, double reach,
                                                  double altitude)
        {
            double north = (depth * altitude - height * height) / reach - reach;
            var projection = new WebMercatorProjection();
            double target = projection.Project(lookAt).z + north, south = -80.0, northLat = 84.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (south + northLat);
                if (projection.Project(new GeoCoordinate { Latitude = mid, Longitude = lookAt.Longitude }).z < target)
                    south = mid;
                else northLat = mid;
            }
            return new GeoCoordinate { Latitude = 0.5 * (south + northLat), Longitude = lookAt.Longitude };
        }

        // Ink of the glyph at a point: 1 − darkest / brightest linear luminance in a window round its anchor.
        // The window stops below the far cut, so the dark clear colour above it never counts as ink.
        private static double InkAround(VisualFrame frame, GeoCoordinate lookAt, GeoCoordinate point)
        {
            var projection = new WebMercatorProjection();
            MapCamera camera = frame.MapView.Camera;
            double2 px = GroundRuler.ProjectPx(frame.Camera, projection.Project(point) - projection.Project(lookAt));
            double edgeRow = (0.5 + 0.5 * math.tan(math.radians(SkyGradient.MapEdgeElevation(
                    camera.CameraRelativePosition, camera.CurrentFarMetres, camera.Projection).Degrees + 90.0 - TiltDeg))
                / math.tan(math.radians(FovDeg * 0.5))) * frame.Height;
            int cx = (int)math.round(px.x), cy = (int)math.round(px.y);
            int yEnd = math.min(math.min(frame.Height, cy + 30), (int)edgeRow - 2);
            Assert.That(yEnd - cy, Is.GreaterThan(8), "precondition: the glyph sits clear of the far cut.");
            double darkest = double.MaxValue, brightest = 0.0;
            for (int y = math.max(0, cy - 30); y < yEnd; y++)
            for (int x = math.max(0, cx - 30); x < math.min(frame.Width, cx + 30); x++)
            {
                Color linear = ((Color)frame.Pixels[x, y]).linear;
                double luminance = 0.2126 * linear.r + 0.7152 * linear.g + 0.0722 * linear.b;
                darkest = math.min(darkest, luminance);
                brightest = math.max(brightest, luminance);
            }
            return 1.0 - darkest / brightest;
        }

        // How far the hazed pixel moved from the clear one toward white, in linear colour, over the three channels.
        private static double FogFraction(Color32 clear, Color32 hazed)
        {
            Color clearLinear = ((Color)clear).linear, hazedLinear = ((Color)hazed).linear;
            double sum = 0.0;
            for (int channel = 0; channel < 3; channel++)
                sum += (hazedLinear[channel] - clearLinear[channel]) / (1.0 - clearLinear[channel]);
            return sum / 3.0;
        }
    }

    // Unity EditMode only. The fill material carries the configured MapMaterialSet's shader (Map/Fill or
    // Map/FillUnlit). Awaiting the real SetStyle is safe: a tiles[]-only source makes no TileJSON fetch.

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderModeMaterialSelectionTests — the fill layer's material comes from the configured set
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins that the fill layer's material is cloned from whichever
    /// <see cref="MapMaterialSet"/> the config references, so an unlit set's <c>Map/FillUnlit</c> base
    /// reaches the rendered layer (the twin is wired end-to-end), and a lit set's <c>Map/Fill</c> base does
    /// under lit. Render mode itself lives on the set (<see cref="MapMaterialSet.RenderMode"/>) and drives
    /// the lighting bootstrap — see <c>DirectionalLightBootstrapTests</c>/<c>EnvironmentLightingTests</c>.
    /// </summary>
    [TestFixture]
    public class RenderModeMaterialSelectionTests
    {
        private static StyleDocument OneFillLayerStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""OneFill"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""fill-a"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 255, 0, 0, 1] }
                }
            ]
        }");

        [Test]
        public void FillLayer_FromLitMaterialSet_UsesLitShader()
            => AssertFillLayerShader(RenderMode.Lit, "Map/Fill");

        [Test]
        public void FillLayer_FromUnlitMaterialSet_UsesUnlitShader()
            => AssertFillLayerShader(RenderMode.Unlit, "Map/FillUnlit");

        private static void AssertFillLayerShader(RenderMode mode, string expectedShaderName)
        {
            var litSet = MapMaterialSetTestUtil.Load();

            // Unlit: a throwaway set with the real Map/FillUnlit twin; Line/Symbol reuse the lit bases because
            // Validate() needs all three. Lit: the shared production set already carries Map/Fill.
            using var bag = new ObjectDisposalBag();
            var isUnlit  = mode == RenderMode.Unlit;
            var unlitSet = bag.Track(isUnlit ? ScriptableObject.CreateInstance<MapMaterialSet>() : null);
            if (isUnlit)
            {
                unlitSet.RenderMode      = RenderMode.Unlit;
                unlitSet.FillMaterial    = bag.Track(new Material(Shader.Find("Map/FillUnlit")));
                unlitSet.LineMaterial    = litSet.LineMaterial;
                unlitSet.SymbolTextWorld = litSet.SymbolTextWorld;
            }
            MapMaterialSet set = isUnlit ? unlitSet : litSet;

            byte[] fixtureBytes = SampleTileFixture.Bytes();
            var go   = bag.Track(new GameObject("MapView"));
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = set;
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            // Never actually dials the network — see file header.
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(fixtureBytes);

            try
            {
                Await(view.View.SetStyle(OneFillLayerStyle(), "test-style", CancellationToken.None));

                Assert.AreEqual(1, view.FillLayerCount(), "The style declares exactly one fill layer.");
                Material mat = view.Layers[0].Material;
                Assert.IsNotNull(mat, "The fill layer must have resolved a material.");
                Assert.AreEqual(expectedShaderName, mat.shader.name,
                    $"A {mode} MapMaterialSet must resolve the fill layer's material onto shader " +
                    $"'{expectedShaderName}', not '{mat.shader.name}' — the fill layer is a clone of the " +
                    "referenced set's FillMaterial base, so the unlit twin must reach the rendered layer.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/> (mirrors
        /// <c>GeoJsonSourceTests.SpinToCompleted</c>) — parks the calling thread rather than yielding the
        /// continuation to whatever thread completes the task, so every line after this still runs on the
        /// main thread (DestroyImmediate requires it).</summary>
        private static void Await(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SkyGradientRenderTests — the Map/Sky skybox, rendered and read back.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders only the sky through a demo-like committed camera (tilt 60°, 60° vertical FOV, square target)
    /// and reads the centre column bottom-up. The blend fills the visible sky strip: horizon-color up to the
    /// map edge, near horizon-color just above it, and sky-color at the top of the screen.
    /// </summary>
    [TestFixture]
    public class SkyGradientRenderTests : BaseTestFixture
    {
        private const int    Size    = 65;  // odd: the centre column's rays have no horizontal component
        private const double TiltDeg = 60.0;
        private const double FovDeg  = 60.0;

        [Test]
        public void RenderedSky_AtTilt60_FillsTheStripFromMapEdgeToScreenTop()
        {
            var camera = Track(new GameObject("SkyRenderCamera")).AddComponent<Camera>();
            camera.aspect = 1f;
            var mapCamera = new MapCamera(camera, new MapRenderer.Core.Geo.CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 }, 15.0, 0.0, TiltDeg, FovDeg));
            mapCamera.SyncToCamera();

            using var sky  = new SkyGradient(camera);
            using var snap = new SnapshotRenderer(Size, Size);
            sky.ApplyStyle(StyleSky.Parse(null), zoom: 15.0);
            sky.SetOverride(Color.red, Color.blue);
            sky.UpdateMapEdge(mapCamera);
            snap.Render(camera); // absorbs shader warm-up
            snap.Render(camera);
            Frame frame = snap.Pixels;
            SnapshotRenderer.WritePngFromRgba32(frame, "sky-gradient-tilt60.png");

            double edgeDeg = SkyGradient.MapEdgeElevation(
                mapCamera.CameraRelativePosition, mapCamera.CurrentFarMetres, mapCamera.Projection).Degrees;
            double RowElevationDeg(int row) // centre column: view pitch plus the row's angle off the axis
                => -(90.0 - TiltDeg) + math.degrees(math.atan(((row + 0.5) / Size * 2.0 - 1.0)
                                                              * math.tan(math.radians(FovDeg * 0.5))));
            int firstAbove = 0;
            while (firstAbove < Size && RowElevationDeg(firstAbove) <= edgeDeg) firstAbove++;
            Assert.That(firstAbove, Is.InRange(1, Size - 4),
                $"precondition: the map edge ({edgeDeg:F2}°) must be on screen with a strip of sky above it.");

            int centre = Size / 2;
            for (int row = 0; row < firstAbove; row++)
            {
                Color32 below = frame[centre, row];
                Assert.That(below.r <= 1 && below.b >= 254,
                    $"row {row} is at or below the map edge ({edgeDeg:F2}°) and must be horizon-color; got {below}.");
            }

            Color32 justAbove = frame[centre, firstAbove];
            Assert.That(justAbove.r < justAbove.b,
                $"row {firstAbove}, just above the map edge, must be nearer horizon-color than sky-color; got {justAbove}.");

            Color32 top = frame[centre, Size - 1];
            Assert.That(top.r >= 252 && top.b <= 3,
                $"the top row must be sky-color: the blend fills the visible strip; got {top}.");
        }
    }
}
