// Rendering-backend GPU/visual acceptance tests, part 1 of 2. Split by the CS0104 `CameraProperties` collision
// (docs/test-conventions.md): a bare CameraProperties user goes in BackendCameraTests.cs instead.
//
// Contents:
//   EntitiesGraphicsSpikeTests  — ECS render spike: an Entities-Graphics entity renders in the headless snapshot path.
//   GlobeSnapshotTests          — Renders the z=0 "countries" fixture tile projected onto a sphere and writes a PNG — visible proof the pipeline renders a spherical earth.
//   EntitiesTileRendererTests   — EntitiesTileRenderer engine tests.
//   GlobeBackendSnapshotTests   — proves the globe places correctly through a REAL render backend, not just the hand-wired snapshot.
//   BrgLinePropReadbackTests    — CPU-buffer readback acceptance tests: line props must be packed at the correct SoA slots.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Entities;
using Unity.Transforms;
using Unity.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.View;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Tile;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapRenderer.Core.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Visual
{
    // ECS render spike: an Entities-Graphics entity drawing Map/Fill renders NON-EMPTY pixels in the headless
    // EditMode SnapshotRenderer, the path the GameObject and BRG backends use.
    //
    // Non-obvious why: EntitiesGraphicsSystem ticks in the player-loop presentation group, which does NOT run
    // in headless EditMode, so an EG entity renders BLANK until its systems are ticked by hand. The SAME mesh
    // also renders as a GameObject MeshRenderer, the control: both blank means no GPU context, and the test
    // fails with that message.

    // ───────────────────────────────────────────────────────────────────────────────────
    // EntitiesGraphicsSpikeTests — ECS render spike
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class EntitiesGraphicsSpikeTests : BaseTestFixture
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        // Background: same dark slate as the fill snapshot tests (bytes 26,28,38).
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

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
            => SnapshotCoverage.Analyse(snap.Pixels, Bg32).FilledFraction;

        /// <summary>
        /// An Entities-Graphics entity drawing Map/Fill renders non-empty pixels in the
        /// headless SnapshotRenderer, driven by a manual system-group tick before camera.Render().
        /// </summary>
        [Test]
        public void Spike_EntitiesGraphics_RendersMapFill_InHeadlessHarness()
        {
            // Same mesh+material+transform the GO/BRG backends use (fixture countries fill, white).
            var (mapGo, mat) = FillSceneHelper.BuildFillGo();
            Track(mapGo);
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
            using var snapControl = new SnapshotRenderer(SnapW, SnapH);
            using var snapEg      = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                var (cg, cam) = BuildCamera();
                var camGo   = Track(cg);
                var lightGo = Track(AddDirectionalLight(1.5f, Quaternion.Euler(50f, 0f, 0f)));

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

                // Tick the three top-level groups, twice (register/upload, then steady). EntitiesGraphicsSystem
                // in the presentation group registers BRG batches; camera.Render() then makes EG submit.
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
                    $"[EG spike] control(GO) filled={controlCov:F4}  EG(entity) filled={egCov:F4}");

                // No-GPU guard: if even the GO control is blank, there's no GPU context in this batch
                // session — the EG gate can't be evaluated (matches the suite's snapshot convention).
                Assert.That(controlCov, Is.GreaterThanOrEqualTo(CoverageFloor),
                    $"GameObject control did not render (filled={controlCov:F4}) — no GPU context " +
                    $"in this batch session; the EG render gate cannot be evaluated.");

                // THE GATE.
                Assert.That(egCov, Is.GreaterThanOrEqualTo(CoverageFloor),
                    $"GATE: an Entities-Graphics entity drawing Map/Fill must produce non-empty " +
                    $"pixels in the headless SnapshotRenderer. control(GO)={controlCov:F4}, " +
                    $"EG(entity)={egCov:F4}. If the control renders but EG is blank, " +
                    $"EntitiesGraphicsSystem did not submit under camera.Render() in EditMode — " +
                    $"the alternatives are a PlayMode harness or a hand-rolled BRG-over-entities.");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientLight = prevAmbient;
                if (world != null && world.IsCreated)
                {
                    World.DefaultGameObjectInjectionWorld = prevDefault;
                    world.Dispose();
                }
            }
        }
    }

    // GlobeSnapshotTests — the z=0 "countries" tile (the whole world) projected onto a SPHERE, written as a PNG.
    // Fills bake the radial normal, so the Lit material shades the sphere; FitToView frames the ECEF bounds.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeSnapshotTests — Renders the z=0 "countries" fixture tile projected onto a sphere and writes a PNG
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeSnapshotTests : BaseTestFixture
    {
        private const int SnapW = 512, SnapH = 512;

        // Ocean-ish clear colour so the sphere reads as an earth (land polygons over water background).
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void RendersCountriesOnASphere_WritesPng()
        {
            // Land polygons in green, projected onto the globe.
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]",
                viewSize: 2f,                       // globe ≈ 2 units across (radius ≈ 1)
                projection: new SphericalProjection());
            Track(mapGo);

            // Stock Cull Back, as the shipped MapFill.mat: StyledFillTileBuilder's winding reversal makes near-side
            // fills Unity-front, so the far hemisphere does not bleed through ocean gaps.
            if (mat != null) mat.SetCull(CullMode.Back);

            // Directional light to shade the sphere (Lit material is near-black at ambient-only).
            var lightGo = new GameObject("GlobeLight");
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);
            lightGo.transform.SetParent(mapGo.transform, worldPositionStays: true);

            // Perspective camera outside the globe, looking at the origin (the globe is centred there).
            var cameraGo = Track(new GameObject("GlobeCamera"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(1.7f, 1.2f, -3.0f);
            camera.transform.rotation = Quaternion.LookRotation(-camera.transform.position, Vector3.up);
            camera.orthographic    = false;
            camera.fieldOfView     = 35f;
            camera.nearClipPlane   = 0.05f;
            camera.farClipPlane    = 100f;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled         = false;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            string path = snap.WritePng("globe-countries.png");
            TestContext.WriteLine($"[GlobeSnapshotTests] wrote {path}");
        }
    }

    // EntitiesTileRenderer engine tests, independent of MapView/TileManager: lifecycle, floating-origin rebase
    // (the BRG backend's formula), idempotent Dispose, and a render smoke test that the engine drives EG.

    // ───────────────────────────────────────────────────────────────────────────────────
    // EntitiesTileRendererTests — the ECS backend engine, independent of MapView
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class EntitiesTileRendererTests : BaseTestFixture
    {
        private static (Mesh mesh, Material mat) FixtureFill()
        {
            using var bag = new ObjectDisposalBag();
            var (go, mat) = FillSceneHelper.BuildFillGo();
            bag.Track(go);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            // Keep mesh + material (separate assets); drop the helper GO.
            return (mesh, mat);
        }

        // ── Lifecycle (GPU-independent) ─────────────────────────────────────────────────────────

        [Test]
        public void AddAndRemove_TracksEntityLifetime()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 0, tid);
                int h2 = r.AddTileLayer(mesh, o, 0, tid);
                Assert.AreEqual(3, r.DrawItemCount(), "Three draw items registered.");
                Assert.IsTrue(r.EntityExists(h0) && r.EntityExists(h1) && r.EntityExists(h2),
                    "All three entities must be live.");
                Assert.AreEqual(1, r.TileRootCount(), "Three layers of one tile share a single root entity.");

                r.RemoveItem(h1);
                Assert.AreEqual(2, r.DrawItemCount(), "RemoveItem must drop the item.");
                Assert.IsFalse(r.EntityExists(h1), "Removed entity must be destroyed.");
                Assert.IsTrue(r.EntityExists(h0) && r.EntityExists(h2),
                    "Sibling entities must survive a removal.");
                Assert.IsTrue(r.TileRootExists(tid), "Root survives while the tile still has layers.");

                r.RemoveItem(h1); // idempotent
                Assert.AreEqual(2, r.DrawItemCount(), "Removing an unknown handle is a no-op.");

                // Removing the last two layers must tear the tile root down (no empty Hierarchy node).
                r.RemoveItem(h0);
                r.RemoveItem(h2);
                Assert.AreEqual(0, r.DrawItemCount(), "All layers removed.");
                Assert.IsFalse(r.TileRootExists(tid), "Root is destroyed once its last layer is removed.");
                Assert.AreEqual(0, r.TileRootCount(), "No orphan roots remain.");
            }
        }

        // ── ID route — no per-entity RenderMeshArray, and balanced mesh register/unregister ────────────

        [Test]
        public void AddTileLayer_IdRoute_NoPerEntityArray_BalancedMeshRegistration()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat, mat });
            {
                Assert.AreEqual(1, r.RenderMeshArraysCreated, "the prototype creates exactly ONE RenderMeshArray at construction.");
                Assert.AreEqual(0, r.RegisteredMeshCount, "no meshes registered before any add.");

                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 1, tid);
                int h2 = r.AddTileLayer(mesh, o, 0, tid);

                Assert.AreEqual(1, r.RenderMeshArraysCreated,
                    "AddTileLayer must NOT create a per-entity RenderMeshArray — still just " +
                    "the prototype's ONE. A per-entity path would leave this at 3 (the falsifier).");
                Assert.AreEqual(3, r.RegisteredMeshCount, "one EG mesh registration per AddTileLayer.");
                Assert.AreEqual(3, r.DrawItemCount());

                // Batched removal balances every registration (the ID route's leak trap, review #3).
                r.RemoveItems(new[] { h0, h1, h2 });
                Assert.AreEqual(0, r.RegisteredMeshCount, "every RegisterMesh must be matched by an UnregisterMesh.");

                // Re-add then single-RemoveItem — mirrors a prepared-cache revisit re-registering the cached
                // mesh via BuildTileFromCache→AddTileLayer, then evicting again. Must stay balanced.
                int h3 = r.AddTileLayer(mesh, o, 0, tid);
                Assert.AreEqual(1, r.RegisteredMeshCount);
                r.RemoveItem(h3);
                Assert.AreEqual(0, r.RegisteredMeshCount, "RemoveItem unregisters too (not just RemoveItems).");
                Assert.AreEqual(1, r.RenderMeshArraysCreated, "no extra arrays after a full add/remove/re-add cycle.");
            }
        }

        // ── Batched removal — one DestroyEntity(NativeArray) structural change per record ─────────────

        [Test]
        public void RemoveItems_DestroysWholeRecord_InOneBatchedStructuralChange()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat, mat, mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 1, tid);
                int h2 = r.AddTileLayer(mesh, o, 2, tid);
                Assert.AreEqual(3, r.DrawItemCount());
                Assert.AreEqual(1, r.TileRootCount());

                // Remove the whole 3-layer record in ONE call.
                r.RemoveItems(new[] { h0, h1, h2 });

                Assert.AreEqual(1, r.DestroyEntityBatchesLastRemove,
                    "the record's entities must be destroyed in ONE DestroyEntity(NativeArray) structural " +
                    "change — a shallow loop-over-RemoveItem leaves this 0 (the falsifier).");
                Assert.AreEqual(4, r.EntitiesDestroyedLastRemove,
                    "3 layer entities + 1 emptied tile root = 4 entities in the batch.");
                Assert.AreEqual(0, r.DrawItemCount(), "all layers removed.");
                Assert.AreEqual(0, r.TileRootCount(), "the emptied root is torn down by the same batch.");
                Assert.IsFalse(r.EntityExists(h0) || r.EntityExists(h1) || r.EntityExists(h2),
                    "every layer entity is destroyed.");

                // Idempotent: a second RemoveItems for the same (now unknown) handles destroys nothing.
                r.RemoveItems(new[] { h0, h1, h2 });
                Assert.AreEqual(0, r.DestroyEntityBatchesLastRemove, "no live entities → no batch (counter resets to 0).");
                Assert.AreEqual(0, r.EntitiesDestroyedLastRemove);
            }
        }

        // ── Play-mode Stop race: RemoveItems tolerates the World being torn down first (GPU-independent) ──
        // Non-local invariant: on Play-mode Stop, Unity disposes every Entities World BEFORE
        // MapViewComponent.OnDestroy reaches RemoveItems. Without the _world.IsCreated guard, RemoveItems would
        // throw ObjectDisposedException and abort DoDispose mid-loop. This tooth pins that guard directly,
        // not MapView.Teardown's separate catch.

        [Test]
        public void RemoveItems_AfterWorldDisposedExternally_DoesNotTouchDeadEntityManager()
        {
            World prevDefault = World.DefaultGameObjectInjectionWorld; // capture BEFORE the backend hijacks it
            var (mesh, mat) = FixtureFill();
            Track(mesh);
            using var r = new EntitiesTileRenderer(new[] { mat, mat, mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 1, tid);
                int h2 = r.AddTileLayer(mesh, o, 2, tid);
                Assert.AreEqual(3, r.DrawItemCount(),
                    "precondition: real handles must be registered so RemoveItems REACHES the _em.Exists touch — " +
                    "an empty span would skip the loop and pass vacuously without exercising the guard.");

                // Simulate Unity's Stop ordering: dispose the World out from under the still-live backend.
                World world = r._em.World;
                Assert.IsTrue(world.IsCreated, "precondition: the backend's World is alive before we dispose it.");
                world.Dispose();
                Assert.IsFalse(world.IsCreated,
                    "precondition: the World is dead — the exact state RemoveItems must tolerate.");
                Assert.IsFalse(r.IsDisposed,
                    "precondition: the backend itself is NOT disposed (only its World) — so the existing " +
                    "IsDisposed early-return is NOT what saves us; the _world.IsCreated guard is.");

                // The tooth: RemoveItems must skip the dead EntityManager, not throw out of teardown.
                Assert.DoesNotThrow(() => r.RemoveItems(new[] { h0, h1, h2 }),
                    "RemoveItems must tolerate an externally-disposed World (the _world.IsCreated guard) instead " +
                    "of touching the deallocated EntityManager and throwing ObjectDisposedException.");
            }
            finally
            {
                // The external World disposal made the backend's own DefaultGameObjectInjectionWorld restore
                // (guarded by _world.IsCreated) a no-op; restore it so later Entities tests see a clean global.
                if (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
                    World.DefaultGameObjectInjectionWorld = prevDefault;
            }
        }

        // ── Entities Hierarchy naming: layer entity is named after its style layer, not the material ──

        [Test]
        public void AddTileLayer_NamesEntityAfterStyleLayer_NotMaterial()
        {
            var (mesh, mat) = FixtureFill();           // mat.name == "MapView_Fill" (shared, generic)
            // Two layers sharing one material — the entity name must come from the per-layer id, not the mat.
            using var r = new EntitiesTileRenderer(new[] { mat, mat }, new[] { "water", "road-primary" });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int hWater = r.AddTileLayer(mesh, o, 0, tid);
                int hRoad  = r.AddTileLayer(mesh, o, 1, tid);

                Assert.AreEqual("water", r.GetLayerEntityName(hWater),
                    "Layer entity must be named after its style layer id, not the shared material name.");
                Assert.AreEqual("road-primary", r.GetLayerEntityName(hRoad),
                    "Two layers sharing a material must still get distinct, layer-specific names.");
                Assert.AreNotEqual(mat.name, r.GetLayerEntityName(hWater),
                    "Regression: the entity must NOT fall back to the material name when an id is supplied.");
            }
        }

        [Test]
        public void AddTileLayer_FallsBackToMaterialName_WhenNoLayerNames()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat });   // no names supplied
            var tid = new TileId { Z = 0, X = 0, Y = 0 };
            double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
            int h = r.AddTileLayer(mesh, o, 0, tid);
            Assert.AreEqual(mat.name, r.GetLayerEntityName(h),
                "With no layer names, the entity name falls back to the material name (back-compat).");
        }

        // ── Floating-origin rebase (GPU-independent) — same formula as the BRG backend ──────────

        [Test]
        public void Rebuild_SetsAndUpdates_TileLocalToSceneTranslation()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(tid);
                double2 sceneOrigin0 = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                int h = r.AddTileLayer(mesh, tileOrigin.ToRenderOrigin(), 0, tid);

                r.Rebuild(SceneFrame.Mercator(sceneOrigin0));
                float3 expected0 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin0);
                var (x0, z0) = r.GetInstanceTranslation(h);
                Assert.That(x0, Is.EqualTo(expected0.x).Within(0.01f),
                    "LocalToWorld.X must equal TileLocalToScene.x after Rebuild.");
                Assert.That(z0, Is.EqualTo(expected0.z).Within(0.01f),
                    "LocalToWorld.Z must equal TileLocalToScene.z after Rebuild.");

                // The child carries only LocalTransform.Identity, so its LocalToWorld came from the ROOT. These
                // assertions show the hierarchy is wired: the layer is parented and linked under the root.
                Assert.IsTrue(r.IsParentedToTileRoot(h, tid),
                    "The layer entity must carry a Parent pointing at its tile root.");
                Assert.That(r.RootChildBufferCount(tid), Is.EqualTo(1),
                    "ParentSystem must populate the root's Child buffer after a Rebuild tick.");

                // Origin shift (a look-at move): translation must track the new origin.
                double2 sceneOrigin1 = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 1, X = 1, Y = 1 });
                r.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                var (x1, z1) = r.GetInstanceTranslation(h);
                Assert.That(x1, Is.EqualTo(expected1.x).Within(0.01f),
                    "After an origin shift, X must update to the new TileLocalToScene.x.");
                Assert.That(z1, Is.EqualTo(expected1.z).Within(0.01f),
                    "After an origin shift, Z must update to the new TileLocalToScene.z.");
                Assert.AreNotEqual(x0, x1, "The origin shift must actually move the instance.");
            }
        }

        // ── Spawn-position flash regression (GPU-independent) ───────────────────────────────────
        // Non-obvious why: MapView runs Rebuild BEFORE it consumes new tiles, so a layer added after Rebuild
        // must spawn at its scene position, not at the origin, where it would flash for one frame.

        [Test]
        public void AddTileLayer_AfterRebuild_PositionedImmediately_NotAtOrigin()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(tid);
                double2 sceneOrigin  = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 1, X = 1, Y = 1 });
                float3  expected     = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin);

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

        // ── Frustum-cull bounds (GPU-independent) ───────────────────────────────────────────────
        // Non-obvious why: at low zoom one tile mesh spans several 1e6 m, so a fixed 1e6 box would let EG cull a
        // visible tile. RenderBounds must ENCLOSE the mesh, so the tooth asserts enclosure.

        [Test]
        public void AddTileLayer_RenderBounds_EncloseLargeMesh_NotFixed1e6Box()
        {
            // A tile-sized mesh whose extent exceeds the old 1e6 box, positioned like a real tile (one
            // corner at the local origin, geometry reaching out to +5e6 m — a Mercator ~z3 span).
            const float span = 5_000_000f; // > 1e6 → the old fixed box cannot enclose it
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0f,    0f, 0f),
                    new Vector3(span,  0f, 0f),
                    new Vector3(span,  0f, span),
                    new Vector3(0f,    0f, span),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            mesh.RecalculateBounds(); // tight AABB the builders would carry

            var (fixtureMesh, mat) = FixtureFill();
            Track(mesh);
            Track(fixtureMesh);
            using var r = new EntitiesTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 3, X = 0, Y = 0 };
                int h = r.AddTileLayer(mesh, double3.zero, 0, tid);

                var (center, extents) = r.GetRenderBoundsLocal(h);
                float3 lo = center - extents;
                float3 hi = center + extents;

                // Every mesh vertex must lie inside the RenderBounds AABB (with a hair of slack).
                foreach (Vector3 vtx in mesh.vertices)
                {
                    var v = new float3(vtx.x, vtx.y, vtx.z);
                    Assert.IsTrue(math.all(v >= lo - 1f) && math.all(v <= hi + 1f),
                        $"RenderBounds [{lo} .. {hi}] must enclose mesh vertex {v}. The fixed 1e6 box " +
                        "(the regression) leaves this tile's far corner outside the AABB, so EG culls the " +
                        "whole tile once its near corner exits the Game frustum.");
                }

                // Teeth against a degenerate pass: the box must actually be tile-sized, not the 1e6 hack.
                Assert.Greater(extents.x, 2_000_000f,
                    "RenderBounds must track the (5e6-wide) mesh, not the fixed 1e6 box.");
            }
        }

        // ── Dispose (GPU-independent) ───────────────────────────────────────────────────────────

        [Test]
        public void Dispose_IsIdempotent_AndRestoresDefaultWorld()
        {
            var (mesh, mat) = FixtureFill();
            World before = World.DefaultGameObjectInjectionWorld;
            var r = new EntitiesTileRenderer(new[] { mat });
            r.AddTileLayer(mesh, FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 }).ToRenderOrigin(), 0, new TileId { Z = 0, X = 0, Y = 0 });

            r.Dispose();
            Assert.IsTrue(r.IsDisposed, "IsDisposed must be true after Dispose.");
            Assert.AreSame(before, World.DefaultGameObjectInjectionWorld,
                "Dispose must restore the previous DefaultGameObjectInjectionWorld.");
            Assert.DoesNotThrow(() => r.Dispose(), "Dispose must be idempotent.");
        }

        // ── Steady-state allocation profile (informational) ─────────────────────────────────────
        // Rebuild drives the EG system groups and is expected to allocate, so this logs instead of asserting.

        [Test]
        public void Rebuild_SteadyState_AllocationProfile()
        {
            var (mesh, mat) = FixtureFill();
            using var r = new EntitiesTileRenderer(new[] { mat });
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 o = FloatingOrigin.TileLocalOriginMercator(tid);
                r.AddTileLayer(mesh, o.ToRenderOrigin(), 0, tid);
                // Warm up (JIT + first-tick system allocations).
                for (int i = 0; i < 5; i++) r.Rebuild(SceneFrame.Mercator(o));

                const int N = 50;

                // Non-obvious why: this constraint counts GC.Alloc sampler calls, not bytes, and is immune to GC
                // timing. GC.GetTotalMemory depends on GC timing, and GetAllocatedBytesForCurrentThread returns 0
                // on this Unity Mono build. Constructing it directly avoids a second, colliding `Is` import.
                // Rebuild ticks the EG system groups synchronously on the calling thread, so this measures them.
                var allocates = new UnityEngine.TestTools.Constraints.AllocatingGCMemoryConstraint();
                string verdict;
                try
                {
                    Assert.That(() => { for (int i = 0; i < N; i++) r.Rebuild(SceneFrame.Mercator(o)); },
                                new NUnit.Framework.Constraints.NotConstraint(allocates));
                    verdict = $"NO GC allocation over {N} Rebuilds";
                }
                catch (AssertionException)
                {
                    // The constraint trips on ≥1 GC.Alloc sampler call; its byte/count actual prints blank
                    // here, so report the trip itself, not the unhelpful message tail.
                    verdict = $"ALLOCATES over {N} Rebuilds (GC.Alloc recorder tripped)";
                }
                TestContext.WriteLine($"[EG alloc] EntitiesTileRenderer.Rebuild steady-state (NUnit GC.Alloc recorder): {verdict}");
            }
        }

        // ── Render smoke (GPU) — the engine actually rasterizes ─────────────────────────────────

        [Test]
        public void Rebuild_EntityRendersNonEmpty()
        {
            var (mesh, mat) = FixtureFill();

            int   prevQuality = QualitySettings.GetQualityLevel();
            Color prevAmbient = RenderSettings.ambientLight;
            QualitySettings.SetQualityLevel(0, false);
            RenderSettings.ambientLight = new Color(0.3f, 0.3f, 0.3f, 1f);

            // Place one tile at sceneOrigin == tileOrigin → world position ≈ 0; frame the mesh natively.
            double2 tileOrigin = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
            float3  pos        = FloatingOrigin.TileLocalToScene(tileOrigin, tileOrigin);
            Bounds  mb         = mesh.bounds;
            Vector3 center     = new Vector3(pos.x + mb.center.x, 0f, pos.z + mb.center.z);
            float   orthoSize  = Mathf.Max(mb.size.x, mb.size.z, 1f) * 0.6f;

            var bg = new Color(0.10f, 0.11f, 0.15f, 1f);
            var camGo  = Track(new GameObject("EgTileCam"));
            var cam    = camGo.AddComponent<Camera>();
            cam.transform.position = center + new Vector3(0f, 200f, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.orthographic = true; cam.orthographicSize = orthoSize; cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg; cam.enabled = false;
            var lightGo = Track(new GameObject("EgTileLight"));
            lightGo.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
            var light = lightGo.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.5f;

            // GameObject control (same mesh+material at the same world position) → no-GPU guard.
            var controlGo = Track(new GameObject("EgTileControl"));
            controlGo.transform.position = new Vector3(pos.x, pos.y, pos.z);
            controlGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            controlGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            using var snapControl = new SnapshotRenderer(512, 512);
            using var snapEg      = new SnapshotRenderer(512, 512);
            try
            {
                snapControl.Render(cam);
                controlGo.SetActive(false);

                using var r = new EntitiesTileRenderer(new[] { mat });
                r.AddTileLayer(mesh, tileOrigin.ToRenderOrigin(), 0, new TileId { Z = 0, X = 0, Y = 0 });
                r.Rebuild(SceneFrame.Mercator(tileOrigin));
                snapEg.Render(cam);

                var bg32 = new Color32(26, 28, 38, 255);
                float controlCov = SnapshotCoverage.Analyse(snapControl.Pixels, bg32).FilledFraction;
                float egCov      = SnapshotCoverage.Analyse(snapEg.Pixels,      bg32).FilledFraction;
                TestContext.WriteLine($"[EG engine] control(GO) filled={controlCov:F4}  EG(entity) filled={egCov:F4}");

                Assert.That(controlCov, Is.GreaterThanOrEqualTo(0.01f),
                    $"GameObject control did not render (filled={controlCov:F4}) — no GPU context.");

                Assert.That(egCov, Is.GreaterThanOrEqualTo(0.01f),
                    $"EntitiesTileRenderer must render the tile-layer entity non-empty. " +
                    $"control(GO)={controlCov:F4}, EG(entity)={egCov:F4}.");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientLight = prevAmbient;
            }
        }
    }

    // GlobeBackendSnapshotTests — the globe through a REAL render backend with a NON-identity rebase: placement is
    // asserted numerically, then rendered through the live MapCamera's CameraPoseMath.ComputeRelativePose orbit.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeBackendSnapshotTests — proves the globe places correctly through a REAL render backend
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeBackendSnapshotTests : BaseTestFixture
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void GameObjectBackend_PlacesAndRendersGlobe()
        {
            var proj   = new SphericalProjection();
            var tid    = new TileId { Z = 0, X = 0, Y = 0 };
            var lookAt = new GeoCoordinate { Latitude = 20.0, Longitude = 12.0 }; // over Africa

            // Globe-baked fill mesh (vertices ECEF, relative to the tile's projected SW corner).
            var (meshGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]", projection: proj, fitToView: false);
            Mesh mesh = meshGo.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(meshGo); // keep the mesh + material, drop the helper GO
            Track(mesh);
            Track(mat);
            if (mat != null) mat.SetFloat("_Cull", 2f); // back-face cull → near hemisphere only

            // The SAME projected SW-corner origin the mesh was baked against (Level-1 == Level-2 origin).
            double3 tileOriginRender  = TileRenderOrigin.Project(tid, proj);
            double3 sceneOriginRender = proj.Project(lookAt);
            float3x3 rebase           = math.transpose(proj.TangentBasisAt(lookAt));
            var frame                 = new SceneFrame { SceneOriginRender = sceneOriginRender, Rebase = rebase };

            using var backend = new GameObjectTileRenderer(new[] { mat });
            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                backend.AddTileLayer(mesh, tileOriginRender, 0, tid);
                backend.Rebuild(frame);

                // ── Numerical proof: the backend applied the globe rebase (rotation + rebased position). ──
                Transform container = backend.Container(tid);
                Assert.IsNotNull(container, "backend must create the tile container");

                float3 expectedPos = FloatingOrigin.TileToSceneRebased(tileOriginRender, sceneOriginRender, rebase);
                Vector3 pos = container.localPosition;
                Assert.AreEqual(expectedPos.x, pos.x, 1f, "container X == TileToSceneRebased.x");
                Assert.AreEqual(expectedPos.y, pos.y, 1f, "container Y == TileToSceneRebased.y (non-zero on the globe)");
                Assert.AreEqual(expectedPos.z, pos.z, 1f, "container Z == TileToSceneRebased.z");
                // NOT identity — this is the branch Slice 1 left untested (Mercator would give pos.y == 0).
                Assert.Greater(math.abs(pos.y), 1f, "globe placement must lift the tile off the XZ plane");

                quaternion expectedRot = new quaternion(rebase);
                Quaternion rot = container.localRotation;
                Assert.AreEqual(expectedRot.value.x, rot.x, 1e-4f, "container rotation.x == quaternion(rebase)");
                Assert.AreEqual(expectedRot.value.y, rot.y, 1e-4f, "container rotation.y == quaternion(rebase)");
                Assert.AreEqual(expectedRot.value.z, rot.z, 1e-4f, "container rotation.z == quaternion(rebase)");
                Assert.AreEqual(expectedRot.value.w, rot.w, 1e-4f, "container rotation.w == quaternion(rebase)");
                Assert.AreNotEqual(quaternion.identity.value.x, rot.x, "globe rotation must not be identity");

                // ── Visual proof: render the backend's globe through the real ComputeRelativePose orbit. ──
                double altitude = 2.5 * SphericalProjection.Radius;
                CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                    out double3 cpos, out double3 fwd, out double3 up);

                var cameraGo = Track(new GameObject("GlobeBackendCamera"));
                var camera = cameraGo.AddComponent<Camera>();
                camera.transform.position = new Vector3((float)cpos.x, (float)cpos.y, (float)cpos.z);
                camera.transform.rotation = Quaternion.LookRotation(
                    new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                    new Vector3((float)up.x,  (float)up.y,  (float)up.z));
                camera.fieldOfView     = 35f;
                camera.nearClipPlane   = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
                camera.farClipPlane    =                  (float)CameraPoseMath.FarClip(altitude);
                camera.clearFlags      = CameraClearFlags.SolidColor;
                camera.backgroundColor = OceanBg;
                camera.enabled         = false;

                var lightGo = Track(new GameObject("GlobeBackendLight"));
                var light   = lightGo.AddComponent<Light>();
                light.type      = LightType.Directional;
                light.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);

                snap.Render(camera);
                string path = snap.WritePng("globe-backend-countries.png");
                TestContext.WriteLine($"[GlobeBackendSnapshotTests] wrote {path}");
            }
        }
    }

    // BRG line-prop readback tests — the CPU-buffer gate (GPU-independent). A BrgTileRenderer built from a
    // line-only RenderLayerSet must pack line props at the right SoA slots; a missing plan entry reads NaN.

    // ───────────────────────────────────────────────────────────────────────────────────
    // BrgLinePropReadbackTests — CPU-buffer readback acceptance tests
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CPU-buffer readback acceptance tests: line props must be packed at the correct SoA slots.
    /// All assertions are GPU-independent (read the CPU float[] buffer via <see cref="BrgTileRenderer.GetInstancePropValue"/>).
    /// </summary>
    [TestFixture]
    public class BrgLinePropReadbackTests : BaseTestFixture
    {
        // Line-only style: one line layer with line-width=40 (literal, so the styler evaluates it as 40
        // at any zoom). This sets _Width=40 on the material after RenderLayerSet.Build.
        private const string LineStyleJson = @"{
    ""version"": 8,
    ""name"": ""LinePropReadbackTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""test-line"", ""type"": ""line"",
          ""source"": ""s"", ""source-layer"": ""geolines"",
          ""paint"": { ""line-color"": [""rgba"", 80, 200, 80, 1], ""line-width"": 40 } }
    ]
}";

        /// <summary>
        /// Headless CI gate: after Rebuild with a line material that has _Width=40,
        /// the CPU buffer must contain 40.0 at the correct SoA slot for _Width.
        /// </summary>
        [Test]
        public void BrgLinePropReadback_PackedValuesMatchMaterial()
        {
            var style  = StyleParser.Parse(LineStyleJson);
            using var set = new RenderLayerSet();
            set.Build(style, 0.0, MapMaterialSetTestUtil.Load());

            // Line-only style: one render layer (a line) → _layerMaterials[0] = layer[0].
            Assert.That(set.Count, Is.EqualTo(1), "Line-only style must have exactly 1 render layer.");
            Assert.IsInstanceOf<MapRenderer.Core.Style.Line.StyleLayer>(set[0].StyleLayer,
                "The single render layer must be a line.");

            Material mat = set[0].Material;
            Assert.IsNotNull(mat, "Line material must be non-null after RenderLayerSet.Build.");

            // Pre-check: styler must have applied line-width=40 to the material.
            int widthId       = ShaderProperties.Line.PropertyId.Width;
            int opacityId     = ShaderProperties.PropertyId.Opacity;

            float matWidth      = mat.HasProperty(widthId)      ? mat.GetFloat(widthId)      : float.NaN;
            float matOpacity    = mat.HasProperty(opacityId)    ? mat.GetFloat(opacityId)    : float.NaN;

            Assert.That(matWidth, Is.EqualTo(40f).Within(1e-3f),
                "Pre-check: mat._Width must be 40 after RenderLayerSet.Build with line-width:40. " +
                "If NaN, the Line shader does not declare _Width in Properties{}.");

            var mesh = Track(new Mesh());
            using var brg  = new BrgTileRenderer(new[] { set[0].Material });

            {
                // ── Exact plan-count tooth ────────────────────────────────────────────────────
                Assert.That(brg.FloatsPerInstance(), Is.EqualTo(82),
                    "BrgTileRenderer.FloatsPerInstance must be 82 (MapInstanceData: 24 transform + 58 material floats). " +
                    "An incompletely-generated plan (e.g. missing line props) produces a smaller value.");

                Assert.That(brg.MetadataEntryCount(), Is.EqualTo(33),
                    "BrgTileRenderer.MetadataEntryCount must be 33 (2 transforms + 31 material props). " +
                    "Missing entries mean the BRG batch omits those props from the GPU instancing table.");

                // ── Register and Rebuild ──────────────────────────────────────────────────────
                // materialIndex=0: FillCount=0 → lines[0] is at index 0.
                int h = brg.AddTileLayer(mesh, double3.zero, 0, new TileId { Z = 0, X = 0, Y = 0 });
                brg.Rebuild(SceneFrame.Mercator(double2.zero));

                // ── _Width readback (the direct line-prop pack proof) ─────────────────────────
                float packedWidth = brg.GetInstancePropValue(h, widthId);

                Assert.That(packedWidth, Is.Not.NaN,
                    "GetInstancePropValue(_Width) returned NaN — no plan entry for _Width. " +
                    "This is the original bug: _Width has no SoA slot → reads garbage (byte 0 = transform). " +
                    "MapInstanceData must declare _Width as a field so InstancePropPlan creates its entry.");

                Assert.That(packedWidth, Is.EqualTo(matWidth).Within(1e-3f),
                    $"Packed _Width ({packedWidth}) must equal mat.GetFloat(_Width) ({matWidth}). " +
                    "If they differ, the pack gate (HasProperty + GetFloat) is broken for _Width.");

                Assert.That(packedWidth, Is.EqualTo(40f).Within(1e-3f),
                    $"Packed _Width must be 40.0 (the style's line-width). Got {packedWidth}.");

                // ── _Opacity byte-identical-wire spot check ───────────────────────────────────
                // _Opacity must stay at SoA float offset 46, so the fill props' wire layout does not shift.
                int opacitySoaOffset = brg.GetPropSoaOffset(opacityId);
                Assert.That(opacitySoaOffset, Is.EqualTo(46),
                    $"_Opacity SoA float offset must be 46. " +
                    $"Got {opacitySoaOffset}. If shifted, the fill wire layout changed and existing " +
                    "fill-rendered tiles would misread per-instance properties.");
            }
        }
    }
}
