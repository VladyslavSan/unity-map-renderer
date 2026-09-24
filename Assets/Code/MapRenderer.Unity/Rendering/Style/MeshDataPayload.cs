using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The single per-<c>(tile, layer)</c> mesh payload: a writable <see cref="Mesh.MeshDataArray"/>
    /// (count 1) the worker populated, applied to a fresh <see cref="Mesh"/> at consume.
    ///
    /// <para>Lifecycle: allocated on the MAIN THREAD (<see cref="AllocateTracked"/>) by the graph's WRITE
    /// step, written on the WORKER, then either <see cref="Upload"/>d or <see cref="Dispose"/>d on the
    /// MAIN THREAD at consume — exactly one of the two frees the native array, both guarded by
    /// <see cref="_consumed"/>. <see cref="DebugLiveAllocCount"/> is the leak guard: incremented at
    /// <see cref="AllocateTracked"/>, decremented on Upload or Dispose, net-zero after every load+release
    /// cycle. It counts live native arrays, not live instances, so pooling the wrapper does not touch it.</para>
    ///
    /// <para>Non-local invariant: a reference type, not a struct, because the <see cref="_consumed"/> flag
    /// that makes the free exactly-once must live on ONE instance — a struct copied into the
    /// <c>MeshDataPayload[]</c> slot and into every local reading it would let Upload's flip land on one
    /// copy while the consume loop's following Dispose reads <c>_consumed == false</c> on another,
    /// double-freeing the array.</para>
    ///
    /// <para>Pooled, not <c>new</c>d — rented from <see cref="MeshDataPayloadPool"/> (<see cref="Reset"/>)
    /// and returned from <see cref="Dispose"/>; see that method's own doc for why the return lives only
    /// there. The consume loop reads <see cref="VertexCount"/> (per-frame vertex budget) and
    /// <see cref="MaterialIndex"/> (the layer's global slot) per entry of the dense, SLOT-ordered
    /// <c>MeshDataPayload[]</c>, then calls Upload followed by Dispose, or Dispose alone.</para>
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

        // Guards the pool-return independently of _consumed: Upload sets _consumed, yet the Dispose that
        // follows it on the same reference must still return the instance to the pool.
        private bool _returnedToPool;

        /// <summary>Vertex count written by the worker (0 = empty layer — allocated but never populated).</summary>
        public int VertexCount { get; private set; }

        /// <summary>Global SLOT / material index of the render layer this payload belongs to.</summary>
        public int MaterialIndex { get; private set; }

        // Pool-only: real construction happens via Reset, called from MeshDataPayloadPool.Rent()'s fallback
        // and from MeshWriteOutput.TakePayload after renting. Never invoked directly outside the pool.
        internal MeshDataPayload() { }

        /// <summary>Non-pooled direct construction — its only callers are tests that build a payload over
        /// real worker-written <c>MeshData</c>. A payload minted this way still returns to the shared pool
        /// on <see cref="Dispose"/>: pooling is transparent to how an instance was first created.</summary>
        internal MeshDataPayload(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds, string meshName,
            int materialIndex)
        {
            _mda          = mda;
            VertexCount   = vertexCount;
            _bounds       = bounds;
            _meshName     = meshName;
            MaterialIndex = materialIndex;
        }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance to a clean state — every field
        /// <see cref="Upload"/>/<see cref="Dispose"/> read, so a reused
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
            // No index re-validation, no RecalculateBounds scan.
            Mesh.ApplyAndDisposeWritableMeshData(_mda, mesh,
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            _consumed = true;
            Interlocked.Decrement(ref LiveAllocCount);
            mesh.bounds = _bounds; // worker-computed tight AABB — no main-thread scan
            return mesh;
        }

        /// <summary>Main-thread: dispose the writable array WITHOUT applying, then return the instance to the
        /// pool once (<see cref="_returnedToPool"/>); the array free is a no-op after <see cref="Upload"/>.
        /// Non-local invariant: the pool return lives here, not in Upload, because <c>ConsumeMeshBuild</c> calls
        /// Upload then Dispose on the same reference, and an earlier return would let a concurrent build
        /// Rent+Reset the instance before this call runs.</summary>
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
