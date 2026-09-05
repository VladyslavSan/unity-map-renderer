using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's aggregate node: reproduces <c>FillMeshPipeline.cs:475–519</c>'s per-polygon
    /// merged-vertex/index concat — prefix sums over the per-polygon earcut outputs, then one pass copying
    /// each polygon's scratch slice into the layer's global merged-vertex and rebased-index arrays
    /// (job-scheduling-design.md §3.2, §8 stage 1).
    ///
    /// <para><b>Exact sizing, not <c>math.max(count, 1)</c>.</b> Unlike
    /// <c>FillMeshPipeline.cs:488–491</c>'s padding (never read), every output list here is resized
    /// to the exact total; a zero-length <see cref="NativeList{T}"/> is a valid, empty output.</para>
    ///
    /// <para><b>Pre-sizes, but does not fill, three downstream outputs.</b> <see cref="WorldPositions"/>,
    /// <see cref="VertexUp"/> and <see cref="Geo"/> are <see cref="NativeList{T}.Resize"/>d to the exact
    /// vertex total here and populated by the tile→geo/project nodes that follow — so their
    /// <c>AsDeferredJobArray()</c> views have the right length the moment those nodes execute, without this
    /// node needing to know their content.</para>
    ///
    /// <para><b>Bounds its own loop by <c>PerPolyMergedVertexCount.Length</c>, never the borrowed
    /// <c>PolyCountArr[0]</c></b> — see <see cref="Execute"/>'s own comment for the mechanism. This was a real
    /// bug found and fixed during job-scheduling-design.md §8 stage 6's review, PRE-EXISTING and reachable
    /// before that stage (<see cref="FillSizingJob"/>'s own doc: a test can hand it undersized capacity): a
    /// borrowed count does not shrink when <see cref="FillSizingJob"/> returns early, so this job would
    /// otherwise index a length-0 <c>PerPolyMergedVertexCount</c> et al. — silent, since
    /// <see cref="NativeList{T}"/>'s indexer bounds check is
    /// <c>[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]</c> and compiled OUT of a release player
    /// build.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct FillAggregateJob : IJob
    {
        // ── Input ──────────────────────────────────────────────────────────────────────────────
        /// <summary>Reads <c>PerPolyMergedVertexCount</c>/<c>PerPolyIndexCount</c>/<c>PerPolyForceClip</c>/
        /// <c>PerPolyFeatureIndex</c>/<c>WorkOffsets</c>/<c>IndexOffsets</c>/<c>FlatVx</c>/
        /// <c>FlatVy</c>/<c>FlatIndexArrays</c> of the nine columns it needs.</summary>
        public FillTriangulationBuffers Buffers;

        /// <summary>Set (never cleared) on capacity overrun — job-scheduling-design.md §8 stage 6, C.1: moved
        /// here from <see cref="EarcutBatchJob"/>, which parallelised over polygons and so can no longer hold
        /// a <see cref="NativeReference{T}"/> writer without a disable attribute this backstop does not
        /// warrant (a <see cref="NativeReference{T}"/> is not index-restricted). This job already reads
        /// <see cref="Buffers"/>' <c>PerPolyMergedVertexCount</c>/<c>WorkOffsets</c> in its own per-polygon
        /// loop below, so the check costs one comparison per polygon it was already visiting — observable
        /// behaviour is identical: the flag is read only after the whole graph completes, and no node
        /// branches on it (<c>FillMeshGraph.cs</c>'s own doc).</summary>
        public NativeReference<int> Error;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        public NativeList<double2> TileVertices;
        public NativeList<double3> WorldPositions; // pre-sized only — ProjectPointsJob fills it
        public NativeList<double3> VertexUp;       // pre-sized only — ProjectPointsJob fills it

        /// <summary>Filled here with the constant flat east <c>(1,0,0)</c> — job-scheduling-design.md §3.7's
        /// one-column-set output needs an east column on both arms, and this is the laziest correct home for
        /// it: this job is graph-only (no synchronous caller), already resizes the sibling columns, and a
        /// dedicated node would be a whole job for one constant. On the curved arm this write is scratch —
        /// <see cref="GlobeFillScatterJob"/> overwrites it with the real per-vertex east.</summary>
        public NativeList<double3> VertexEast;

        public NativeList<int>     VertexFeatureIdx;
        public NativeList<int>     TriangleIndices;

        /// <summary>Pre-sized only — <c>TileToGeoJob</c> fills it (the intermediate tile→geodetic buffer).</summary>
        public NativeList<GeoCoordinate> Geo;

        /// <summary>[0]'s <see cref="FillGraphCounts.PolygonCount"/>/<see cref="FillGraphCounts.ForceClipCount"/>
        /// are written here. Vertex/index counts are NOT — <see cref="TileVertices"/>/<see cref="TriangleIndices"/>
        /// are resized to their exact totals by this same job, so a reader just takes <c>.Length</c> off
        /// them once <see cref="FillGraphOutput.Handle"/> is completed; a duplicate scalar here would be one
        /// more value that could drift from the list it mirrors.</summary>
        public NativeArray<FillGraphCounts> Counts;

        public void Execute()
        {
            NativeList<int> perPolyMergedVertexCount  = Buffers.PerPolyMergedVertexCount;
            NativeList<int> perPolyIndexCount  = Buffers.PerPolyIndexCount;
            NativeList<int> perPolyForceClip = Buffers.PerPolyForceClip;
            NativeList<int> perPolyFeatureIndex = Buffers.PerPolyFeatureIndex;
            NativeList<int> workOffsets   = Buffers.WorkOffsets;
            NativeList<int> indexOffsets       = Buffers.IndexOffsets;
            NativeList<double> flatVx = Buffers.FlatVx;
            NativeList<double> flatVy = Buffers.FlatVy;
            NativeList<int>    flatIndexArrays = Buffers.FlatIndexArrays;

            // Bound by PerPolyMergedVertexCount's OWN length, never PolyCountArr[0] — a borrowed input
            // FillSizingJob's own early returns (MaxPolygons/MaxHoles capacity overrun) never touch. Those
            // early returns leave every Buffers column at length 0; bounding by PolyCountArr[0] would still
            // loop `polyCount` times over a zero-length list — NativeList's indexer bounds check is
            // [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")], compiled OUT of a release player build, so
            // that read is not a throw there, it is Ptr[index] past the allocation.
            int polyCount = perPolyMergedVertexCount.Length;

            int totalMergedVerts = 0, totalIdxCount = 0, totalForceClips = 0;
            for (int pi = 0; pi < polyCount; pi++)
            {
                totalMergedVerts += perPolyMergedVertexCount[pi];
                totalIdxCount    += perPolyIndexCount[pi];
                totalForceClips  += perPolyForceClip[pi];
            }

            TileVertices.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            WorldPositions.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            VertexUp.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            VertexEast.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            VertexFeatureIdx.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            TriangleIndices.Resize(totalIdxCount, NativeArrayOptions.UninitializedMemory);
            Geo.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < totalMergedVerts; i++)
                VertexEast[i] = new double3(1, 0, 0);

            int globalVertBase = 0;
            int globalIdxBase  = 0;
            for (int pi = 0; pi < polyCount; pi++)
            {
                int mergedVC = perPolyMergedVertexCount[pi];
                int idxCount = perPolyIndexCount[pi];
                int featIdx  = perPolyFeatureIndex[pi];
                int sOff     = workOffsets[pi];
                int iOff     = indexOffsets[pi];

                if (mergedVC > workOffsets[pi + 1] - sOff)
                    Error.Value = FillGraphCounts.ErrorEarcutMergedVertexCapacity;

                for (int i = 0; i < mergedVC; i++)
                {
                    TileVertices[globalVertBase + i]     = new double2(flatVx[sOff + i], flatVy[sOff + i]);
                    VertexFeatureIdx[globalVertBase + i] = featIdx;
                }

                for (int i = 0; i < idxCount; i++)
                    TriangleIndices[globalIdxBase + i] = flatIndexArrays[iOff + i] + globalVertBase;

                globalVertBase += mergedVC;
                globalIdxBase  += idxCount;
            }

            FillGraphCounts c = Counts[0];
            c.PolygonCount    = polyCount;
            c.ForceClipCount  = totalForceClips;
            Counts[0] = c;
        }
    }
}
