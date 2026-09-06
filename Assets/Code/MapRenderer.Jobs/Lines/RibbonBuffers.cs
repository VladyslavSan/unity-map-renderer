using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon buffers, from sizing through aggregation (job-scheduling-design.md §8 stage
    /// 6, E.2) — the ribbon twin of <see cref="TriangulationBuffers"/>: 2 prefix-sum offset tables + 2
    /// flat oversized-per-ring output columns + 2 per-ring real-count columns, shared by
    /// <see cref="RibbonSizingJob"/>, <see cref="RibbonBatchJob"/> and
    /// <see cref="RibbonAggregateJob"/>, instead of each declaring its own field list.
    ///
    /// <para><b>Every field is plain, not <c>[ReadOnly]</c></b> — same reasoning as
    /// <see cref="TriangulationBuffers"/>'s own doc: which of the six columns a given job reads versus
    /// writes differs per job, and the three nodes here are already a strictly linear chain (sizing → ribbon
    /// → aggregate), so the dependency a conservative writer needs is the same edge the chain already
    /// has.</para>
    ///
    /// <para><b>Every field must be a CREATED container</b> — same hazard as
    /// <see cref="TriangulationBuffers"/>'s own doc: the safety system validates the whole struct at
    /// schedule time, not just the fields a job body touches. Always build a value via <see cref="Allocate"/>.</para>
    /// </summary>
    internal struct RibbonBuffers
    {
        public NativeList<int> RingVertexOffsets;
        public NativeList<int> RingIndexOffsets;

        public NativeList<LineRibbonVertex> FlatVertices;
        public NativeList<int>              FlatIndices;

        public NativeList<int> PerRingVertexCount;
        public NativeList<int> PerRingIndexCount;

        /// <summary>Allocates all six columns at capacity 1 (each <c>Resize</c>d to its real length by
        /// <see cref="RibbonSizingJob"/>). Each column's allocation is recorded individually, right where
        /// it happens, mirroring <see cref="TriangulationBuffers.Allocate"/>'s own reasoning.</summary>
        internal static RibbonBuffers Allocate() => new RibbonBuffers
        {
            RingVertexOffsets  = NewList<int>(),
            RingIndexOffsets   = NewList<int>(),
            FlatVertices       = NewList<LineRibbonVertex>(),
            FlatIndices        = NewList<int>(),
            PerRingVertexCount = NewList<int>(),
            PerRingIndexCount  = NewList<int>(),
        };

        /// <summary>Schedules a <c>Dispose(handle)</c> node for every column, mirroring
        /// <see cref="TriangulationBuffers.DisposeAfter"/>'s own per-column recording, fan-out-then-combine
        /// shape, and <c>Allocator.Temp</c> reasoning for the handle array.</summary>
        internal JobHandle DisposeAfter(JobHandle deps)
        {
            var handles = new NativeArray<JobHandle>(6, Allocator.Temp);
            try
            {
                handles[0] = DisposeField(RingVertexOffsets, deps);
                handles[1] = DisposeField(RingIndexOffsets, deps);
                handles[2] = DisposeField(FlatVertices, deps);
                handles[3] = DisposeField(FlatIndices, deps);
                handles[4] = DisposeField(PerRingVertexCount, deps);
                handles[5] = DisposeField(PerRingIndexCount, deps);
                return JobHandle.CombineDependencies(handles);
            }
            finally { handles.Dispose(); }
        }

        private static NativeList<T> NewList<T>() where T : unmanaged
        {
            LineGraphOutput.RecordBuffersAllocated();
            return new NativeList<T>(1, Allocator.Persistent);
        }

        private static JobHandle DisposeField<T>(NativeList<T> list, JobHandle deps) where T : unmanaged
        {
            LineGraphOutput.RecordBufferDisposeNode();
            return list.Dispose(deps);
        }
    }
}
