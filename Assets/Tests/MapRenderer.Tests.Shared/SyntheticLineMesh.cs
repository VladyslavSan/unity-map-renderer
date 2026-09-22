// S54: test-only helper for building a single-line mesh with the production line vertex layout.
// S89 Stage B: builds via the Mesh.MeshData advanced API (LayerMeshData + UploadMesh retired), reusing
// StyledLineTileBuilder.LineVertexDescriptors so the REAL stream-3 interleave is still exercised.
// UMR-173: the generator moved from the retired managed line tessellator onto RibbonJob (via the shared
// FlatRibbon harness) — same flat-centerline mapping LineRibbonJobTests uses. Every field the streams
// below read (Position/Across/Side/DistanceAlong/WidthScale) crosses into the mesh through a
// (float) cast, and the documented managed↔Burst divergence (~1e-9 absolute at these coordinate
// magnitudes) is three orders below float's ~1e-7 resolution, so the cast quantises it away.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Unity.Rendering.Meshing;
namespace MapRenderer.Tests
{
    /// <summary>
    /// Builds a synthetic line mesh from a flat polyline point list, with the production line vertex
    /// layout. Callers attach the returned <see cref="Mesh"/> to a <see cref="GameObject"/> and set
    /// material uniforms as needed. Replaces the retired <c>LineMeshBuilder</c>.
    /// </summary>
    internal static class SyntheticLineMesh
    {
        /// <summary>
        /// Build a <see cref="Mesh"/> from a polyline point list (world-space double2 coords),
        /// built with <see cref="JoinType.Miter"/> / <see cref="CapType.Butt"/>.
        /// </summary>
        public static Mesh BuildFromPoints(IReadOnlyList<double2> pts,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var result = FlatRibbon.Build(pts, join, cap);
            return BuildFromRibbon(result, new Vector4(1f, 1f, 1f, 1f), widthScaleOverride: null);
        }

        /// <summary>
        /// Build a <see cref="Mesh"/> from a polyline with an explicit per-vertex baked <paramref name="color"/>
        /// and <paramref name="widthScale"/> multiplier written into the real stream-3 <c>LineWidthColor</c>
        /// interleave. Used by the S54 LineWidthColor-flip falsifiability tooth.
        /// </summary>
        public static Mesh BuildFromPoints(IReadOnlyList<double2> pts, Vector4 color, float widthScale,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var result = FlatRibbon.Build(pts, join, cap);
            return BuildFromRibbon(result, color, widthScale);
        }

        /// <summary>
        /// Build the three golden test shapes (horizontal, L-shape, diagonal) combined into one mesh.
        /// Used by tests that previously relied on <c>LineBootstrap</c>.
        /// </summary>
        public static Mesh BuildGoldenShapes(JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var lines = new List<IReadOnlyList<double2>>
            {
                new List<double2> { new double2(-40, 0),  new double2( 40, 0)  },                       // horizontal
                new List<double2> { new double2(-30, 30), new double2(-30, -30), new double2(30, -30) },// L-shape
                new List<double2> { new double2(-20, -20), new double2(20, 20)  },                      // diagonal
            };

            int totalV = 0, totalI = 0;
            var results = new (LineRibbonVertex[] Vertices, int[] Indices)[lines.Count];
            for (int k = 0; k < lines.Count; k++)
            {
                results[k] = FlatRibbon.Build(lines[k], join, cap);
                totalV += results[k].Vertices.Length;
                totalI += results[k].Indices.Length;
            }

            if (totalV == 0 || totalI == 0) return null;

            var s0 = new StyledLineTileBuilder.LinePositionNormal[totalV];
            var s1 = new Vector3[totalV];
            var s2 = new Vector2[totalV];
            var s3 = new StyledLineTileBuilder.LineWidthColor[totalV];
            var indices = new int[totalI];

            var white = new Vector4(1f, 1f, 1f, 1f);
            int vOff = 0, iOff = 0;
            foreach (var res in results)
            {
                for (int i = 0; i < res.Vertices.Length; i++)
                    WriteVertex(res.Vertices[i], white, null, s0, s1, s2, s3, vOff + i);

                // Winding reversal happens once in Upload() (the shared chokepoint) — keep canonical here.
                for (int i = 0; i < res.Indices.Length; i++)
                    indices[iOff + i] = vOff + res.Indices[i];
                iOff += res.Indices.Length;
                vOff += res.Vertices.Length;
            }

            return Upload(s0, s1, s2, s3, indices);
        }

        /// <summary>Builds a <see cref="Mesh"/> from a <see cref="RibbonJob"/> result, with an explicit
        /// per-vertex baked <paramref name="color"/> and an optional <paramref name="widthScaleOverride"/>
        /// (when null, the builder's per-vertex miter factor is used). Returns null when empty.</summary>
        private static Mesh BuildFromRibbon(
            (LineRibbonVertex[] Vertices, int[] Indices) result, Vector4 color, float? widthScaleOverride)
        {
            if (result.Vertices == null || result.Vertices.Length == 0) return null;
            if (result.Indices  == null || result.Indices.Length  == 0) return null;

            int vCount = result.Vertices.Length;

            var s0 = new StyledLineTileBuilder.LinePositionNormal[vCount];
            var s1 = new Vector3[vCount];
            var s2 = new Vector2[vCount];
            var s3 = new StyledLineTileBuilder.LineWidthColor[vCount];

            for (int i = 0; i < vCount; i++)
                WriteVertex(result.Vertices[i], color, widthScaleOverride, s0, s1, s2, s3, i);

            return Upload(s0, s1, s2, s3, result.Indices);
        }

        /// <summary>Writes one <see cref="LineRibbonVertex"/> into the four production line streams at
        /// index <paramref name="i"/> — the single place every build path interleaves a vertex.</summary>
        private static void WriteVertex(
            LineRibbonVertex v, Vector4 color, float? widthScaleOverride,
            StyledLineTileBuilder.LinePositionNormal[] s0, Vector3[] s1, Vector2[] s2,
            StyledLineTileBuilder.LineWidthColor[] s3, int i)
        {
            s0[i] = new StyledLineTileBuilder.LinePositionNormal
            {
                Position = new Vector3((float)v.Position.x, (float)v.Position.y, (float)v.Position.z),
                Normal   = new Vector3((float)v.Up.x, (float)v.Up.y, (float)v.Up.z),
            };
            s1[i] = new Vector3((float)v.Across.x, (float)v.Across.y, (float)v.Across.z);
            s2[i] = new Vector2(v.Side, (float)v.DistanceAlong);
            s3[i] = new StyledLineTileBuilder.LineWidthColor
            {
                Color      = color,
                WidthScale = widthScaleOverride.HasValue ? v.WidthScale * widthScaleOverride.Value : v.WidthScale,
            };
        }

        /// <summary>Uploads the four line streams + indices to a <see cref="Mesh"/> via the writable-mesh-data
        /// API, using the production <see cref="StyledLineTileBuilder.LineVertexDescriptors"/> layout.</summary>
        private static Mesh Upload(
            StyledLineTileBuilder.LinePositionNormal[] pn, Vector3[] ex, Vector2[] sd,
            StyledLineTileBuilder.LineWidthColor[] wc, int[] indices)
        {
            int vCount = pn.Length;
            int iCount = indices.Length;

            var mda = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData md = mda[0];
            md.SetVertexBufferParams(vCount, StyledLineTileBuilder.LineVertexDescriptors);
            var s0 = md.GetVertexData<StyledLineTileBuilder.LinePositionNormal>(0);
            var s1 = md.GetVertexData<Vector3>(1);
            var s2 = md.GetVertexData<Vector2>(2);
            var s3 = md.GetVertexData<StyledLineTileBuilder.LineWidthColor>(3);
            for (int i = 0; i < vCount; i++) { s0[i] = pn[i]; s1[i] = ex[i]; s2[i] = sd[i]; s3[i] = wc[i]; }

            md.SetIndexBufferParams(iCount, IndexFormat.UInt32);
            var idx = md.GetIndexData<int>();
            // Reverse triangle winding to match StyledLineTileBuilder's GPU-boundary reversal, so these synthetic
            // ribbons stay Unity-front under the shipped MapLine.mat _Cull:2 (stock Cull Back). Single chokepoint
            // for every SyntheticLineMesh build path (BuildFromPoints / BuildGoldenShapes).
            for (int i = 0; i + 2 < iCount; i += 3)
            {
                idx[i + 0] = indices[i + 0];
                idx[i + 1] = indices[i + 2]; // 2nd/3rd
                idx[i + 2] = indices[i + 1]; // swapped
            }

            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, iCount, MeshTopology.Triangles));

            var mesh = new Mesh { name = "SyntheticLine", indexFormat = IndexFormat.UInt32 };
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
