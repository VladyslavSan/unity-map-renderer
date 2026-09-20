// S82 acceptance — PreparedTileCache MapView-integration teeth (real cover→fetch→build→consume→evict
// cycles through TileManager). PlayMode half: the async-settle + cache-hit-counting teeth, which settle over
// real frames (yield, never Thread.Sleep). The mesh-DESTRUCTION-timing / absolute-mesh-count teeth
// (CacheDisabled, CachedMeshes) stay in the EditMode half — Object.Destroy is deferred to end-of-frame in
// PlayMode, so "destroyed immediately" and process-wide FindObjectsOfTypeAll<Mesh> counts don't hold here.
//
// Every test uses interp-fill-style.json (a zoom-`interpolate` fill-color) as its style fixture.
// Post-Stage-1, a zoom-`interpolate` fill-color bakes vColor white and rides `_BaseColor` — same as a
// constant style — so this fixture is no longer a defence against a vertex-color vacuity trap; none of
// these teeth read vertex color, so nothing here is lost.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Geometry;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.PlayMode.Tiles
{
    [TestFixture]
    public class PreparedCacheTests
    {
        // z=4 tile containing (lon=10, lat=10) — verified to sit comfortably inside a tile, away from any
        // tile-boundary floating-point edge case.
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        private static StyleDocument InterpFillStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return StyleParser.Parse(File.ReadAllText(path));
        }

        /// <summary>Composite (zoom + feature) fill-color — unlike <see cref="InterpFillStyle"/>'s pure
        /// Zoom-kind expression, this still bakes a per-feature COLOR stream after Stage 1's fill-color
        /// carrier split, so <see cref="ZoomBake_AtIdZ_NotStaleCamZoom"/>'s "bake happens at id.Z, not the
        /// fractional camera zoom" claim stays observable on the mesh.</summary>
        private static StyleDocument InterpFillCompositeStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-composite-style.json");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return StyleParser.Parse(File.ReadAllText(path));
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2000)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    yield break;
                yield return null;
            }
        }

        /// <summary>
        /// Independent ground truth: the uniform baked vertex color a FRESH <see cref="SyncMeshWrite.Fill"/>
        /// call produces at <paramref name="zoom"/>, built directly from Core (never TileManager/MapView).
        /// </summary>
        private static Color GroundTruthColorAtZoom(byte[] mvtBytes, StyleDocument style, TileId id, double zoom)
        {
            using var mvtTile = MvtDecoder.Decode(id, mvtBytes);
            var fillLayer = style.Layers[0];
            var paint     = ((Fill.StyleLayer)fillLayer).Paint;
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "Fixture must contain a resolvable MVT layer.");
            var features  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, zoom);
            Assert.Greater(features.Count, 0, "Fixture must produce >=1 feature (non-vacuous ground truth).");

            var (bMin, _) = id.MercatorBounds();
            var tileOrigin = new double3(bMin.x, 0.0, bMin.y);

            var mda = MeshDataPayload.AllocateTracked(1);
            TileGeometryBuffers geometry = mvtLayer.Geometry; // BORROWED (IR C1 P3) — the tile owns it
            int vc; Bounds b;
            SyncMeshWrite.Fill(mda[0], features, geometry, paint, zoom, tileOrigin, out vc, out b);
            Assert.Greater(vc, 0, "Ground-truth build must produce geometry.");

            var payload = new MeshDataPayload(mda, vc, b, "ground-truth", materialIndex: 0);
            Mesh mesh = payload.Upload();
            Color c0 = FirstVertexColor(mesh);
            Object.DestroyImmediate(mesh);
            return c0;
        }

        private static Color FirstVertexColor(Mesh mesh)
        {
            var colors = new List<Color>();
            mesh.GetColors(colors);
            Assert.Greater(colors.Count, 0, "Mesh must have vertex colors.");
            return colors[0];
        }

        private static bool ColorsClose(Color a, Color b, float eps)
            => Mathf.Abs(a.r - b.r) < eps && Mathf.Abs(a.g - b.g) < eps && Mathf.Abs(a.b - b.b) < eps;

        private static void AssertColorsClose(Color expected, Color actual, float eps, string message)
        {
            Assert.AreEqual(expected.r, actual.r, eps, $"{message} (R: expected={expected.r:F4} actual={actual.r:F4})");
            Assert.AreEqual(expected.g, actual.g, eps, $"{message} (G: expected={expected.g:F4} actual={actual.g:F4})");
            Assert.AreEqual(expected.b, actual.b, eps, $"{message} (B: expected={expected.b:F4} actual={actual.b:F4})");
        }

        // ── Stall #3: EG mesh registrations balance across the prepared-cache round-trip ─────────────
        [UnityTest]
        public IEnumerator Stall3_MeshRegistrations_StableAcrossCacheRoundTrips_Entities()
        {
            var style = InterpFillStyle();
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_S82_RegBalance");
            var view  = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.Backend               = RenderBackend.Entities; // the ID-route path under test
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // uncapped — synchronous whole-cover eviction each move

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                yield return PumpUntilSettled(view);
                int rNear = view.RegisteredMeshCount();
                Assert.Greater(rNear, 0, "loading the near cover must register >=1 EG mesh (Entities backend).");

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                    yield return PumpUntilSettled(view);
                    view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                    yield return PumpUntilSettled(view);
                    Assert.AreEqual(rNear, view.RegisteredMeshCount(),
                        $"cycle {cycle}: EG mesh registrations must return to the post-load count. A drift means a " +
                        "RegisterMesh on the cache-revisit path is not balanced by an UnregisterMesh on eviction.");
                }
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── THE decisive revisit ───────────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator Revisit_ServesCached_PixelIdentical()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_Revisit");
            var view     = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be built on first visit.");

                Assert.Greater(view.PreparedCacheMisses(), 0,
                    "Positive control: the first visit must register >=1 PreparedTileCache MISS.");

                Mesh[] beforeEvict = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(beforeEvict);
                Assert.GreaterOrEqual(beforeEvict.Length, 1, "First prepare must produce >=1 mesh.");
                Mesh  originalMesh = beforeEvict[0];
                Color colorBefore  = FirstVertexColor(originalMesh);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                yield return PumpUntilSettled(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate(); // the recompute (cover diff + probe) runs in THIS tick
                int kicksOnRevisitTick = view.TileBuildsStartedLastTick();
                int hitsOnRevisitTick  = view.PreparedCacheHits();
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be re-built (from cache) on revisit.");

                Assert.AreEqual(0, kicksOnRevisitTick,
                    "DECISIVE: the revisit tick must issue ZERO mesh build kicks.");
                Assert.Greater(hitsOnRevisitTick, 0,
                    "DECISIVE: the revisit tick must register >=1 PreparedCacheHit.");

                Mesh[] afterRevisit = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(afterRevisit);
                Mesh revisitMesh = afterRevisit[0];

                Assert.AreSame(originalMesh, revisitMesh,
                    "A cache hit must hand back the SAME Mesh object the original prepare built.");

                Color colorAfter = FirstVertexColor(revisitMesh);
                AssertColorsClose(colorBefore, colorAfter, 1e-4f,
                    "Revisit content must be pixel-identical to the original fresh prepare.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Zoom-bake soundness (branch A: bake at id.Z) ───────────────────────────────────────
        [UnityTest]
        public IEnumerator ZoomBake_AtIdZ_NotStaleCamZoom()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillCompositeStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_ZoomBake");
            var view     = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — synchronous whole-cover eviction+transfer

            const double Z1 = 4.2, Z2 = 4.8;

            try
            {
                Color truthAtIdZ = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: 4.0);
                Color truthAtZ1  = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: Z1);
                Color truthAtZ2  = GroundTruthColorAtZoom(bytes, style, TrackedTile, zoom: Z2);

                Assert.IsFalse(ColorsClose(truthAtIdZ, truthAtZ1, 1e-3f),
                    "Fixture must be zoom-sensitive enough that the id.Z bake differs from a Z1 bake (non-vacuous).");
                Assert.IsFalse(ColorsClose(truthAtIdZ, truthAtZ2, 1e-3f),
                    "Fixture must be zoom-sensitive enough that the id.Z bake differs from a Z2 bake (non-vacuous).");

                view.LoadTestStyle(src, Cam(10, 10, Z1), style: style);
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));

                Color colorAtZ1Visit = FirstVertexColor(view.GetTileMeshes(TrackedTile)[0]);
                AssertColorsClose(truthAtIdZ, colorAtZ1Visit, 1e-4f,
                    "First prepare must bake at id.Z (4.0), NOT the fractional cam.Zoom (Z1=4.2) — Decision 2 option A.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                yield return PumpUntilSettled(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0, Zoom = Z2 });
                view.LateUpdate();
                int kicksOnRevisit = view.TileBuildsStartedLastTick();
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Assert.AreEqual(0, kicksOnRevisit,
                    "The revisit at a DIFFERENT fractional camera zoom (Z2=4.8) must still be a cache HIT (no re-kick).");

                Color colorAtZ2Revisit = FirstVertexColor(view.GetTileMeshes(TrackedTile)[0]);
                AssertColorsClose(truthAtIdZ, colorAtZ2Revisit, 1e-4f,
                    "Cached render at Z2 must equal a fresh prepare AT id.Z — proving the bake is sound across a " +
                    "fractional camera-zoom change, not frozen at the stale Z1 the tile happened to first load at.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── NativeArray invariant preserved (DebugLiveAllocCount is a code-maintained counter, not a
        //    Unity-object scan — safe under PlayMode's deferred Object.Destroy) ─────────────────────
        [UnityTest]
        public IEnumerator NativeArrayInvariant_AfterLoadEvictRevisit()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go       = new GameObject("MapView_S82_NativeArrayInvariant");
            var view     = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // stall #2: uncapped — synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "After the first (real) prepare, NativeArrays must already be back at baseline.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                yield return PumpUntilSettled(view);
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "The cache stores Mesh, never NativeArrays — must remain at baseline while a tile is cached.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                // TileBuildsStartedLastTick counts tiles admitted, not budget units — job-scheduling-design.md
                // §11 fork 2 deleted the two-units-per-tile charge this assertion used to ride on top of. It
                // no longer catches a write kick on a DIFFERENT cover tile during this same Tick (started <=
                // the old kicked count, always); it still asserts exactly what this test claims — a revisit
                // is a hit, not a re-prepare.
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "Revisit must be a hit, not a re-prepare.");
                yield return PumpUntilSettled(view);
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    "After a cache-hit revisit, NativeArray count must remain at baseline.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }

            Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                "After full Teardown, NativeArray count must return to baseline.");
        }
    }
}
