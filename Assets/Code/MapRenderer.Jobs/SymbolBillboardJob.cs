using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst-compiled per-quad billboard builder — the S20 Jobs-layer port of
    /// <see cref="BillboardMath.BuildQuad"/>, mirroring <c>LineRibbonJob</c>'s shape (worst-case-sized
    /// output <c>NativeArray</c>s + an <c>OutVertexCount</c>/<c>OutIndexCount</c> pair, scratch kept as
    /// job FIELDS only for the INPUT/OUTPUT containers — see the "job field vs. Execute-local scratch"
    /// lesson; this job needs no internal scratch at all, so nothing else applies).
    ///
    /// <para>4 vertices + 6 indices per <see cref="PlacedQuad"/>, emitted in input order (Slice 1 has no
    /// collision/sort yet — every quad in <see cref="Quads"/> is placed).</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct SymbolBillboardJob : IJob
    {
        // ── Input ─────────────────────────────────────────────────────────────────────────────
        /// <summary>One entry per glyph quad to place (label anchor/size/color repeated across a label's quads).</summary>
        [ReadOnly] public NativeArray<PlacedQuad> Quads;

        /// <summary>Number of valid entries in <see cref="Quads"/>.</summary>
        [ReadOnly] public int QuadCount;

        // ── Output ────────────────────────────────────────────────────────────────────────────
        /// <summary>Billboard vertices in emission order (written [0..OutVertexCount[0])).</summary>
        [WriteOnly] public NativeArray<BillboardVertex> OutVertices;

        /// <summary>Triangle indices, 3 per triangle (written [0..OutIndexCount[0])).</summary>
        [WriteOnly] public NativeArray<int> OutIndices;

        /// <summary>[0] = number of vertices written.</summary>
        public NativeArray<int> OutVertexCount;

        /// <summary>[0] = number of indices written.</summary>
        public NativeArray<int> OutIndexCount;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Worst-case sizing — every quad emits exactly 4 vertices / 6 indices (no join/cap variability,
        // unlike LineRibbonJob's topology).
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Exact (not just an upper bound) vertex count for <paramref name="quadCount"/> quads.</summary>
        public static int MaxVertexCount(int quadCount) => quadCount < 0 ? 0 : quadCount * 4;

        /// <summary>Exact (not just an upper bound) index count for <paramref name="quadCount"/> quads.</summary>
        public static int MaxIndexCount(int quadCount) => quadCount < 0 ? 0 : quadCount * 6;

        // ── IJob ──────────────────────────────────────────────────────────────────────────────
        public void Execute()
        {
            OutVertexCount[0] = 0;
            OutIndexCount[0] = 0;
            if (QuadCount <= 0) return;

            int v = 0;
            int idx = 0;

            for (int i = 0; i < QuadCount; i++)
            {
                PlacedQuad q = Quads[i];

                BillboardMath.BuildQuad(
                    in q.Quad, in q.AnchorScreenPx, q.TextSizePx, q.Depth, in q.Color, q.RotationRadians,
                    out BillboardVertex topLeft, out BillboardVertex topRight,
                    out BillboardVertex bottomRight, out BillboardVertex bottomLeft);

                int baseIndex = v;
                OutVertices[v++] = topLeft;
                OutVertices[v++] = topRight;
                OutVertices[v++] = bottomRight;
                OutVertices[v++] = bottomLeft;

                // Two triangles: (topLeft,topRight,bottomRight) + (topLeft,bottomRight,bottomLeft) —
                // matches BillboardMath's documented corner winding.
                OutIndices[idx++] = baseIndex + 0; // topLeft
                OutIndices[idx++] = baseIndex + 1; // topRight
                OutIndices[idx++] = baseIndex + 2; // bottomRight
                OutIndices[idx++] = baseIndex + 0; // topLeft
                OutIndices[idx++] = baseIndex + 2; // bottomRight
                OutIndices[idx++] = baseIndex + 3; // bottomLeft
            }

            OutVertexCount[0] = v;
            OutIndexCount[0] = idx;
        }
    }
}
