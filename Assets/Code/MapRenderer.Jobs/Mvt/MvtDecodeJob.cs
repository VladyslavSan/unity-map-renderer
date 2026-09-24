using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// Burst job: the production MVT polygon geometry decoder. It decodes the layer's command stream
    /// (<see cref="Commands"/>, per-feature <see cref="FeatureOffsets"/>) into the caller-pre-sized flat
    /// <see cref="OutVertices"/> and per-ring <see cref="OutRingOffsets"/>. Its managed twin in the test
    /// assembly is an independent oracle. It does not use Core, so Core's System.Math stays off Burst.
    /// It is an <c>IJob</c> because decoding is sequential per tile; parallelism is one job per tile.
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
