// MapView-loop tile-pipeline GPU/visual acceptance test. Not merged with TileSeamSnapshotTests: bare
// `CameraProperties` here is Core.Geo's, and that file imports UnityEngine.Rendering (CS0104).
//
// Contents:
//   MapViewSnapshotTests  — visual proof: the LIVE multi-tile loop (MapView) renders a non-blank fill cover.

using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MapViewSnapshotTests — visual proof
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Visual proof: the LIVE multi-tile loop (<see cref="MapView"/>) renders a non-blank fill cover.
    /// The geometry is the committed fixture replayed on every tile, not a real multi-zoom basemap.
    /// <c>FloatingOrigin_ExtremeMercator_RenderCoordsBounded_SubMillimetre</c> covers pan/tilt jitter. The
    /// camera frames the REBASED render-space bounds of the loaded tiles, not the raw Mercator extent.
    /// </summary>
    [TestFixture]
    public class MapViewSnapshotTests : BaseTestFixture
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);
        private const float MinFill = 0.05f, MaxFill = 0.95f;
        private const int   MinBuckets = 4;


        // Minimal one-fill-layer style for snapshot testing (constant red, countries source-layer).
        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""SnapTest"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 80, 80, 1] }
                }
            ]
        }");

        [Test]
        public void MapViewLiveLoop_RendersMultiTileFill_NonBlank()
        {
            string fixturePath = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            FileAssert.Exists(fixturePath);
            var src = TestDataSource.FromBytes(File.ReadAllBytes(fixturePath));

            var mapGo = Track(new GameObject("MapView"));
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 3; view.Config.TileSelection.MaxZoom = 3;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            // Light so the URP Lit fill is bright enough for coverage.
            var lightGo = Track(new GameObject("SceneLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var cameraGo = Track(new GameObject("SnapCam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BgColor;
            camera.enabled = false;
            camera.farClipPlane = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                view.LoadTestStyle(src, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 3.0, 0, 0), style: MinimalStyle());
                // Non-obvious why: AwaitInFlightMeshBuilds, not DrainMeshBuilds. Entities Graphics needs
                // more Rebuild ticks after the last consume before its BRG batch is cullable (see
                // VisualScene.WarmupFrames). This loop keeps one real LateUpdate per iteration; a forced
                // drain settles in ~1 tick and renders BLANK.
                for (int f = 0; f < 2500 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                {
                    view.LateUpdate();
                    view.AwaitInFlightMeshBuilds();
                }
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "live loop must load + build the tile cover");

                // Frame the camera on the render-space bounds of the loaded tiles (backend-agnostic;
                // the instanced backends create no GameObjects to bound). tileSize = world extent at z3.
                float tileSize = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, 3));
                Bounds b = view.ComputeSceneBounds(tileSize);
                Assert.Greater(b.size.magnitude, 0f, "loaded tiles must have non-degenerate bounds");
                camera.transform.position = new Vector3(b.center.x, b.center.y + 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // top-down
                camera.orthographicSize   = Mathf.Max(b.size.x, b.size.z) * 0.55f;

                snap.Render(camera);
                string pngPath = snap.WritePng("mapview-live-cover.png");
                FileAssert.Exists(pngPath);
                Debug.Log($"[MapViewSnapshot] wrote {pngPath}, tiles={view.LoadedTileCount()}");

                var verdict = SnapshotCoverage.Analyse(snap.Pixels, Bg32);
                Debug.Log($"[MapViewSnapshot] filled={verdict.FilledFraction:P1} buckets={verdict.DistinctRegionBucketsHit}/64 blank={verdict.IsBlank}");

                Assert.IsFalse(verdict.IsBlank, "live multi-tile fill must not be blank");
                Assert.That(verdict.FilledFraction, NUnit.Framework.Is.InRange(MinFill, MaxFill),
                    "live fill fraction must be within a sane band (geometry visible, not full-frame)");
                Assert.That(verdict.DistinctRegionBucketsHit,
                    NUnit.Framework.Is.GreaterThanOrEqualTo(MinBuckets),
                    "the fill must be spatially spread across multiple tiles, not one corner");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }
    }
}
