using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The buffers the fill graph's triangulation works over, from sizing through aggregation — 4
    /// prefix-sum offset tables, 9 flat working/output columns, 5 per-polygon scalar columns — as one job
    /// field shared by <see cref="SizingJob"/>, <see cref="FillGatherJob{TComparer}"/>,
    /// <see cref="EarcutBatchJob"/> and <see cref="AggregateJob"/>, instead of each declaring its own
    /// field list (job-scheduling-design.md §8 stage 4's R2 reshape).
    ///
    /// <para><b>Unity's job reflection walks a nested struct field for container safety</b> the same way it
    /// walks a job's own fields — proven for this exact construction by a throwaway compile checkpoint before
    /// this type existed: two jobs each taking a struct field over the SAME nested <see cref="NativeList{T}"/>
    /// containers, scheduled with no dependency between them, threw <c>InvalidOperationException</c> at the
    /// second <c>Schedule</c> call. No precedent for this shape existed in this repo before that check.</para>
    ///
    /// <para><b>Every field is plain, not <c>[ReadOnly]</c>.</b> Which of the 18 columns a given job reads
    /// versus writes differs per job (<see cref="SizingJob"/> writes all 18; <see cref="FillGatherJob{TComparer}"/>
    /// only touches 6 of them, 2 read-only); a single struct field cannot carry a per-column access mode, so
    /// every job that takes <see cref="TriangulationBuffers"/> is conservatively "writes all 18" from the safety
    /// system's view. That costs nothing here: every node in <c>FillMeshGraph.Schedule</c> that touches this
    /// scratch is already a strictly linear chain (sizing → gather → earcut → aggregate), so the dependency a
    /// conservative writer needs is the same edge the chain already has.</para>
    ///
    /// <para><b>Every field must be a CREATED container, even one a job's <c>Execute</c> never reads.</b> The
    /// safety system validates the whole struct at schedule time, not just the fields a job body touches — an
    /// uncreated <see cref="NativeList{T}"/> left at its literal <c>default</c> throws
    /// <c>InvalidOperationException</c> ("has not been assigned or constructed") at <c>Schedule</c>/<c>Run</c>,
    /// even for a column the job in question never uses. Confirmed empirically
    /// (<c>FillGraphBurstProbeTests.EarcutBatchJob_MatchesPerPolygonEarcutJobRun</c>'s first run, before its
    /// hand-built <see cref="TriangulationBuffers"/> value carried over every field): the same optional-container
    /// hazard <see cref="RingAssemblyJob.RingCountFromOffsetsLength"/>'s doc names for a single job field
    /// applies to every field of a nested struct field too. Always build a value via <see cref="Allocate"/>,
    /// or by copying every field from one.</para>
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

        /// <summary>Allocates all 18 columns at capacity 1 (every one of them is <c>Resize</c>d to its real
        /// length by <see cref="SizingJob"/>). Each column's allocation is recorded individually via
        /// <see cref="NewList{T}"/>, right where it happens — not a separate summary loop — so a column
        /// added or removed here changes the recorded count by construction, and the balance check in
        /// <see cref="DisposeAfter"/> stays tied to actual per-column calls rather than to two counts that
        /// could drift out of sync with the field list.</summary>
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

        /// <summary>Schedules a <c>Dispose(handle)</c> node for every column, via <see cref="DisposeField{T}"/>
        /// so each one records its own dispose node right where it happens — a dropped column here drops its
        /// recording with it, so the allocated/disposed-node counts stop matching. That's what lets the balance
        /// check in <c>FillMeshGraphSchedulingTests.Dispose_ReturnsLiveOutputsToBaseline_...</c> DETECT a
        /// forgotten column (as a count mismatch — it does not name which one) rather than staying "18"
        /// regardless of how many columns this method actually disposed. Every element takes
        /// <paramref name="deps"/>, never the combined result — the 18
        /// dispose jobs fan OUT in parallel; the single <see cref="JobHandle.CombineDependencies(NativeArray{JobHandle})"/>
        /// call only fans them back in, and there is no accumulator variable in scope for a copy-paste to
        /// mistakenly serialize.
        ///
        /// <para><b><c>Allocator.Temp</c>, not <c>Persistent</c>, for the handle array.</b> This method runs on
        /// the MAIN thread — by construction, not by convention: <c>NativeList{T}.Dispose(JobHandle)</c>
        /// schedules a dispose job, and the job system only schedules from the main thread
        /// (job-scheduling-design.md §1, "where the graph is scheduled"). Main-thread method-local scratch —
        /// allocated, combined and freed inside this one call — is exactly what gc-and-allocation-design.md §5
        /// points at <c>Temp</c>; the off-main-scratch-is-<c>Persistent</c> rule (driven by <c>TempJob</c>'s
        /// 4-frame main-thread-frame cap) doesn't apply here at all.</para>
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
