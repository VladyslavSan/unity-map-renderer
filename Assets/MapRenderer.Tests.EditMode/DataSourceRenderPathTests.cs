// Unity EditMode only — uses UnityEngine.Application and MapRenderer.Unity.MeshBuilder.
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Jobs;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Proves that both <see cref="FileDataSource"/> and <see cref="HttpDataSource"/> feed the
    /// render path identically: same bytes → same decoded tile → same vertex/index CONTENT HASH
    /// (not just count) from the full decode→assemble→earcut→MeshBuilder pipeline.
    ///
    /// S04 upgrade: asserts buffer content hashes (SHA-256 over vertex positions and index arrays)
    /// rather than just counts. A count-only comparison is blind to divergent vertex positions —
    /// two pipelines could produce the same count with completely different geometry. Content hash
    /// guards against any regression in decode / assembly / triangulation across sources.
    ///
    /// This subsumes the S03 "count-only" follow-up from docs/follow-ups.md (line 46-48).
    /// </summary>
    [TestFixture]
    public class DataSourceRenderPathTests
    {
        [Test]
        public void FileSource_And_HttpSource_FeedRenderPath_ProduceSameVertexAndIndexContentHash()
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
                var fileResp = fileSource.FetchAsync(new TileId(0, 0, 0)).GetAwaiter().GetResult();
                Assert.IsTrue(fileResp.HasData, "FileDataSource must return HasData=true");
                fileBytes = fileResp.Bytes;
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }

            // 2. Feed bytes via HttpDataSource (stub handler).
            byte[] httpBytes;
            using (var handler = new TestStubHttpHandler(HttpStatusCode.OK, fixtureBytes))
            using (var client  = new HttpClient(handler))
            using (var httpSource = new HttpDataSource(client, "http://fake/{z}/{x}/{y}.mvt"))
            {
                var httpResp = httpSource.FetchAsync(new TileId(0, 0, 0)).GetAwaiter().GetResult();
                Assert.IsTrue(httpResp.HasData, "HttpDataSource must return HasData=true");
                httpBytes = httpResp.Bytes;
            }

            // 3. Run the full render pipeline on each source's bytes; compare CONTENT HASHES.
            // Hash includes vertex positions and triangle indices (not just counts).
            var (baselineVH, baselineIH) = BuildContentHashes(fixtureBytes);
            var (fileVH,     fileIH)     = BuildContentHashes(fileBytes);
            var (httpVH,     httpIH)     = BuildContentHashes(httpBytes);

            Assert.AreEqual(baselineVH, fileVH,
                "FileDataSource render path vertex content hash must match the direct baseline. " +
                "A mismatch means the file source returns different bytes or the decode path is non-deterministic.");
            Assert.AreEqual(baselineIH, fileIH,
                "FileDataSource render path index content hash must match the direct baseline.");
            Assert.AreEqual(baselineVH, httpVH,
                "HttpDataSource render path vertex content hash must match the direct baseline. " +
                "A mismatch means the HTTP source returns different bytes or decode is non-deterministic.");
            Assert.AreEqual(baselineIH, httpIH,
                "HttpDataSource render path index content hash must match the direct baseline.");

            Debug.Log($"[DataSourceRenderPathTests] All three sources produce identical vertex+index content hashes: {baselineVH[..16]}...");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the full managed pipeline on MVT bytes and returns SHA-256 hashes of the flat
        /// vertex position array and flat index array (both in pipeline order across all features).
        /// Vertex positions are float3 world positions (output of ProjectTileVerticesJob).
        /// </summary>
        private static (string vertHash, string idxHash) BuildContentHashes(byte[] mvtBytes)
        {
            var tile  = MvtDecoder.Decode(mvtBytes);
            var layer = tile.GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present");

            double extent = layer.Extent;
            var tileId    = new TileId(0, 0, 0);
            var (bMin, _) = tileId.MercatorBounds();
            double originX = bMin.x;
            double originY = bMin.y;

            using var sha256 = SHA256.Create();
            var vertBytes = new List<byte>();
            var idxBytes  = new List<byte>();
            int globalIndexOffset = 0;

            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon) continue;

                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
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
                    var worldPos   = new NativeArray<float3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    for (int i = 0; i < vCount; i++)
                        tileCoords[i] = flatVerts[i];

                    try
                    {
                        var job = new ProjectTileVerticesJob
                        {
                            TileZ = 0, TileX = 0, TileY = 0,
                            Extent       = extent,
                            OriginMercX  = originX,
                            OriginMercY  = originY,
                            TileCoords   = tileCoords,
                            WorldPositions = worldPos
                        };
                        job.Schedule(vCount, 64).Complete();

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
                        worldPos.Dispose();
                    }
                }
            }

            string vh = Convert.ToBase64String(sha256.ComputeHash(vertBytes.ToArray()));
            string ih = Convert.ToBase64String(sha256.ComputeHash(idxBytes.ToArray()));
            return (vh, ih);
        }

        // ── Stub HTTP handler (local, EditMode-only) ──────────────────────────────────────────

        private sealed class TestStubHttpHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly byte[]         _content;

            public TestStubHttpHandler(HttpStatusCode status, byte[] content)
            {
                _status  = status;
                _content = content;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, System.Threading.CancellationToken ct)
            {
                var msg = new HttpResponseMessage(_status);
                if (_content != null)
                    msg.Content = new ByteArrayContent(_content);
                return Task.FromResult(msg);
            }
        }
    }
}
