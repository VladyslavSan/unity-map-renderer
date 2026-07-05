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
    /// <para><b>Lifecycle (the S89 choreography):</b> the array is allocated on the MAIN THREAD at kick
    /// (<see cref="AllocateTracked"/>), written on the WORKER, then either <see cref="Upload"/>d (applied +
    /// disposed) or <see cref="Dispose"/>d (unapplied — disposed) on the MAIN THREAD at consume. Exactly one
    /// of Upload/Dispose frees the native array; both are guarded by <see cref="_consumed"/>.</para>
    ///
    /// <para><b>Leak guard:</b> <see cref="DebugLiveAllocCount"/> counts allocated-but-not-yet-freed
    /// <c>MeshDataArray</c>s (an unapplied array is a native leak Unity tracks). Incremented at
    /// <see cref="AllocateTracked"/>, decremented on Upload or Dispose. Net-zero after every load+release
    /// cycle; the S51 positive control asserts it goes positive on a deliberate leak.</para>
    /// </summary>
    internal sealed class MeshDataPayload : IRenderLayerPayload
    {
        internal static long LiveAllocCount;

        /// <summary>Test accessor: net live (allocated-but-not-freed) writable-mesh-data arrays.</summary>
        public static long DebugLiveAllocCount => Interlocked.Read(ref LiveAllocCount);

        /// <summary>Allocate a writable <see cref="Mesh.MeshDataArray"/> on the MAIN THREAD (kick time) and
        /// track it for the leak guard. <c>Mesh.AllocateWritableMeshData</c> is main-thread only.</summary>
        internal static Mesh.MeshDataArray AllocateTracked(int count)
        {
            var mda = Mesh.AllocateWritableMeshData(count);
            Interlocked.Increment(ref LiveAllocCount);
            return mda;
        }

        private Mesh.MeshDataArray _mda;
        private bool               _consumed;
        private readonly Bounds    _bounds;
        private readonly string    _meshName;

        /// <summary>Vertex count written by the worker (0 = empty layer — allocated but never populated).</summary>
        public int VertexCount { get; }

        /// <summary>Global draw-order / material index of the render layer this payload belongs to (S89 C).</summary>
        public int MaterialIndex { get; }

        public MeshDataPayload(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds, string meshName,
            int materialIndex)
        {
            _mda          = mda;
            VertexCount   = vertexCount;
            _bounds       = bounds;
            _meshName     = meshName;
            MaterialIndex = materialIndex;
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
        /// or teardown). No-op after <see cref="Upload"/> (ApplyAndDispose already freed it).</summary>
        public void Dispose()
        {
            if (_consumed) return;
            _mda.Dispose();
            _consumed = true;
            Interlocked.Decrement(ref LiveAllocCount);
        }
    }
}
