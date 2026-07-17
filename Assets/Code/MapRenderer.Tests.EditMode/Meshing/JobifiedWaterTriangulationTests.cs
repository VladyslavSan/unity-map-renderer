// Unity EditMode only — uses NativeArray, Burst jobs (TileMeshPipeline). NOT included in
// Tools/core-tests/core-tests.csproj (see WaterTriangulationTests.cs for the engine-free twin).

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// mesh-triangulation-robustness Stage 3 acceptance tooth (plan Edit 4): drives the REAL jobified
    /// fill path — <see cref="TileMeshPipeline.Schedule"/>, the Burst <see cref="EarcutJob"/> — over the
    /// committed corpus water tile, and validates the output has no folds and conserves area. This is the
    /// jobified analogue of <c>WaterTriangulationTests</c> (which only exercises the managed twin via
    /// <c>Earcut.Triangulate</c>, engine-free); it is the tooth that actually proves the VISIBLE render
    /// path is fixed, since production fill meshes are built exclusively through
    /// <c>StyledFillTileBuilder</c> → <c>TileMeshPipeline</c> → <see cref="EarcutJob"/> (the managed
    /// <c>Earcut</c> is a differential oracle only, never in the render path).
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
            var mvtTile = MvtDecoder.Decode(mvtBytes);
            var layer   = mvtTile.GetLayer("water");
            Assert.IsNotNull(layer, "water layer present");

            // Ground truth: managed decode → assemble (PolygonAssembler is unchanged / out of Stage-3
            // scope) gives the polygon structure (outer+holes) for the even-odd coverage + area check.
            // This does NOT triangulate — the triangulation under test comes from the REAL Burst path
            // below, fed into the SAME ground truth via MeshCoverageValidator.ValidateTriangulation.
            var groundTruthPolys = new List<Polygon>();
            var polyGeoms        = new List<uint[]>();
            foreach (var f in layer.Features)
            {
                if (f.GeometryType != TileGeometryType.Polygon || f.Geometry == null) continue;
                polyGeoms.Add(f.Geometry);
                groundTruthPolys.AddRange(PolygonAssembler.Assemble(MvtGeometry.Decode(f.Geometry)));
            }
            Assert.Greater(groundTruthPolys.Count, 0, "water layer has polygons");

            double extent = layer.Extent;
            var tileId    = new TileId { Z = 8, X = 135, Y = 80 };
            var (bMin, _) = tileId.MercatorBounds();

            var pipelineInput = new TileMeshPipeline.LayerInput
            {
                FeatureGeometries = polyGeoms,
                Extent       = extent,
                Tile         = tileId,
                OriginRender = new double3(bMin.x, 0.0, bMin.y),
            };

            TileMeshBuffers buffers = TileMeshPipeline.Schedule(pipelineInput);
            try
            {
                Assert.IsTrue(buffers.IsCreated, "jobified pipeline produced no buffers for a tile with water polygons");

                int indexCount = buffers.TotalIndexCount;
                var tris = new List<(double2 a, double2 b, double2 c)>(indexCount / 3);
                for (int i = 0; i + 2 < indexCount; i += 3)
                {
                    double2 a = buffers.TileVertices[buffers.TriangleIndices[i]];
                    double2 b = buffers.TileVertices[buffers.TriangleIndices[i + 1]];
                    double2 c = buffers.TileVertices[buffers.TriangleIndices[i + 2]];
                    tris.Add((a, b, c));
                }

                var rep = MeshCoverageValidator.ValidateTriangulation(
                    groundTruthPolys, tris, buffers.TotalForceClipCount, (int)extent);

                Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                    "jobified (Burst EarcutJob) water z8/135/80 triangulation is broken: " +
                    rep.Summary + "\n" + rep.AsciiMap);
            }
            finally
            {
                buffers.Dispose();
            }
        }
    }
}
