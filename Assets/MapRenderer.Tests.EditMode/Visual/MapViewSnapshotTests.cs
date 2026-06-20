using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.Imaging;
using MapRenderer.Unity;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S06 visual proof: the LIVE multi-tile loop (<see cref="MapView"/>) renders a non-blank fill cover.
    /// The geometry is the committed fixture replayed across the cover (synthetic, same shape per tile) —
    /// this proves the go-live loop produces visible meshes through the jobified pipeline; it is NOT a real
    /// multi-zoom basemap. Pan/tilt jitter is proven separately by the Core
    /// <c>FloatingOrigin_ExtremeMercator_RenderCoordsBounded_SubMillimetre</c> assertion, not by eyeballs.
    ///
    /// The camera is framed on the REBASED render-space bounds of the loaded tiles (near the local origin
    /// after floating-origin rebasing), not the raw ~world-scale Mercator extent.
    /// </summary>
    [TestFixture]
    public class MapViewSnapshotTests
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte BgR8 = 26, BgG8 = 28, BgB8 = 38;
        private const float MinFill = 0.05f, MaxFill = 0.95f;
        private const int   MinBuckets = 4;

        private sealed class FixtureSource : IDataSource
        {
            private readonly byte[] _bytes;
            public FixtureSource(byte[] b) { _bytes = b; }
            public TileEncoding Encoding => TileEncoding.Mvt;
            public System.Threading.Tasks.Task<TileResponse> FetchAsync(TileId id, System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(new TileResponse(_bytes, TileEncoding.Mvt));
            public void Dispose() { }
        }

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
            Assert.IsTrue(File.Exists(fixturePath), $"Fixture missing: {fixturePath}");
            var src = new FixtureSource(File.ReadAllBytes(fixturePath));

            var mapGo = new GameObject("MapView");
            var view  = mapGo.AddComponent<MapView>();
            view.MinZoom = 3; view.MaxZoom = 3;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            // Light so the URP Lit fill is bright enough for coverage.
            var lightGo = new GameObject("SceneLight");
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var cameraGo = new GameObject("SnapCam");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BgColor;
            camera.enabled = false;
            camera.farClipPlane = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                view.Initialise(src, new ViewState(0, 0, 3.0), ownsSource: false, style: MinimalStyle());
                // Pump to settle all tiles.
                for (int f = 0; f < 500 && !(view.LoadedTileCount > 0 && view.AllTilesSettled()); f++)
                {
                    view.Tick();
                    Thread.Sleep(1);
                }
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount > 0,
                    "live loop must load + build the tile cover");

                // Frame the camera on the combined render-space bounds of the loaded tile meshes.
                Bounds b = ComputeChildBounds(mapGo);
                Assert.Greater(b.size.magnitude, 0f, "loaded tiles must have non-degenerate bounds");
                camera.transform.position = new Vector3(b.center.x, b.center.y + 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // top-down
                camera.orthographicSize   = Mathf.Max(b.size.x, b.size.z) * 0.55f;

                snap.Render(camera);
                string pngPath = snap.WritePng("mapview-live-cover.png");
                Assert.IsTrue(File.Exists(pngPath), $"PNG must exist: {pngPath}");
                Debug.Log($"[MapViewSnapshot] wrote {pngPath}, tiles={view.LoadedTileCount}");

                if (snap.IsAllBlack())
                {
                    Assert.Inconclusive("Live-cover render all-black → no GPU context in batchmode. " +
                                        "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                    return;
                }

                var verdict = SnapshotCoverage.Analyse(snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
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
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(mapGo);
            }
        }

        /// <summary>Combined world-space bounds of every child MeshRenderer under <paramref name="root"/>.</summary>
        private static Bounds ComputeChildBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>();
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }
    }
}
