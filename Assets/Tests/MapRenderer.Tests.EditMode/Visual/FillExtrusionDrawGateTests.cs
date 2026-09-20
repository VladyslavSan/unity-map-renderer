#if UNITY_EDITOR
// Unity-only: render tests requiring a GPU context (SnapshotRenderer). Degrade to Inconclusive when the
// context is unavailable in batch mode, per the other snapshot fixtures.
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

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using Unity.Mathematics;
using Color = UnityEngine.Color;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class FillExtrusionDrawGateTests
    {
        private const int    SnapW = 256;
        private const int    SnapH = 256;
        private const double Extent = 4096.0;
        private const double Zoom   = 14.0;
        private const int    SampleHalf = 6;

        private static readonly TileId Tile = new TileId { Z = 14, X = 8192, Y = 8192 };

        // BOTH expected states are non-black, so IsAllBlack stays an unambiguous no-GPU signal rather than
        // colliding with a legitimately dark arm (the defect PaintColorRenderTests' own comment records).
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
                int b = (y * SnapW + x) * 4;
                sum += new float3(snap.RawPixels[b] / 255f, snap.RawPixels[b + 1] / 255f,
                                  snap.RawPixels[b + 2] / 255f);
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
        /// returns the sampled centre pixel, or null when nothing rendered (no GPU context). The authored
        /// opacity is 1 in both arms; the ONLY variable is the backend's per-slot draw gate.
        /// </summary>
        /// <param name="drawn">False to gate the layer's slot out before rendering.</param>
        private static float3? RenderGatedExtrusionLayer(bool drawn, string tag)
        {
            var paint = TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-color\":\"{BuildingHex}\",\"fill-extrusion-height\":40," +
                "\"fill-extrusion-opacity\":1}");

            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(
                new[] { BuildingFootprint() }, paint, Zoom, Extent, Tile);
            Assert.IsNotNull(mesh, "the fixture feature must produce fill-extrusion geometry.");

            // LIT, not Unlit: Lit is what routes the draw to FillExtrusion_LitForwardPass.hlsl, the pass a
            // gated slot must never reach.
            Material mat = MaterialFactory.CreateFillExtrusionMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/FillExtrusion base material must be configured.");
            FillExtrusionTweaker.ApplyElevatedContract(mat);

            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillExtrusionPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Bounds b = mesh.bounds;
            // No Rebuild: the tile container stays at the world origin, where a bare MeshRenderer would have
            // put the mesh, so the camera framing below is the same one every other snapshot fixture uses.
            var backend = new GameObjectTileRenderer(
                new List<Material> { mat },
                new List<string> { "buildings-3d" },
                new List<ShadowCastingMode> { ShadowCastingMode.On });
            backend.AddTileLayer(mesh, double3.zero, 0, Tile);
            backend.SetLayerVisible(0, drawn);

            var camGo  = new GameObject("FillExtrusionDrawGate_Camera");
            var camera = camGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(b.center.x, 500f, b.center.z);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = math.max(b.extents.x, b.extents.z) * 1.2f;
            camera.farClipPlane       = 5000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = Background;
            camera.enabled            = false;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng($"fill-extrusion-draw-gate-{tag}.png");
                if (snap.IsAllBlack()) return null; // neither expected state is black => this is "no GPU"
                return SampleCentre(snap);
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                backend.Dispose();
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(mesh);
            }
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
            int  prevQuality      = QualitySettings.GetQualityLevel();
            var  prevAmbientMode  = RenderSettings.ambientMode;
            var  prevAmbientLight = RenderSettings.ambientLight;
            bool prevFog          = RenderSettings.fog;
            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f, 1f);
            RenderSettings.fog          = false;
            try
            {
                float3? drawn = RenderGatedExtrusionLayer(true, "ungated");
                if (drawn == null)
                {
                    Assert.Inconclusive("No GPU context (the ungated control rendered blank).");
                    return;
                }

                var bg = new float3(Background.r, Background.g, Background.b);
                float controlDelta = math.length(drawn.Value - bg);
                Assert.Greater(controlDelta, 0.05f,
                    "CONTROL: ungated, the sampled centre must differ from the background — the building " +
                    $"has to actually draw there for its absence to mean anything. sampled={drawn.Value} " +
                    $"background={bg}.");

                float3? gated = RenderGatedExtrusionLayer(false, "gated");
                Assert.IsNotNull(gated, "the gated arm rendered blank while the ungated arm did not.");

                float gatedDelta = math.length(gated.Value - bg);
                Assert.Less(gatedDelta, 0.02f,
                    $"a gated-out fill-extrusion slot must leave the BACKGROUND at the centre pixel. It " +
                    $"came back as {gated.Value} against a background of {bg} (the control drew " +
                    $"{drawn.Value}). Both arms author fill-extrusion-opacity 1, so the draw item reached " +
                    "the GPU: ITileRenderBackend.SetLayerVisible did not retire it.");
            }
            finally
            {
                RenderSettings.fog          = prevFog;
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                QualitySettings.SetQualityLevel(prevQuality, false);
            }
        }
    }
}
#endif
