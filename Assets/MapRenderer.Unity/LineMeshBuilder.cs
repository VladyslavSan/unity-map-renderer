using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Tightly-packed struct for interleaved Position + Normal in stream 0.
    /// Stride = 6 × 4 = 24 bytes, matching the descriptor layout (Position float3 + Normal float3).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PositionNormal
    {
        public Vector3 Position;
        public Vector3 Normal;
    }

    /// <summary>
    /// S14: stream-3 struct carrying widthScale (TEXCOORD2) and per-vertex color (COLOR),
    /// interleaved on the same stream. Stride = 4 (float) + 16 (float4) = 20 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WidthScaleColor
    {
        public float   WidthScale;
        public Vector4 Color;
    }

    /// <summary>
    /// Assembles <see cref="LineTessellator.Result"/> output into a <see cref="Mesh"/> with
    /// an explicit vertex-attribute layout matching the Line shader contract.
    ///
    /// Vertex attribute layout (shader contract — do NOT change without updating Line.shader):
    /// <list type="bullet">
    ///   <item><description>POSITION (float3)  — centerline position: east=+X, height=+Y=0, north=+Z.
    ///     double2 (x,y) maps to float3 (x, 0, y).</description></item>
    ///   <item><description>NORMAL   (float3)  — constant +Y = (0,1,0) lighting/surface normal (S33).
    ///     NOT the extrusion direction. Kept separate to satisfy the lit-rendering convention.</description></item>
    ///   <item><description>TEXCOORD0 (float2) — extrusion normal (xy in meter space; miter normals have |n|>1).</description></item>
    ///   <item><description>TEXCOORD1 (float2) — (side ∈ {+1,−1}, distanceAlong).</description></item>
    ///   <item><description>TEXCOORD2 (float)  — widthScale (per-feature scale, default=1).</description></item>
    ///   <item><description>COLOR     (float4) — S14: per-vertex baked color (RGBA). White = identity
    ///     multiply when not data-driven. Interleaved on stream 3 alongside TEXCOORD2.</description></item>
    /// </list>
    ///
    /// Stream 3 layout: <see cref="WidthScaleColor"/> struct — TEXCOORD2 (float) then COLOR (float4),
    /// 20-byte stride, matching the descriptor order (TEXCOORD2 first, Color second).
    ///
    /// Normal packing note: the extrusion normal is stored as a raw float2 — NOT SNORM. Miter
    /// join normals exceed length 1 (the miter factor encodes the corner sharpness). Using SNORM
    /// would clip miter normals and produce wrong width at sharp corners.
    ///
    /// Usage: call <see cref="AddLineResult"/> for each tessellated feature, then <see cref="Build"/>.
    /// </summary>
    public sealed class LineMeshBuilder
    {
        // MapRenderer.Line.MeshBuild — wraps the line mesh assembly + upload in Build().
        private static readonly ProfilerMarker PmLineMeshBuild = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Line.MeshBuild");

        // White = identity multiply: default color for vertices when data-driven color not set.
        private static readonly Vector4 White = new Vector4(1f, 1f, 1f, 1f);

        // Raw attribute arrays — filled per vertex.
        private readonly List<Vector3>        _positions    = new List<Vector3>();
        private readonly List<Vector3>        _lightNormals = new List<Vector3>(); // NORMAL: constant +Y (S33)
        private readonly List<Vector2>        _extrudeNs    = new List<Vector2>(); // TEXCOORD0: extrusion normal
        private readonly List<Vector2>        _sideAndDist  = new List<Vector2>(); // TEXCOORD1: (side, distAlong)
        private readonly List<WidthScaleColor> _stream3     = new List<WidthScaleColor>(); // TEXCOORD2 + COLOR
        private readonly List<int>            _indices      = new List<int>();

        /// <summary>Total vertex count accumulated so far.</summary>
        public int VertexCount => _positions.Count;

        /// <summary>Clear all accumulated geometry (allows builder reuse without new allocation).</summary>
        public void Clear()
        {
            _positions.Clear();
            _lightNormals.Clear();
            _extrudeNs.Clear();
            _sideAndDist.Clear();
            _stream3.Clear();
            _indices.Clear();
        }

        /// <summary>Total index count accumulated so far.</summary>
        public int IndexCount => _indices.Count;

        /// <summary>
        /// Append a <see cref="LineTessellator.Result"/> to the builder with white (identity) vertex color.
        /// All vertex attributes are appended; indices are offset by the current global vertex count.
        /// </summary>
        public void AddLineResult(LineTessellator.Result result)
            => AddLineResult(result, White);

        /// <summary>
        /// Append a <see cref="LineTessellator.Result"/> to the builder with a specified per-feature vertex color.
        /// Used by <see cref="StyledLineTileBuilder"/> for S14 data-driven color baking.
        /// The color should already be linearized (sRGB→linear) before passing here.
        /// </summary>
        public void AddLineResult(LineTessellator.Result result, Vector4 vertexColor)
        {
            if (result.Vertices == null || result.Indices == null ||
                result.Vertices.Length == 0 || result.Indices.Length == 0)
                return;

            int offset = _positions.Count;

            foreach (var v in result.Vertices)
            {
                // double2 (east=x, north=y) → float3 (east=+X, height=+Y=0, north=+Z).
                _positions.Add(new Vector3((float)v.Position.x, 0f, (float)v.Position.y));
                // Lighting normal is always +Y (flat XZ geometry). The extrusion direction is on TEXCOORD0.
                _lightNormals.Add(Vector3.up);
                _extrudeNs.Add(new Vector2((float)v.Normal.x, (float)v.Normal.y));
                _sideAndDist.Add(new Vector2(v.Side, (float)v.DistanceAlong));
                // Stream 3: widthScale + vertex color (S14).
                _stream3.Add(new WidthScaleColor { WidthScale = v.WidthScale, Color = vertexColor });
            }

            foreach (int idx in result.Indices)
                _indices.Add(offset + idx);
        }

        /// <summary>
        /// Build and return the <see cref="Mesh"/> with explicit vertex layout matching the
        /// Line shader contract. Returns null if no geometry was added.
        /// </summary>
        public Mesh Build()
        {
            if (_positions.Count == 0)
                return null;

            using var sLineMeshBuild = PmLineMeshBuild.Auto();

            var mesh = new Mesh
            {
                name        = "LineMesh",
                indexFormat = IndexFormat.UInt32,
            };

            // Set vertex count first so SetVertexBufferData works.
            // Unity supports max 4 vertex streams (0–3).
            // S33 + S14 layout (Position + Normal share stream 0; max 4 streams fits):
            //   Stream 0 — Position (float3) + Normal (float3, +Y) — interleaved (PositionNormal struct).
            //   Stream 1 — TEXCOORD0 (float2): extrusion normal.
            //   Stream 2 — TEXCOORD1 (float2): (side, distanceAlong).
            //   Stream 3 — TEXCOORD2 (float) + COLOR (float4): widthScale + per-vertex color (S14, 20B stride).
            mesh.SetVertexBufferParams(_positions.Count, new[]
            {
                new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream: 2),
                // S14: stream 3 carries TEXCOORD2 (widthScale, 4B) then Color (float4, 16B) = 20B stride.
                // Descriptor order must match WidthScaleColor struct field order.
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 1, stream: 3),
                new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4, stream: 3),
            });

            // Stream 0 — Position + Normal interleaved via PositionNormal struct.
            // SetVertexBufferData<T> matches the struct stride (6 × 4 = 24 bytes) to the
            // stream stride computed from the descriptors (Position 12B + Normal 12B = 24B). ✓
            int vCount = _positions.Count;
            var stream0 = new PositionNormal[vCount];
            for (int i = 0; i < vCount; i++)
                stream0[i] = new PositionNormal { Position = _positions[i], Normal = _lightNormals[i] };
            mesh.SetVertexBufferData(stream0, 0, 0, vCount, stream: 0);

            // Extrusion normals (stream 1 — TEXCOORD0).
            var extArr = _extrudeNs.ToArray();
            mesh.SetVertexBufferData(extArr, 0, 0, extArr.Length, stream: 1);

            // Side + distanceAlong (stream 2 — TEXCOORD1).
            var sidArr = _sideAndDist.ToArray();
            mesh.SetVertexBufferData(sidArr, 0, 0, sidArr.Length, stream: 2);

            // Stream 3 — WidthScale (TEXCOORD2) + Color (COLOR), 20B stride via WidthScaleColor struct.
            // Default color = white (identity multiply) when set via AddLineResult(result) overload.
            var s3Arr = _stream3.ToArray();
            mesh.SetVertexBufferData(s3Arr, 0, 0, s3Arr.Length, stream: 3);

            // Index buffer.
            mesh.SetIndexBufferParams(_indices.Count, IndexFormat.UInt32);
            var idxArr = _indices.ToArray();
            mesh.SetIndexBufferData(idxArr, 0, 0, idxArr.Length);

            // One sub-mesh covering all triangles.
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, _indices.Count, MeshTopology.Triangles));

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
