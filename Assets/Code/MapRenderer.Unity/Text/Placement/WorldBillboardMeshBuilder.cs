// Namespace-collision guard (see GlyphAtlasTexture.cs's header comment for the full explanation): this
// file lives in MapRenderer.Unity.Text.Placement and uses Unity.Mathematics — TOP-LEVEL
// `using Unity.Mathematics;` + unqualified types, NEVER an inline `Unity.Mathematics.X`.

using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Epic A / A0 (world-anchored-labels-design.md §3.1, §11 A0): the type-explicit builder (naming
    /// convention) that assembles a two-stream world-anchored label <see cref="Mesh"/> from CPU corner
    /// data. Reused by A1's real per-(tile,DrawIndex,kind) emit and A0's test scaffold.
    ///
    /// <para><b>Two streams</b> (so a per-frame fade update never touches topology, §3.1): stream 0 is
    /// <see cref="WorldBillboardVertex"/> — Position(AnchorLocal)/Color(ColorRGB)/TexCoord0(Uv)/
    /// TexCoord1(Page)/TexCoord2(Offset)/TexCoord3(AlignFlags)/TexCoord5(Tangent, Stage AC)/TexCoord6(Up, P2),
    /// FROZEN load-bearing byte layout (see <see cref="WorldBillboardVertex"/>'s header). Stream 1 is a single
    /// <c>float</c> Opacity (TexCoord4) — A0 uploads a constant 1 array; A2 re-uploads this stream alone
    /// per frame for the fade.</para>
    ///
    /// <para>Thin and testable: no projection/collision logic here (that is the decision, A1) — this type
    /// only turns already-computed corner data into a GPU mesh. Main-thread only (touches <see cref="Mesh"/>,
    /// mirrors every other GPU-resource boundary in this codebase).</para>
    /// </summary>
    public static class WorldBillboardMeshBuilder
    {
        // Combined stream-0 + stream-1 descriptor set. ORDER IS LOAD-BEARING for stream 0 (mirrors
        // WorldBillboardVertex's header / LabelPlacementSystem.VertexDescriptors' identical rule): the array
        // MUST stay in globally-ASCENDING VertexAttribute enum order ACROSS THE WHOLE ARRAY regardless of
        // stream — Position=0, Color=3, TexCoord0=4, TexCoord1=5, TexCoord2=6, TexCoord3=7, TexCoord4=8
        // (stream 1!), TexCoord5=9, TexCoord6=10 — declaring them out of order triggers a silent
        // "non-standard order" layout re-adjustment that reads the wrong bytes for the wrong attribute (at
        // worst: 0 ink pixels). TexCoord4 (Opacity) is stream 1 — a SEPARATE vertex buffer, so re-uploading
        // it per frame (A2) never touches stream 0 — but it still occupies enum slot 8, so Stage AC's
        // TexCoord5 (Tangent, enum 9, stream 0) MUST be declared after it, and P2's TexCoord6 (Up, enum 10,
        // stream 0) after THAT — the new truly-LAST element — to keep 0,3,4,5,6,7,8,9,10 ascending. Putting
        // TexCoord5/6 before TexCoord4 would read descending — the exact hazard this codebase's
        // vertex-descriptor convention exists to prevent.
        private static readonly VertexAttributeDescriptor[] VertexDescriptors =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0), // AnchorLocal
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 3, stream: 0), // ColorRGB
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 0), // Uv
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 1, stream: 0), // Page
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2, stream: 0), // Offset
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 1, stream: 0), // AlignFlags
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 1, stream: 1), // Opacity
            new VertexAttributeDescriptor(VertexAttribute.TexCoord5, VertexAttributeFormat.Float32, 3, stream: 0), // Tangent (Stage AC)
            new VertexAttributeDescriptor(VertexAttribute.TexCoord6, VertexAttributeFormat.Float32, 3, stream: 0), // Up (P2) — truly LAST
        };

        // Skip main-thread index validation + redundant bounds recompute (mirrors LabelPlacementSystem's
        // NoValidate / StyledLineTileBuilder's NoValidate) — Build sets Mesh.bounds explicitly below anyway.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // A MeshRenderer's CPU frustum cull is evaluated against Mesh.bounds — an anchor can sit off-object
        // while a glyph/halo extends on-screen (§3.3 "never-cull"), so "never cull" is the only correct
        // choice, mirroring LabelPlacementSystem.HugeBounds (the screen-space path's identical reasoning).
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
