using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Lines;

namespace MapRenderer.Tests.TestSupport
{
    /// <summary>
    /// Runs <see cref="RibbonJob"/> over a FLAT centerline (points on the XZ plane, <c>up = +Y</c>): the
    /// 2D-into-3D harness shared by every EditMode test that needs the Burst ribbon builder's output
    /// without a projection or a GPU. Maps 2D <c>(x, y)</c> to 3D <c>(x, 0, y)</c>.
    /// </summary>
    internal static class FlatRibbon
    {
        /// <summary>Builds the ribbon for <paramref name="pts"/> and returns its vertices/indices.</summary>
        public static (LineRibbonVertex[] Vertices, int[] Indices) Build(
            IReadOnlyList<double2> pts, JoinType join, CapType cap,
            double miterLimit = 2.0, int roundSegments = 4, double roundLimit = 1.05)
        {
            int n = pts.Count;
            int capV = RibbonJob.MaxVertexCount(n, roundSegments);
            int capI = RibbonJob.MaxIndexCount(n, roundSegments);

            var points = new NativeArray<double3>(n == 0 ? 1 : n, Allocator.TempJob);
            var ups    = new NativeArray<double3>(n == 0 ? 1 : n, Allocator.TempJob);
            var outV   = new NativeArray<LineRibbonVertex>(capV == 0 ? 1 : capV, Allocator.TempJob);
            var outI   = new NativeArray<int>(capI == 0 ? 1 : capI, Allocator.TempJob);
            var vc     = new NativeArray<int>(1, Allocator.TempJob);
            var ic     = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    points[i] = new double3(pts[i].x, 0.0, pts[i].y); // flat centerline: 2D (x,y) → 3D (x,0,y)
                    ups[i]    = new double3(0.0, 1.0, 0.0);            // Mercator up = +Y
                }

                new RibbonJob
                {
                    Points         = points,
                    Ups            = ups,
                    PointCount     = n,
                    Join           = join,
                    Cap            = cap,
                    MiterLimit     = miterLimit,
                    RoundSegments  = roundSegments,
                    RoundLimit     = roundLimit,
                    OutVertices    = outV,
                    OutIndices     = outI,
                    OutVertexCount = vc,
                    OutIndexCount  = ic,
                }.Schedule().Complete();

                int nv = vc[0], ni = ic[0];
                var verts   = new LineRibbonVertex[nv];
                var indices = new int[ni];
                for (int i = 0; i < nv; i++) verts[i]   = outV[i];
                for (int i = 0; i < ni; i++) indices[i] = outI[i];
                return (verts, indices);
            }
            finally
            {
                points.Dispose(); ups.Dispose(); outV.Dispose(); outI.Dispose(); vc.Dispose(); ic.Dispose();
            }
        }
    }
}
