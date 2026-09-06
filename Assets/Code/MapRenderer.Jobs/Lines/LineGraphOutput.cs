using System;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// One line layer's graph output — the write-side contract mirror of <see cref="FillGraphOutput"/>
    /// (job-scheduling-design.md §8 stage 5). Returned by <see cref="LineMeshGraph.Schedule"/> with
    /// <see cref="Handle"/> UNCOMPLETED: the caller polls <c>Handle.IsCompleted</c>, calls
    /// <see cref="Dispose"/> (which completes first) or reads outputs only after its own
    /// <c>Handle.Complete()</c>.
    /// </summary>
    public struct LineGraphOutput : IDisposable
    {
        /// <summary>Ribbon vertices in emission order, across every ring the layer's rings gathered.</summary>
        public NativeList<LineRibbonVertex> Vertices;

        /// <summary>One feature ordinal per <see cref="Vertices"/> entry — the write step's colour/width
        /// index, mirroring <see cref="FillGraphOutput.VertexFeatureIdx"/>.</summary>
        public NativeList<int> VertexFeatureIdx;

        /// <summary>Triangle indices into <see cref="Vertices"/> — already offset per ring and
        /// winding-swapped for stock Cull Back BY <see cref="RibbonAggregateJob"/> (unlike fill,
        /// where the write job does the swap — see <see cref="RibbonAggregateJob"/>'s own doc for
        /// why).</summary>
        public NativeList<int> Indices;

        /// <summary>Non-zero ⇒ one of <see cref="LineGraphCounts"/>'s <c>Error*</c> codes;
        /// <see cref="LineGraphCounts.Ok"/> otherwise.</summary>
        public NativeReference<int> Error;

        /// <summary>The terminal handle: every geometry node plus every scratch dispose node, combined.
        /// <c>Complete()</c> before reading any field above.</summary>
        public JobHandle Handle;

        /// <summary>True once minted by <see cref="LineMeshGraph.Schedule"/>'s general path; false for the
        /// empty-input fast-out.</summary>
        public bool IsCreated;

        // ── Leak/balance counters — public, like FillGraphOutput's own three (:80/:84/:92) ──────────
        // Mirrors FillGraphOutput's static allocate-and-count idiom.

        private static long _liveCount;
        private static long _buffersAllocated;
        private static long _bufferDisposeNodes;

        /// <summary>Net live OUTPUT containers (the four fields above) allocated by
        /// <see cref="LineMeshGraph.Schedule"/> but not yet freed by <see cref="Dispose"/>.</summary>
        public static long DebugLiveCount => Interlocked.Read(ref _liveCount);

        /// <summary>Buffers (<see cref="NativeList{T}"/>s) allocated by <see cref="LineMeshGraph"/>'s
        /// <c>NewBuffer&lt;T&gt;</c> helper during one <see cref="LineMeshGraph.Schedule"/> call.</summary>
        public static long DebugBuffersAllocated => Interlocked.Read(ref _buffersAllocated);

        /// <summary>Dispose(handle) nodes <see cref="LineMeshGraph"/>'s <c>ScheduleDispose&lt;T&gt;</c>
        /// helper scheduled during one <see cref="LineMeshGraph.Schedule"/> call. Must equal
        /// <see cref="DebugBuffersAllocated"/> once <see cref="LineMeshGraph.Schedule"/> returns.</summary>
        public static long DebugBufferDisposeNodes => Interlocked.Read(ref _bufferDisposeNodes);

        /// <summary>Allocates one of this output's list containers and counts it live. Used only by
        /// <see cref="LineMeshGraph.Schedule"/>.</summary>
        internal static NativeList<T> AllocateOutputList<T>() where T : unmanaged
        {
            Interlocked.Increment(ref _liveCount);
            return new NativeList<T>(Allocator.Persistent);
        }

        /// <summary>Allocates the <see cref="Error"/> reference and counts it live.</summary>
        internal static NativeReference<int> AllocateError()
        {
            Interlocked.Increment(ref _liveCount);
            return new NativeReference<int>(Allocator.Persistent);
        }

        /// <summary>Records one buffer allocation — called by <c>LineMeshGraph.NewBuffer&lt;T&gt;</c>.</summary>
        internal static void RecordBuffersAllocated() => Interlocked.Increment(ref _buffersAllocated);

        /// <summary>Records one scratch dispose node scheduled — called by
        /// <c>LineMeshGraph.ScheduleDispose&lt;T&gt;</c>.</summary>
        internal static void RecordBufferDisposeNode() => Interlocked.Increment(ref _bufferDisposeNodes);

        /// <summary><c>Handle.Complete()</c>, then free every output container. Never dispose while jobs
        /// referencing these buffers are in flight — this call enforces it by completing first.</summary>
        public void Dispose()
        {
            if (!IsCreated) return;
            Handle.Complete();
            IsCreated = false;

            Vertices.Dispose();         Interlocked.Decrement(ref _liveCount);
            VertexFeatureIdx.Dispose(); Interlocked.Decrement(ref _liveCount);
            Indices.Dispose();          Interlocked.Decrement(ref _liveCount);
            Error.Dispose();            Interlocked.Decrement(ref _liveCount);
        }
    }
}
