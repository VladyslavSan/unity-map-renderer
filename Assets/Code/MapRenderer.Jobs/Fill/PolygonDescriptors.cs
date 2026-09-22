using Unity.Collections;
using Unity.Jobs;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// <see cref="RingAssemblyJob"/>'s six polygon-descriptor outputs, grouped as one value
    /// <see cref="FillMeshGraph.Schedule"/> allocates and disposes as a unit. A <c>FillMeshGraph</c>-LOCAL
    /// grouping only: <see cref="RingAssemblyJob"/>'s own six fields stay separate, because several test
    /// fixtures construct that job directly.
    /// </summary>
    internal struct PolygonDescriptors
    {
        public NativeArray<int> PolyOuterRingIdx;
        public NativeArray<int> PolyHoleListStart;
        public NativeArray<int> PolyHoleCount;
        public NativeArray<int> HoleRingIdxs;
        public NativeArray<int> PolyCountArr;
        public NativeArray<int> HoleCountArr;

        /// <summary>Plain <see cref="NativeArray{T}"/>s, not scratch <see cref="NativeList{T}"/>s. Only the
        /// <see cref="TriangulationBuffers"/> allocations are counted by the leak/balance counters.</summary>
        internal static PolygonDescriptors Allocate(int maxPolygons, int maxHoles) => new PolygonDescriptors
        {
            PolyOuterRingIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            PolyHoleListStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            PolyHoleCount     = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            HoleRingIdxs      = new NativeArray<int>(maxHoles,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            PolyCountArr      = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory),
            HoleCountArr      = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory),
        };

        /// <summary>Schedules a <c>Dispose(handle)</c> for every field, fanned out on <paramref name="deps"/> and
        /// combined once via the array overload — same shape, and same <c>Allocator.Temp</c> reasoning, as
        /// <see cref="TriangulationBuffers.DisposeAfter"/>. No <see cref="FillGraphOutput.RecordBufferDisposeNode"/>
        /// call here: unlike that type's scratch, these six fields are not counted by the balance
        /// check (see <see cref="Allocate"/>'s own doc).</summary>
        internal JobHandle DisposeAfter(JobHandle deps)
        {
            var handles = new NativeArray<JobHandle>(6, Allocator.Temp);
            try
            {
                handles[0] = PolyOuterRingIdx.Dispose(deps);
                handles[1] = PolyHoleListStart.Dispose(deps);
                handles[2] = PolyHoleCount.Dispose(deps);
                handles[3] = HoleRingIdxs.Dispose(deps);
                handles[4] = PolyCountArr.Dispose(deps);
                handles[5] = HoleCountArr.Dispose(deps);
                return JobHandle.CombineDependencies(handles);
            }
            finally { handles.Dispose(); }
        }
    }
}
