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
    ///
    /// Vertex COLOR (S12): optional per-vertex color channel for data-driven styling.
    /// When supplied via AddFeature(…, Color featureColor), one color is baked per feature and
    /// replicated across all its vertices. The COLOR stream is a mesh attribute — it does NOT live
    /// in the UnityPerMaterial CBUFFER, so the SRP Batcher is not broken by its presence.
    /// In the shader, vertex color × material _MapColor × _Opacity implement the composite
    /// (data-driven × zoom) styling semantics.
    /// Color space (D2): Core Color channels are sRGB [0,1]. Before upload to Mesh.SetColors,
    /// each color is converted to linear via Color.linear (the standard sRGB→linear gamma ramp).
    /// This matches the _MapColor uniform path: material.SetColor performs sRGB→linear at the
    /// material boundary when the project is in Linear color space (Unity's default for URP).
    /// Without this conversion, the vertex color stream would be in sRGB while _MapColor is in
    /// linear — the shader multiply would combine mismatched spaces and produce wrong brightness.
    /// Default (when no per-vertex color is provided): Color.white linearized = Color.white
    /// (white is its own linear value), preserving S11 behavior unchanged.
    /// </summary>
    public sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices  = new List<Vector3>();
        private readonly List<Vector3> _normals   = new List<Vector3>();
        private readonly List<Vector2> _uvs       = new List<Vector2>();
        private readonly List<Vector4> _tangents  = new List<Vector4>();
        private readonly List<int>     _indices   = new List<int>();
        private readonly List<Color>   _colors    = new List<Color>();

        // Constant tangent: +X direction, +1 bitangent sign, valid for flat +Y-normal geometry.
        private static readonly Vector4 FlatTangent = new Vector4(1f, 0f, 0f, 1f);

        /// <summary>
        /// Append a feature's projected vertices and earcut indices to the builder.
        /// UV0 and tangent channels are populated; vertex color defaults to white.
        /// </summary>
        public void AddFeature(float3[] verts, int[] triangleIndices)
        {
            AddFeature(verts, triangleIndices, null, 1.0, Color.white);
        }

        /// <summary>
        /// Append a feature's projected vertices, tile-space UVs, and earcut indices to the builder.
        /// Vertex color defaults to white (S11 behavior: albedo × white = albedo unchanged).
        /// </summary>
        /// <param name="verts">World-space (origin-relative) float3 vertices.</param>
        /// <param name="triangleIndices">Earcut triangle indices (into verts).</param>
        /// <param name="tileVerts">Pre-projection tile-space double2 coords for UV generation.
        ///   Must have the same length as verts. UV = (tileX/extent, tileZ/extent).</param>
        /// <param name="extent">Tile extent in tile units (typically 4096). Used to normalise UVs.</param>
        public void AddFeature(float3[] verts, int[] triangleIndices, double2[] tileVerts, double extent)
        {
            AddFeature(verts, triangleIndices, tileVerts, extent, Color.white);
        }

        /// <summary>
        /// Append a feature's projected vertices, tile-space UVs, earcut indices, and a per-feature
        /// vertex color to the builder. The color is replicated across all vertices of this feature.
        /// This is the S12 data-driven path.
        /// </summary>
        /// <param name="verts">World-space (origin-relative) float3 vertices.</param>
        /// <param name="triangleIndices">Earcut triangle indices (into verts).</param>
        /// <param name="tileVerts">Pre-projection tile-space double2 coords for UV generation.</param>
        /// <param name="extent">Tile extent in tile units (typically 4096).</param>
        /// <param name="featureColor">Per-feature vertex color baked from a data-driven expression.
        ///   Replicated across all vertices of this feature. Use Color.white for S11 uniform behavior.</param>
        public void AddFeature(float3[] verts, int[] triangleIndices, double2[] tileVerts, double extent,
            Color featureColor)
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

                // Per-vertex color: same color for all vertices in this feature.
                // The COLOR stream is a mesh attribute — not in UnityPerMaterial CBUFFER (SRP Batcher safe).
                _colors.Add(featureColor);
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

            // Vertex colors: always set — defaults to white when no data-driven color is baked.
            // D2 gamma fix: convert sRGB→linear before upload. Mesh.SetColors does NOT linearize
            // (unlike material.SetColor which linearizes at the material boundary in Linear color space).
            // Without this, vertex colors would be sRGB while _MapColor is linear → wrong composite.
            // Color.linear applies the standard sRGB→linear gamma ramp (IEC 61966-2-1).
            // white.linear = white: S11 zoom-uniform behavior is preserved exactly.
            var linearColors = new System.Collections.Generic.List<Color>(_colors.Count);
            foreach (var c in _colors)
                linearColors.Add(c.linear);
            mesh.SetColors(linearColors);

            mesh.SetTriangles(_indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public int VertexCount => _vertices.Count;
        public int IndexCount  => _indices.Count;
    }
}
