// Unity EditMode only — Mesh.AcquireReadOnlyMeshData needs the engine. NOT registered in core-tests.csproj.
//
// Point/icon draws land on a WorldSymbolRenderer-built mesh (WorldBillboardVertex stream 0 plus a separate
// stream-1 Opacity float), which mesh.vertices/mesh.colors cannot read. This is the single shared readback
// the point/icon test files use, so none of them re-derive the MeshData round-trip. Internal, not public:
// a test helper's footprint stays inside the test assembly.

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class WorldMeshReadback
    {
        /// <summary>
        /// Reads back a <c>WorldSymbolRenderer</c>-built mesh's stream-0 <see cref="WorldBillboardVertex"/>s and
        /// stream-1 Opacity floats — the SAME struct/descriptor layout <c>WorldBillboardMeshBuilder.Build</c>
        /// wrote, so this is a faithful round-trip regardless of the mesh's actual vertex-attribute wiring.
        /// Empty arrays (not null) for a null/vertex-less mesh.
        /// </summary>
        public static void Read(Mesh mesh, out WorldBillboardVertex[] vertices, out float[] opacity)
        {
            if (mesh == null || mesh.vertexCount == 0)
            {
                vertices = System.Array.Empty<WorldBillboardVertex>();
                opacity = System.Array.Empty<float>();
                return;
            }

            using Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            Mesh.MeshData data = dataArray[0];

            NativeArray<WorldBillboardVertex> nativeVerts = data.GetVertexData<WorldBillboardVertex>(stream: 0);
            vertices = nativeVerts.ToArray(); // GetVertexData's array is a view into the MeshDataArray — copy out before it's released

            NativeArray<float> nativeOpacity = data.GetVertexData<float>(stream: 1);
            opacity = nativeOpacity.ToArray();
        }

        /// <summary>The maximum stream-1 Opacity value across every vertex. 0 for a null/empty mesh (no live
        /// slot, or nothing built this Tick).</summary>
        public static float MaxOpacity(Mesh mesh)
        {
            Read(mesh, out _, out float[] opacity);
            float max = 0f;
            for (int i = 0; i < opacity.Length; i++) max = math.max(max, opacity[i]);
            return max;
        }
    }
}
