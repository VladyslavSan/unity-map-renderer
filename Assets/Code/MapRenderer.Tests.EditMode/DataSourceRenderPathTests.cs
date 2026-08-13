// Unity EditMode only — uses UnityEngine.Application and MapRenderer.Unity.MeshBuilder.
// NOT included in Tools/core-tests/core-tests.csproj.
//
// S51: HttpDataSource removed from Core (HTTP moved to UnityWebRequestDataSource in Unity layer).
// This test now covers FileDataSource → render pipeline only; the HTTP → render path parity
// is covered at integration level via MapViewLiveLoopTests (which uses FixtureSource, equivalent).

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Proves that <see cref="FileDataSource"/> feeds the render path correctly: bytes round-trip
    /// through the source and produce an identical vertex/index CONTENT HASH
    /// (not just count) from the full decode→assemble→earcut→job pipeline.
    ///
    /// S04 upgrade: asserts buffer content hashes (SHA-256 over vertex positions and index arrays)
    /// rather than just counts. A count-only comparison is blind to divergent vertex positions —
    /// two pipelines could produce the same count with completely different geometry. Content hash
    /// guards against any regression in decode / assembly / triangulation across sources.
    ///
    /// This subsumes the earlier S03 "count-only" follow-up.
    /// S51: HttpDataSource deleted from Core; HTTP is now UnityWebRequestDataSource (Unity layer).
    /// </summary>
    [TestFixture]
    public class DataSourceRenderPathTests
    {
        [Test]
        public void FileSource_FeedsRenderPath_VertexAndIndexContentHashMatchesBaseline()
        {
            string fixturePath = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(fixturePath), $"Fixture missing: {fixturePath}");
            byte[] fixtureBytes = File.ReadAllBytes(fixturePath);

            // 1. Feed bytes via FileDataSource.
            string tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string tilePath = Path.Combine(tempRoot, "0", "0", "0.mvt");
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath));
            File.WriteAllBytes(tilePath, fixtureBytes);

            byte[] fileBytes;
            try
            {
                using var fileSource = new FileDataSource(tempRoot);
                // S51: FetchAsync is async (SwitchToThreadPool pattern). It does NOT complete
                // synchronously, so calling .GetAwaiter().GetResult() immediately throws
                // "Not yet completed". Spin-wait until the UniTask completes on the ThreadPool.
                // Thread.Sleep(1) yields real CPU time so the ThreadPool can run the continuation.
                var fetchTask = fileSource.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 });
                int spins = 0;
                while (!fetchTask.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);
                Assert.IsTrue(fetchTask.Status.IsCompleted(),
                    "FileDataSource.FetchAsync must complete within 10 seconds (10000 × 1ms).");
                var fileResp = fetchTask.GetAwaiter().GetResult();
                Assert.IsTrue(fileResp.HasData, "FileDataSource must return HasData=true");
                fileBytes = fileResp.Bytes;
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }

            // 2. Run the full render pipeline on each source's bytes; compare CONTENT HASHES.
            // Hash includes vertex positions and triangle indices (not just counts).
            var (baselineVH, baselineIH) = BuildContentHashes(fixtureBytes);
            var (fileVH,     fileIH)     = BuildContentHashes(fileBytes);

            Assert.AreEqual(baselineVH, fileVH,
                "FileDataSource render path vertex content hash must match the direct baseline. " +
                "A mismatch means the file source returns different bytes or the decode path is non-deterministic.");
            Assert.AreEqual(baselineIH, fileIH,
                "FileDataSource render path index content hash must match the direct baseline.");

            Debug.Log($"[DataSourceRenderPathTests] FileDataSource produces identical vertex+index content hashes: {baselineVH[..16]}...");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the full managed pipeline on MVT bytes and returns SHA-256 hashes of the flat
        /// vertex position array and flat index array (both in pipeline order across all features).
        /// Vertex positions are double3 origin-relative world positions (TileToGeoJob → ProjectPointsJob).
        /// </summary>
        private static (string vertHash, string idxHash) BuildContentHashes(byte[] mvtBytes)
        {
            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer, "countries layer must be present");

            double extent = layer.Extent;
            var tileId    = new TileId { Z = 0, X = 0, Y = 0 };
            var (bMin, _) = tileId.MercatorBounds();
            double originX = bMin.x;
            double originY = bMin.y;

            using var sha256 = SHA256.Create();
            var vertBytes = new List<byte>();
            var idxBytes  = new List<byte>();
            int globalIndexOffset = 0;

            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;

                List<List<double2>> rings = MvtGeometry.Decode(layer.Commands[fi]);
                if (rings == null || rings.Count == 0) continue;

                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    Earcut.Result earcutResult = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (earcutResult.Indices == null || earcutResult.Indices.Length == 0) continue;

                    double2[] flatVerts = earcutResult.Vertices;
                    int[]     triIdx    = earcutResult.Indices;
                    int       vCount    = flatVerts.Length;

                    // Not using 'using var' — CS1654 makes using-var NativeArrays read-only in C# 8+.
                    var tileCoords = new NativeArray<double2>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var geo        = new NativeArray<GeoCoordinate>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var worldPos   = new NativeArray<double3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var vertUp     = new NativeArray<double3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    for (int i = 0; i < vCount; i++)
                        tileCoords[i] = flatVerts[i];

                    try
                    {
                        new TileToGeoJob
                        {
                            Tile = new TileId { Z = 0, X = 0, Y = 0 }, Extent = extent,
                            TileCoords = tileCoords, OutGeo = geo,
                        }.Schedule(vCount, 64).Complete();

                        new ProjectPointsJob<WebMercatorProjection>
                        {
                            Projection     = new WebMercatorProjection(),
                            OriginWorld    = new double3(originX, 0.0, originY),
                            Points         = geo,
                            WorldPositions = worldPos,
                            Normals        = vertUp,
                        }.Schedule(vCount, 64).Complete();

                        for (int i = 0; i < vCount; i++)
                        {
                            vertBytes.AddRange(BitConverter.GetBytes(worldPos[i].x));
                            vertBytes.AddRange(BitConverter.GetBytes(worldPos[i].y));
                            vertBytes.AddRange(BitConverter.GetBytes(worldPos[i].z));
                        }

                        foreach (int idx in triIdx)
                            idxBytes.AddRange(BitConverter.GetBytes(globalIndexOffset + idx));

                        globalIndexOffset += vCount;
                    }
                    finally
                    {
                        tileCoords.Dispose();
                        geo.Dispose();
                        worldPos.Dispose();
                        vertUp.Dispose();
                    }
                }
            }

            string vh = Convert.ToBase64String(sha256.ComputeHash(vertBytes.ToArray()));
            string ih = Convert.ToBase64String(sha256.ComputeHash(idxBytes.ToArray()));
            return (vh, ih);
        }
    }
}
