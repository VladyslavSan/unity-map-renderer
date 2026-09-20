// S53b increment 2 — Entities backend wired through MapView/TileManager.
//
// Proves the live path (not just the EntitiesTileRenderer unit): selecting RenderBackend.Entities
// constructs the backend, ConsumeMeshBuild creates one entity per tile-layer, and the per-frame
// InstancedRebuild positions them via FloatingOrigin.TileLocalToScene. GPU-independent (reads the
// entity's LocalToWorld translation), mirroring BrgBackendSnapshotTests' floating-origin tooth.

using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class MapViewEntitiesBackendTests
    {
        private static CameraProperties MakeCam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""EntTest"",
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
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
            }
        }

        // ── Backend selection is exclusive: selecting BRG constructs no Entities renderer ───────

        [Test]
        public void BrgBackend_DoesNotConstructEntitiesRenderer()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_EntOff");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Brg; // S53c: default is Entities; pin BRG to prove exclusivity.
            try
            {
                view.LoadTestStyle(src, MakeCam(0, 0, 0.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsNull(view.EntitiesRenderer(),
                    "The BRG backend must NOT construct the Entities renderer (backend selection is exclusive).");
                Assert.IsNull(view.GameObjectRenderer(),
                    "The BRG backend must NOT construct the GameObject renderer (backend selection is exclusive).");
                Assert.IsNotNull(view.BrgRenderer(),
                    "The BRG backend must construct the BrgTileRenderer.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Wiring + floating origin: consume creates entities; rebuild positions them ──────────

        [Test]
        public void EntitiesBackend_BuildsEntities_AtTileLocalToScene()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_Ent");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Entities;
            try
            {
                var cam0 = MakeCam(0, 0, 0.0);
                view.LoadTestStyle(src, cam0, style: MinimalStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle on the Entities backend.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built via the Entities path.");

                var ent = view.EntitiesRenderer();
                Assert.IsNotNull(ent, "Entities renderer must be constructed when Backend == Entities.");
                Assert.IsNull(view.GameObjectRenderer(),
                    "The Entities backend must NOT construct the GameObject renderer (backend selection is exclusive).");
                Assert.Greater(ent.DrawItemCount(), 0,
                    "ConsumeMeshBuild must have created at least one tile-layer entity.");

                // Floating origin: the entity translation must equal TileLocalToScene(tileOrigin, sceneOrigin).
                double2 tileOrigin   = FloatingOrigin.TileLocalOriginMercator(new TileId { Z = 0, X = 0, Y = 0 });
                double2 sceneOrigin0 = cam0.CenterMercator();
                ent.Rebuild(SceneFrame.Mercator(sceneOrigin0));
                float3 expected0 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin0);

                bool found = false;
                for (int h = 0; h < ent.DrawItemCount() + 100; h++)
                {
                    var (tx, tz) = ent.GetInstanceTranslation(h);
                    if (float.IsNaN(tx)) continue;
                    Assert.That(tx, Is.EqualTo(expected0.x).Within(0.1f),
                        $"Entity translation X must equal TileLocalToScene.x ({expected0.x:F3}). Got {tx:F3}.");
                    Assert.That(tz, Is.EqualTo(expected0.z).Within(0.1f),
                        $"Entity translation Z must equal TileLocalToScene.z ({expected0.z:F3}). Got {tz:F3}.");
                    found = true;
                    break;
                }
                Assert.IsTrue(found, "At least one entity must have a valid (non-NaN) translation.");

                // Origin shift (look-at move): translation must track the new origin.
                double2 sceneOrigin1 = MakeCam(10, 0, 0.0).CenterMercator();
                ent.Rebuild(SceneFrame.Mercator(sceneOrigin1));
                float3 expected1 = FloatingOrigin.TileLocalToScene(tileOrigin, sceneOrigin1);
                bool updated = false;
                for (int h = 0; h < ent.DrawItemCount() + 100; h++)
                {
                    var (tx, tz) = ent.GetInstanceTranslation(h);
                    if (float.IsNaN(tx)) continue;
                    Assert.That(tx, Is.EqualTo(expected1.x).Within(0.1f),
                        $"After an origin shift, entity X must update to {expected1.x:F3}. Got {tx:F3}.");
                    updated = true;
                    break;
                }
                Assert.IsTrue(updated, "After an origin shift, the entity translation must update.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── GPU pixel render through the full MapView path ──────────────────────────────────────

        [Test]
        public void EntitiesBackend_RendersFill_ThroughMapView()
        {
            const int SnapW = 512, SnapH = 512;
            var  bgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            const byte BgR8 = 26, BgG8 = 28, BgB8 = 38;
            const float MinFill = 0.05f, MaxFill = 0.95f;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var mapGo = new GameObject("MapView_EntPixel");
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 3; view.Config.TileSelection.MaxZoom = 3; view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = RenderBackend.Entities;

            var lightGo = new GameObject("EntPixelLight");
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var cameraGo = new GameObject("EntPixelCam");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor; camera.enabled = false; camera.farClipPlane = 1e9f;

            var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 3.0, 0, 0),
                    style: MinimalStyle());
                for (int f = 0; f < 2500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                { view.LateUpdate(); view.DrainMeshBuilds(); }
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "Entities path must load + settle tiles.");
                view.LateUpdate(); // staleness: one more frame so the last tile's entity is positioned + uploaded

                float tileSizeZ3 = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 3));
                var ent = view.EntitiesRenderer();
                Assert.IsNotNull(ent, "Entities renderer must be present.");
                Bounds b = ent.ComputeSceneBounds(tileSizeZ3);
                Assert.Greater(b.size.magnitude, 0f, "Entities scene bounds must be non-degenerate.");

                camera.transform.position = new Vector3(b.center.x, 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize   = Mathf.Max(b.size.x, b.size.z) * 0.55f;

                snap.Render(camera);
                snap.WritePng("entities-backend-fill.png");

                var verdict = SnapshotCoverage.Analyse(snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                TestContext.WriteLine($"[EntitiesBackend] filled={verdict.FilledFraction:P1} blank={verdict.IsBlank}");

                if (verdict.IsBlank)
                {
                    // Blank render: distinguish no-GPU (Inconclusive) from a real failure (GPU present).
                    var blankGo  = new GameObject("EntBlankCam");
                    var blankCam = blankGo.AddComponent<Camera>();
                    blankCam.orthographic = true; blankCam.clearFlags = CameraClearFlags.SolidColor;
                    blankCam.backgroundColor = bgColor; blankCam.enabled = false;
                    using var blankSnap = new SnapshotRenderer(SnapW, SnapH);
                    try
                    {
                        blankSnap.Render(blankCam);
                        if (blankSnap.IsAllBlack())
                            Assert.Inconclusive(
                                "Entities render is blank and blank-control is all-black: no GPU context " +
                                "in batch EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                    }
                    finally { Object.DestroyImmediate(blankGo); }

                    Assert.Fail("Entities render is blank but GPU is present (blank-control non-black) — " +
                                "the Entities backend produced no visible pixels through MapView.");
                }

                Assert.That(verdict.FilledFraction, Is.InRange(MinFill, MaxFill),
                    "Entities fill fraction through MapView must be in a sane band.");
            }
            finally
            {
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                QualitySettings.SetQualityLevel(prevQuality, false);
                snap.Dispose();
                view.Teardown(); // dispose the Entities World so EG's BRG stops drawing into later tests
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(mapGo);
            }
        }
    }
}
