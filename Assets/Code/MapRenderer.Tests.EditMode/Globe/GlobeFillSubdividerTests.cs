// S91-C (C-3): the globe fill subdivider (Burst job) must refine flat earcut triangles onto the sphere, pass
// flat Mercator straight through, and never explode (depth cap + vertex budget). Run through the same dispatch
// + NativeArray/NativeList path the fill builder uses.

using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Globe
{
    public class GlobeFillSubdividerTests
    {
        private const double Extent = 4096;

        // One full-tile triangle → run the Burst subdivide job into NativeLists (caller disposes).
        private static void Run(IProjection proj, TileId id, int maxDepth, int budget,
            out NativeList<GlobeFillVertex> verts, out NativeList<int> idx)
        {
            var tileVerts = new NativeArray<double2>(3, Allocator.Persistent);
            tileVerts[0] = new double2(0, 0); tileVerts[1] = new double2(Extent, 0); tileVerts[2] = new double2(0, Extent);
            var tris = new NativeArray<int>(3, Allocator.Persistent); tris[0] = 0; tris[1] = 1; tris[2] = 2;
            var feat = new NativeArray<int>(3, Allocator.Persistent); // all feature 0

            verts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            idx   = new NativeList<int>(64, Allocator.Persistent);
            GlobeFillSubdivideDispatch.Run(
                proj, tileVerts, tris, feat, 3, 3, id, Extent, new double3(0, 0, 0),
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, maxDepth, budget, verts, idx);

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
                Assert.AreEqual(v.Length, idx.Length, "no dedup ⇒ sequential indices, one per emitted vertex");
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
            try { Assert.Less(v.Length, 20000, "the vertex budget must prevent the low-zoom subdivision explosion"); }
            finally { v.Dispose(); idx.Dispose(); }
        }
    }
}
