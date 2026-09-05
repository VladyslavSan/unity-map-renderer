using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The buffers the fill graph's triangulation works over, from sizing through aggregation — 4
    /// prefix-sum offset tables, 10 flat working/output columns, 5 per-polygon scalar columns — as one job
    /// field shared by <see cref="FillSizingJob"/>, <see cref="FillGatherJob{TComparer}"/>,
    /// <see cref="EarcutBatchJob"/> and <see cref="FillAggregateJob"/>, instead of each declaring its own
    /// field list (job-scheduling-design.md §8 stage 4's R2 reshape).
    ///
    /// <para><b>Unity's job reflection walks a nested struct field for container safety</b> the same way it
    /// walks a job's own fields — proven for this exact construction by a throwaway compile checkpoint before
    /// this type existed: two jobs each taking a struct field over the SAME nested <see cref="NativeList{T}"/>
    /// containers, scheduled with no dependency between them, threw <c>InvalidOperationException</c> at the
    /// second <c>Schedule</c> call. No precedent for this shape existed in this repo before that check.</para>
    ///
    /// <para><b>Every field is plain, not <c>[ReadOnly]</c>.</b> Which of the 19 columns a given job reads
    /// versus writes differs per job (<see cref="FillSizingJob"/> writes all 19; <see cref="FillGatherJob{TComparer}"/>
    /// only touches 6 of them, 2 read-only); a single struct field cannot carry a per-column access mode, so
    /// every job that takes <see cref="FillTriangulationBuffers"/> is conservatively "writes all 19" from the safety
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
    /// hand-built <see cref="FillTriangulationBuffers"/> value carried over every field): the same optional-container
    /// hazard <see cref="RingAssemblyJob.RingCountFromOffsetsLength"/>'s doc names for a single job field
    /// applies to every field of a nested struct field too. Always build a value via <see cref="Allocate"/>,
    /// or by copying every field from one.</para>
    /// </summary>
    internal struct FillTriangulationBuffers
    {
        public NativeList<int> VertexOffsets;
        public NativeList<int> HoleCountOffsets;
        public NativeList<int> WorkOffsets;
        public NativeList<int> IndexOffsets;

        public NativeList<double2> FlatPolyVerts;
        public NativeList<int>     FlatSortedHoleCounts;
        public NativeList<int>     FlatIndexArrays;
        public NativeList<double>  FlatVx;
        public NativeList<double>  FlatVy;
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

        /// <summary>Allocates all 19 columns at capacity 1 (every one of them is <c>Resize</c>d to its real
        /// length by <see cref="FillSizingJob"/>). Each column's allocation is recorded individually via
        /// <see cref="NewList{T}"/>, right where it happens — not a separate summary loop — so a column
        /// added or removed here changes the recorded count by construction, and the balance check in
        /// <see cref="DisposeAfter"/> stays tied to actual per-column calls rather than to two counts that
        /// could drift out of sync with the field list.</summary>
        internal static FillTriangulationBuffers Allocate() => new FillTriangulationBuffers
        {
            VertexOffsets       = NewList<int>(),
            HoleCountOffsets    = NewList<int>(),
            WorkOffsets       = NewList<int>(),
            IndexOffsets        = NewList<int>(),
            FlatPolyVerts     = NewList<double2>(),
            FlatSortedHoleCounts = NewList<int>(),
            FlatIndexArrays     = NewList<int>(),
            FlatVx            = NewList<double>(),
            FlatVy            = NewList<double>(),
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
        /// recording with it, which is what lets the balance check in
        /// <c>FillMeshGraphSchedulingTests.Dispose_ReturnsLiveOutputsToBaseline_...</c> catch a forgotten
        /// column rather than a summary count that stays "19" regardless of how many columns this method
        /// actually disposed.</summary>
        internal JobHandle DisposeAfter(JobHandle deps)
        {
            JobHandle h = DisposeField(VertexOffsets, deps);
            h = JobHandle.CombineDependencies(h, DisposeField(HoleCountOffsets, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(WorkOffsets, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(IndexOffsets, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatPolyVerts, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatSortedHoleCounts, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatIndexArrays, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatVx, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatVy, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatPreviousIndex, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatNextIndex, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatIsBridge, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatRemoved, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(FlatIsEar, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(PerPolyIndexCount, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(PerPolyForceClip, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(PerPolyMergedVertexCount, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(PerPolyFeatureIndex, deps));
            h = JobHandle.CombineDependencies(h, DisposeField(PerPolyOuterCount, deps));
            return h;
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
