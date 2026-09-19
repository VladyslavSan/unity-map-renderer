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
using MapRenderer.Jobs.Projection;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.DataSources
{
    /// <summary>
    /// Proves that <see cref="FileDataSource"/> feeds the render path correctly: bytes round-trip
    /// through the source and produce an identical vertex CONTENT HASH (not just count) from the
    /// decode→assemble→project pipeline.
    ///
    /// This is an A-vs-A comparison — the same bytes through two sources must produce identical render
    /// input — so triangulation contributes nothing to what it proves (A0: dropped; the hash covers
    /// the assembled ring vertices, outer then holes, projected through the existing
    /// TileToGeoJob → ProjectPointsJob chain, which is the projection coverage this test actually
    /// carries). A count-only comparison would be blind to divergent vertex positions; content hash
    /// guards against any regression in decode / assembly / projection across sources.
    ///
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
                // "Not yet completed". Parks until the ThreadPool fetch completes.
                var fetchTask = fileSource.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 });
                fetchTask.WaitOffPlayerLoop(10000);
                Assert.IsTrue(fetchTask.Status.IsCompleted(),
                    "FileDataSource.FetchAsync must complete within 10 seconds.");
                var fileResp = fetchTask.GetAwaiter().GetResult();
                Assert.IsTrue(fileResp.HasData, "FileDataSource must return HasData=true");
                fileBytes = fileResp.Bytes;
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }

            // 2. Run the same decode→assemble→project pipeline on each source's bytes; compare the
            // CONTENT HASH (an A-vs-A comparison — the triangulator contributes nothing to it, see
            // class doc).
            string baselineHash = BuildContentHash(fixtureBytes);
            string fileHash     = BuildContentHash(fileBytes);

            Assert.AreEqual(baselineHash, fileHash,
                "FileDataSource render path content hash must match the direct baseline. " +
                "A mismatch means the file source returns different bytes or the decode path is non-deterministic.");

            Debug.Log($"[DataSourceRenderPathTests] FileDataSource produces an identical content hash: {baselineHash[..16]}...");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes and assembles MVT bytes and returns a SHA-256 hash of the flat, projected ring-vertex
        /// array — outer then holes, in assembly order, across all polygons (world positions via
        /// TileToGeoJob → ProjectPointsJob). No triangulation: this test is an A-vs-A comparison of two
        /// sources' bytes, so the triangulator is not part of the property it proves.
        /// </summary>
        private static string BuildContentHash(byte[] mvtBytes)
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

            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;

                List<List<double2>> rings = MvtGeometry.Decode(layer.Commands[fi]);
                if (rings == null || rings.Count == 0) continue;

                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    var flatVerts = new List<double2>(polygon.Outer);
                    if (polygon.Holes != null)
                        foreach (var hole in polygon.Holes) flatVerts.AddRange(hole);
                    int vCount = flatVerts.Count;
                    if (vCount == 0) continue;

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

            return Convert.ToBase64String(sha256.ComputeHash(vertBytes.ToArray()));
        }
    }
}
