using System;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Jobs;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Builds a <see cref="Mesh"/> directly from a tile's <see cref="TileMeshBuffers"/>
    /// (<c>NativeArray</c>) — the go-live bridge that replaces the managed <see cref="MeshBuilder"/> in the
    /// per-tile path. Used by <see cref="MapView"/>'s live loop.
    ///
    /// <para><b>Vertex channels — must match MeshBuilder exactly</b> so the same <c>MapRenderer/Fill</c>
    /// (URP Lit) material renders identically (a missing normal renders near-black under the scene light;
    /// a missing tangent errors when <c>_NORMALMAP</c> is enabled):</para>
    /// <list type="bullet">
    ///   <item><b>position</b> = <see cref="TileMeshBuffers.WorldPositions"/> (origin-relative float3,
    ///     east=+X, height=+Y, north=+Z).</item>
    ///   <item><b>normal</b> = +Y per vertex (flat XZ fill; NOT RecalculateNormals — winding is mixed).</item>
    ///   <item><b>uv0</b> = tile-local [0,1] = <see cref="TileMeshBuffers.TileVertices"/> / extent.</item>
    ///   <item><b>tangent</b> = (1,0,0,1) constant — +X tangent against the +Y normal.</item>
    /// </list>
    ///
    /// <para>Index format is always <see cref="IndexFormat.UInt32"/> (dense tiles exceed 65535 verts).</para>
    ///
    /// <para>This allocates managed arrays + a Mesh object per tile <i>load</i> — that is acceptable
    /// because tile loads are not the steady-state hot path (the no-GC acceptance criterion measures a
    /// frame with no tile loads). The buffers are read on the main thread after the pipeline has
    /// <c>Complete()</c>d its jobs.</para>
    /// </summary>
    public static class TileMeshFactory
    {
        private static readonly Vector4 FlatTangent = new Vector4(1f, 0f, 0f, 1f);

        /// <summary>
        /// Creates a <see cref="Mesh"/> from <paramref name="buffers"/>. Returns null if the tile produced
        /// no geometry.
        /// </summary>
        /// <param name="buffers">A completed tile pipeline output (jobs already Complete'd).</param>
        /// <param name="extent">MVT tile extent (typically 4096) for UV normalisation.</param>
        /// <param name="name">Mesh name (for the Profiler / debugging).</param>
        public static Mesh CreateMesh(in TileMeshBuffers buffers, double extent, string name = "MapTile")
        {
            if (!buffers.IsCreated) return null;

            int vertCount  = buffers.VertexCount.IsCreated ? buffers.VertexCount[0] : 0;
            int indexCount = buffers.TotalIndexCount;
            if (vertCount == 0 || indexCount == 0) return null;

            NativeArray<float3>  world  = buffers.WorldPositions;
            NativeArray<double2> tile   = buffers.TileVertices;
            NativeArray<int>     tris   = buffers.TriangleIndices;

            double extentInv = extent > 0.0 ? 1.0 / extent : 0.0;

            var vertices = new Vector3[vertCount];
            var normals  = new Vector3[vertCount];
            var uvs      = new Vector2[vertCount];
            var tangents = new Vector4[vertCount];

            for (int i = 0; i < vertCount; i++)
            {
                float3 w = world[i];
                vertices[i] = new Vector3(w.x, w.y, w.z);
                normals[i]  = Vector3.up;

                double2 tv = tile[i];
                uvs[i]      = new Vector2((float)(tv.x * extentInv), (float)(tv.y * extentInv));
                tangents[i] = FlatTangent;
            }

            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
                indices[i] = tris[i];

            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTangents(tangents);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
