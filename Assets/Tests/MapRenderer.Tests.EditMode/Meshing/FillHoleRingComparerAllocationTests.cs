// EditMode only: FillMeshPipeline.HoleRingComparer is internal to Jobs, and Is.Not.AllocatingGCMemory() is
// this runner's only live GC meter. Limitation: FillMeshGraph.Schedule allocates per call, so this pins the
// per-polygon hole-ring sort alone — a reused NativeArray sorted through a struct comparer, with no boxing.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// H2/H3 — the hole-ring sort used a managed <c>new int[holeCount]</c> per polygon (a GC allocation every
    /// iteration). It now sorts a single reused <see cref="NativeArray{T}"/> through the
    /// <c>HoleRingComparer</c> struct. This tooth pins both halves: the ordering is correct, and sorting the
    /// native buffer through the struct comparer allocates no managed memory.
    /// </summary>
    [TestFixture]
    public class FillHoleRingComparerAllocationTests
    {
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        // Three single-point "rings" whose leftmost-x is 5, 1, 3 — so (leftmost-x asc) orders them 1,2,0.
        private static TileGeometryBuffers ThreeRingFixture()
        {
            var g = TileGeometryBuffers.Allocate(Tile, extent: 4096.0, featureCount: 1, maxRings: 3, maxVertices: 3);
            NativeArrayWrite(g.RingOffsets, 0, 0);
            NativeArrayWrite(g.RingOffsets, 1, 1);
            NativeArrayWrite(g.RingOffsets, 2, 2);
            NativeArrayWrite(g.RingOffsets, 3, 3);
            NativeArrayWrite(g.Vertices, 0, new double2(5.0, 0.0)); // ring 0
            NativeArrayWrite(g.Vertices, 1, new double2(1.0, 0.0)); // ring 1
            NativeArrayWrite(g.Vertices, 2, new double2(3.0, 0.0)); // ring 2
            return g;
        }

        private static void NativeArrayWrite<T>(NativeArray<T> a, int i, T v) where T : struct => a[i] = v;

        [Test]
        public void HoleRingSort_OrdersByLeftmostX_AndSortsTheNativeBufferWithNoManagedAllocation()
        {
            TileGeometryBuffers g = ThreeRingFixture();
            var holeRIs = new NativeArray<int>(3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                var comparer = new FillMeshPipeline.HoleRingComparer(g); // a struct — Sort<int,U> takes it unboxed
                int[] src = { 0, 1, 2 };

                // Correctness: leftmost-x is 5,1,3 for rings 0,1,2 → ascending order is 1,2,0.
                for (int i = 0; i < 3; i++) holeRIs[i] = src[i];
                holeRIs.GetSubArray(0, 3).Sort(comparer);
                Assert.AreEqual(new[] { 1, 2, 0 }, holeRIs.ToArray(),
                    "comparer must order hole rings by ascending leftmost-x");

                // Refilling the reused native buffer and sorting it through the struct comparer allocates nothing.
                // Warm the exact measured delegate first, so one-time JIT stays out of the window.
                TestDelegate act = () =>
                {
                    for (int i = 0; i < 3; i++) holeRIs[i] = src[i];
                    holeRIs.GetSubArray(0, 3).Sort(comparer);
                };
                for (int w = 0; w < 50; w++) act();
                Assert.That(act, Is.Not.AllocatingGCMemory(),
                    "sorting the reused NativeArray through the struct comparer must allocate no managed memory " +
                    "(no per-polygon int[], no boxed comparer).");
            }
            finally
            {
                holeRIs.Dispose();
                g.Dispose();
            }
        }
    }
}
