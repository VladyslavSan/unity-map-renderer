using Unity.Collections;
using Unity.Jobs;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// <see cref="RingAssemblyJob"/>'s six polygon-descriptor outputs, grouped as one value
    /// <see cref="FillMeshGraph.Schedule"/> allocates and disposes as a unit (job-scheduling-design.md §8
    /// stage 4's R2 reshape). A <c>FillMeshGraph</c>-LOCAL grouping only — <see cref="RingAssemblyJob"/>'s own
    /// six fields are untouched: reshaping its field list would touch every caller of a job several test
    /// fixtures still construct directly, for a saving this cycle's duplication complaint does not name.
    /// </summary>
    internal struct PolygonDescriptors
    {
        public NativeArray<int> PolyOuterRingIdx;
        public NativeArray<int> PolyHoleListStart;
        public NativeArray<int> PolyHoleCount;
        public NativeArray<int> HoleRingIdxs;
        public NativeArray<int> PolyCountArr;
        public NativeArray<int> HoleCountArr;

        /// <summary>Plain <see cref="NativeArray{T}"/>s, not scratch <see cref="NativeList{T}"/>s — matches
        /// <c>FillMeshGraph.cs</c>'s existing convention that only the <see cref="FillTriangulationBuffers"/> allocations
        /// are counted by the leak/balance counters.</summary>
        internal static PolygonDescriptors Allocate(int maxPolygons, int maxHoles) => new PolygonDescriptors
        {
            PolyOuterRingIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            PolyHoleListStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            PolyHoleCount     = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            HoleRingIdxs      = new NativeArray<int>(maxHoles,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            PolyCountArr      = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory),
            HoleCountArr      = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory),
        };

        internal JobHandle DisposeAfter(JobHandle deps)
        {
            JobHandle h = PolyOuterRingIdx.Dispose(deps);
            h = JobHandle.CombineDependencies(h, PolyHoleListStart.Dispose(deps));
            h = JobHandle.CombineDependencies(h, PolyHoleCount.Dispose(deps));
            h = JobHandle.CombineDependencies(h, HoleRingIdxs.Dispose(deps));
            h = JobHandle.CombineDependencies(h, PolyCountArr.Dispose(deps));
            h = JobHandle.CombineDependencies(h, HoleCountArr.Dispose(deps));
            return h;
        }
    }
}
