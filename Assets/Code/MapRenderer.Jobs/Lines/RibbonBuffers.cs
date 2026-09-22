using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon buffers, from sizing through aggregation — the ribbon twin of
    /// <see cref="TriangulationBuffers"/>: 2 prefix-sum offset tables, 2 flat oversized-per-ring output
    /// columns, and 2 per-ring real-count columns, shared by <see cref="RibbonSizingJob"/>,
    /// <see cref="RibbonBatchJob"/> and <see cref="RibbonAggregateJob"/>.
    ///
    /// <para><b>Every field is plain, not <c>[ReadOnly]</c></b>, and <b>every field must be a CREATED
    /// container</b> — both for the reasons <see cref="TriangulationBuffers"/>'s own doc gives. Always build
    /// a value through <see cref="Allocate"/>.</para>
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
