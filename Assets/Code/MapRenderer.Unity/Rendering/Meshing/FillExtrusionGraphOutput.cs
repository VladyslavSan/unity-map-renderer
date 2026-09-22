using System;
using System.Threading;
using Unity.Jobs;
using MapRenderer.Jobs.Fill;
namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// One fill-extrusion layer's graph output — the roof measure plus the wall geometry, scheduled together
    /// by <see cref="FillExtrusionMeshGraph.Schedule"/> (job-scheduling-design.md).
    /// Mirrors <see cref="FillGraphOutput"/>'s shape: returned with <see cref="Handle"/> UNCOMPLETED —
    /// the caller polls <c>Handle.IsCompleted</c>, calls <see cref="Dispose"/> (which completes first), or
    /// reads <see cref="Roof"/>/<see cref="Walls"/> only after its own <c>Handle.Complete()</c>.
    /// </summary>
    internal struct FillExtrusionGraphOutput : IDisposable
    {
        /// <summary>The roof measure — the SAME <see cref="FillGraphOutput"/> a flat fill layer produces,
        /// composed unchanged by <see cref="FillExtrusionMeshGraph.Schedule"/>.</summary>
        public FillGraphOutput Roof;

        /// <summary>The wall geometry — see <see cref="StyledFillExtrusionTileBuilder.WallColumns"/>'s own
        /// doc for the stream shape and its allocate/dispose contract.</summary>
        public StyledFillExtrusionTileBuilder.WallColumns Walls;

        /// <summary>The terminal handle: the roof terminal ⊕ the wall terminal ⊕ every scratch dispose node.
        /// <c>Complete()</c> before reading <see cref="Roof"/> or <see cref="Walls"/>.</summary>
        public JobHandle Handle;

        /// <summary>True once minted by <see cref="FillExtrusionMeshGraph.Schedule"/>'s general path; false
        /// for the empty-input fast-out (mirrors <see cref="FillGraphOutput.IsCreated"/>'s own doc).</summary>
        public bool IsCreated;

        // ── Scratch balance counters — internal (test-code-bloat rule; the test assemblies see internals).
        // A separate pair from FillGraphOutput's own: the two graphs allocate different scratch, and a
        // shared counter would conflate them. ──────────────────────────────────────────────────────────

        private static long _buffersAllocated;
        private static long _bufferDisposeNodes;

        /// <summary>Buffers (<see cref="Unity.Collections.NativeList{T}"/>s) allocated by
        /// <see cref="FillExtrusionMeshGraph"/>'s own <c>NewBuffer&lt;T&gt;</c> helper during one
        /// <see cref="FillExtrusionMeshGraph.Schedule"/> call.</summary>
        public static long DebugBuffersAllocated => Interlocked.Read(ref _buffersAllocated);

        /// <summary>Dispose(handle) nodes <see cref="FillExtrusionMeshGraph"/>'s own <c>ScheduleDispose&lt;T&gt;</c>
        /// helper scheduled during one <see cref="FillExtrusionMeshGraph.Schedule"/> call. Must equal
        /// <see cref="DebugBuffersAllocated"/> once <see cref="FillExtrusionMeshGraph.Schedule"/> returns —
        /// same pairing-not-live-count reasoning as <see cref="FillGraphOutput.DebugBufferDisposeNodes"/>'s
        /// own doc.</summary>
        public static long DebugBufferDisposeNodes => Interlocked.Read(ref _bufferDisposeNodes);

        /// <summary>Records one buffer allocation — called by <c>FillExtrusionMeshGraph.NewBuffer&lt;T&gt;</c>.</summary>
        internal static void RecordBuffersAllocated() => Interlocked.Increment(ref _buffersAllocated);

        /// <summary>Records one scratch dispose node scheduled — called by
        /// <c>FillExtrusionMeshGraph.ScheduleDispose&lt;T&gt;</c>.</summary>
        internal static void RecordBufferDisposeNode() => Interlocked.Increment(ref _bufferDisposeNodes);

        /// <summary><c>Handle.Complete()</c>, then free the roof and the walls. <see cref="Roof"/>'s own
        /// <c>Dispose()</c> completes <see cref="FillGraphOutput.Handle"/> a second time — harmless, it is
        /// already complete via <see cref="Handle"/> above.</summary>
        public void Dispose()
        {
            if (!IsCreated) return;
            Handle.Complete();
            IsCreated = false;
            Roof.Dispose();
            Walls.Dispose();
        }
    }
}
