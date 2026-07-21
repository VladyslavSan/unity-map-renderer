// Unity EditMode only — Mesh.AcquireReadOnlyMeshData needs the engine. NOT registered in core-tests.csproj.
//
// Epic A / A1: after A1, point/icon draws land on a WorldLabelRenderer-built mesh (WorldBillboardVertex
// stream 0 + a separate stream-1 Opacity float), not the screen slot mesh's BillboardVertex (Position=px,
// Color=RGBA, ...) the pre-A1 tests read via mesh.vertices/mesh.colors. This is the single shared readback
// several point/icon test files need (LabelFadeTests, HorizonCullGatherTests, LabelPlacementStructureTests,
// LabelPlacementDemoProductionFlipTests) — kept in its own file (mirrors WorldSymbolInkAnalysis's identical
// "one shared helper, not duplicated per test" reasoning) so none of them re-derive the MeshData readback.
// Internal, not public (test-code-bloat convention: a test helper's footprint stays inside the test assembly).

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    internal static class WorldMeshReadback
    {
        /// <summary>
        /// Reads back a <c>WorldLabelRenderer</c>-built mesh's stream-0 <see cref="WorldBillboardVertex"/>s and
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

        /// <summary>The maximum stream-1 Opacity value across every vertex — the world-mesh analogue of the
        /// pre-A1 <c>MaxAlpha(mesh)</c> helper (which read <c>BillboardVertex.Color.a</c>). 0 for a
        /// null/empty mesh (no live slot / nothing built this Tick).</summary>
        public static float MaxOpacity(Mesh mesh)
        {
            Read(mesh, out _, out float[] opacity);
            float max = 0f;
            for (int i = 0; i < opacity.Length; i++) max = math.max(max, opacity[i]);
            return max;
        }
    }
}
