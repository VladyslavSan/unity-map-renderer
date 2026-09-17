using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// S89 Stage B — the single per-<c>(tile, layer)</c> mesh payload: a writable
    /// <see cref="Mesh.MeshDataArray"/> (count 1) the worker populated, applied to a fresh <see cref="Mesh"/>
    /// at consume. Replaces Stage A's per-type fill/line payload handles —
    /// once the worker has written the <c>MeshData</c>, the payload is layer-type-agnostic.
    ///
    /// <para><b>Lifecycle (the S89 choreography):</b> the array is allocated on the MAIN THREAD
    /// (<see cref="AllocateTracked"/>) — since job-scheduling-design.md §8 stage 5 Group B, by the graph's
    /// WRITE step (<c>MeshWriteOutput</c>'s <c>ScheduleWrite</c>), not at kick — written on the WORKER, then
    /// either <see cref="Upload"/>d (applied + disposed) or <see cref="Dispose"/>d (unapplied — disposed) on
    /// the MAIN THREAD at consume. Exactly one of Upload/Dispose frees the native array; both are guarded by
    /// <see cref="_consumed"/>.</para>
    ///
    /// <para><b>Leak guard:</b> <see cref="DebugLiveAllocCount"/> counts allocated-but-not-yet-freed
    /// <c>MeshDataArray</c>s (an unapplied array is a native leak Unity tracks). Incremented at
    /// <see cref="AllocateTracked"/>, decremented on Upload or Dispose. Net-zero after every load+release
    /// cycle; the S51 positive control asserts it goes positive on a deliberate leak. Pooling the WRAPPER
    /// (below) does not touch this counter — it still counts live native arrays, not live instances.</para>
    ///
    /// <para><b>perf/gc-elimination — pooled, not `new`d:</b> instances are rented from
    /// <see cref="MeshDataPayloadPool"/> (<see cref="Reset"/>) rather than constructed fresh per layer per
    /// tile-build, and returned there from <see cref="Dispose"/> — see that method's doc for why the return
    /// is placed there and nowhere else.</para>
    ///
    /// <para><b>Reference type on purpose</b> (vestige sweep: moved here from the retired
    /// <c>IRenderLayerPayload</c>, one-implementer interface): the <see cref="_consumed"/> flag that makes
    /// Upload/Dispose exactly-once lives on the instance. A struct would be copied into the dense
    /// <c>MeshDataPayload[]</c> slot and into every local that reads it, so <see cref="Upload"/>'s flip would
    /// land on one copy while the consume loop's <see cref="Dispose"/> saw <c>_consumed == false</c> on
    /// another — double-freeing the <see cref="Mesh.MeshDataArray"/>. A class holds it in one place.
    /// (Allocated at mesh-build time only — load-time, never the steady-state per-frame path — so the
    /// allocation is not a no-GC concern.)</para>
    ///
    /// <para><b>The consume-loop contract</b> (vestige sweep: moved here from the same interface): every
    /// source-tile layer settles into a dense <c>MeshDataPayload[]</c>, one entry per layer, in SLOT order —
    /// the consume loop reads <see cref="VertexCount"/> (charged against the S87 per-frame vertex budget),
    /// calls <see cref="Upload"/> or <see cref="Dispose"/> (exactly one, never both), and reads
    /// <see cref="MaterialIndex"/> for the layer's global slot.</para>
    /// </summary>
    internal sealed class MeshDataPayload
    {
        internal static long LiveAllocCount;

        /// <summary>Test accessor: net live (allocated-but-not-freed) writable-mesh-data arrays.</summary>
        public static long DebugLiveAllocCount => Interlocked.Read(ref LiveAllocCount);

        /// <summary>Allocate a writable <see cref="Mesh.MeshDataArray"/> on the MAIN THREAD (the graph's
        /// write step, not kick — see this type's own Lifecycle doc) and track it for the leak guard.
        /// <c>Mesh.AllocateWritableMeshData</c> is main-thread only.</summary>
        internal static Mesh.MeshDataArray AllocateTracked(int count)
        {
            var mda = Mesh.AllocateWritableMeshData(count);
            Interlocked.Increment(ref LiveAllocCount);
            return mda;
        }

        /// <summary>Frees an <see cref="AllocateTracked"/>-allocated array WITHOUT going through a
        /// <see cref="MeshDataPayload"/> wrapper — the direct twin of <see cref="AllocateTracked"/>, for a
        /// caller that never wraps the array in a payload at all (never applied, never uploaded).</summary>
        internal static void DisposeTracked(Mesh.MeshDataArray mda)
        {
            mda.Dispose();
            Interlocked.Decrement(ref LiveAllocCount);
        }

        private Mesh.MeshDataArray _mda;
        private bool               _consumed;
        private Bounds             _bounds;
        private string             _meshName;

        // Guards the pool-return, independently of _consumed: Dispose() is called TWICE in the normal
        // complete-tile flow (once per-payload during TileManager.ConsumeMeshBuild's budgeted loop, again —
        // idempotently, by design — from DisposeWholePayloads's unconditional sweep over every payload). Both
        // calls must reach the pool-return exactly ONCE in total: _consumed alone can't guard it, because
        // _consumed is ALREADY true by the time the (successful) Upload() case reaches its own Dispose() —
        // that call would be skipped entirely by the `if (_consumed) return;` early-out below, and this
        // payload — the common, successful-upload case, not just the degenerate ones — would never make it
        // back to the pool at all.
        private bool _returnedToPool;

        /// <summary>Vertex count written by the worker (0 = empty layer — allocated but never populated).</summary>
        public int VertexCount { get; private set; }

        /// <summary>Global SLOT / material index of the render layer this payload belongs to (S89 C).</summary>
        public int MaterialIndex { get; private set; }

        // Pool-only: real construction happens via Reset, called from MeshDataPayloadPool.Rent()'s fallback
        // and from MeshWriteOutput.TakePayload after renting. Never invoked directly outside the pool.
        internal MeshDataPayload() { }

        /// <summary>Non-pooled direct construction — test-only-constructed now: the source-less background
        /// processor that used to call this directly retired with job-scheduling-design.md §8 stage 3 (a
        /// background tile schedules its measure graph, no processor of its own), so the sole remaining
        /// callers are tests that build a payload directly over real worker-written <c>MeshData</c>. A
        /// payload minted this way still returns to the shared pool on <see cref="Dispose"/>, exactly like a
        /// pooled one — pooling is transparent to how an instance was first created.</summary>
        internal MeshDataPayload(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds, string meshName,
            int materialIndex)
        {
            _mda          = mda;
            VertexCount   = vertexCount;
            _bounds       = bounds;
            _meshName     = meshName;
            MaterialIndex = materialIndex;
        }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance to the same state the constructor
        /// used to establish — every field <see cref="Upload"/>/<see cref="Dispose"/> read, so a reused
        /// instance never leaks a prior build's state into the next one.</summary>
        internal void Reset(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds, string meshName,
            int materialIndex)
        {
            _mda            = mda;
            VertexCount     = vertexCount;
            _bounds         = bounds;
            _meshName       = meshName;
            MaterialIndex   = materialIndex;
            _consumed       = false;
            _returnedToPool = false;
        }

        /// <summary>Main-thread: apply the worker-written MeshData to a fresh <see cref="Mesh"/> (which also
        /// disposes the array), assign the worker-computed bounds, and return it. Returns null (leaving the
        /// array for <see cref="Dispose"/>) when the layer produced no geometry.</summary>
        public Mesh Upload()
        {
            if (_consumed || VertexCount == 0) return null;

            var mesh = new Mesh { name = _meshName, indexFormat = IndexFormat.UInt32 };
            // Perf-parity with the retired UploadMesh: no index re-validation, no RecalculateBounds scan.
            Mesh.ApplyAndDisposeWritableMeshData(_mda, mesh,
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            _consumed = true;
            Interlocked.Decrement(ref LiveAllocCount);
            mesh.bounds = _bounds; // worker-computed tight AABB — no main-thread scan
            return mesh;
        }

        /// <summary>Main-thread: dispose the writable array WITHOUT applying (empty layer, mid-flight discard,
        /// or teardown). The native-array free is a no-op after <see cref="Upload"/> (ApplyAndDispose already
        /// freed it) — but the pool-return below is NOT folded into that guard: it must fire exactly once
        /// whichever of Upload-then-Dispose or a bare Dispose ran the real free, including when THIS call is
        /// itself the redundant second Dispose <c>TileManager.DisposeWholePayloads</c>'s unconditional sweep
        /// performs over an already-consumed payload (see <see cref="_returnedToPool"/>'s comment).
        ///
        /// <para>The return is placed here, never in <see cref="Upload"/>, deliberately: <c>ConsumeMeshBuild</c>
        /// calls <c>payload.Upload(); payload.Dispose();</c> back-to-back on the same reference — if Upload's
        /// success path already returned this instance to the pool, a concurrent build could Rent+Reset it in
        /// the gap before the caller's own following Dispose() call, corrupting cross-build state.</para></summary>
        public void Dispose()
        {
            if (!_consumed)
            {
                _mda.Dispose();
                _consumed = true;
                Interlocked.Decrement(ref LiveAllocCount);
            }

            if (!_returnedToPool)
            {
                _returnedToPool = true;
                MeshDataPayloadPool.Return(this);
            }
        }
    }
}
