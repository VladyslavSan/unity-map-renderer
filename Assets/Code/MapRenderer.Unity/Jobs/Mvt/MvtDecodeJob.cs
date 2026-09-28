using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Unity.Jobs.Mvt
{
    /// <summary>
    /// Burst job: the production MVT polygon geometry decoder. It decodes the layer's command stream
    /// (<see cref="Commands"/>, per-feature <see cref="FeatureOffsets"/>) into the caller-pre-sized flat
    /// <see cref="OutVertices"/> and per-ring <see cref="OutRingOffsets"/>. Its managed twin in the test
    /// assembly is an independent oracle. It does not use Core, so Core's System.Math stays off Burst.
    /// It is an <c>IJob</c> because decoding is sequential per tile; parallelism is one job per tile.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct MvtDecodeJob : IJob
    {
        // Command IDs (MVT spec §4.3)
        private const uint MoveTo    = 1;
        private const uint LineTo    = 2;
        private const uint ClosePath = 7;

        /// <summary>
        /// Exact pre-count of the rings and vertices <see cref="Execute"/> emits for a layer's
        /// command stream: one ring and one vertex per MoveTo point, one vertex per LineTo point, for any
        /// input. Non-local invariant: this walk must advance its cursor exactly as the decode job does
        /// (2 params per MoveTo/LineTo point, none for ClosePath or unknown), or the two desync silently and
        /// the job writes out of range. A truncated param stream still over-reads input, never over-writes.
        /// </summary>
        /// <remarks>Takes the same native-flat <c>(commands, featureOffsets, featureLengths)</c> shape the
        /// job reads. A feature with <c>featureLengths[fi] == 0</c> is skipped.</remarks>
        public static void PrecountRingsAndVertices(
            NativeArray<uint> commands, NativeArray<int> featureOffsets, NativeArray<int> featureLengths,
            out int rings, out int vertices)
        {
            rings    = 0;
            vertices = 0;
            if (!featureOffsets.IsCreated) return;

            for (int fi = 0; fi < featureOffsets.Length; fi++)
            {
                int start = featureOffsets[fi];
                int len   = featureLengths[fi];
                if (len == 0) continue;

                int i = 0;
                while (i < len)
                {
                    uint commandInteger = commands[start + i++];
                    uint command = commandInteger & 0x7u;
                    uint count   = commandInteger >> 3;

                    if (command == MoveTo)
                    {
                        // One ring AND one vertex per point; 2 param uints each.
                        rings    += (int)count;
                        vertices += (int)count;
                        i        += 2 * (int)count;
                    }
                    else if (command == LineTo)
                    {
                        // One vertex per point; 2 param uints each. No new ring.
                        vertices += (int)count;
                        i        += 2 * (int)count;
                    }
                    // ClosePath / unknown: header consumed, no params (matches the job's i++ only).
                }
            }
        }

        /// <summary>
        /// Never-fired capacity backstop for the sizing pre-pass. With exact <see cref="PrecountRingsAndVertices"/>
        /// sizing no job can report a count past the buffers it was sized for. The check is a plain <c>if</c>,
        /// not behind <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, so a sizing mistake fails fast in a release build too.
        /// </summary>
        /// <param name="count">The actual count reported by a job (or computed in the pre-pass).</param>
        /// <param name="capacity">The capacity the buffer was sized to.</param>
        /// <param name="what">A short label naming the quantity, for the exception message.</param>
        public static void EnsureCapacity(int count, int capacity, string what)
        {
            if (count > capacity)
                throw new InvalidOperationException(
                    $"MvtDecodeJob sizing overflow: {what} count {count} exceeds pre-sized " +
                    $"capacity {capacity}. With exact PrecountRingsAndVertices sizing this should be " +
                    "unreachable — it indicates a sizing-vs-decode desync (the pre-count walk no longer " +
                    "mirrors MvtDecodeJob.Execute). Fix the pre-count to match the decode job.");
        }

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

                // running cursor
                long cx = 0;
                long cy = 0;
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
