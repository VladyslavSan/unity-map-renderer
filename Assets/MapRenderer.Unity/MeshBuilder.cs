using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Assembles per-feature float3 vertices and int indices into a UnityEngine.Mesh with
    /// UInt32 index format (required for dense tiles that exceed 65535 vertices).
    ///
    /// Usage: call AddFeature() for each triangulated feature, then Build() to create the Mesh.
    /// East=+X, height=+Y, north=+Z (matching ProjectTileVerticesJob output and docs §7 conventions).
    /// </summary>
    public sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<int> _indices = new List<int>();

        /// <summary>
        /// Append a feature's projected vertices and earcut indices to the builder.
        /// Indices are offset by the current vertex count so they index into the global list.
        /// </summary>
        public void AddFeature(float3[] verts, int[] triangleIndices)
        {
            if (verts == null || triangleIndices == null || verts.Length == 0 || triangleIndices.Length == 0)
                return;

            int offset = _vertices.Count;
            foreach (var v in verts)
                _vertices.Add(new Vector3(v.x, v.y, v.z));
            foreach (var idx in triangleIndices)
                _indices.Add(offset + idx);
        }

        /// <summary>
        /// Build and return the combined UnityEngine.Mesh. indexFormat is always UInt32.
        /// Returns null if no geometry was added.
        /// </summary>
        public Mesh Build()
        {
            if (_vertices.Count == 0)
                return null;

            var mesh = new Mesh
            {
                name = "MapFill",
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(_vertices);
            mesh.SetTriangles(_indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public int VertexCount => _vertices.Count;
        public int IndexCount => _indices.Count;
    }
}
