// Unity EditMode only — uses MonoBehaviour, NativeArray jobs, the live MapView loop.
// NOT included in Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using NIs = NUnit.Framework.Is;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S40 live loop tests for <see cref="MapView"/> with per-layer styled fill rendering.
    /// Covers:
    ///   (1) view-state drives tile selection + eviction releases (container destroyed).
    ///   (2) wiring parity: MapView with a 1-fill-layer style produces the same mesh vertex count
    ///       as StyledFillTileBuilder called directly — proves the live-loop wiring is correct.
    ///       (Replaces the retired S06 bit-identical Burst-vs-managed assertion; that test pinned
    ///        the Burst pipeline which is now replaced by the managed per-layer path.)
    ///   (3) NO per-frame GC in steady state (the acceptance teeth — the ApplyZoom loop and all
    ///       reused buffers must not allocate in the pan / static-frame / bearing-only cases).
    /// </summary>
    [TestFixture]
    public class MapViewLiveLoopTests
    {
        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new LookAtPoint(lon, lat, 0), zoom, 0, 0);

        /// <summary>
        /// Minimal 1-fill-layer style for the live loop tests: a single fill layer over the
        /// "countries" MVT source-layer with a constant red fill color (Constant expression kind).
        /// </summary>
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

        /// <summary>In-memory source that serves the same fixture bytes for ANY tile id.</summary>
        private sealed class FixtureSource : IDataSource
        {
            private readonly byte[] _bytes;
            public int FetchCount;
            public FixtureSource(byte[] bytes) { _bytes = bytes; }
            public TileEncoding Encoding => TileEncoding.Mvt;
            public UniTask<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
            {
                Interlocked.Increment(ref FetchCount);
                return UniTask.FromResult(new TileResponse(_bytes, TileEncoding.Mvt));
            }
            public void Dispose() { }
        }

        /// <summary>Pumps Tick() until every loaded tile has settled or a spin budget is hit.</summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.Tick();
                if (view.LoadedTileCount > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);
            }
        }

        // ── (1) cover drives selection + eviction releases container ───────────────────────────

        [Test]
        public void MapView_CoverDrivesTileSelection_AndEvictionReleases()
        {
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.MinZoom = 5; view.MaxZoom = 5;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            try
            {
                view.Initialise(src, Cam(0, 0, 5.0), ownsSource: false, style: style);
                PumpUntilSettled(view);

                Assert.AreEqual(9, view.LoadedTileCount, "z5 center cover is a 3×3 block");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(5, 16, 16), out _),
                    "the center tile must be built");

                // Pan far east (lon=170) → new cover does NOT overlap the old one.
                view.Camera.Apply(new CameraPropertiesUpdate { Lon = 170, Lat = 0 }, CameraAnimation.Instant);
                PumpUntilSettled(view);

                Assert.AreEqual(9, view.LoadedTileCount, "still a 3×3 cover after panning");
                Assert.IsFalse(view.TryGetBuiltTile(new TileId(5, 16, 16), out _),
                    "the old center tile must have been evicted after the pan");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(5, 31, 16), out _),
                    "the new center tile must be built after the pan");
                // Container destruction: the old tile's Go was DestroyImmediate'd on eviction.
                // Unity's leak detector would fail the run on teardown if any per-tile resources leaked.
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── (2) wiring parity: live loop vs StyledFillTileBuilder direct ──────────────────────

        /// <summary>
        /// The live MapView (1-fill-layer style over the fixture) produces the same mesh vertex count
        /// as a direct call to StyledFillTileBuilder.BuildMesh. Proves the wiring is correct without
        /// pinning the exact Burst vertex layout (which the old bit-identical test did; the new managed
        /// path processes features in the same order as FeatureSelector, so vertex count matches).
        /// </summary>
        [Test]
        public void MapView_GoLive_ProducesSameGeometryAsDirectBuilder()
        {
            byte[] bytes = FixtureBytes();
            var src   = new FixtureSource(bytes);
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            try
            {
                view.Initialise(src, Cam(0, 0, 0.0), ownsSource: false, style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId(0, 0, 0), out var tileGo),
                    "z0/0/0 tile must be built by the live loop");

                // The tile container has one child per fill layer (just 1 in MinimalStyle).
                Assert.AreEqual(1, tileGo.transform.childCount,
                    "The tile container must have exactly 1 child (one fill layer in MinimalStyle).");

                var childMf = tileGo.transform.GetChild(0).GetComponent<MeshFilter>();
                Assert.IsNotNull(childMf, "First child must have a MeshFilter");
                Mesh liveMesh = childMf.sharedMesh;
                Assert.IsNotNull(liveMesh, "First child must have a sharedMesh");

                // Direct builder for the same tile.
                var mvtTile = MapRenderer.Core.Mvt.MvtDecoder.Decode(bytes);
                var fillLayer = style.Layers[0]; // countries-fill
                var paint     = new FillPaint(fillLayer);
                var features  = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(
                    fillLayer, mvtTile, 0.0);
                var mvtLayer  = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(fillLayer, mvtTile);

                Assert.IsNotNull(mvtLayer, "The fixture must contain the 'countries' MVT layer");
                Assert.Greater(features.Count, 0, "FeatureSelector must return at least 1 feature");

                var (bMin, _) = new TileId(0, 0, 0).MercatorBounds();
                Mesh directMesh = StyledFillTileBuilder.BuildMesh(
                    features, paint, 0.0, mvtLayer.Extent, new TileId(0, 0, 0),
                    new double2(bMin.x, bMin.y));

                Assert.IsNotNull(directMesh,
                    "StyledFillTileBuilder.BuildMesh must produce a mesh for the 'countries' layer");

                Assert.AreEqual(directMesh.vertexCount, liveMesh.vertexCount,
                    "Live loop mesh vertex count must equal the direct builder output " +
                    "(parity: same feature set, same pipeline).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── (3) NO per-frame GC in steady state (acceptance teeth) ─────────────────────────────

        [Test]
        public void MapView_SteadyStateTick_DoesNotAllocateGCMemory()
        {
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.MinZoom = 2; view.MaxZoom = 2;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            try
            {
                // Warm up: load the whole cover and let every tile settle.
                view.Initialise(src, Cam(0, 0, 2.0), ownsSource: false, style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime the reused buffers (_cover, _coverSet, _toRelease) to steady capacity.
                view.Camera.Apply(new CameraPropertiesUpdate { Lon = 0.5, Lat = 0.0 }, CameraAnimation.Instant);
                view.Tick();
                view.Camera.Apply(new CameraPropertiesUpdate { Lon = 0.0, Lat = 0.0 }, CameraAnimation.Instant);
                view.Tick();

                // ── (a) THE PAN CASE ──
                // A small center nudge stays within the loaded z2 cover (one z2 tile spans ~10,000 km
                // at the equator, so a 111 km pan loads no new tiles), but it DOES dirty the cover so
                // Tick runs the full recompute: TileCover.Cover + _coverSet rebuild + request/release
                // scan + floating-origin rebase loop + ApplyZoom loop. All must allocate ZERO bytes.
                view.Camera.Apply(new CameraPropertiesUpdate { Lon = 1.0, Lat = 0.0 }, CameraAnimation.Instant);
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "MapView.Tick must not allocate during a within-cover pan (cover recompute path: " +
                    "ApplyZoom loop + TileCover.Cover + set rebuild + request/release scan + rebase). " +
                    "A failure means a per-frame List/Task/closure/LINQ leaked into the hot path.");

                Assert.AreEqual(9, view.LoadedTileCount,
                    "the within-cover pan must not have loaded new tiles");

                // ── (b) the fully-static frame also early-outs allocation-free. ──
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "A static frame (cover clean, nothing pending) must early-out with zero allocation.");

                // ── (c) a heading/tilt-only change is camera-only → no cover dirty → alloc-free. ──
                view.Camera.Apply(new CameraPropertiesUpdate { Heading = 45.0, Tilt = 30.0 }, CameraAnimation.Instant);
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "A heading/tilt-only camera change must not dirty the cover, so Tick stays alloc-free.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
