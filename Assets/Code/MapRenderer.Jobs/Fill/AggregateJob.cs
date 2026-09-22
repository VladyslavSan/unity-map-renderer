using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's aggregate node: prefix sums over the per-polygon earcut outputs, then one pass
    /// copying each polygon's scratch slice into the layer's merged-vertex and rebased-index arrays. Every
    /// output list is resized to its exact total; a zero-length list is a valid, empty output.
    ///
    /// <para><b>Pre-sizes, but does not fill, three downstream outputs.</b> <see cref="WorldPositions"/>,
    /// <see cref="VertexUp"/> and <see cref="Geo"/> are resized to the exact vertex total here and filled by
    /// the tile→geo/project nodes that follow, so their <c>AsDeferredJobArray()</c> views already have the
    /// right length when those nodes execute.</para>
    ///
    /// <para><b>This node sizes the INTERIOR, not the layer.</b> <see cref="FillBandJob"/> runs after it on
    /// both arms and grows every column by the boundary band's own vertices. Nothing downstream may take a
    /// vertex total off this job.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct AggregateJob : IJob
    {
        // ── Input ──────────────────────────────────────────────────────────────────────────────
        /// <summary>Reads the eight columns this job needs: the four per-polygon counts, both offset
        /// columns, and the flat vertex and index arrays.</summary>
        public TriangulationBuffers Buffers;

        /// <summary>Set, and never cleared, on a capacity overrun. No node branches on it; a reader takes it
        /// only after the whole graph completes.</summary>
        public NativeReference<int> Error;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        public NativeList<double2> TileVertices;
        public NativeList<double3> WorldPositions; // pre-sized only — ProjectPointsJob fills it
        public NativeList<double3> VertexUp;       // pre-sized only — ProjectPointsJob fills it

        /// <summary>Filled here with the constant flat east <c>(1,0,0)</c>, because the graph's output needs
        /// an east column on both arms. On the curved arm the write is scratch —
        /// <see cref="GlobeFillScatterJob"/> overwrites it with the real per-vertex east.</summary>
        public NativeList<double3> VertexEast;

        /// <summary>Filled here with <c>(0,0,0)</c> — every interior vertex is outside the boundary band, and
        /// the explicit zero is what makes its <c>side</c> read exactly 0 and its coverage exactly 1.</summary>
        public NativeList<float3>  VertexBand;

        public NativeList<int>     VertexFeatureIdx;
        public NativeList<int>     TriangleIndices;

        /// <summary>Pre-sized only — <c>TileToGeoJob</c> fills it (the intermediate tile→geodetic buffer).</summary>
        public NativeList<GeoCoordinate> Geo;

        /// <summary>[0]'s <see cref="FillGraphCounts.PolygonCount"/>/<see cref="FillGraphCounts.ForceClipCount"/>
        /// are written here. Vertex and index counts are NOT: this job resizes
        /// <see cref="TileVertices"/>/<see cref="TriangleIndices"/> to their exact totals, so a reader takes
        /// <c>.Length</c> off them and no duplicate scalar can drift.</summary>
        public NativeArray<FillGraphCounts> Counts;

        public void Execute()
        {
            NativeList<int> perPolyMergedVertexCount  = Buffers.PerPolyMergedVertexCount;
            NativeList<int> perPolyIndexCount  = Buffers.PerPolyIndexCount;
            NativeList<int> perPolyForceClip = Buffers.PerPolyForceClip;
            NativeList<int> perPolyFeatureIndex = Buffers.PerPolyFeatureIndex;
            NativeList<int> workOffsets   = Buffers.WorkOffsets;
            NativeList<int> indexOffsets       = Buffers.IndexOffsets;
            NativeList<double2> flatWorkVerts = Buffers.FlatWorkVerts;
            NativeList<int>     flatIndexArrays = Buffers.FlatIndexArrays;

            // Bound by PerPolyMergedVertexCount's OWN length, never PolyCountArr[0] — a borrowed input that
            // SizingJob's capacity early-return leaves stale while every Buffers column is length 0. The
            // indexer bounds check is [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")], gone in a player.
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
            VertexBand.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            VertexFeatureIdx.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);
            TriangleIndices.Resize(totalIdxCount, NativeArrayOptions.UninitializedMemory);
            Geo.Resize(totalMergedVerts, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < totalMergedVerts; i++)
            {
                VertexEast[i] = new double3(1, 0, 0);
                VertexBand[i] = float3.zero;
            }

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
                    TileVertices[globalVertBase + i]     = flatWorkVerts[sOff + i];
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
