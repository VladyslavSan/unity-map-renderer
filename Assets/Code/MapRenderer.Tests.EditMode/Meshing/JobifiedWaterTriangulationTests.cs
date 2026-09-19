// Unity EditMode only — uses NativeArray, Burst jobs (FillMeshPipeline). NOT included in
// Tools/core-tests/core-tests.csproj (see WaterTriangulationTests.cs for the managed twin — also Unity
// EditMode only, since it now depends on MapRenderer.Jobs.Mvt).

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// mesh-triangulation-robustness Stage 3 acceptance tooth (plan Edit 4): drives the REAL jobified
    /// fill path — <see cref="FillMeshGraph.Schedule"/>, the Burst <see cref="EarcutJob"/> — over the
    /// committed corpus water tile, and validates the output has no folds and conserves area. This is
    /// the tooth that actually proves the VISIBLE render path is fixed, since production fill meshes
    /// are built exclusively through <c>StyledFillTileBuilder</c> → <c>FillMeshGraph</c> →
    /// <see cref="EarcutBatchJob"/>. Covers water-8-135-80 only; <c>WaterTriangulationTests</c> covers
    /// the remaining 7 corpus tiles on the same Burst arm, not duplicated here.
    /// </summary>
    public class JobifiedWaterTriangulationTests
    {
        private static byte[] LoadFixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        [Test]
        public void JobifiedPipeline_Water_8_135_80_TriangulatesFaithfully()
        {
            byte[] mvtBytes = LoadFixture("water-8-135-80.pbf.bytes");
            var tileId  = new TileId { Z = 8, X = 135, Y = 80 };
            using var mvtTile = MvtDecoder.Decode(tileId, mvtBytes);
            var layer   = mvtTile.GetLayer("water");
            Assert.IsNotNull(layer, "water layer present");

            // Arm A: the command streams read from the BYTES, independently of the decoder under test.
            var oracle = MvtFixtureStreams.ReadLayer(mvtBytes, "water");

            // Ground truth: managed decode → assemble (PolygonAssembler is unchanged / out of Stage-3
            // scope) gives the polygon structure (outer+holes) for the even-odd coverage + area check.
            // This does NOT triangulate — the triangulation under test comes from the REAL Burst path
            // below, fed into the SAME ground truth via MeshCoverageValidator.ValidateTriangulation.
            var groundTruthPolys = new List<Polygon>();
            for (int fi = 0; fi < oracle.Kinds.Count; fi++)
            {
                if (oracle.Kinds[fi] != TileGeometryType.Polygon || oracle.Commands[fi] == null) continue;
                groundTruthPolys.AddRange(PolygonAssembler.Assemble(MvtGeometry.Decode(oracle.Commands[fi])));
            }
            Assert.Greater(groundTruthPolys.Count, 0, "water layer has polygons");

            double extent = layer.Extent;
            var (bMin, _) = tileId.MercatorBounds();

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED (IR C1 P3) — the decoded tile owns it
            NativeArray<int> visitOrder   = TestTileMeshBuilder.FullVisitOrder(geometry);
            var pipelineInput = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };

            FillGraphOutput buffers = FillMeshGraph.Schedule(pipelineInput);
            buffers.Handle.Complete();
            try
            {
                Assert.IsTrue(buffers.IsCreated, "jobified pipeline produced no buffers for a tile with water polygons");

                int indexCount = buffers.TriangleIndices.Length;
                var tris = new List<(double2 a, double2 b, double2 c)>(indexCount / 3);
                for (int i = 0; i + 2 < indexCount; i += 3)
                {
                    double2 a = buffers.TileVertices[buffers.TriangleIndices[i]];
                    double2 b = buffers.TileVertices[buffers.TriangleIndices[i + 1]];
                    double2 c = buffers.TileVertices[buffers.TriangleIndices[i + 2]];
                    tris.Add((a, b, c));
                }

                var rep = MeshCoverageValidator.ValidateTriangulation(
                    groundTruthPolys, tris, buffers.Counts[0].ForceClipCount, (int)extent);

                Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                    "jobified (Burst EarcutJob) water z8/135/80 triangulation is broken: " +
                    rep.Summary + "\n" + rep.AsciiMap);
            }
            finally
            {
                buffers.Dispose();
                visitOrder.Dispose();
                // geometry is BORROWED from the decoded layer (IR C1 P3) — the `using` frees it.
            }
        }
    }
}
