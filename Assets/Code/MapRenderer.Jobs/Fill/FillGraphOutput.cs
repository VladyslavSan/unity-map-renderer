using System;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// One fill layer's graph output — the scheduled form of what the (retired) synchronous
    /// <c>FillMeshPipeline.Schedule</c> used to return computed and complete (job-scheduling-design.md §3.2
    /// rule 1). Returned by <see cref="FillMeshGraph.Schedule"/> with <see cref="Handle"/> UNCOMPLETED: the
    /// caller polls <c>Handle.IsCompleted</c>, calls <see cref="Dispose"/> (which completes first) or reads
    /// outputs only after its own <c>Handle.Complete()</c>.
    ///
    /// <para><b>Two ways to end up with a value.</b> A layer with nothing to draw
    /// (<c>!Geometry.IsCreated || !RingVisitOrder.IsCreated || Length == 0</c>) makes
    /// <see cref="FillMeshGraph.Schedule"/> return <c>default</c> — <see cref="IsCreated"/> false, no lists
    /// ever allocated, <see cref="Handle"/> the completed default handle. Every other call allocates real
    /// lists up front and schedules the whole chain over them, even when the layer turns out to have zero
    /// polygons after the chain runs — the graph cannot know that at schedule time, unlike a synchronous
    /// pipeline that reads the polygon count back before returning (job-scheduling-design.md §8 stage 4
    /// Group B: this is exactly the shape a parity check against such a pipeline had to normalise, back when
    /// one existed as an independent oracle to check against).</para>
    /// </summary>
    public struct FillGraphOutput : IDisposable
    {
        /// <summary>Merged-polygon vertices in tile-space <c>double2</c> — the earcut IR on the flat arm, the
        /// SUBDIVIDED tile coordinate on the curved arm (job-scheduling-design.md §3.7: one column set for
        /// both arms).</summary>
        public NativeList<double2> TileVertices;

        /// <summary>Origin-relative projected world positions, one per <see cref="TileVertices"/> entry.</summary>
        public NativeList<double3> WorldPositions;

        /// <summary>Per-vertex surface up, one per <see cref="TileVertices"/> entry.</summary>
        public NativeList<double3> VertexUp;

        /// <summary>Per-vertex surface east (the fill Tangent stream's xyz), one per <see cref="TileVertices"/>
        /// entry. Flat arm: constant <c>(1,0,0)</c>, written by <see cref="AggregateJob"/>. Curved arm:
        /// <see cref="GlobeFillVertex.East"/>, scattered by <see cref="GlobeFillScatterJob"/>.</summary>
        public NativeList<double3> VertexEast;

        /// <summary>Feature index of each vertex (into the caller's selected-feature list), one per
        /// <see cref="TileVertices"/> entry.</summary>
        public NativeList<int> VertexFeatureIdx;

        /// <summary>Flat triangle index array into <see cref="TileVertices"/>.</summary>
        public NativeList<int> TriangleIndices;

        /// <summary>[0] holds this layer's counts — the scalars a surviving list's length cannot give.
        /// <see cref="Error"/> is separate; see <see cref="FillGraphCounts"/>'s doc for why.</summary>
        public NativeArray<FillGraphCounts> Counts;

        /// <summary>Non-zero ⇒ one of <see cref="FillGraphCounts"/>'s <c>Error*</c> codes; <see cref="FillGraphCounts.Ok"/>
        /// otherwise. A standalone <see cref="NativeReference{T}"/>, not a <see cref="Counts"/> field — every
        /// writer of an error code (<see cref="SizingJob"/>, <see cref="EarcutBatchJob"/>) would otherwise
        /// also become a writer of <see cref="Counts"/>, which is exactly the hidden-edge hazard this split
        /// removes (job-scheduling-design.md §3.2's own shape).</summary>
        public NativeReference<int> Error;

        /// <summary>The terminal handle: every geometry node plus every scratch dispose node, combined.
        /// <c>Complete()</c> before reading any field above.</summary>
        public JobHandle Handle;

        /// <summary>True once minted by <see cref="FillMeshGraph.Schedule"/>'s general path; false for the
        /// empty-input fast-out (see the type doc).</summary>
        public bool IsCreated;

        // ── Leak/balance counters — internal (test-code-bloat rule; the test assemblies see internals) ──
        // copying the MeshDataPayload.DebugLiveAllocCount idiom (a static allocate-and-count helper pairs
        // with Dispose's decrement, so the increment can never be forgotten at a call site).

        private static long _liveCount;
        private static long _buffersAllocated;
        private static long _bufferDisposeNodes;

        /// <summary>Net live OUTPUT containers (the eight fields above, <see cref="Error"/> included)
        /// allocated by <see cref="FillMeshGraph.Schedule"/> but not yet freed by <see cref="Dispose"/>.</summary>
        public static long DebugLiveCount => Interlocked.Read(ref _liveCount);

        /// <summary>Buffers (<see cref="NativeList{T}"/>s) allocated by <see cref="FillMeshGraph"/>'s
        /// <c>NewBuffer&lt;T&gt;</c> helper during one <see cref="FillMeshGraph.Schedule"/> call.</summary>
        public static long DebugBuffersAllocated => Interlocked.Read(ref _buffersAllocated);

        /// <summary>Dispose(handle) nodes <see cref="FillMeshGraph"/>'s <c>ScheduleDispose&lt;T&gt;</c>
        /// helper scheduled during one <see cref="FillMeshGraph.Schedule"/> call. Must equal
        /// <see cref="DebugBuffersAllocated"/> once <see cref="FillMeshGraph.Schedule"/> returns — a
        /// worker-side <c>Dispose(handle)</c> node cannot decrement a managed counter on completion (it runs
        /// off the main thread), so scratch balance is observable only as this PAIRING, never as a live
        /// count the way <see cref="DebugLiveCount"/> is for outputs.</summary>
        public static long DebugBufferDisposeNodes => Interlocked.Read(ref _bufferDisposeNodes);

        /// <summary>Allocates one of this output's list containers and counts it live. Used only by
        /// <see cref="FillMeshGraph.Schedule"/>.</summary>
        internal static NativeList<T> AllocateOutputList<T>() where T : unmanaged
        {
            Interlocked.Increment(ref _liveCount);
            return new NativeList<T>(Allocator.Persistent);
        }

        /// <summary>Allocates the <see cref="Counts"/> array and counts it live.</summary>
        internal static NativeArray<FillGraphCounts> AllocateCounts()
        {
            Interlocked.Increment(ref _liveCount);
            return new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        /// <summary>Allocates the <see cref="Error"/> reference and counts it live.</summary>
        internal static NativeReference<int> AllocateError()
        {
            Interlocked.Increment(ref _liveCount);
            return new NativeReference<int>(Allocator.Persistent);
        }

        /// <summary>Records one buffer allocation — called by <c>FillMeshGraph.NewBuffer&lt;T&gt;</c>.</summary>
        internal static void RecordBuffersAllocated() => Interlocked.Increment(ref _buffersAllocated);

        /// <summary>Records one scratch dispose node scheduled — called by
        /// <c>FillMeshGraph.ScheduleDispose&lt;T&gt;</c>.</summary>
        internal static void RecordBufferDisposeNode() => Interlocked.Increment(ref _bufferDisposeNodes);

        /// <summary><c>Handle.Complete()</c>, then free every output container. Never dispose while jobs
        /// referencing these buffers are in flight — this call enforces it by completing first.</summary>
        public void Dispose()
        {
            if (!IsCreated) return;
            Handle.Complete();
            IsCreated = false;

            TileVertices.Dispose();         Interlocked.Decrement(ref _liveCount);
            WorldPositions.Dispose();       Interlocked.Decrement(ref _liveCount);
            VertexUp.Dispose();             Interlocked.Decrement(ref _liveCount);
            VertexEast.Dispose();           Interlocked.Decrement(ref _liveCount);
            VertexFeatureIdx.Dispose();     Interlocked.Decrement(ref _liveCount);
            TriangleIndices.Dispose();      Interlocked.Decrement(ref _liveCount);
            Counts.Dispose();               Interlocked.Decrement(ref _liveCount);
            Error.Dispose();                Interlocked.Decrement(ref _liveCount);
        }
    }
}
