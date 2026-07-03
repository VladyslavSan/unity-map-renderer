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
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
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
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

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


        /// <summary>Pumps Tick() until every loaded tile has settled or a spin budget is hit.</summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.Tick();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);
            }
        }

        // ── (1) cover drives selection + eviction releases container ───────────────────────────

        [Test]
        public void MapView_CoverDrivesTileSelection_AndEvictionReleases()
        {
            var src   = TestDataSource.FromBytes(FixtureBytes());
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.MinZoom = 5; view.Config.MaxZoom = 5;
            view.Config.PadTiles = 0; view.WithTestCamera();
            view.Config.MaxBuildsPerTick = 64;
            view.Config.MaxTessellationsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                PumpUntilSettled(view);

                // S71: the cover now tracks the framing viewport span (no longer a magic 3×3); assert the
                // behaviour (center built, far pan evicts + re-covers), not a frozen count.
                Assert.Greater(view.LoadedTileCount(), 0, "z5 center cover must be non-empty");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 5, X = 16, Y = 16 }),
                    "the center tile must be built");

                // Pan far east (lon=170) → new cover does NOT overlap the old one.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170, Latitude = 0 });
                PumpUntilSettled(view);

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
            var src   = TestDataSource.FromBytes(bytes);
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.MinZoom = 0; view.Config.MaxZoom = 0;
            view.Config.PadTiles = 0; view.WithTestCamera();
            view.Config.MaxBuildsPerTick = 64;
            view.Config.MaxTessellationsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built by the live loop");

                // Backend-agnostic: one Mesh per fill layer (just 1 in MinimalStyle).
                Mesh[] liveMeshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(liveMeshes, "The built tile must expose its layer meshes.");
                Assert.AreEqual(1, liveMeshes.Length,
                    "The tile must have exactly 1 layer mesh (one fill layer in MinimalStyle).");
                Mesh liveMesh = liveMeshes[0];
                Assert.IsNotNull(liveMesh, "The fill layer must have a built mesh");

                // Direct builder for the same tile.
                var mvtTile = MapRenderer.Core.Mvt.MvtDecoder.Decode(bytes);
                var fillLayer = style.Layers[0]; // countries-fill
                var paint     = new Fill.PaintProperties(fillLayer);
                var features  = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(
                    fillLayer, mvtTile, 0.0);
                var mvtLayer  = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(fillLayer, mvtTile);

                Assert.IsNotNull(mvtLayer, "The fixture must contain the 'countries' MVT layer");
                Assert.Greater(features.Count, 0, "FeatureSelector must return at least 1 feature");

                Mesh directMesh = TestTileMeshBuilder.BuildFill(
                    features, paint, 0.0, mvtLayer.Extent, new TileId { Z = 0, X = 0, Y = 0 });

                Assert.IsNotNull(directMesh,
                    "StyledFillTileBuilder.BuildMesh must produce a mesh for the 'countries' layer");

                Assert.AreEqual(directMesh.vertexCount, liveMesh.vertexCount,
                    "Live loop mesh vertex count must equal the direct builder output " +
                    "(parity: same feature set, same pipeline).");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── (3) NO per-frame GC in steady state (acceptance teeth) ─────────────────────────────
        // Zero-allocation per-frame is the BRG backend's contract. The Entities default does allocate GC
        // intermittently over many frames (verified by MapView_SteadyStateTick_Entities_AllocationVerdict
        // below, via the same GC.Alloc-recorder instrument) — magnitude unverified; the once-quoted
        // "~409 B/frame" came from a since-debunked GC.GetTotalMemory measure. Pin BRG: this asserts a BRG property.

        [Test]
        public void MapView_SteadyStateTick_DoesNotAllocateGCMemory()
        {
            var src   = TestDataSource.FromBytes(FixtureBytes());
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.Backend = RenderBackend.Brg; // zero-alloc path under test
            view.Config.MinZoom = 2; view.Config.MaxZoom = 2;
            view.Config.PadTiles = 0; view.WithTestCamera();
            view.Config.MaxBuildsPerTick = 64;
            view.Config.MaxTessellationsPerTick = 64;

            try
            {
                // Warm up: load the whole cover and let every tile settle.
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime the reused buffers (_cover, _coverSet, _toRelease) to steady capacity.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.5, Latitude = 0.0 });
                view.Tick();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.Tick();

                // ── (a) THE PAN CASE ──
                // A small center nudge stays within the loaded z2 cover (one z2 tile spans ~10,000 km
                // at the equator, so a 111 km pan loads no new tiles), but it DOES dirty the cover so
                // Tick runs the full recompute: TileCover.Cover + _coverSet rebuild + request/release
                // scan + floating-origin rebase loop + ApplyZoom loop. All must allocate ZERO bytes.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 1.0, Latitude = 0.0 });
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "MapView.Tick must not allocate during a within-cover pan (cover recompute path: " +
                    "ApplyZoom loop + TileCover.Cover + set rebuild + request/release scan + rebase). " +
                    "A failure means a per-frame List/Task/closure/LINQ leaked into the hot path.");

                Assert.AreEqual(16, view.LoadedTileCount(),
                    "z2 cover is the whole world (4×4); a within-cover pan loads no new tiles");

                // ── (b) the fully-static frame also early-outs allocation-free. ──
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "A static frame (cover clean, nothing pending) must early-out with zero allocation.");

                // ── (c) a heading/tilt change still ticks alloc-free. ──
                // S71: heading now DIRTIES the cover (it rotates the viewport quad → a different tile bbox),
                // so this Tick runs the full recompute — but at the whole-world z2 cover the re-selected set
                // is identical, so request/release find nothing and the recompute stays zero-alloc. (Tilt is
                // not in the cover key — the selector has no tilt branch, D3.)
                view.Camera.Apply(new CameraPropertiesUpdate { Heading = 45.0, Tilt = 30.0 });
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "A heading/tilt change must tick alloc-free (cover recompute over an unchanged whole-world set).");

                // ── (d) AT SCALE: zero-alloc must hold over MANY frames, not just one. ──
                // This is the symmetric counterpart to MapView_SteadyStateTick_Entities_AllocationVerdict,
                // which trips the GC.Alloc recorder over N Ticks while a single Entities Tick is clean. BRG
                // must stay clean at the SAME N — otherwise the divergence would be shared-Tick-path churn,
                // not EG-specific. (Same N as the Entities verdict so the comparison is genuine.)
                const int N = 50;
                Assert.That(() => { for (int i = 0; i < N; i++) view.Tick(); }, Is.Not.AllocatingGCMemory(),
                    $"BRG.Tick must not allocate across {N} steady-state frames — proving the zero-alloc " +
                    "contract holds at the scale where the Entities backend trips the recorder.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── (3b) Entities backend allocation VERDICT (informational, not a hard tooth) ──────────────
        // Mirrors the BRG test above exactly — same within-cover pan on the same loaded cover, same
        // GC.Alloc-recorder instrument (Is.Not.AllocatingGCMemory) — but with the default Entities backend.
        // This is the apples-to-apples answer to "do the EG system groups allocate in the live Tick path?"
        // (the claim the BRG pin is justified on). It REPORTS the verdict rather than asserting zero, because
        // whether Entities-allocates-per-frame is a measured trade-off, not a contract. The figure the
        // constraint carries is a COUNT of GC.Alloc calls, not bytes — reported as such.
        [Test]
        public void MapView_SteadyStateTick_Entities_AllocationVerdict()
        {
            var src   = TestDataSource.FromBytes(FixtureBytes());
            var go    = new GameObject("MapView");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.Backend = RenderBackend.Entities; // the default backend under measurement
            view.Config.MinZoom = 2; view.Config.MaxZoom = 2;
            view.Config.PadTiles = 0; view.WithTestCamera();
            view.Config.MaxBuildsPerTick = 64;
            view.Config.MaxTessellationsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 2.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // Prime reused buffers, identical to the BRG test.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.5, Latitude = 0.0 });
                view.Tick();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 0.0, Latitude = 0.0 });
                view.Tick();

                // Same within-cover pan as BRG case (a): full cover recompute, no new tiles loaded.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 1.0, Latitude = 0.0 });
                view.Tick(); // consume the pan; now steady.
                Assert.AreEqual(16, view.LoadedTileCount(),
                    "z2 cover is the whole world (4×4); a within-cover pan loads no new tiles");

                // Measure over MANY frames, not one. A single Entities Tick is alloc-free, but EG's system
                // groups allocate INTERMITTENTLY (the isolated Rebuild×50 test trips the recorder) — so a
                // 1-frame sample under-reports. Looping N static Ticks (InstancedRebuild fires every frame)
                // is the honest "does sitting still leak GC over time" characterization of the live path.
                const int N = 50;
                string verdict;
                try
                {
                    Assert.That(() => { for (int i = 0; i < N; i++) view.Tick(); },
                                Is.Not.AllocatingGCMemory());
                    verdict = $"NO GC allocation across {N} steady-state Ticks";
                }
                catch (AssertionException)
                {
                    // Trips on ≥1 GC.Alloc sampler call over the run; the constraint's byte/count actual
                    // prints blank here, so report the trip (intermittent: a single Tick does not trip).
                    verdict = $"ALLOCATES across {N} Ticks (GC.Alloc recorder tripped; intermittent)";
                }
                TestContext.WriteLine($"[S53b alloc] MapView.Tick (Backend=Entities) steady-state: {verdict}");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
