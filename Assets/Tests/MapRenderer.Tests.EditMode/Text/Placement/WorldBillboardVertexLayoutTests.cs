// Unity EditMode only — Mesh vertex-buffer introspection needs the engine. NOT registered in core-tests.csproj.
//
// P2 T-1: the vertex-layout/struct sync tooth. WorldBillboardVertex's field DECLARATION ORDER is the
// stream-0 byte layout (see that file's header) and MUST match WorldBillboardMeshBuilder.VertexDescriptors'
// order exactly, with the WHOLE descriptor array staying in globally-ascending VertexAttribute enum order
// regardless of stream — declaring TEXCOORD6 (Up, P2) out of that order triggers Unity's silent
// "non-standard order" re-adjustment, whose real-world symptom is zero ink (docs/lessons-learned.md).

using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldBillboardVertexLayoutTests
    {
        private static Mesh BuildFourVertexMesh()
        {
            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            vertices[0] = new WorldBillboardVertex();
            vertices[1] = new WorldBillboardVertex();
            vertices[2] = new WorldBillboardVertex();
            vertices[3] = new WorldBillboardVertex();
            var opacity = new NativeArray<float>(4, Allocator.Temp);
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f;
            var indices = new NativeArray<int>(6, Allocator.Temp);
            indices[0] = 0; indices[1] = 1; indices[2] = 2; indices[3] = 0; indices[4] = 2; indices[5] = 3;

            var mesh = new Mesh();
            WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh);
            vertices.Dispose();
            opacity.Dispose();
            indices.Dispose();
            return mesh;
        }

        [Test]
        public void Build_VertexBufferStride_MatchesTheStructSize()
        {
            Mesh mesh = BuildFourVertexMesh();
            try
            {
                // 80 B: 18 pre-halo floats + SdfWidenPx's 2 floats (72 B -> 80 B). A field added without a
                // matching descriptor (or vice versa) mismatches the struct size against Unity's own accounting.
                Assert.AreEqual(UnsafeUtility.SizeOf<WorldBillboardVertex>(), mesh.GetVertexBufferStride(0));
                Assert.AreEqual(80, mesh.GetVertexBufferStride(0), "stream 0 grew 72 B -> 80 B for SdfWidenPx");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Build_DeclaresTexCoord6_Float32x3_OnStream0()
        {
            Mesh mesh = BuildFourVertexMesh();
            try
            {
                Assert.IsTrue(mesh.HasVertexAttribute(VertexAttribute.TexCoord6), "Up must be declared as TEXCOORD6");
                Assert.AreEqual(3, mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord6), "Up is a float3");
                Assert.AreEqual(VertexAttributeFormat.Float32, mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord6));
                Assert.AreEqual(0, mesh.GetVertexAttributeStream(VertexAttribute.TexCoord6), "Up rides stream 0, with AnchorLocal/.../Tangent — not the per-frame Opacity stream");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Build_DeclaresTexCoord7_Float32x2_OnStream0()
        {
            Mesh mesh = BuildFourVertexMesh();
            try
            {
                Assert.IsTrue(mesh.HasVertexAttribute(VertexAttribute.TexCoord7), "SdfWidenPx must be declared as TEXCOORD7");
                Assert.AreEqual(2, mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord7), "SdfWidenPx is a float2 — edge widening and AA widening");
                Assert.AreEqual(VertexAttributeFormat.Float32, mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord7));
                Assert.AreEqual(0, mesh.GetVertexAttributeStream(VertexAttribute.TexCoord7), "SdfWidenPx rides stream 0 — it is fixed per run, unlike the per-frame Opacity stream");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Build_VertexAttributeArray_StaysStrictlyAscendingByAttributeAcrossBothStreams()
        {
            Mesh mesh = BuildFourVertexMesh();
            try
            {
                VertexAttributeDescriptor[] descriptors = mesh.GetVertexAttributes();

                for (int i = 1; i < descriptors.Length; i++)
                    Assert.Less((int)descriptors[i - 1].attribute, (int)descriptors[i].attribute,
                        $"descriptor {i - 1} ({descriptors[i - 1].attribute}) must be strictly before descriptor {i} " +
                        "({descriptors[i].attribute}) — Unity's silent non-standard-order re-adjustment (zero ink) " +
                        "triggers the instant this is violated, REGARDLESS of which stream each attribute is on.");

                // The array's LAST element must be TexCoord7 (SdfWidenPx) — the frozen "append, never
                // reshuffle" rule, now one append further on than P2's Up.
                Assert.AreEqual(VertexAttribute.TexCoord7, descriptors[descriptors.Length - 1].attribute,
                    "TexCoord7 (SdfWidenPx) must be the new truly-last descriptor");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
