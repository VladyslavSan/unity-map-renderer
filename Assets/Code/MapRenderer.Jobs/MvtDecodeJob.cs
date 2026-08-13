using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst job: decodes an MVT polygon-feature geometry command stream (uint[] → NativeArray&lt;uint&gt;)
    /// into ring vertices stored in a flat <see cref="RingVertices"/> buffer with per-ring offsets.
    ///
    /// <b>This job IS the production MVT geometry decoder</b> — the only one. (It began as a native
    /// reimplementation of a managed <c>MvtGeometry.Decode</c>; since IR C1 P3 that managed twin exists only
    /// as an independent oracle in <c>MapRenderer.Tests.EditMode/TestSupport/</c>, so nothing in production
    /// decodes commands but this.) It implements the MVT spec's encoding over native containers:
    ///   command = id &amp; 0x7; count = id &gt;&gt; 3
    ///   MoveTo=1, LineTo=2, ClosePath=7
    ///   Parameters are zigzag-encoded deltas applied to a running cursor.
    ///
    /// Does NOT reference MapRenderer.Core — Core's System.Math stays off the Burst path.
    ///
    /// Input layout: one contiguous NativeArray&lt;uint&gt; holding the raw geometry commands for
    /// ALL polygon features of a layer, with per-feature start offsets in <see cref="FeatureOffsets"/>.
    /// Output: flat vertex array (<see cref="OutVertices"/>) + per-ring start-index array
    /// (<see cref="OutRingOffsets"/>), both pre-sized by the caller.
    ///
    /// Note: this is an IJob (not IJobParallelFor) because MVT decoding is sequential per tile
    /// and pre-sizing the output requires a pass anyway. The parallelism is tile-level
    /// (multiple <see cref="MvtDecodeJob"/>s scheduled in parallel, one per tile).
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct MvtDecodeJob : IJob
    {
        // Zigzag command IDs (per MVT spec §4.3)
        private const uint MoveTo    = 1;
        private const uint LineTo    = 2;
        private const uint ClosePath = 7;

        /// <summary>Flat geometry command stream for all polygon features in the layer.</summary>
        [ReadOnly] public NativeArray<uint> Commands;

        /// <summary>
        /// Per-feature start offsets into <see cref="Commands"/>. Length = number of features.
        /// Feature [i] occupies Commands[FeatureOffsets[i] .. FeatureOffsets[i+1]-1].
        /// </summary>
        [ReadOnly] public NativeArray<int> FeatureOffsets;

        /// <summary>
        /// Per-feature command lengths (Commands to read per feature).
        /// Feature [i] has Commands[FeatureOffsets[i] .. FeatureOffsets[i] + FeatureLengths[i] - 1].
        /// </summary>
        [ReadOnly] public NativeArray<int> FeatureLengths;

        /// <summary>Output: flat vertex array (tile-space integer coords as double2).</summary>
        [WriteOnly] public NativeArray<double2> OutVertices;

        /// <summary>
        /// Output: per-ring start offsets into <see cref="OutVertices"/>. Length = total ring count + 1
        /// (last element = total vertex count, sentinel).
        /// </summary>
        [WriteOnly] public NativeArray<int> OutRingOffsets;

        /// <summary>
        /// Output: per-ring feature index (which feature each ring belongs to),
        /// length = total ring count.
        /// </summary>
        [WriteOnly] public NativeArray<int> OutRingFeatureIndex;

        /// <summary>Total number of rings decoded. Written by Execute().</summary>
        public NativeArray<int> OutRingCount;

        /// <summary>Total number of vertices decoded. Written by Execute().</summary>
        public NativeArray<int> OutVertexCount;

        public void Execute()
        {
            int featureCount   = FeatureOffsets.Length;
            int ringCount      = 0;
            int vertexCount    = 0;

            for (int fi = 0; fi < featureCount; fi++)
            {
                int cmdStart  = FeatureOffsets[fi];
                int cmdLength = FeatureLengths[fi];
                int cmdEnd    = cmdStart + cmdLength;

                long cx = 0, cy = 0;  // running cursor
                int i = cmdStart;

                while (i < cmdEnd)
                {
                    uint commandInteger = Commands[i++];
                    uint command = commandInteger & 0x7u;
                    uint count   = commandInteger >> 3;

                    if (command == MoveTo)
                    {
                        for (uint k = 0; k < count; k++)
                        {
                            cx += ZigZag(Commands[i++]);
                            cy += ZigZag(Commands[i++]);

                            // Start a new ring.
                            OutRingOffsets[ringCount]      = vertexCount;
                            OutRingFeatureIndex[ringCount] = fi;
                            OutVertices[vertexCount]       = new double2((double)cx, (double)cy);
                            vertexCount++;
                            ringCount++;
                        }
                    }
                    else if (command == LineTo)
                    {
                        for (uint k = 0; k < count; k++)
                        {
                            cx += ZigZag(Commands[i++]);
                            cy += ZigZag(Commands[i++]);
                            OutVertices[vertexCount++] = new double2((double)cx, (double)cy);
                        }
                    }
                    else if (command == ClosePath)
                    {
                        // Ring is implicitly closed (first vertex not repeated). No-op.
                    }
                }
            }

            // Write sentinel offset (= total vertex count).
            OutRingOffsets[ringCount] = vertexCount;
            OutRingCount[0]   = ringCount;
            OutVertexCount[0] = vertexCount;
        }

        /// <summary>Protobuf zigzag decode: (n >> 1) ^ -(n &amp; 1).</summary>
        private static long ZigZag(uint u) => (long)(u >> 1) ^ -(long)(u & 1);
    }
}
