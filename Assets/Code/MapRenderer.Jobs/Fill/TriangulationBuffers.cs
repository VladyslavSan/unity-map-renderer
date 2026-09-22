using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The buffers the fill graph's triangulation works over, from sizing through aggregation — 4
    /// prefix-sum offset tables, 9 flat working/output columns, 5 per-polygon scalar columns — as one job
    /// field shared by <see cref="SizingJob"/>, <see cref="FillGatherJob{TComparer}"/>,
    /// <see cref="EarcutBatchJob"/> and <see cref="AggregateJob"/>.
    ///
    /// <para><b>Unity's job reflection walks a nested struct field for container safety</b> the same way it
    /// walks a job's own fields. Two jobs holding the same nested containers, scheduled with no dependency
    /// between them, throw <c>InvalidOperationException</c> at the second <c>Schedule</c> call.</para>
    ///
    /// <para><b>Every field is plain, not <c>[ReadOnly]</c>.</b> A single struct field cannot carry a
    /// per-column access mode, so every job that takes this struct reads as "writes all 18" to the safety
    /// system. That costs nothing: the nodes that touch this scratch are already a linear chain.</para>
    ///
    /// <para><b>Every field must be a CREATED container, even one a job's <c>Execute</c> never reads.</b>
    /// The safety system validates the whole struct at schedule time, so an uncreated
    /// <see cref="NativeList{T}"/> left at <c>default</c> throws at <c>Schedule</c>/<c>Run</c> even for a
    /// column that job never uses. Always build a value through <see cref="Allocate"/>, or by copying every
    /// field from one.</para>
    /// </summary>
    internal struct TriangulationBuffers
    {
        public NativeList<int> VertexOffsets;
        public NativeList<int> HoleCountOffsets;
        public NativeList<int> WorkOffsets;
        public NativeList<int> IndexOffsets;

        public NativeList<double2> FlatPolyVerts;
        public NativeList<int>     FlatSortedHoleCounts;
        public NativeList<int>     FlatIndexArrays;
        public NativeList<double2> FlatWorkVerts;
        public NativeList<int>     FlatPreviousIndex;
        public NativeList<int>     FlatNextIndex;
        public NativeList<bool>    FlatIsBridge;
        public NativeList<bool>    FlatRemoved;
        public NativeList<bool>    FlatIsEar;

        public NativeList<int> PerPolyIndexCount;
        public NativeList<int> PerPolyForceClip;
        public NativeList<int> PerPolyMergedVertexCount;
        public NativeList<int> PerPolyFeatureIndex;
        public NativeList<int> PerPolyOuterCount;

        /// <summary>Allocates all 18 columns at capacity 1; <see cref="SizingJob"/> resizes each to its real
        /// length. <see cref="NewList{T}"/> records each allocation where it happens, so a column added or
        /// removed here also changes the count that <see cref="DisposeAfter"/> balances against.</summary>
        internal static TriangulationBuffers Allocate() => new TriangulationBuffers
        {
            VertexOffsets       = NewList<int>(),
            HoleCountOffsets    = NewList<int>(),
            WorkOffsets       = NewList<int>(),
            IndexOffsets        = NewList<int>(),
            FlatPolyVerts     = NewList<double2>(),
            FlatSortedHoleCounts = NewList<int>(),
            FlatIndexArrays     = NewList<int>(),
            FlatWorkVerts     = NewList<double2>(),
            FlatPreviousIndex          = NewList<int>(),
            FlatNextIndex          = NewList<int>(),
            FlatIsBridge      = NewList<bool>(),
            FlatRemoved       = NewList<bool>(),
            FlatIsEar         = NewList<bool>(),
            PerPolyIndexCount   = NewList<int>(),
            PerPolyForceClip  = NewList<int>(),
            PerPolyMergedVertexCount   = NewList<int>(),
            PerPolyFeatureIndex = NewList<int>(),
            PerPolyOuterCount = NewList<int>(),
        };

        /// <summary>Schedules a <c>Dispose(handle)</c> node for every column through
        /// <see cref="DisposeField{T}"/>, so a dropped column drops its recording too and the
        /// allocated/disposed counts stop matching. Every element takes <paramref name="deps"/>, never the
        /// combined result: the 18 dispose jobs fan OUT in parallel and the single
        /// <see cref="JobHandle.CombineDependencies(NativeArray{JobHandle})"/> only fans them back in.
        ///
        /// <para><c>Allocator.Temp</c> is right for the handle array: this method runs on the main thread,
        /// because the job system only schedules from there, and the array is allocated, combined and freed
        /// inside this one call (gc-and-allocation-design.md).</para>
        /// </summary>
        internal JobHandle DisposeAfter(JobHandle deps)
        {
            var handles = new NativeArray<JobHandle>(18, Allocator.Temp);
            try
            {
                handles[0]  = DisposeField(VertexOffsets, deps);
                handles[1]  = DisposeField(HoleCountOffsets, deps);
                handles[2]  = DisposeField(WorkOffsets, deps);
                handles[3]  = DisposeField(IndexOffsets, deps);
                handles[4]  = DisposeField(FlatPolyVerts, deps);
                handles[5]  = DisposeField(FlatSortedHoleCounts, deps);
                handles[6]  = DisposeField(FlatIndexArrays, deps);
                handles[7]  = DisposeField(FlatWorkVerts, deps);
                handles[8]  = DisposeField(FlatPreviousIndex, deps);
                handles[9]  = DisposeField(FlatNextIndex, deps);
                handles[10] = DisposeField(FlatIsBridge, deps);
                handles[11] = DisposeField(FlatRemoved, deps);
                handles[12] = DisposeField(FlatIsEar, deps);
                handles[13] = DisposeField(PerPolyIndexCount, deps);
                handles[14] = DisposeField(PerPolyForceClip, deps);
                handles[15] = DisposeField(PerPolyMergedVertexCount, deps);
                handles[16] = DisposeField(PerPolyFeatureIndex, deps);
                handles[17] = DisposeField(PerPolyOuterCount, deps);
                return JobHandle.CombineDependencies(handles);
            }
            finally { handles.Dispose(); }
        }

        private static NativeList<T> NewList<T>() where T : unmanaged
        {
            FillGraphOutput.RecordBuffersAllocated();
            return new NativeList<T>(1, Allocator.Persistent);
        }

        private static JobHandle DisposeField<T>(NativeList<T> list, JobHandle deps) where T : unmanaged
        {
            FillGraphOutput.RecordBufferDisposeNode();
            return list.Dispose(deps);
        }
    }
}
