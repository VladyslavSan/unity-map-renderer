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
    ///
    /// Normals: explicitly set to Vector3.up (+Y) per vertex. Fill geometry is flat on the XZ plane;
    /// the lighting/surface normal is unconditionally +Y. RecalculateNormals is NOT used — it infers
    /// normals from winding, which is inconsistent with the current mixed-winding geometry, yielding
    /// wrong lighting. Per docs/lit-rendering-design.md §"Geometry contract": do NOT overload the
    /// lighting NORMAL channel with an extrusion direction.
    ///
    /// UV0 (S34): tile-local [0,1] coordinates derived from the original tile-space vertices divided
    /// by the tile extent. Required for base map, normal map, and other texture sampling in the
    /// full URP Lit material surface (InitializeStandardLitSurfaceData reads input.uv).
    /// UV = (tileX / extent, tileZ / extent) where tileX/tileZ are the pre-projection tile coords.
    ///
    /// Tangents (S34): constant float4(1,0,0,1) per vertex — the +X tangent against the +Y normal
    /// yields a valid orthonormal tangent space for the flat XZ fill geometry. Required when
    /// _NORMALMAP or _DETAIL shader_feature is enabled (GetVertexNormalInputs reads tangentOS).
    /// The .w component is the bitangent sign (+1 = right-handed).
    /// </summary>
    public sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices  = new List<Vector3>();
        private readonly List<Vector3> _normals   = new List<Vector3>();
        private readonly List<Vector2> _uvs       = new List<Vector2>();
        private readonly List<Vector4> _tangents  = new List<Vector4>();
        private readonly List<int>     _indices   = new List<int>();

        // Constant tangent: +X direction, +1 bitangent sign, valid for flat +Y-normal geometry.
        private static readonly Vector4 FlatTangent = new Vector4(1f, 0f, 0f, 1f);

        /// <summary>
        /// Append a feature's projected vertices and earcut indices to the builder.
        /// UV0 and tangent channels are still populated (UV0 = zero, tangent = FlatTangent).
        /// Use the 4-arg overload to supply tile-space UVs for texture sampling.
        /// </summary>
        public void AddFeature(float3[] verts, int[] triangleIndices)
        {
            AddFeature(verts, triangleIndices, null, 1.0);
        }

        /// <summary>
        /// Append a feature's projected vertices, tile-space UVs, and earcut indices to the builder.
        /// Indices are offset by the current vertex count so they index into the global list.
        /// </summary>
        /// <param name="verts">World-space (origin-relative) float3 vertices.</param>
        /// <param name="triangleIndices">Earcut triangle indices (into verts).</param>
        /// <param name="tileVerts">Pre-projection tile-space double2 coords for UV generation.
        ///   Must have the same length as verts. UV = (tileX/extent, tileZ/extent).</param>
        /// <param name="extent">Tile extent in tile units (typically 4096). Used to normalise UVs.</param>
        public void AddFeature(float3[] verts, int[] triangleIndices, double2[] tileVerts, double extent)
        {
            if (verts == null || triangleIndices == null || verts.Length == 0 || triangleIndices.Length == 0)
                return;

            int offset = _vertices.Count;
            double extentInv = extent > 0.0 ? 1.0 / extent : 0.0;

            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                _vertices.Add(new Vector3(v.x, v.y, v.z));
                _normals.Add(Vector3.up);  // explicit +Y: flat XZ fill geometry, lighting normal is always up

                // UV0: tile-local [0,1] from pre-projection tile coordinates.
                // tileVerts[i].x = tile-space east; tileVerts[i].y = tile-space north (note: MVT y is inverted).
                if (tileVerts != null && i < tileVerts.Length)
                {
                    float u = (float)(tileVerts[i].x * extentInv);
                    float vCoord = (float)(tileVerts[i].y * extentInv);
                    _uvs.Add(new Vector2(u, vCoord));
                }
                else
                {
                    _uvs.Add(Vector2.zero);
                }

                _tangents.Add(FlatTangent);
            }

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
            mesh.SetNormals(_normals);   // explicit +Y per vertex (not RecalculateNormals — winding is mixed)
            mesh.SetUVs(0, _uvs);
            mesh.SetTangents(_tangents);
            mesh.SetTriangles(_indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public int VertexCount => _vertices.Count;
        public int IndexCount  => _indices.Count;
    }
}
