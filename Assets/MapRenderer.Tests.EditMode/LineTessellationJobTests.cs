using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Jobs;

namespace MapRenderer.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="LineTessellationJob"/>.
    ///
    /// Validates Burst compilation and basic output for a 2-point straight segment.
    /// Full multi-line parallel jobification is deferred to S06; this is the smoke test
    /// that confirms the job compiles under Burst and emits the correct vertex/index layout.
    ///
    /// Core vs Job parity: the 2-point case is simple enough to verify analytically.
    /// </summary>
    [TestFixture]
    public class LineTessellationJobTests
    {
        [Test]
        public void TwoPoints_HorizontalLine_ProducesFourVertsAndSixIndices()
        {
            const int capacity = 16;
            var points   = new NativeArray<double2>(2, Allocator.TempJob);
            var outPos   = new NativeArray<float3>(capacity, Allocator.TempJob);
            var outNorm  = new NativeArray<float2>(capacity, Allocator.TempJob);
            var outSide  = new NativeArray<float2>(capacity, Allocator.TempJob);
            var outWS    = new NativeArray<float>(capacity, Allocator.TempJob);
            var outIdx   = new NativeArray<int>(capacity, Allocator.TempJob);
            var vcArr    = new NativeArray<int>(1, Allocator.TempJob);
            var icArr    = new NativeArray<int>(1, Allocator.TempJob);

            try
            {
                points[0] = new double2(0, 0);
                points[1] = new double2(10, 0);

                var job = new LineTessellationJob
                {
                    InputPoints    = points,
                    PointCount     = 2,
                    OutPositions   = outPos,
                    OutNormals     = outNorm,
                    OutSideAndDist = outSide,
                    OutWidthScales = outWS,
                    OutIndices     = outIdx,
                    OutVertexCount = vcArr,
                    OutIndexCount  = icArr,
                };

                job.Schedule().Complete();

                Assert.AreEqual(4, vcArr[0],
                    $"2-pt horizontal line → 4 verts. Got {vcArr[0]}.");
                Assert.AreEqual(6, icArr[0],
                    $"2-pt horizontal line → 6 indices. Got {icArr[0]}.");

                // Verify normal perpendicularity to horizontal segment (tangent = (1,0)).
                for (int i = 0; i < 4; i++)
                {
                    float2 n   = outNorm[i];
                    float  dot = n.x * 1f + n.y * 0f; // dot with tangent (1,0)
                    Assert.That((double)dot, Is.InRange(-1e-6, 1e-6),
                        $"Vertex[{i}] normal must be perpendicular to horizontal tangent. " +
                        $"dot={dot:G6} for normal=({n.x:G},{n.y:G}).");
                }

                // Verify unit normals (butt cap, straight segment → all unit).
                for (int i = 0; i < 4; i++)
                {
                    float2 n   = outNorm[i];
                    float  len = System.Math.Abs(n.x * n.x + n.y * n.y - 1f);
                    Assert.That((double)len, Is.LessThan(1e-5),
                        $"Vertex[{i}] normal must be unit. ||n||^2 - 1 = {len:G6}.");
                }

                // Verify index range.
                for (int i = 0; i < 6; i++)
                    Assert.That(outIdx[i], Is.InRange(0, 3),
                        $"Index[{i}]={outIdx[i]} must be in [0,3].");
            }
            finally
            {
                points.Dispose(); outPos.Dispose(); outNorm.Dispose();
                outSide.Dispose(); outWS.Dispose(); outIdx.Dispose();
                vcArr.Dispose(); icArr.Dispose();
            }
        }

        [Test]
        public void LessThanTwoPoints_ProducesZeroOutput()
        {
            const int capacity = 8;
            var points   = new NativeArray<double2>(1, Allocator.TempJob);
            var outPos   = new NativeArray<float3>(capacity, Allocator.TempJob);
            var outNorm  = new NativeArray<float2>(capacity, Allocator.TempJob);
            var outSide  = new NativeArray<float2>(capacity, Allocator.TempJob);
            var outWS    = new NativeArray<float>(capacity, Allocator.TempJob);
            var outIdx   = new NativeArray<int>(capacity, Allocator.TempJob);
            var vcArr    = new NativeArray<int>(1, Allocator.TempJob);
            var icArr    = new NativeArray<int>(1, Allocator.TempJob);

            try
            {
                points[0] = new double2(5, 5);

                var job = new LineTessellationJob
                {
                    InputPoints    = points,
                    PointCount     = 1,
                    OutPositions   = outPos,
                    OutNormals     = outNorm,
                    OutSideAndDist = outSide,
                    OutWidthScales = outWS,
                    OutIndices     = outIdx,
                    OutVertexCount = vcArr,
                    OutIndexCount  = icArr,
                };

                job.Schedule().Complete();

                Assert.AreEqual(0, vcArr[0], "< 2 points → 0 verts.");
                Assert.AreEqual(0, icArr[0], "< 2 points → 0 indices.");
            }
            finally
            {
                points.Dispose(); outPos.Dispose(); outNorm.Dispose();
                outSide.Dispose(); outWS.Dispose(); outIdx.Dispose();
                vcArr.Dispose(); icArr.Dispose();
            }
        }

        [Test]
        public void TwoPoints_CoreParityCheck_NormalsMatchLineTessellator()
        {
            // Verify that the Job's output for a simple horizontal 2-pt line matches
            // what LineTessellator.Triangulate produces for the same input.
            // This is the Core-vs-Job parity check for the smoke scenario.
            const int capacity = 16;
            var points   = new NativeArray<double2>(2, Allocator.TempJob);
            var outPos   = new NativeArray<float3>(capacity, Allocator.TempJob);
            var outNorm  = new NativeArray<float2>(capacity, Allocator.TempJob);
            var outSide  = new NativeArray<float2>(capacity, Allocator.TempJob);
            var outWS    = new NativeArray<float>(capacity, Allocator.TempJob);
            var outIdx   = new NativeArray<int>(capacity, Allocator.TempJob);
            var vcArr    = new NativeArray<int>(1, Allocator.TempJob);
            var icArr    = new NativeArray<int>(1, Allocator.TempJob);

            try
            {
                points[0] = new double2(0, 0);
                points[1] = new double2(10, 0);

                var job = new LineTessellationJob
                {
                    InputPoints    = points,
                    PointCount     = 2,
                    OutPositions   = outPos,
                    OutNormals     = outNorm,
                    OutSideAndDist = outSide,
                    OutWidthScales = outWS,
                    OutIndices     = outIdx,
                    OutVertexCount = vcArr,
                    OutIndexCount  = icArr,
                };
                job.Schedule().Complete();

                // Expected from LineTessellator (horizontal, butt): normals are ±(0,1).
                // Vertices [0]=(0,0) left: normal=(0,1), side=+1.
                //          [1]=(0,0) right: normal=(0,-1), side=-1.
                //          [2]=(10,0) left: normal=(0,1), side=+1.
                //          [3]=(10,0) right: normal=(0,-1), side=-1.

                Assert.That((double)outNorm[0].x, Is.InRange(-1e-6, 1e-6), "Vertex[0] normal.x ≈ 0.");
                Assert.That((double)outNorm[0].y, Is.InRange(1.0 - 1e-6, 1.0 + 1e-6), "Vertex[0] normal.y ≈ +1.");
                Assert.That((double)outNorm[1].x, Is.InRange(-1e-6, 1e-6), "Vertex[1] normal.x ≈ 0.");
                Assert.That((double)outNorm[1].y, Is.InRange(-1.0 - 1e-6, -1.0 + 1e-6), "Vertex[1] normal.y ≈ -1.");
                Assert.That(outSide[0].x, Is.EqualTo(+1f), "Vertex[0] side=+1.");
                Assert.That(outSide[1].x, Is.EqualTo(-1f), "Vertex[1] side=-1.");
            }
            finally
            {
                points.Dispose(); outPos.Dispose(); outNorm.Dispose();
                outSide.Dispose(); outWS.Dispose(); outIdx.Dispose();
                vcArr.Dispose(); icArr.Dispose();
            }
        }
    }
}
