using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Assembles <see cref="LineTessellator.Result"/> output into a <see cref="Mesh"/> with
    /// an explicit vertex-attribute layout matching the Line shader contract.
    ///
    /// Vertex attribute layout (shader contract — do NOT change without updating Line.shader):
    /// <list type="bullet">
    ///   <item><description>POSITION (float3)  — centerline position: east=+X, height=+Y=0, north=+Z.
    ///     double2 (x,y) maps to float3 (x, 0, y).</description></item>
    ///   <item><description>TEXCOORD0 (float2) — extrusion normal (xy in meter space; miter normals have |n|>1).</description></item>
    ///   <item><description>TEXCOORD1 (float2) — (side ∈ {+1,−1}, distanceAlong).</description></item>
    ///   <item><description>TEXCOORD2 (float)  — widthScale (reserved for S12 data-driven width).</description></item>
    /// </list>
    ///
    /// Normal packing note: the extrusion normal is stored as a raw float2 — NOT SNORM. Miter
    /// join normals exceed length 1 (the miter factor encodes the corner sharpness). Using SNORM
    /// would clip miter normals and produce wrong width at sharp corners.
    ///
    /// Usage: call <see cref="AddLineResult"/> for each tessellated feature, then <see cref="Build"/>.
    /// </summary>
    public sealed class LineMeshBuilder
    {
        // Raw attribute arrays — filled per vertex in POSITION/TEXCOORD0/TEXCOORD1/TEXCOORD2 order.
        private readonly List<Vector3> _positions  = new List<Vector3>();
        private readonly List<Vector2> _normals    = new List<Vector2>(); // TEXCOORD0: extrusion normal
        private readonly List<Vector2> _sideAndDist = new List<Vector2>(); // TEXCOORD1: (side, distAlong)
        private readonly List<float>   _widthScales = new List<float>();   // TEXCOORD2: widthScale
        private readonly List<int>     _indices    = new List<int>();

        /// <summary>Total vertex count accumulated so far.</summary>
        public int VertexCount => _positions.Count;

        /// <summary>Total index count accumulated so far.</summary>
        public int IndexCount => _indices.Count;

        /// <summary>
        /// Append a <see cref="LineTessellator.Result"/> to the builder.
        /// All vertex attributes are appended; indices are offset by the current global vertex count.
        /// </summary>
        public void AddLineResult(LineTessellator.Result result)
        {
            if (result.Vertices == null || result.Indices == null ||
                result.Vertices.Length == 0 || result.Indices.Length == 0)
                return;

            int offset = _positions.Count;

            foreach (var v in result.Vertices)
            {
                // double2 (east=x, north=y) → float3 (east=+X, height=+Y=0, north=+Z).
                _positions.Add(new Vector3((float)v.Position.x, 0f, (float)v.Position.y));
                _normals.Add(new Vector2((float)v.Normal.x, (float)v.Normal.y));
                _sideAndDist.Add(new Vector2(v.Side, (float)v.DistanceAlong));
                _widthScales.Add(v.WidthScale);
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

            var mesh = new Mesh
            {
                name        = "LineMesh",
                indexFormat = IndexFormat.UInt32,
            };

            // Set vertex count first so SetVertexBufferData works.
            mesh.SetVertexBufferParams(_positions.Count, new[]
            {
                new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream: 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 1, stream: 3),
            });

            // Positions (stream 0).
            var posArr = _positions.ToArray();
            mesh.SetVertexBufferData(posArr, 0, 0, posArr.Length, stream: 0);

            // Extrusion normals (stream 1 — TEXCOORD0).
            var normArr = _normals.ToArray();
            mesh.SetVertexBufferData(normArr, 0, 0, normArr.Length, stream: 1);

            // Side + distanceAlong (stream 2 — TEXCOORD1).
            var sidArr = _sideAndDist.ToArray();
            mesh.SetVertexBufferData(sidArr, 0, 0, sidArr.Length, stream: 2);

            // WidthScale (stream 3 — TEXCOORD2).
            var wsArr = _widthScales.ToArray();
            mesh.SetVertexBufferData(wsArr, 0, 0, wsArr.Length, stream: 3);

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
