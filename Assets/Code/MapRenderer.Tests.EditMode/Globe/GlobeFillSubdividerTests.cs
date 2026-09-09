// S91-C (C-3): the globe fill subdivider (Burst job) must refine flat earcut triangles onto the sphere, pass
// flat Mercator straight through, and never explode (depth cap + vertex budget). Scheduled through the same
// dispatch + NativeList path the fill graph uses (job-scheduling-design.md §8 stage 4 Group B: the
// synchronous Run/RunTyped entry point is retired with its synchronous callers).

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Fill;
namespace MapRenderer.Tests.Globe
{
    public class GlobeFillSubdividerTests
    {
        private const double Extent = 4096;

        // One full-tile triangle → schedule the Burst subdivide job into NativeLists (caller disposes).
        private static void Run(IProjection proj, TileId id, int maxDepth, int budget,
            out NativeList<GlobeFillVertex> verts, out NativeList<int> idx)
        {
            var tileVerts = new NativeList<double2>(3, Allocator.Persistent);
            tileVerts.Add(new double2(0, 0)); tileVerts.Add(new double2(Extent, 0)); tileVerts.Add(new double2(0, Extent));
            var tris = new NativeList<int>(3, Allocator.Persistent); tris.Add(0); tris.Add(1); tris.Add(2);
            var feat = new NativeList<int>(3, Allocator.Persistent); feat.Add(0); feat.Add(0); feat.Add(0);

            verts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            idx   = new NativeList<int>(64, Allocator.Persistent);
            JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                proj, tileVerts, tris, feat, id, Extent, new double3(0, 0, 0),
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, maxDepth, budget, verts, idx, default);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            tileVerts.Dispose(); tris.Dispose(); feat.Dispose();
        }

        [Test]
        public void Globe_Subdivides_AndKeepsEveryVertexOnTheSphere()
        {
            Run(new SphericalProjection(), new TileId { Z = 3, X = 3, Y = 3 },
                GlobeFillSubdivideDispatch.DefaultMaxDepth, GlobeFillSubdivideDispatch.DefaultMaxOutputVertices,
                out var v, out var idx);
            try
            {
                Assert.Greater(v.Length, 3, "a 45° globe triangle must subdivide past the single flat triangle");
                // vertex sharing: OutVerts (v) is now the UNIQUE count and OutIndices
                // (idx) the EMITTED count — every 1→4 split's 3 midpoints are each shared by 3 of its 4
                // children (GlobeFillVertexKey), so a single-triangle subdivision this deep MUST show real
                // sharing, not just "no more than" the emitted count.
                Assert.Less(v.Length, idx.Length, "a multi-level split must produce SOME shared split-edge vertices");
                Assert.AreEqual(0, idx.Length % 3, "indices form whole triangles");

                double R = EarthConstants.A;
                for (int i = 0; i < v.Length; i++)
                {
                    GlobeFillVertex fv = v[i]; // origin = 0 ⇒ World IS the ECEF position
                    Assert.AreEqual(R, math.length(fv.World), R * 1e-6, $"vertex {i} on the sphere (midpoints re-projected)");
                    Assert.AreEqual(1.0, math.length(fv.Up),   1e-6, "up unit");
                    Assert.AreEqual(1.0, math.length(fv.East), 1e-6, "east unit");
                    Assert.AreEqual(0.0, math.dot(fv.Up, fv.East), 1e-6, "east ⊥ up (valid TBN per refined vertex)");
                }
            }
            finally { v.Dispose(); idx.Dispose(); }
        }

        [Test]
        public void Mercator_IsFlat_PassesThroughWithoutSubdivision()
        {
            Run(new WebMercatorProjection(), new TileId { Z = 0, X = 0, Y = 0 },
                GlobeFillSubdivideDispatch.DefaultMaxDepth, GlobeFillSubdivideDispatch.DefaultMaxOutputVertices,
                out var v, out var idx);
            try { Assert.AreEqual(3, v.Length, "a flat projection (constant up) must NOT subdivide"); }
            finally { v.Dispose(); idx.Dispose(); }
        }

        [Test]
        public void Budget_BoundsTheOutput_NoLowZoomExplosion()
        {
            // depth 8 unbounded ≈ 4^8·3 ≈ 196k verts for ONE whole-globe triangle; the 2 000 budget must cap it.
            Run(new SphericalProjection(), new TileId { Z = 0, X = 0, Y = 0 }, 8, 2000, out var v, out var idx);
            try
            {
                // vertex sharing: the budget counts EMITTED vertices (idx.Length, one
                // OutIndices.Add per Emit call) — v (OutVerts, the unique count) is always <= idx.Length, so
                // asserting on v no longer pins the bound that actually exists: sharing made it strictly
                // easier to pass without the budget doing any more work. Assert on idx.Length instead.
                Assert.Less(idx.Length, 20000, "the vertex budget must prevent the low-zoom subdivision explosion");
            }
            finally { v.Dispose(); idx.Dispose(); }
        }
    }
}
