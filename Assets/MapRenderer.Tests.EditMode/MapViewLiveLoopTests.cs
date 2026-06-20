// Unity EditMode only — uses MonoBehaviour, NativeArray jobs, the live MapView loop.
// NOT included in Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using NIs = NUnit.Framework.Is;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.View;
using MapRenderer.Jobs;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S06 Batch C: the live multi-tile render loop (<see cref="MapView"/>) going live on the S04
    /// jobified pipeline. Covers:
    ///   (1) view-state drives tile selection + eviction releases/disposes (no leaked NativeArray),
    ///   (2) the go-live path produces the SAME geometry as a direct pipeline call (parity),
    ///   (3) NO per-frame GC in steady state (the acceptance teeth — closes the deferred follow-up).
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

        /// <summary>In-memory source that serves the same fixture bytes for ANY tile id.</summary>
        private sealed class FixtureSource : IDataSource
        {
            private readonly byte[] _bytes;
            public int FetchCount;
            public FixtureSource(byte[] bytes) { _bytes = bytes; }
            public TileEncoding Encoding => TileEncoding.Mvt;
            public Task<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
            {
                Interlocked.Increment(ref FetchCount);
                return Task.FromResult(new TileResponse(_bytes, TileEncoding.Mvt));
            }
            public void Dispose() { }
        }

        /// <summary>Pumps Tick() until every loaded tile has settled (built) or a spin budget is hit.</summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.Tick();
                if (view.LoadedTileCount > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);   // let threadpool fetch continuations run
            }
        }

        // ── (1) cover drives selection + eviction disposes ─────────────────────────────────────

        [Test]
        public void MapView_CoverDrivesTileSelection_AndEvictionReleases()
        {
            var src  = new FixtureSource(FixtureBytes());
            var go   = new GameObject("MapView");
            var view = go.AddComponent<MapView>();
            view.MinZoom = 5; view.MaxZoom = 5;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            try
            {
                // Center (0,0) at z5 → center tile (5,16,16), 3×3 cover x,y∈{15,16,17}.
                view.Initialise(src, new ViewState(0, 0, 5.0), ownsSource: false);
                PumpUntilSettled(view);

                Assert.AreEqual(9, view.LoadedTileCount, "z5 center cover is a 3×3 block");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(5, 16, 16), out _),
                    "the center tile must be built");

                // Pan far east (lon=170) → center tile (5,31,16); the new cover does NOT overlap the old
                // one, so the old tiles are evicted and a fresh 3×3 is loaded.
                view.SetView(new ViewState(170, 0, 5.0));
                PumpUntilSettled(view);

                Assert.AreEqual(9, view.LoadedTileCount, "still a 3×3 cover after panning");
                Assert.IsFalse(view.TryGetBuiltTile(new TileId(5, 16, 16), out _),
                    "the old center tile must have been evicted after the pan (new cover does not overlap)");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(5, 31, 16), out _),
                    "the new center tile must be built after the pan");
                // No NativeArray leak: Unity's leak detector would fail the run on teardown if a tile's
                // TileMeshBuffers were not disposed on eviction.
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── (2) go-live parity vs direct pipeline ──────────────────────────────────────────────

        [Test]
        public void MapView_GoLive_ProducesSameGeometryAsDirectPipeline()
        {
            byte[] bytes = FixtureBytes();
            var src  = new FixtureSource(bytes);
            var go   = new GameObject("MapView");
            var view = go.AddComponent<MapView>();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            try
            {
                view.Initialise(src, new ViewState(0, 0, 0.0), ownsSource: false);
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId(0, 0, 0), out var tileGo),
                    "z0/0/0 tile must be built by the live loop");
                Mesh liveMesh = tileGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.IsNotNull(liveMesh, "live tile must have a mesh");

                // Direct pipeline reference for the SAME tile id/origin.
                var mvtTile = MvtDecoder.Decode(bytes);
                var layer   = mvtTile.GetLayer("countries");
                var polyGeoms = new List<uint[]>();
                foreach (var ft in layer.Features)
                    if (ft.GeometryType == MvtGeometryType.Polygon && ft.Geometry != null)
                        polyGeoms.Add(ft.Geometry);

                var (bMin, _) = new TileId(0, 0, 0).MercatorBounds();
                var input = new TileTessellationPipeline.LayerInput
                {
                    FeatureGeometries = polyGeoms,
                    Extent = layer.Extent,
                    TileZ = 0, TileX = 0, TileY = 0,
                    OriginMercX = bMin.x, OriginMercY = bMin.y,
                };
                TileMeshBuffers buffers = TileTessellationPipeline.Schedule(input);
                try
                {
                    int vc = buffers.VertexCount[0];
                    int ic = buffers.TotalIndexCount;

                    Assert.AreEqual(vc, liveMesh.vertexCount,
                        "live mesh vertex count must equal the direct pipeline output");

                    string directVertHash = HashWorldPositions(buffers.WorldPositions, vc);
                    string liveVertHash   = HashVector3(liveMesh.vertices);
                    Assert.AreEqual(directVertHash, liveVertHash,
                        "live mesh vertex positions must be bit-identical to the direct pipeline output " +
                        "(the go-live path must not diverge from the jobified pipeline).");

                    string directIdxHash = HashIndices(buffers.TriangleIndices, ic);
                    string liveIdxHash   = HashIntArray(liveMesh.triangles);
                    Assert.AreEqual(directIdxHash, liveIdxHash,
                        "live mesh triangle indices must be bit-identical to the direct pipeline output.");
                }
                finally { buffers.Dispose(); }
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
            var src  = new FixtureSource(FixtureBytes());
            var go   = new GameObject("MapView");
            var view = go.AddComponent<MapView>();
            view.MinZoom = 2; view.MaxZoom = 2;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            try
            {
                // Warm up: load the whole cover and let every tile settle (builds, fetch continuations,
                // and the cover buffers are all warm) BEFORE we measure.
                view.Initialise(src, new ViewState(0, 0, 2.0), ownsSource: false);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // A couple of settled ticks to flush any first-call JIT / one-time allocation, and prime
                // the cover-recompute path once so its reused buffers (_cover, _coverSet, _toRelease) are
                // at their steady capacity BEFORE we measure.
                view.SetView(view.View.WithCenter(0.5, 0.0));
                view.Tick();
                view.SetView(view.View.WithCenter(0.0, 0.0));
                view.Tick();

                // ── (a) THE PAN CASE — the steady state the acceptance criterion names. ──
                // A small center nudge stays WITHIN the loaded z2 cover (one z2 tile spans ~10,000 km, so a
                // 111 km pan loads no new tiles), but it DOES dirty the cover so Tick runs the full
                // recompute: TileCover.Cover + the _coverSet rebuild + the request/release scan + the
                // floating-origin rebase loop. THIS is the per-frame hot path. It must allocate ZERO bytes:
                // reused buffers, struct dict enumerator, no LINQ/closures, no Request (all tiles loaded).
                view.SetView(view.View.WithCenter(1.0, 0.0));   // ~111 km pan; same 9-tile cover
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "MapView.Tick must not allocate during a within-cover pan (the cover RECOMPUTE path: " +
                    "TileCover.Cover + set rebuild + request/release scan + rebase). A failure means a " +
                    "per-frame List/Task/closure/LINQ leaked into the hot path.");

                // Confirm the pan loaded no new tiles (it really stayed within the cover).
                Assert.AreEqual(9, view.LoadedTileCount,
                    "the within-cover pan must not have loaded new tiles (recompute, not reload)");

                // ── (b) the fully-static frame also early-outs allocation-free. ──
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "A static frame (cover clean, nothing pending) must early-out with zero allocation.");

                // ── (c) a bearing/pitch-only change is camera-only → no cover dirty → alloc-free. ──
                view.SetView(view.View.WithOrientation(45.0, 30.0));
                Assert.That(() => view.Tick(), Is.Not.AllocatingGCMemory(),
                    "A bearing/pitch-only view change must not dirty the cover, so Tick stays allocation-free.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── hashing helpers ────────────────────────────────────────────────────────────────────

        private static string HashWorldPositions(NativeArray<float3> arr, int count)
        {
            var bytes = new List<byte>(count * 12);
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(BitConverter.GetBytes(arr[i].x));
                bytes.AddRange(BitConverter.GetBytes(arr[i].y));
                bytes.AddRange(BitConverter.GetBytes(arr[i].z));
            }
            return Sha(bytes.ToArray());
        }

        private static string HashVector3(Vector3[] arr)
        {
            var bytes = new List<byte>(arr.Length * 12);
            foreach (var v in arr)
            {
                bytes.AddRange(BitConverter.GetBytes(v.x));
                bytes.AddRange(BitConverter.GetBytes(v.y));
                bytes.AddRange(BitConverter.GetBytes(v.z));
            }
            return Sha(bytes.ToArray());
        }

        private static string HashIndices(NativeArray<int> arr, int count)
        {
            var bytes = new List<byte>(count * 4);
            for (int i = 0; i < count; i++)
                bytes.AddRange(BitConverter.GetBytes(arr[i]));
            return Sha(bytes.ToArray());
        }

        private static string HashIntArray(int[] arr)
        {
            var bytes = new List<byte>(arr.Length * 4);
            foreach (int v in arr)
                bytes.AddRange(BitConverter.GetBytes(v));
            return Sha(bytes.ToArray());
        }

        private static string Sha(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(data));
        }
    }
}
