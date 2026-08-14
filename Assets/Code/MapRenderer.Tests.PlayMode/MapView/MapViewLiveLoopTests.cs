// PlayMode half of the S40 live-loop tests — the cover-drives-selection + eviction-releases tooth, which
// settles the async fetch/build over real frames (yield, never Thread.Sleep). The steady-state GC.Alloc
// verdicts stay in the EditMode half (Is.Not.AllocatingGCMemory measures a region PlayMode's per-frame
// engine allocations would pollute). NOT included in Tools/core-tests.

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.PlayMode.MapViews
{
    [TestFixture]
    public class MapViewLiveLoopTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": {
                        ""fill-color"": [""rgba"", 200, 50, 50, 1]
                    }
                }
            ]
        }");

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    yield break;
                yield return null;
            }
        }

        // ── cover drives selection + eviction releases container ───────────────────────────
        [UnityTest]
        public IEnumerator MapView_CoverDrivesTileSelection_AndEvictionReleases()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                yield return PumpUntilSettled(view);

                // S71: the cover now tracks the framing viewport span (no longer a magic 3×3); assert the
                // behaviour (center built, far pan evicts + re-covers), not a frozen count.
                Assert.Greater(view.LoadedTileCount(), 0, "z5 center cover must be non-empty");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "the center tile must be built");

                // Pan far east (lon=170) → new cover does NOT overlap the old one.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170, Latitude = 0 });
                yield return PumpUntilSettled(view);

                Assert.Greater(view.LoadedTileCount(), 0, "cover must be re-selected (non-empty) after the pan");
                Assert.IsFalse(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "the old center tile must have been evicted after the pan");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 5, X = 31, Y = 16 }),
                    "the new center tile must be built after the pan");
                // Eviction unregisters the tile's instanced draw items (and, on Entities, destroys its
                // layer entities + tile root). Unity's leak detector fails the run on teardown if leaked.
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
