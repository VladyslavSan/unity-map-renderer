// S53a — ECS render spike (the de-risking GATE for the S53 ECS epic).
//
// Proves the single fact the whole epic hinges on: an Entities-Graphics entity drawing the live
// Map/Fill material renders NON-EMPTY pixels in the existing headless EditMode SnapshotRenderer
// (camera.Render()), the same path the GameObject and BRG backends use.
//
// Why this is non-trivial: Entities Graphics submits draws from EntitiesGraphicsSystem, which ticks
// in the player-loop presentation group — and that loop does NOT run in headless EditMode. So an EG
// entity renders BLANK under a bare camera.Render() until its systems are ticked manually. This spike
// finds the minimal manual driving sequence (or proves it needs PlayMode → honest-stop).
//
// Design: render the SAME mesh+material+transform two ways and compare —
//   (1) a GameObject MeshRenderer  → the CONTROL: proves GPU + Map/Fill work headless at all.
//   (2) an Entities-Graphics entity → THE GATE.
// If BOTH are blank → no GPU context in this batch session → Inconclusive (matches the suite's
// existing IsAllBlack/Inconclusive convention). If the control renders but the entity is blank →
// EG did not submit under camera.Render() in EditMode → the spike has found its honest-stop result.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Entities;
using Unity.Transforms;
using Unity.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Imaging;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class EntitiesGraphicsSpikeTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        // Background: same dark slate as the fill snapshot tests (bytes 26,28,38).
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;

        // The entity must cover at least this fraction of the frame (well above noise).
        private const float CoverageFloor = 0.01f;

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("EgSpikeCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // top-down
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        private static GameObject AddDirectionalLight(float intensity, Quaternion rotation)
        {
            var lightGo = new GameObject("EgSpikeDirLight");
            lightGo.transform.rotation = rotation;
            var light = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = intensity;
            return lightGo;
        }

        private static float FilledFraction(SnapshotRenderer snap)
            => SnapshotCoverage.Analyse(snap.RawPixels, snap.Width, snap.Height, BgR8, BgG8, BgB8)
                               .FilledFraction;

        /// <summary>
        /// S53a GATE: an Entities-Graphics entity drawing Map/Fill renders non-empty pixels in the
        /// headless SnapshotRenderer, driven by a manual system-group tick before camera.Render().
        /// </summary>
        [Test]
        public void Spike_EntitiesGraphics_RendersMapFill_InHeadlessHarness()
        {
            // Same mesh+material+transform the GO/BRG backends use (fixture countries fill, white).
            var (mapGo, mat) = FillSceneHelper.BuildFillGo();
            Assert.IsNotNull(mat, "Fixture fill material must build.");
            var mesh = mapGo.GetComponent<MeshFilter>().sharedMesh;
            Assert.IsNotNull(mesh, "Fixture fill mesh must build.");

            var m   = mapGo.transform.localToWorldMatrix;
            var ltw = new float4x4(m.GetColumn(0), m.GetColumn(1), m.GetColumn(2), m.GetColumn(3));

            int   prevQuality = QualitySettings.GetQualityLevel();
            Color prevAmbient = RenderSettings.ambientLight;
            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientLight = new Color(0.3f, 0.3f, 0.3f, 1f); // ambient up so the fill is visible

            World prevDefault = World.DefaultGameObjectInjectionWorld;
            World world       = null;
            GameObject camGo = null, lightGo = null;
            var snapControl = new SnapshotRenderer(SnapW, SnapH);
            var snapEg      = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                var (cg, cam) = BuildCamera();
                camGo   = cg;
                lightGo = AddDirectionalLight(1.5f, Quaternion.Euler(50f, 0f, 0f));

                // ── (1) CONTROL: the GameObject MeshRenderer ──
                snapControl.Render(cam);

                // ── (2) EG ENTITY: deactivate the GO so only the entity draws ──
                mapGo.SetActive(false);

                world = DefaultWorldInitialization.Initialize("MapSpikeWorld", editorWorld: false);
                World.DefaultGameObjectInjectionWorld = world;
                var em = world.EntityManager;

                var desc = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
                var rma  = new RenderMeshArray(new Material[] { mat }, new Mesh[] { mesh });
                var e    = em.CreateEntity();
                RenderMeshUtility.AddComponents(
                    e, em, desc, rma, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                em.SetComponentData(e, new LocalToWorld { Value = ltw });

                // Generous bounds so frustum culling never drops the entity in the spike.
                if (em.HasComponent<RenderBounds>(e))
                    em.SetComponentData(e, new RenderBounds
                    {
                        Value = new AABB { Center = float3.zero, Extents = new float3(1e6f) }
                    });

                // Minimal driving sequence: tick the three top-level groups by name. The
                // PresentationSystemGroup contains EntitiesGraphicsSystem (uploads instance data +
                // registers BRG batches); camera.Render() then triggers SRP culling → EG submits.
                // Two passes: register/upload, then steady.
                for (int pass = 0; pass < 2; pass++)
                {
                    world.GetExistingSystemManaged<InitializationSystemGroup>()?.Update();
                    world.GetExistingSystemManaged<SimulationSystemGroup>()?.Update();
                    world.GetExistingSystemManaged<PresentationSystemGroup>()?.Update();
                }

                snapEg.Render(cam);

                // ── Diagnostics ──
                snapControl.WritePng("s53a-spike-control-go.png");
                snapEg.WritePng("s53a-spike-eg-entity.png");
                float controlCov = FilledFraction(snapControl);
                float egCov      = FilledFraction(snapEg);
                TestContext.WriteLine(
                    $"[S53a spike] control(GO) filled={controlCov:F4}  EG(entity) filled={egCov:F4}");

                // No-GPU guard: if even the GO control is blank, there's no GPU context in this batch
                // session — the EG gate can't be evaluated (matches the suite's snapshot convention).
                if (controlCov < CoverageFloor)
                    Assert.Inconclusive(
                        $"GameObject control did not render (filled={controlCov:F4}) — no GPU context " +
                        $"in this batch session; the EG render gate cannot be evaluated.");

                // THE GATE.
                Assert.That(egCov, Is.GreaterThanOrEqualTo(CoverageFloor),
                    $"S53a GATE: an Entities-Graphics entity drawing Map/Fill must produce non-empty " +
                    $"pixels in the headless SnapshotRenderer. control(GO)={controlCov:F4}, " +
                    $"EG(entity)={egCov:F4}. If the control renders but EG is blank, " +
                    $"EntitiesGraphicsSystem did not submit under camera.Render() in EditMode — " +
                    $"see S53a honest-stop (PlayMode harness vs hand-rolled BRG-over-entities).");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientLight = prevAmbient;
                snapControl.Dispose();
                snapEg.Dispose();
                if (world != null && world.IsCreated)
                {
                    World.DefaultGameObjectInjectionWorld = prevDefault;
                    world.Dispose();
                }
                if (camGo   != null) Object.DestroyImmediate(camGo);
                if (lightGo != null) Object.DestroyImmediate(lightGo);
                if (mapGo   != null) Object.DestroyImmediate(mapGo);
            }
        }
    }
}
