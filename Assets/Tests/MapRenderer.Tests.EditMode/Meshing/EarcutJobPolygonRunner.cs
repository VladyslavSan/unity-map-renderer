using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Jobs.Fill;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Single-polygon <see cref="EarcutJob"/> driver for hand-built rings — the H1 harness A0 uses to
    /// re-home the former managed-<c>Earcut</c> unit tests onto the Burst job. Any number of holes:
    /// multiple hole rings are sorted by (leftmost-x, min-y, ring-index) with the production
    /// <c>FillMeshPipeline.HoleRingComparer</c> — the same tie-break the real gather step uses — so this
    /// runner never re-implements the ordering, only calls it.
    /// </summary>
    internal static class EarcutJobPolygonRunner
    {
        /// <summary>One polygon's triangulation, trimmed to the used prefixes — the capacity tails are
        /// unwritten scratch per <see cref="EarcutJob.OutMergedVertexCount"/>'s own doc.</summary>
        internal readonly struct Result
        {
            public readonly double2[] Vertices;
            public readonly int[]     Indices;
            public readonly int       ForceClips;
            public readonly long      CandidateVisits;

            public Result(double2[] vertices, int[] indices, int forceClips, long candidateVisits)
            {
                Vertices = vertices; Indices = indices; ForceClips = forceClips; CandidateVisits = candidateVisits;
            }
        }

        /// <summary>Convenience overload for a single (or absent) hole.</summary>
        internal static Result Run(
            IReadOnlyList<double2> outer, IReadOnlyList<double2> hole, bool forceLinearEarScan = false)
            => Run(outer, hole == null ? null : new List<IReadOnlyList<double2>> { hole }, forceLinearEarScan);

        /// <summary>Runs <see cref="EarcutJob"/> on one outer ring plus any number of holes.</summary>
        internal static Result Run(
            IReadOnlyList<double2> outer, IReadOnlyList<IReadOnlyList<double2>> holes = null,
            bool forceLinearEarScan = false)
        {
            int outerCount = outer?.Count ?? 0;
            int holeCount  = holes?.Count ?? 0;

            int holeVertexTotal = 0;
            for (int h = 0; h < holeCount; h++) holeVertexTotal += holes[h].Count;

            int[] holeOrder = SortHoleOrder(holes, holeCount);

            int polyVC        = outerCount + holeVertexTotal;
            int baseCap       = polyVC + holeCount * 2;
            int splitBudget   = math.min(EarcutJob.MaxSplits, math.max(8, holeCount * 4));
            int workCap       = baseCap + splitBudget * 2;
            int idxCap        = workCap > 2 ? (workCap - 2) * 3 : 3;
            int sortedHoleLen = holeCount > 0 ? holeCount : 1;

            var polyVertices = new NativeArray<double2>(math.max(1, polyVC), Allocator.Persistent);
            var sortedHoleCounts = new NativeArray<int>(sortedHoleLen, Allocator.Persistent);
            var outIndices = new NativeArray<int>(math.max(1, idxCap), Allocator.Persistent);
            var outIndexCount = new NativeArray<int>(1, Allocator.Persistent);
            var outForceClipCount = new NativeArray<int>(1, Allocator.Persistent);
            var outMergedVertexCount = new NativeArray<int>(1, Allocator.Persistent);
            var outCandidateVisits = new NativeArray<long>(1, Allocator.Persistent);
            var verts = new NativeArray<double2>(math.max(1, workCap), Allocator.Persistent);
            var prev = new NativeArray<int>(math.max(1, workCap), Allocator.Persistent);
            var next = new NativeArray<int>(math.max(1, workCap), Allocator.Persistent);
            var isBridgeCopy = new NativeArray<bool>(math.max(1, workCap), Allocator.Persistent);
            var removed = new NativeArray<bool>(math.max(1, workCap), Allocator.Persistent);
            var isEar = new NativeArray<bool>(math.max(1, workCap), Allocator.Persistent);

            try
            {
                for (int i = 0; i < outerCount; i++) polyVertices[i] = outer[i];
                int vCursor = outerCount;
                for (int h = 0; h < holeCount; h++)
                {
                    IReadOnlyList<double2> ring = holes[holeOrder[h]];
                    sortedHoleCounts[h] = ring.Count;
                    for (int i = 0; i < ring.Count; i++) polyVertices[vCursor + i] = ring[i];
                    vCursor += ring.Count;
                }

                new EarcutJob
                {
                    PolyVertices = polyVertices, OuterCount = outerCount,
                    SortedHoleCounts = sortedHoleCounts, HoleCount = holeCount,
                    ForceLinearEarScan = forceLinearEarScan,
                    OutIndices = outIndices, OutIndexOffset = 0,
                    OutIndexCount = outIndexCount, OutForceClipCount = outForceClipCount,
                    OutMergedVertexCount = outMergedVertexCount, OutCandidateVisits = outCandidateVisits,
                    Verts = verts, Prev = prev, Next = next,
                    IsBridgeCopy = isBridgeCopy, Removed = removed, IsEar = isEar,
                }.Run();

                int mergedVertexCount = outMergedVertexCount[0];
                int indexCount = outIndexCount[0];
                var resultVertices = new double2[mergedVertexCount];
                for (int i = 0; i < mergedVertexCount; i++) resultVertices[i] = verts[i];
                var resultIndices = new int[indexCount];
                for (int i = 0; i < indexCount; i++) resultIndices[i] = outIndices[i];

                return new Result(resultVertices, resultIndices, outForceClipCount[0], outCandidateVisits[0]);
            }
            finally
            {
                polyVertices.Dispose(); sortedHoleCounts.Dispose(); outIndices.Dispose();
                outIndexCount.Dispose(); outForceClipCount.Dispose(); outMergedVertexCount.Dispose();
                outCandidateVisits.Dispose();
                verts.Dispose(); prev.Dispose(); next.Dispose();
                isBridgeCopy.Dispose(); removed.Dispose(); isEar.Dispose();
            }
        }

        /// <summary>Hole-ring indices in <see cref="FillMeshPipeline.HoleRingComparer"/> order
        /// (leftmost-x, then min-y, then ring index) — the same tie-break the production gather step
        /// sorts with. A single hole (or none) skips the sort entirely (already ordered).</summary>
        private static int[] SortHoleOrder(IReadOnlyList<IReadOnlyList<double2>> holes, int holeCount)
        {
            var order = new int[holeCount];
            for (int h = 0; h < holeCount; h++) order[h] = h;
            if (holeCount < 2) return order;

            int flatLen = 0;
            for (int h = 0; h < holeCount; h++) flatLen += holes[h].Count;

            var flatVerts = new NativeArray<double2>(math.max(1, flatLen), Allocator.Temp);
            var ringOffsets = new NativeArray<int>(holeCount + 1, Allocator.Temp);
            try
            {
                int cursor = 0;
                for (int h = 0; h < holeCount; h++)
                {
                    ringOffsets[h] = cursor;
                    IReadOnlyList<double2> ring = holes[h];
                    for (int i = 0; i < ring.Count; i++) flatVerts[cursor + i] = ring[i];
                    cursor += ring.Count;
                }
                ringOffsets[holeCount] = cursor;

                var comparer = new FillMeshPipeline.HoleRingComparer(flatVerts, ringOffsets);
                Array.Sort(order, comparer);
                return order;
            }
            finally
            {
                flatVerts.Dispose();
                ringOffsets.Dispose();
            }
        }
    }
}
