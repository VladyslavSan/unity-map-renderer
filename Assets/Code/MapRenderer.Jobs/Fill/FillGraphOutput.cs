using System;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// One fill layer's graph output. <see cref="FillMeshGraph.Schedule"/> returns it with <see cref="Handle"/>
    /// uncompleted: poll <c>Handle.IsCompleted</c>, call <see cref="Dispose"/> (which completes first), or read
    /// after <c>Handle.Complete()</c>. A layer with nothing to draw gets <c>default</c> (<see cref="IsCreated"/>
    /// false, completed default handle); every other call allocates its lists up front, even for zero polygons,
    /// because the graph cannot tell at schedule time.
    /// </summary>
    public struct FillGraphOutput : IDisposable
    {
        /// <summary>Merged-polygon vertices in tile-space <c>double2</c> — the earcut IR on the flat arm, the
        /// SUBDIVIDED tile coordinate on the curved arm. One column set serves both arms.</summary>
        public NativeList<double2> TileVertices;

        /// <summary>Origin-relative projected world positions, one per <see cref="TileVertices"/> entry.</summary>
        public NativeList<double3> WorldPositions;

        /// <summary>Per-vertex surface up, one per <see cref="TileVertices"/> entry.</summary>
        public NativeList<double3> VertexUp;

        /// <summary>Per-vertex surface east (the fill Tangent stream's xyz), one per <see cref="TileVertices"/>
        /// entry. Flat arm: constant <c>(1,0,0)</c>, written by <see cref="AggregateJob"/>. Curved arm:
        /// <see cref="GlobeFillVertex.East"/>, scattered by <see cref="GlobeFillScatterJob"/>.</summary>
        public NativeList<double3> VertexEast;

        /// <summary>The band's per-vertex attribute <c>(dirEast, dirNorth, side)</c> in the vertex's surface frame
        /// (TEXCOORD3): <c>(0,0,0)</c> on interior and inner-band vertices, <c>side = 1</c> with an outward miter on
        /// the outer ring. <see cref="AggregateJob"/> writes zeros, <see cref="FillBandJob"/> the band; subdivision
        /// carries it on <see cref="GlobeFillVertex.Band"/>, so a midpoint can hold an interpolated band. The
        /// shader never multiplies the direction by <c>side</c>.</summary>
        public NativeList<float3> VertexBand;

        /// <summary>Feature index of each vertex (into the caller's selected-feature list), one per
        /// <see cref="TileVertices"/> entry.</summary>
        public NativeList<int> VertexFeatureIdx;

        /// <summary>Flat triangle index array into <see cref="TileVertices"/>.</summary>
        public NativeList<int> TriangleIndices;

        /// <summary>[0] holds this layer's counts — the scalars a surviving list's length cannot give.
        /// <see cref="Error"/> is separate; see <see cref="FillGraphCounts"/>'s doc for why.</summary>
        public NativeArray<FillGraphCounts> Counts;

        /// <summary>Non-zero ⇒ one of <see cref="FillGraphCounts"/>'s <c>Error*</c> codes;
        /// <see cref="FillGraphCounts.Ok"/> otherwise. A standalone <see cref="NativeReference{T}"/>, not a
        /// <see cref="Counts"/> field: folding it in would make every error writer a writer of
        /// <see cref="Counts"/> too, and add a hidden edge.</summary>
        public NativeReference<int> Error;

        /// <summary>The terminal handle: every geometry node plus every scratch dispose node, combined.
        /// <c>Complete()</c> before reading any field above.</summary>
        public JobHandle Handle;

        /// <summary>True once minted by <see cref="FillMeshGraph.Schedule"/>'s general path; false for the
        /// empty-input fast-out (see the type doc).</summary>
        public bool IsCreated;

        // ── Leak/balance counters ────────────────────────────────────────────────────────────────────
        // A static allocate-and-count helper pairs with Dispose's decrement, so no call site can skip it.

        private static long _liveCount;
        private static long _buffersAllocated;
        private static long _bufferDisposeNodes;

        /// <summary>Net live OUTPUT containers (the nine fields above, <see cref="Error"/> included)
        /// allocated by <see cref="FillMeshGraph.Schedule"/> but not yet freed by <see cref="Dispose"/>.</summary>
        public static long DebugLiveCount => Interlocked.Read(ref _liveCount);

        /// <summary>Buffers (<see cref="NativeList{T}"/>s) allocated by <see cref="FillMeshGraph"/>'s
        /// <c>NewBuffer&lt;T&gt;</c> helper during one <see cref="FillMeshGraph.Schedule"/> call.</summary>
        public static long DebugBuffersAllocated => Interlocked.Read(ref _buffersAllocated);

        /// <summary>Dispose(handle) nodes <see cref="FillMeshGraph"/>'s <c>ScheduleDispose&lt;T&gt;</c>
        /// helper scheduled during one <see cref="FillMeshGraph.Schedule"/> call. Must equal
        /// <see cref="DebugBuffersAllocated"/> once <see cref="FillMeshGraph.Schedule"/> returns. A
        /// worker-side dispose node cannot decrement a managed counter, so scratch balance is observable
        /// only as this PAIRING, never as a live count.</summary>
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
            VertexBand.Dispose();           Interlocked.Decrement(ref _liveCount);
            VertexFeatureIdx.Dispose();     Interlocked.Decrement(ref _liveCount);
            TriangleIndices.Dispose();      Interlocked.Decrement(ref _liveCount);
            Counts.Dispose();               Interlocked.Decrement(ref _liveCount);
            Error.Dispose();                Interlocked.Decrement(ref _liveCount);
        }
    }
}
