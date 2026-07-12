using System;
using UnityEngine;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The transitional per-<c>(tile, layer)</c> mesh payload — the single uniform handle the tile
    /// consume loop uploads and disposes, regardless of the producing layer's type. This is what collapses
    /// the two-array <c>MeshBuildResult</c> (fills lane + lines lane) into one ordered payload array.
    ///
    /// <para><b>Reference type on purpose:</b> the concrete handles wrap a NativeArray-backed
    /// <c>struct</c> whose <c>Dispose</c> flips its own <c>IsCreated</c> flag. A boxed struct behind an
    /// interface would be copied, so the flag flip would be lost and a second dispose would double-free /
    /// leak. A class wrapper holds the struct in a mutable field, so <see cref="IDisposable.Dispose"/>
    /// mutates it in place. (Allocated at mesh-build time only — load-time, never the steady-state
    /// per-frame path — so the class allocation is not a no-GC concern.)</para>
    ///
    /// <para>Stage B replaces the innards with <c>Mesh.MeshData</c>; the consume-loop contract
    /// (<see cref="VertexCount"/> + <see cref="Upload"/> + <see cref="IDisposable.Dispose"/>) is unchanged,
    /// so B swaps the handle's implementation without touching its callers.</para>
    /// </summary>
    internal interface IRenderLayerPayload : IDisposable
    {
        /// <summary>Vertex count of the produced geometry — charged against the S87 per-frame vertex budget.</summary>
        int VertexCount { get; }

        /// <summary>The render layer's global draw-order index (== material index). S89 Stage C: the payload
        /// carries its own index so a dense per-<c>(tile, source)</c> result no longer relies on
        /// <c>cursor == materialIndex</c> (which only held for a full-width sparse union).</summary>
        int MaterialIndex { get; }

        /// <summary>Main-thread: upload the payload into a fresh <see cref="Mesh"/>. Returns <c>null</c> when
        /// the payload is empty (defensive — the producer returns null rather than an empty handle).</summary>
        Mesh Upload();
    }
}
