// Render-layer draw-order and draw-gate GPU/visual acceptance tests, part 1 of 2.
//
// Split by a using collision, not the line cap: `MapRenderer.Core.Geo.CameraProperties` vs
// `UnityEngine.Rendering.CameraProperties` (CS0104). Every file with a bare CameraProperties
// reference is in RenderLayerTests2.cs instead.
//
// Contents:
//   LayerOrderSnapshotTests           — multi-layer painter's-algorithm "clean composite" snapshot test.
//   FillExtrusionDrawGateTests        — Unity-only: render tests requiring a GPU context (SnapshotRenderer).
//   RenderModeMaterialSelectionTests  — pins that the fill layer's material is cloned from whichever MapMaterialSet the config references, so an unlit set's Map/FillUnlit base reaches the rendered layer (the twin is wired end-to-end), and a lit set's Map/Fill base does under lit.

using System.Collections.Generic;
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
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // LayerOrderSnapshotTests — multi-layer painter's-algorithm "clean composite" snapshot test.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Multi-layer painter's-algorithm "clean composite" snapshot test.
    ///
    /// Draw-order dominance and the reorder flip are covered live by
    /// <c>BrgBackendSnapshotTests</c>. What lives here is the no-z-fighting clean composite (low
    /// region-color variance), over the live material path (MaterialFactory + LayerDrawOrder +
    /// SyntheticLineMesh), with a synthetic uniform fill quad so the sample region is a single flat colour
    /// (a fixture fill would straddle polygon edges and inflate variance for reasons unrelated to
    /// z-fighting).
    ///
    /// Camera: top-down ortho (512×512, Y=200, orthoSize=70), dark-slate background.
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
                // Declared draw order via renderQueue (painter's algorithm, single transparent band):
                //   [0]=green fill (bottom), [1]=blue line (mid), [2]=red fill (top).
                // FILL-ON-TOP-OF-LINE is present (index 2 fill over index 1 line) — the keystone case.
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

                // ── Non-vacuous guard: the TOP layer (red fill, queue 3004 under the sub-slot band stride —
                // see road-shields-design.md) must win the composite. With fill quads wound the wrong way
                // they render NOTHING (front-facing under _Cull:1), only the blue line draws, this region
                // reads BLUE and the variance tooth below passes vacuously. Requiring red-dominance proves
                // all three layers render AND that painter order puts the top fill on top. ──
                Assert.That(mean[0], Is.GreaterThan(mean[1]).And.GreaterThan(mean[2]),
                    $"Top layer (red fill) must dominate the composite (meanRGB=" +
                    $"({mean[0]:F3},{mean[1]:F3},{mean[2]:F3})). A blue/green-dominant region means the fill " +
                    "quads did not render (a winding bug — docs/coordinates-and-projections.md § \"Handedness, winding & why the ECEF reflection is load-bearing\") or painter order is wrong.");

                // ── No-z-fighting tooth: a clean composite has low colour variance. ──
                // A coplanar ZWrite-On approach would speckle between the saturated layer colours
                // and inflate this far past the threshold.
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
            // This quad is hand-built (it does NOT flow through StyledFillTileBuilder's boundary winding reversal),
            // so it must be wound to be Unity-front under the shipped MapFill.mat _Cull:2 (stock Cull Back).
            // Viewed from above (+Y normal), the Unity-front-facing order is {0,2,1,0,3,2}; {0,1,2,0,2,3} would
            // render INVISIBLE (back-facing → culled), re-creating the vacuous-composite failure. (Pre-flip this
            // was inverted: Cull Front + {0,1,2,0,2,3}. Same render, mirrored convention.)
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
    // The RENDERED half of the layer draw gate. LayerFadeGateTests observes the C# half — the pushed
    // opacity and the PaintsSomething predicate — and BackendDrawGateTests observes each backend's own
    // mechanism against its own state. This one closes the chain at the only place that cannot be argued with:
    // pixels. It drives the real GameObjects backend, the one backend whose gated draw item is visible to
    // a camera in EditMode, and asserts the building is simply not there.
    //
    // Why fill-extrusion specifically: FillExtrusionTweaker.ApplyElevatedContract blends One/Zero with
    // DepthWrite.On, so the destination factor is zero and ALPHA IS DISCARDED. Nothing about an opacity value
    // can hide a submitted fill-extrusion draw — if the draw reaches the GPU the building is there, fully solid,
    // writing depth. That makes it the sharpest possible probe for "was the draw submitted at all".

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
        /// A gated-out fill-extrusion slot produces NO pixels — the background survives where the building
        /// would otherwise be.
        ///
        /// <para>Two renders, one scene, one variable. Arm 1 (ungated) proves the building draws there and
        /// that the camera frames it; arm 2 changes only the gate. Both arms author opacity 1, so nothing
        /// about a uniform can explain arm 2: One/Zero blending discards alpha, and a submitted draw comes
        /// back as a fully solid, depth-writing building.</para>
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

    // Unity EditMode only — uses MonoBehaviour + per-layer material inspection (mirrors MapViewStyledFillTests).
    //
    // Render mode is a property of the MapMaterialSet the config references: a Lit set puts the view in lit,
    // an Unlit set in unlit, with no separate config flag and no per-material mix. This end-to-end tooth
    // proves the unlit shader twin flows
    // through the material pipeline: the material MapView resolves for a fill layer carries the shader of the
    // set's FillMaterial base — Map/Fill from a lit set, Map/FillUnlit from an unlit set.
    //
    // Drives the REAL production entry point, MapView.SetStyle (not the LoadTestStyle test helper). A
    // tiles[]-only vector source needs no TileJSON fetch, so SetStyle never actually awaits network — the
    // TileSourceFactoryOverride seam supplies the fixture bytes when SetSources lazily constructs the source.

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

            // Under Unlit, build a throwaway set (never the shared production asset — same posture as
            // MapMaterialSetValidationTests.NewSet) whose RenderMode is Unlit and whose FillMaterial is the
            // real Map/FillUnlit twin. Line/Symbol bases reuse the lit ones — Validate() (called inside
            // SetStyle) requires all three regardless of mode, and symbols are already unlit.
            // Under Lit, the shared production set already carries Map/Fill and
            // its default RenderMode.Lit.
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
}
