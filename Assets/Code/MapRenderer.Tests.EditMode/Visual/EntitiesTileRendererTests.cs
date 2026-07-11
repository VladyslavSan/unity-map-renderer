// S53b increment 1 — EntitiesTileRenderer engine tests.
//
// Proves the ECS backend engine independently of the live MapView/TileManager wiring:
//   • Lifecycle: AddTileLayer creates entities; RemoveItem destroys the right one (GPU-independent).
//   • Floating-origin rebase: each entity's LocalToWorld translation equals
//     FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin) and updates on an origin shift —
//     byte-for-byte the same formula the BRG backend uses (GPU-independent).
//   • Dispose: idempotent, restores DefaultGameObjectInjectionWorld, destroys the world.
//   • Render smoke: an engine-created entity actually rasterizes (the rendering mechanism itself is
//     already de-risked by EntitiesGraphicsSpikeTests / S53a; here we confirm the engine drives it).

using NUnit.Framework;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.Imaging;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class EntitiesTileRendererTests
    {
        private static (Mesh mesh, Material mat) FixtureFill()
        {
            var (go, mat) = FillSceneHelper.BuildFillGo();
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            // Keep mesh + material (separate assets); drop the helper GO.
            Object.DestroyImmediate(go);
            return (mesh, mat);
        }

        // ── Lifecycle (GPU-independent) ─────────────────────────────────────────────────────────

        [Test]
        public void AddAndRemove_TracksEntityLifetime()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 0, tid);
                int h2 = r.AddTileLayer(mesh, o, 0, tid);
                Assert.AreEqual(3, r.DrawItemCount, "Three draw items registered.");
                Assert.IsTrue(r.EntityExists(h0) && r.EntityExists(h1) && r.EntityExists(h2),
                    "All three entities must be live.");
                Assert.AreEqual(1, r.TileRootCount, "Three layers of one tile share a single root entity.");

                r.RemoveItem(h1);
                Assert.AreEqual(2, r.DrawItemCount, "RemoveItem must drop the item.");
                Assert.IsFalse(r.EntityExists(h1), "Removed entity must be destroyed.");
                Assert.IsTrue(r.EntityExists(h0) && r.EntityExists(h2),
                    "Sibling entities must survive a removal.");
                Assert.IsTrue(r.TileRootExists(tid), "Root survives while the tile still has layers.");

                r.RemoveItem(h1); // idempotent
                Assert.AreEqual(2, r.DrawItemCount, "Removing an unknown handle is a no-op.");

                // Removing the last two layers must tear the tile root down (no empty Hierarchy node).
                r.RemoveItem(h0);
                r.RemoveItem(h2);
                Assert.AreEqual(0, r.DrawItemCount, "All layers removed.");
                Assert.IsFalse(r.TileRootExists(tid), "Root is destroyed once its last layer is removed.");
                Assert.AreEqual(0, r.TileRootCount, "No orphan roots remain.");
            }
            finally { r.Dispose(); }
        }

        // ── Stall #3: ID route — no per-entity RenderMeshArray, and balanced mesh register/unregister ──

        [Test]
        public void AddTileLayer_IdRoute_NoPerEntityArray_BalancedMeshRegistration()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat, mat });
            try
            {
                Assert.AreEqual(1, r.RenderMeshArraysCreated, "the prototype creates exactly ONE RenderMeshArray at construction.");
                Assert.AreEqual(0, r.RegisteredMeshCount, "no meshes registered before any add.");

                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 1, tid);
                int h2 = r.AddTileLayer(mesh, o, 0, tid);

                Assert.AreEqual(1, r.RenderMeshArraysCreated,
                    "AddTileLayer must NOT create a per-entity RenderMeshArray (the stall-#3 fix) — still just " +
                    "the prototype's ONE. The old per-entity path would leave this at 3 (the falsifier).");
                Assert.AreEqual(3, r.RegisteredMeshCount, "one EG mesh registration per AddTileLayer.");
                Assert.AreEqual(3, r.DrawItemCount);

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
            finally { r.Dispose(); }
        }

        // ── Stall #2: batched removal — one DestroyEntity(NativeArray) structural change per record ──

        [Test]
        public void RemoveItems_DestroysWholeRecord_InOneBatchedStructuralChange()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat, mat, mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 1, tid);
                int h2 = r.AddTileLayer(mesh, o, 2, tid);
                Assert.AreEqual(3, r.DrawItemCount);
                Assert.AreEqual(1, r.TileRootCount);

                // Remove the whole 3-layer record in ONE call.
                r.RemoveItems(new[] { h0, h1, h2 });

                Assert.AreEqual(1, r.DestroyEntityBatchesLastRemove,
                    "the record's entities must be destroyed in ONE DestroyEntity(NativeArray) structural " +
                    "change — a shallow loop-over-RemoveItem leaves this 0 (the stall-#2 falsifier).");
                Assert.AreEqual(4, r.EntitiesDestroyedLastRemove,
                    "3 layer entities + 1 emptied tile root = 4 entities in the batch.");
                Assert.AreEqual(0, r.DrawItemCount, "all layers removed.");
                Assert.AreEqual(0, r.TileRootCount, "the emptied root is torn down by the same batch.");
                Assert.IsFalse(r.EntityExists(h0) || r.EntityExists(h1) || r.EntityExists(h2),
                    "every layer entity is destroyed.");

                // Idempotent: a second RemoveItems for the same (now unknown) handles destroys nothing.
                r.RemoveItems(new[] { h0, h1, h2 });
                Assert.AreEqual(0, r.DestroyEntityBatchesLastRemove, "no live entities → no batch (counter resets to 0).");
                Assert.AreEqual(0, r.EntitiesDestroyedLastRemove);
            }
            finally { r.Dispose(); }
        }

        // ── Entities Hierarchy naming: layer entity is named after its style layer, not the material ──

        [Test]
        public void AddTileLayer_NamesEntityAfterStyleLayer_NotMaterial()
        {
            var (mesh, mat) = FixtureFill();           // mat.name == "MapView_Fill" (shared, generic)
            // Two layers sharing one material — the entity name must come from the per-layer id, not the mat.
            var r = new EntitiesTileRenderer(new[] { mat, mat }, new[] { "water", "road-primary" });
            try
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
            finally { r.Dispose(); }
        }

        [Test]
        public void AddTileLayer_FallsBackToMaterialName_WhenNoLayerNames()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat });   // no names supplied
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h = r.AddTileLayer(mesh, o, 0, tid);
                Assert.AreEqual(mat.name, r.GetLayerEntityName(h),
                    "With no layer names, the entity name falls back to the material name (back-compat).");
            }
            finally { r.Dispose(); }
        }

        // ── Floating-origin rebase (GPU-independent) — same formula as the BRG backend ──────────

        [Test]
        public void Rebuild_SetsAndUpdates_TileLocalToSceneTranslation()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat });
            try
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

                // The child's LocalToWorld above was computed by LocalToWorldSystem from the ROOT (the
                // child carries only LocalTransform.Identity), so these two assertions also prove the
                // transform hierarchy is wired: the layer is parented and ParentSystem linked it under
                // the root (the structural proxy for the Entities-Hierarchy grouping).
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
            finally { r.Dispose(); }
        }

        // ── Spawn-position flash regression (GPU-independent) ───────────────────────────────────
        // Reproduces the zoom "blink in the corner": MapView runs Rebuild BEFORE consuming new tiles,
        // so a tile-layer added after the frame's Rebuild must still be created at its correct scene
        // position — NOT at the world origin (identity) where it would render for one frame. Here we
        // Rebuild first (seeds the cached origin, as the live loop does), THEN add, and assert the new
        // entity is already positioned before any further Rebuild runs.

        [Test]
        public void AddTileLayer_AfterRebuild_PositionedImmediately_NotAtOrigin()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat });
            try
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
            finally { r.Dispose(); }
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
        // Measures whether EntitiesTileRenderer.Rebuild allocates managed memory in steady state.
        // Unlike the hand-tuned BrgTileRenderer.Rebuild (asserted zero-alloc), this drives the EG
        // system groups, so it is expected to allocate. Logs the figure rather than hard-asserting so
        // the truth is recorded; the S53b churn-no-alloc tooth is evaluated from this.

        [Test]
        public void Rebuild_SteadyState_AllocationProfile()
        {
            var (mesh, mat) = FixtureFill();
            var r = new EntitiesTileRenderer(new[] { mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 o = FloatingOrigin.TileLocalOriginMercator(tid);
                r.AddTileLayer(mesh, o.ToRenderOrigin(), 0, tid);
                // Warm up (JIT + first-tick system allocations).
                for (int i = 0; i < 5; i++) r.Rebuild(SceneFrame.Mercator(o));

                const int N = 50;

                // AUTHORITATIVE: NUnit's GC.Alloc-recorder constraint (the same instrument the BRG zero-alloc
                // test trusts). It counts the Mono GC.Alloc profiler sampler, so it sees transient churn and
                // is immune to GC timing. The two naive counters were both proven WRONG on this runtime and
                // are deliberately NOT used here:
                //   • GC.GetTotalMemory(false) — net heap delta; GC-timing-dependent, reported 0 and ~409 on
                //     identical code in back-to-back runs.
                //   • GC.GetAllocatedBytesForCurrentThread() — returns a constant 0 on this Unity Mono build
                //     (a self-check allocating 80 KB registered 0 bytes), so a "0" from it is meaningless.
                // Rebuild ticks the EG system groups synchronously on the calling thread, so this measures
                // their managed allocations.
                // NOTE: the constraint reports the NUMBER OF GC.Alloc sampler calls, not a byte total — so
                // the verdict is "allocates: yes/no" + call count, never a bytes figure (quoting it as bytes
                // would just be a fresh wrong number). Instantiate + negate directly to avoid importing a
                // second `Is` (would collide with NUnit's `Is` used elsewhere in this file).
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
                TestContext.WriteLine($"[S53b alloc] EntitiesTileRenderer.Rebuild steady-state (NUnit GC.Alloc recorder): {verdict}");
            }
            finally { r.Dispose(); }
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
            var camGo  = new GameObject("EgTileCam");
            var cam    = camGo.AddComponent<Camera>();
            cam.transform.position = center + new Vector3(0f, 200f, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.orthographic = true; cam.orthographicSize = orthoSize; cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = bg; cam.enabled = false;
            var lightGo = new GameObject("EgTileLight");
            lightGo.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
            var light = lightGo.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.5f;

            // GameObject control (same mesh+material at the same world position) → no-GPU guard.
            var controlGo = new GameObject("EgTileControl");
            controlGo.transform.position = new Vector3(pos.x, pos.y, pos.z);
            controlGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            controlGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var snapControl = new SnapshotRenderer(512, 512);
            var snapEg      = new SnapshotRenderer(512, 512);
            EntitiesTileRenderer r = null;
            try
            {
                snapControl.Render(cam);
                controlGo.SetActive(false);

                r = new EntitiesTileRenderer(new[] { mat });
                r.AddTileLayer(mesh, tileOrigin.ToRenderOrigin(), 0, new TileId { Z = 0, X = 0, Y = 0 });
                r.Rebuild(SceneFrame.Mercator(tileOrigin));
                snapEg.Render(cam);

                float controlCov = SnapshotCoverage.Analyse(snapControl.RawPixels, 512, 512, 26, 28, 38).FilledFraction;
                float egCov      = SnapshotCoverage.Analyse(snapEg.RawPixels,      512, 512, 26, 28, 38).FilledFraction;
                TestContext.WriteLine($"[S53b engine] control(GO) filled={controlCov:F4}  EG(entity) filled={egCov:F4}");

                if (controlCov < 0.01f)
                    Assert.Inconclusive($"GameObject control did not render (filled={controlCov:F4}) — no GPU context.");

                Assert.That(egCov, Is.GreaterThanOrEqualTo(0.01f),
                    $"EntitiesTileRenderer must render the tile-layer entity non-empty. " +
                    $"control(GO)={controlCov:F4}, EG(entity)={egCov:F4}.");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientLight = prevAmbient;
                r?.Dispose();
                snapControl.Dispose(); snapEg.Dispose();
                Object.DestroyImmediate(controlGo);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
            }
        }
    }
}
