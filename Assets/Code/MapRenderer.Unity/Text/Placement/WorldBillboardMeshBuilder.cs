// Namespace-collision guard (see GlyphAtlasTexture.cs's header): TOP-LEVEL `using Unity.Mathematics;` and
// unqualified types, never an inline `Unity.Mathematics.X`.

using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Assembles a two-stream world-anchored symbol <see cref="Mesh"/> from already-computed CPU corner data.
    /// Stream 0 is the frozen <see cref="WorldBillboardVertex"/> layout. Stream 1 is one <c>float</c> Opacity
    /// (TexCoord4), re-uploaded alone per frame, so a fade update never touches topology. Main-thread only.
    /// </summary>
    public static class WorldBillboardMeshBuilder
    {
        // Non-obvious why: descriptors must ascend in VertexAttribute enum order across all streams, or Unity
        // silently reads the wrong bytes. So Opacity (TexCoord4, stream 1) sits mid-array.
        private static readonly VertexAttributeDescriptor[] VertexDescriptors =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0), // AnchorLocal
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 3, stream: 0), // ColorRGB
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 0), // Uv
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 1, stream: 0), // Page
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2, stream: 0), // Offset
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 1, stream: 0), // AlignFlags
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 1, stream: 1), // Opacity
            new VertexAttributeDescriptor(VertexAttribute.TexCoord5, VertexAttributeFormat.Float32, 3, stream: 0), // Tangent
            new VertexAttributeDescriptor(VertexAttribute.TexCoord6, VertexAttributeFormat.Float32, 3, stream: 0), // Up
            new VertexAttributeDescriptor(VertexAttribute.TexCoord7, VertexAttributeFormat.Float32, 2, stream: 0), // SdfWidenPx — truly LAST
        };

        // Skip main-thread index validation + redundant bounds recompute (mirrors SymbolPlacementSystem's
        // NoValidate / StyledLineTileBuilder's NoValidate) — Build sets Mesh.bounds explicitly below anyway.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // The CPU frustum cull tests Mesh.bounds, and an anchor can sit off-screen while its glyph or halo is
        // on-screen, so the mesh is never culled.
        public static readonly Bounds HugeBounds = new Bounds(Vector3.zero, new Vector3(1e9f, 1e9f, 1e9f));

        /// <summary>
        /// Rebuilds <paramref name="mesh"/>'s two-stream vertex/index buffers from CPU corner data.
        /// <paramref name="opacity"/> must have the same length as <paramref name="vertices"/> (one Opacity
        /// per corner, stream 1). Main-thread only.
        /// </summary>
        public static void Build(
            NativeArray<WorldBillboardVertex> vertices,
            NativeArray<float>                opacity,
            NativeArray<int>                  indices,
            Mesh                               mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            int vertexCount = vertices.Length;
            if (opacity.Length != vertexCount)
                throw new ArgumentException(
                    $"opacity length ({opacity.Length}) must match vertices length ({vertexCount}) — one Opacity per corner.",
                    nameof(opacity));

            mesh.SetVertexBufferParams(vertexCount, VertexDescriptors);
            mesh.SetVertexBufferData(vertices, 0, 0, vertexCount, 0, NoValidate);
            mesh.SetVertexBufferData(opacity,  0, 0, vertexCount, 1, NoValidate);

            int indexCount = indices.Length;
            mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indexCount, NoValidate);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles), NoValidate);

            mesh.bounds = HugeBounds;
        }
    }
}
