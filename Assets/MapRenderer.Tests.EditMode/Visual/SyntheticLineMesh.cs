// S54: test-only helper for building a single-line mesh via StyledLineTileBuilder.
// Replaces the retired LineMeshBuilder in all snapshot tests that needed a synthetic line mesh.

using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Builds a synthetic line mesh via <see cref="StyledLineTileBuilder"/> from a
    /// <see cref="LineTessellator.Result"/> (or a polyline point list), using a solid white
    /// vertex color and unit WidthScale. Callers attach the returned <see cref="Mesh"/> to a
    /// <see cref="GameObject"/> and set material uniforms as needed.
    ///
    /// This replaces the retired <c>LineMeshBuilder</c> in all snapshot tests (S54).
    /// </summary>
    internal static class SyntheticLineMesh
    {
        /// <summary>
        /// Build a <see cref="Mesh"/> from a <see cref="LineTessellator.Result"/>.
        /// All vertices get white color and WidthScale=1 (uniform line width driven by shader
        /// <c>_Width</c>/<c>_MetersPerPixel</c> uniforms, not per-vertex scale).
        /// Returns null when the result has no vertices.
        /// </summary>
        public static Mesh BuildFromResult(LineTessellator.Result result)
            => BuildFromResult(result, new Vector4(1f, 1f, 1f, 1f), widthScaleOverride: null);

        /// <summary>
        /// Build a <see cref="Mesh"/> from a <see cref="LineTessellator.Result"/> with an explicit
        /// per-vertex baked <paramref name="color"/> written into the stream-3 <c>LineWidthColor.Color</c>
        /// field, and an optional <paramref name="widthScaleOverride"/> written into the stream-3
        /// <c>LineWidthColor.WidthScale</c> field (when null, the tessellator's per-vertex miter factor
        /// is used). This exercises the REAL stream-3 interleave (S54 LineWidthColor flip): a struct-order
        /// regression scrambles which bytes land in COLOR vs TEXCOORD2 for non-unit values.
        /// Returns null when the result has no vertices.
        /// </summary>
        public static Mesh BuildFromResult(LineTessellator.Result result, Vector4 color, float? widthScaleOverride)
        {
            if (result.Vertices == null || result.Vertices.Length == 0) return null;
            if (result.Indices  == null || result.Indices.Length  == 0) return null;

            int vCount = result.Vertices.Length;
            int iCount = result.Indices.Length;

            var data = new StyledLineTileBuilder.LayerMeshData
            {
                Stream0PositionNormal = new NativeArray<StyledLineTileBuilder.LinePositionNormal>(vCount, Allocator.Persistent),
                Stream1ExtrudeN       = new NativeArray<Vector2>(vCount, Allocator.Persistent),
                Stream2SideAndDist    = new NativeArray<Vector2>(vCount, Allocator.Persistent),
                Stream3WidthColor     = new NativeArray<StyledLineTileBuilder.LineWidthColor>(vCount, Allocator.Persistent),
                Indices               = new NativeArray<int>(iCount, Allocator.Persistent),
                VertexCount           = vCount,
                IndexCount            = iCount,
                IsCreated             = true,
            };

            // Increment the live-alloc counter so Dispose stays balanced.
            System.Threading.Interlocked.Increment(ref StyledLineTileBuilder.LayerMeshData.LiveAllocCount);

            for (int i = 0; i < vCount; i++)
            {
                var v = result.Vertices[i];
                data.Stream0PositionNormal[i] = new StyledLineTileBuilder.LinePositionNormal
                {
                    Position = new Vector3((float)v.Position.x, 0f, (float)v.Position.y),
                    Normal   = Vector3.up,
                };
                data.Stream1ExtrudeN[i]    = new Vector2((float)v.Normal.x, (float)v.Normal.y);
                data.Stream2SideAndDist[i] = new Vector2(v.Side, (float)v.DistanceAlong);
                data.Stream3WidthColor[i]  = new StyledLineTileBuilder.LineWidthColor
                {
                    Color      = color,
                    // When an override is given, multiply the tessellator miter factor by it so the
                    // ribbon scales uniformly (matches StyledLineTileBuilder's data-driven width bake).
                    WidthScale = widthScaleOverride.HasValue ? v.WidthScale * widthScaleOverride.Value : v.WidthScale,
                };
            }

            for (int i = 0; i < iCount; i++)
                data.Indices[i] = result.Indices[i];

            Mesh mesh;
            try
            {
                mesh = StyledLineTileBuilder.UploadMesh(data);
            }
            finally
            {
                data.Dispose();
            }
            return mesh;
        }

        /// <summary>
        /// Build a <see cref="Mesh"/> from a polyline point list (world-space double2 coords),
        /// tessellated with <see cref="JoinType.Miter"/> / <see cref="CapType.Butt"/>.
        /// </summary>
        public static Mesh BuildFromPoints(IReadOnlyList<double2> pts,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var result = LineTessellator.Triangulate(pts, join, cap);
            return BuildFromResult(result);
        }

        /// <summary>
        /// Build a <see cref="Mesh"/> from a polyline with an explicit per-vertex baked
        /// <paramref name="color"/> and <paramref name="widthScale"/> multiplier written into the real
        /// stream-3 <c>LineWidthColor</c> interleave. Used by the S54 LineWidthColor-flip falsifiability
        /// tooth.
        /// </summary>
        public static Mesh BuildFromPoints(IReadOnlyList<double2> pts, Vector4 color, float widthScale,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var result = LineTessellator.Triangulate(pts, join, cap);
            return BuildFromResult(result, color, widthScale);
        }

        /// <summary>
        /// Build the three golden test shapes (horizontal, L-shape, diagonal) from
        /// <see cref="LineBootstrap"/>-equivalent geometry, all combined into one mesh.
        /// Used by tests that previously relied on <see cref="LineBootstrap"/>.
        /// </summary>
        public static Mesh BuildGoldenShapes(JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var lines = new List<IReadOnlyList<double2>>
            {
                // Horizontal segment.
                new List<double2> { new double2(-40, 0),  new double2( 40, 0)  },
                // L-shape.
                new List<double2> { new double2(-30, 30), new double2(-30, -30), new double2(30, -30) },
                // Diagonal.
                new List<double2> { new double2(-20, -20), new double2(20, 20)  },
            };

            int totalV = 0, totalI = 0;
            var results = new LineTessellator.Result[lines.Count];
            for (int k = 0; k < lines.Count; k++)
            {
                results[k] = LineTessellator.Triangulate(lines[k], join, cap);
                if (results[k].Vertices != null) totalV += results[k].Vertices.Length;
                if (results[k].Indices  != null) totalI += results[k].Indices.Length;
            }

            if (totalV == 0 || totalI == 0) return null;

            var data = new StyledLineTileBuilder.LayerMeshData
            {
                Stream0PositionNormal = new NativeArray<StyledLineTileBuilder.LinePositionNormal>(totalV, Allocator.Persistent),
                Stream1ExtrudeN       = new NativeArray<Vector2>(totalV, Allocator.Persistent),
                Stream2SideAndDist    = new NativeArray<Vector2>(totalV, Allocator.Persistent),
                Stream3WidthColor     = new NativeArray<StyledLineTileBuilder.LineWidthColor>(totalV, Allocator.Persistent),
                Indices               = new NativeArray<int>(totalI, Allocator.Persistent),
                VertexCount           = totalV,
                IndexCount            = totalI,
                IsCreated             = true,
            };
            System.Threading.Interlocked.Increment(ref StyledLineTileBuilder.LayerMeshData.LiveAllocCount);

            var white = new Vector4(1f, 1f, 1f, 1f);
            int vOff = 0, iOff = 0;
            foreach (var res in results)
            {
                if (res.Vertices == null) continue;
                for (int i = 0; i < res.Vertices.Length; i++)
                {
                    var v = res.Vertices[i];
                    data.Stream0PositionNormal[vOff + i] = new StyledLineTileBuilder.LinePositionNormal
                    {
                        Position = new Vector3((float)v.Position.x, 0f, (float)v.Position.y),
                        Normal   = Vector3.up,
                    };
                    data.Stream1ExtrudeN[vOff + i]    = new Vector2((float)v.Normal.x, (float)v.Normal.y);
                    data.Stream2SideAndDist[vOff + i] = new Vector2(v.Side, (float)v.DistanceAlong);
                    data.Stream3WidthColor[vOff + i]  = new StyledLineTileBuilder.LineWidthColor
                    {
                        Color      = white,
                        WidthScale = v.WidthScale,
                    };
                }
                if (res.Indices != null)
                {
                    for (int i = 0; i < res.Indices.Length; i++)
                        data.Indices[iOff + i] = vOff + res.Indices[i];
                    iOff += res.Indices.Length;
                }
                vOff += res.Vertices.Length;
            }

            Mesh mesh;
            try
            {
                mesh = StyledLineTileBuilder.UploadMesh(data);
            }
            finally
            {
                data.Dispose();
            }
            return mesh;
        }
    }
}
