// GameObject render backend tests — the restored RenderBackend.GameObject path.
//
// Proves the GameObject backend engine independently of the live MapView/TileManager wiring, mirroring
// EntitiesTileRendererTests so the two debuggable backends are held to the same contract:
//   • Lifecycle: AddTileLayer creates per-layer child GameObjects under one per-tile container; RemoveItem
//     destroys the right child and tears the container down once its last layer is gone.
//   • Naming: each layer GameObject is named after its style layer id (fallback to the material name).
//   • Floating-origin rebase: the container's position equals FloatingOrigin.TileLocalToScene(tileOrigin,
//     sceneOrigin) and updates on an origin shift — the same formula the instanced backends use.
//   • Spawn-position flash regression: a layer consumed AFTER a frame's Rebuild is positioned immediately.
//   • Dispose: idempotent; destroys the backend root (and with it every container + layer child).
//
// All assertions read the live Transform hierarchy (GPU-independent).

using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class GameObjectTileRendererTests
    {
        private static (Mesh mesh, Material mat) FixtureFill()
        {
            var (go, mat) = FillSceneHelper.BuildFillGo();
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(go); // keep mesh + material; drop the helper GO
            return (mesh, mat);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void AddAndRemove_TracksGameObjectLifetime()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                int h0 = r.AddTileLayer(mesh, o, 0, tid);
                int h1 = r.AddTileLayer(mesh, o, 0, tid);
                int h2 = r.AddTileLayer(mesh, o, 0, tid);

                Assert.AreEqual(3, r.DrawItemCount, "Three draw items registered.");
                Assert.AreEqual(1, r.ContainerCount, "Three layers of one tile share a single container.");
                Transform container = r.Container(tid);
                Assert.IsNotNull(container, "A container GameObject must exist for the tile.");
                Assert.AreEqual(3, container.childCount, "All three layer GameObjects hang under the container.");

                r.RemoveItem(h1);
                Assert.AreEqual(2, r.DrawItemCount, "RemoveItem must drop the item.");
                Assert.AreEqual(2, r.Container(tid).childCount, "The removed layer GameObject must be destroyed.");
                Assert.AreEqual(1, r.ContainerCount, "Container survives while the tile still has layers.");

                r.RemoveItem(h1); // idempotent
                Assert.AreEqual(2, r.DrawItemCount, "Removing an unknown handle is a no-op.");

                // Removing the last two layers must tear the container down (no empty Hierarchy node).
                r.RemoveItem(h0);
                r.RemoveItem(h2);
                Assert.AreEqual(0, r.DrawItemCount, "All layers removed.");
                Assert.AreEqual(0, r.ContainerCount, "Container is destroyed once its last layer is removed.");
                Assert.IsNull(r.Container(tid), "No orphan container remains.");
            }
            finally { r.Dispose(); }
        }

        // ── Hierarchy naming: layer GameObject is named after its style layer, not the material ────

        [Test]
        public void AddTileLayer_NamesObjectAfterStyleLayer_NotMaterial()
        {
            var (mesh, mat) = FixtureFill();                         // mat.name == "MapView_Fill" (shared, generic)
            // Two layers sharing one material — the GO name must come from the per-layer id, not the mat.
            var r = new GameObjectTileRenderer(new[] { mat, mat }, new[] { "water", "road-primary" });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                r.AddTileLayer(mesh, o, 0, tid);
                r.AddTileLayer(mesh, o, 1, tid);

                Transform container = r.Container(tid);
                Assert.AreEqual("water", container.GetChild(0).name,
                    "Layer GameObject must be named after its style layer id, not the shared material name.");
                Assert.AreEqual("road-primary", container.GetChild(1).name,
                    "Two layers sharing a material must still get distinct, layer-specific names.");
                Assert.AreNotEqual(mat.name, container.GetChild(0).name,
                    "Regression: the GameObject must NOT fall back to the material name when an id is supplied.");
            }
            finally { r.Dispose(); }
        }

        [Test]
        public void AddTileLayer_FallsBackToMaterialName_WhenNoLayerNames()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });       // no names supplied
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double3 o = FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin();
                r.AddTileLayer(mesh, o, 0, tid);
                Assert.AreEqual(mat.name, r.Container(tid).GetChild(0).name,
                    "With no layer names, the GameObject name falls back to the material name (back-compat).");
            }
            finally { r.Dispose(); }
        }

        [Test]
        public void AddTileLayer_AttachesMeshAndMaterial_AsShared()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                r.AddTileLayer(mesh, FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin(), 0, tid);
                Transform layer = r.Container(tid).GetChild(0);

                var mf = layer.GetComponent<MeshFilter>();
                var mr = layer.GetComponent<MeshRenderer>();
                Assert.AreSame(mesh, mf.sharedMesh, "MeshFilter must reference the shared mesh (no clone).");
                Assert.AreSame(mat, mr.sharedMaterial, "MeshRenderer must reference the live shared material (no clone).");
                Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, mr.shadowCastingMode,
                    "Map geometry casts no shadows.");
                Assert.IsFalse(mr.receiveShadows, "Map geometry receives no shadows.");
            }
            finally { r.Dispose(); }
        }

        // ── Floating-origin rebase — same formula as the instanced backends ───────────────────────

        [Test]
        public void Rebuild_SetsAndUpdates_TileLocalToScenePosition()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });
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
                    "Container X must equal TileLocalToScene.x after Rebuild.");
                Assert.That(z0, Is.EqualTo(expected0.z).Within(0.01f),
                    "Container Z must equal TileLocalToScene.z after Rebuild.");

                // Origin shift (a look-at move): position must track the new origin.
                double2 sceneOrigin1 = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 1, X = 1, Y = 1 });
                r.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                var (x1, z1) = r.GetInstanceTranslation(h);
                Assert.That(x1, Is.EqualTo(expected1.x).Within(0.01f),
                    "After an origin shift, X must update to the new TileLocalToScene.x.");
                Assert.That(z1, Is.EqualTo(expected1.z).Within(0.01f),
                    "After an origin shift, Z must update to the new TileLocalToScene.z.");
                Assert.AreNotEqual(x0, x1, "The origin shift must actually move the tile.");
            }
            finally { r.Dispose(); }
        }

        // ── Spawn-position flash regression ───────────────────────────────────────────────────────
        // MapView runs Rebuild BEFORE consuming new tiles, so a layer added after the frame's Rebuild must
        // be created at its correct scene position — NOT at the world origin where it would render for one
        // frame. Rebuild first (seeds the cached origin, as the live loop does), THEN add, and assert the
        // new container is already positioned before any further Rebuild runs.

        [Test]
        public void AddTileLayer_AfterRebuild_PositionedImmediately_NotAtOrigin()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });
            try
            {
                var tid = new TileId { Z = 0, X = 0, Y = 0 };
                double2 tileOrigin  = FloatingOrigin.TileLocalOriginMercator(tid);
                double2 sceneOrigin = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 1, X = 1, Y = 1 });
                float3  expected    = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin);

                // Frame's Rebuild runs first (no items yet) — seeds the scene origin, like MapView.Tick.
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

        // ── Dispose ───────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Dispose_IsIdempotent_AndDestroysGameObjects()
        {
            var (mesh, mat) = FixtureFill();
            var r = new GameObjectTileRenderer(new[] { mat });
            var tid = new TileId { Z = 0, X = 0, Y = 0 };
            r.AddTileLayer(mesh, FloatingOrigin.TileLocalOriginMercator(tid).ToRenderOrigin(), 0, tid);
            Transform root = r.Root;
            Assert.IsNotNull(root, "Root must exist before dispose.");
            GameObject rootGo = root.gameObject;

            r.Dispose();
            Assert.IsTrue(r.IsDisposed, "IsDisposed must be true after Dispose.");
            Assert.IsTrue(rootGo == null, "Dispose must destroy the backend root GameObject (and its children).");
            Assert.IsNull(r.Root, "Root accessor must read null after dispose.");
            Assert.DoesNotThrow(() => r.Dispose(), "Dispose must be idempotent.");
        }
    }

    /// <summary>
    /// GameObject backend wired through the live MapView/TileManager path: selecting RenderBackend.GameObject
    /// constructs the GameObject renderer (and neither instanced backend), consume creates per-tile
    /// containers, and the per-frame InstancedRebuild positions them via FloatingOrigin.TileLocalToScene.
    /// Mirrors MapViewEntitiesBackendTests' wiring tooth. GPU-independent (reads the live transform).
    /// </summary>
    [TestFixture]
    public class MapViewGameObjectBackendTests
    {
        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties MakeCam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""GoTest"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ {
                ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
            } ]
        }");


        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.Tick();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
        }

        [Test]
        public void GameObjectBackend_BuildsContainers_AtTileLocalToScene_AndIsExclusive()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_Go");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.MinZoom = 0; view.Config.MaxZoom = 0; view.WithTestCamera();
            view.Config.MaxBuildsPerTick = 64;
            view.Config.MaxTessellationsPerTick = 64;
            view.Config.Backend = RenderBackend.GameObject;
            try
            {
                var cam0 = MakeCam(0, 0, 0.0);
                view.LoadTestStyle(src, cam0, style: MinimalStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle on the GameObject backend.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built via the GameObject path.");

                var gor = view.GameObjectRenderer();
                Assert.IsNotNull(gor, "GameObject renderer must be constructed when Backend == GameObject.");
                Assert.IsNull(view.BrgRenderer(),
                    "The GameObject backend must NOT construct the BRG renderer (backend selection is exclusive).");
                Assert.IsNull(view.EntitiesRenderer(),
                    "The GameObject backend must NOT construct the Entities renderer (backend selection is exclusive).");
                Assert.Greater(gor.DrawItemCount, 0, "Consume must have created at least one tile-layer GameObject.");
                Assert.Greater(gor.ContainerCount, 0, "Consume must have created at least one per-tile container.");

                // Floating origin: the container position must equal TileLocalToScene(tileOrigin, sceneOrigin).
                // The scene origin of the last pumped frame is the pumped camera's Mercator centre.
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                double2 sceneOrigin0 = cam0.CenterMercator();
                float3  expected0    = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin0);
                Transform container  = gor.Container(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(container, "A container must exist for the built z0/0/0 tile.");
                Assert.That(container.position.x, Is.EqualTo(expected0.x).Within(0.1f),
                    $"Container X must equal TileLocalToScene.x ({expected0.x:F3}).");
                Assert.That(container.position.z, Is.EqualTo(expected0.z).Within(0.1f),
                    $"Container Z must equal TileLocalToScene.z ({expected0.z:F3}).");

                // Origin shift (a look-at move): rebuild and confirm the container tracks the new origin.
                double2 sceneOrigin1 = MakeCam(10, 0, 0.0).CenterMercator();
                gor.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                Assert.That(container.position.x, Is.EqualTo(expected1.x).Within(0.1f),
                    $"After an origin shift, container X must update to {expected1.x:F3}.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
