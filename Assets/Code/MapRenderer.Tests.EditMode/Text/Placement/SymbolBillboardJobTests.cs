// Unity EditMode only — needs Unity.Collections/NativeArray + Burst job scheduling. NOT registered in
// core-tests.csproj.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20: <see cref="SymbolBillboardJob"/> must reproduce <see cref="BillboardMath.BuildQuad"/>
    /// element-by-element for every quad, plus the exact 4-vert/6-index-per-quad topology.
    /// </summary>
    [TestFixture]
    public class SymbolBillboardJobTests
    {
        private static PlacedQuad MakeQuad(float2 anchor, float textSize, float depth, float4 color, float2 tlOffset, float rotation = 0f)
            => new PlacedQuad
            {
                Quad = new SymbolQuad
                {
                    TopLeft = new float2(-8f, 20f) + tlOffset,
                    BottomRight = new float2(14f, -2f) + tlOffset,
                    UvTopLeft = new float2(0.05f, 0.15f),
                    UvBottomRight = new float2(0.25f, 0.45f),
                    LineIndex = 0,
                },
                AnchorScreenPx = anchor,
                TextSizePx = textSize,
                Depth = depth,
                Color = color,
                RotationRadians = rotation,
            };

        [Test]
        public void Execute_ReproducesBillboardMath_ElementByElement_ForEveryQuad()
        {
            var inputs = new[]
            {
                MakeQuad(new float2(100f, 200f), 24f, 0.1f, new float4(1f, 0f, 0f, 1f), float2.zero),
                MakeQuad(new float2(400f, 50f), 48f, 0.9f, new float4(0f, 1f, 0f, 0.5f), new float2(3f, -3f), 0.6f),
                MakeQuad(new float2(0f, 0f), 12f, 0.5f, new float4(0f, 0f, 1f, 1f), new float2(-1f, 1f), -0.35f),
            };
            int quadCount = inputs.Length;

            var quads = new NativeArray<PlacedQuad>(quadCount, Allocator.TempJob);
            var outVerts = new NativeArray<BillboardVertex>(SymbolBillboardJob.MaxVertexCount(quadCount), Allocator.TempJob);
            var outIndices = new NativeArray<int>(SymbolBillboardJob.MaxIndexCount(quadCount), Allocator.TempJob);
            var vc = new NativeArray<int>(1, Allocator.TempJob);
            var ic = new NativeArray<int>(1, Allocator.TempJob);

            try
            {
                for (int i = 0; i < quadCount; i++) quads[i] = inputs[i];

                new SymbolBillboardJob
                {
                    Quads = quads,
                    QuadCount = quadCount,
                    OutVertices = outVerts,
                    OutIndices = outIndices,
                    OutVertexCount = vc,
                    OutIndexCount = ic,
                }.Run();

                Assert.AreEqual(quadCount * 4, vc[0], "4 vertices per quad, exactly.");
                Assert.AreEqual(quadCount * 6, ic[0], "6 indices per quad, exactly.");

                for (int i = 0; i < quadCount; i++)
                {
                    PlacedQuad q = inputs[i];
                    BillboardMath.BuildQuad(in q.Quad, in q.AnchorScreenPx, q.TextSizePx, q.Depth, in q.Color, q.RotationRadians,
                        out BillboardVertex expectedTopLeft, out BillboardVertex expectedTopRight,
                        out BillboardVertex expectedBottomRight, out BillboardVertex expectedBottomLeft);

                    int baseV = i * 4;
                    AssertVertexEqual(expectedTopLeft, outVerts[baseV + 0], $"quad {i} topLeft");
                    AssertVertexEqual(expectedTopRight, outVerts[baseV + 1], $"quad {i} topRight");
                    AssertVertexEqual(expectedBottomRight, outVerts[baseV + 2], $"quad {i} bottomRight");
                    AssertVertexEqual(expectedBottomLeft, outVerts[baseV + 3], $"quad {i} bottomLeft");

                    // Two triangles: (topLeft,topRight,bottomRight) + (topLeft,bottomRight,bottomLeft).
                    int baseI = i * 6;
                    Assert.AreEqual(baseV + 0, outIndices[baseI + 0], $"quad {i} index 0 (topLeft)");
                    Assert.AreEqual(baseV + 1, outIndices[baseI + 1], $"quad {i} index 1 (topRight)");
                    Assert.AreEqual(baseV + 2, outIndices[baseI + 2], $"quad {i} index 2 (bottomRight)");
                    Assert.AreEqual(baseV + 0, outIndices[baseI + 3], $"quad {i} index 3 (topLeft)");
                    Assert.AreEqual(baseV + 2, outIndices[baseI + 4], $"quad {i} index 4 (bottomRight)");
                    Assert.AreEqual(baseV + 3, outIndices[baseI + 5], $"quad {i} index 5 (bottomLeft)");
                }
            }
            finally
            {
                quads.Dispose();
                outVerts.Dispose();
                outIndices.Dispose();
                vc.Dispose();
                ic.Dispose();
            }
        }

        [Test]
        public void Execute_ZeroQuads_WritesZeroCounts()
        {
            var quads = new NativeArray<PlacedQuad>(1, Allocator.TempJob);
            var outVerts = new NativeArray<BillboardVertex>(1, Allocator.TempJob);
            var outIndices = new NativeArray<int>(1, Allocator.TempJob);
            var vc = new NativeArray<int>(1, Allocator.TempJob);
            var ic = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                new SymbolBillboardJob
                {
                    Quads = quads,
                    QuadCount = 0,
                    OutVertices = outVerts,
                    OutIndices = outIndices,
                    OutVertexCount = vc,
                    OutIndexCount = ic,
                }.Run();

                Assert.AreEqual(0, vc[0]);
                Assert.AreEqual(0, ic[0]);
            }
            finally
            {
                quads.Dispose();
                outVerts.Dispose();
                outIndices.Dispose();
                vc.Dispose();
                ic.Dispose();
            }
        }

        private static void AssertVertexEqual(BillboardVertex expected, BillboardVertex actual, string ctx)
        {
            const float tol = 1e-5f;
            Assert.AreEqual(expected.ScreenPx.x, actual.ScreenPx.x, tol, $"{ctx}: ScreenPx.x");
            Assert.AreEqual(expected.ScreenPx.y, actual.ScreenPx.y, tol, $"{ctx}: ScreenPx.y");
            Assert.AreEqual(expected.Depth, actual.Depth, tol, $"{ctx}: Depth");
            Assert.AreEqual(expected.Uv.x, actual.Uv.x, tol, $"{ctx}: Uv.x");
            Assert.AreEqual(expected.Uv.y, actual.Uv.y, tol, $"{ctx}: Uv.y");
            Assert.AreEqual(expected.Color.x, actual.Color.x, tol, $"{ctx}: Color.x");
            Assert.AreEqual(expected.Color.y, actual.Color.y, tol, $"{ctx}: Color.y");
            Assert.AreEqual(expected.Color.z, actual.Color.z, tol, $"{ctx}: Color.z");
            Assert.AreEqual(expected.Color.w, actual.Color.w, tol, $"{ctx}: Color.w");
        }
    }
}
