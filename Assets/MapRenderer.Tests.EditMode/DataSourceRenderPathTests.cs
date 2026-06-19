// Unity EditMode only — uses UnityEngine.Application and MapRenderer.Unity.MeshBuilder.
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
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
    /// S02 render path identically: same bytes → same decoded tile → same vertex/index count from
    /// <see cref="MeshBuilder"/>.
    ///
    /// This closes the "renders unchanged through each source" acceptance criterion. The byte-identity
    /// and decode-layer-counts are tested in <see cref="DataSourceTests"/>; this test additionally
    /// runs the full decode→assemble→earcut→MeshBuilder pipeline to confirm that the render path
    /// is unchanged when bytes originate from each source rather than a direct file read.
    ///
    /// <b>Note:</b> <see cref="MapFillBootstrap"/> stays synchronous/TextAsset-default in S03.
    /// The sources feed the render path only through this test. Full async integration (ECS/jobs) is S04.
    /// </summary>
    [TestFixture]
    public class DataSourceRenderPathTests
    {
        [Test]
        public void FileSource_And_HttpSource_FeedRenderPath_ProduceSameVertexAndIndexCounts()
        {
            // Load fixture via direct file read (baseline — same as the existing S02 pipeline).
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

            // 3. Run the full render pipeline on each source's bytes; compare vertex/index counts.
            int baselineV, baselineI;
            BuildMeshCounts(fixtureBytes, out baselineV, out baselineI);

            int fileV, fileI;
            BuildMeshCounts(fileBytes, out fileV, out fileI);

            int httpV, httpI;
            BuildMeshCounts(httpBytes, out httpV, out httpI);

            Assert.AreEqual(baselineV, fileV,
                "FileDataSource render path must produce the same vertex count as the direct baseline");
            Assert.AreEqual(baselineI, fileI,
                "FileDataSource render path must produce the same index count as the direct baseline");
            Assert.AreEqual(baselineV, httpV,
                "HttpDataSource render path must produce the same vertex count as the direct baseline");
            Assert.AreEqual(baselineI, httpI,
                "HttpDataSource render path must produce the same index count as the direct baseline");

            Debug.Log($"[DataSourceRenderPathTests] baseline={baselineV}v/{baselineI}i, " +
                      $"file={fileV}v/{fileI}i, http={httpV}v/{httpI}i — all match.");
        }

        // -----------------------------------------------------------------------------------------
        // Helpers — mirrors the pipeline in MapFillBootstrap.BuildMesh (countries layer, TileId 0,0,0)
        // -----------------------------------------------------------------------------------------

        private static void BuildMeshCounts(byte[] mvtBytes, out int vertexCount, out int indexCount)
        {
            var tile  = MvtDecoder.Decode(mvtBytes);
            var layer = tile.GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present");

            double extent = layer.Extent;
            var tileId    = new TileId(0, 0, 0);
            var (bMin, _) = tileId.MercatorBounds();
            double originX = bMin.x;
            double originY = bMin.y;

            var meshBuilder = new MeshBuilder();

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

                    var tileCoords = new NativeArray<double2>(vCount, Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);
                    var worldPos = new NativeArray<float3>(vCount, Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);

                    for (int i = 0; i < vCount; i++)
                        tileCoords[i] = flatVerts[i];

                    var job = new ProjectTileVerticesJob
                    {
                        TileZ         = 0, TileX = 0, TileY = 0,
                        Extent        = extent,
                        OriginMercX   = originX,
                        OriginMercY   = originY,
                        TileCoords    = tileCoords,
                        WorldPositions = worldPos
                    };
                    job.Schedule(vCount, 64).Complete();

                    var verts = new float3[vCount];
                    for (int i = 0; i < vCount; i++)
                        verts[i] = worldPos[i];

                    tileCoords.Dispose();
                    worldPos.Dispose();

                    meshBuilder.AddFeature(verts, triIdx);
                }
            }

            vertexCount = meshBuilder.VertexCount;
            indexCount  = meshBuilder.IndexCount;
        }

        // -----------------------------------------------------------------------------------------
        // Stub HTTP handler (local, EditMode-only; the engine-free version is in DataSourceTests)
        // -----------------------------------------------------------------------------------------

        private sealed class TestStubHttpHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly byte[]         _content;

            public TestStubHttpHandler(HttpStatusCode status, byte[] content)
            {
                _status  = status;
                _content = content;
            }

            protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, System.Threading.CancellationToken ct)
            {
                var msg = new HttpResponseMessage(_status);
                if (_content != null)
                    msg.Content = new ByteArrayContent(_content);
                return System.Threading.Tasks.Task.FromResult(msg);
            }
        }
    }
}
