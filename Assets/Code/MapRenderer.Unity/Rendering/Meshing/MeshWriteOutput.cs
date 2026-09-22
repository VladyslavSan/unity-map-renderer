using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// The write graph's per-layer output — the write-side mirror of <c>FillGraphOutput</c>
    /// (job-scheduling-design.md). Mesh-neutral: these five fields are exactly what any write
    /// step returns (<see cref="StyledFillTileBuilder.ScheduleWrite"/> today; a line write step mints its
    /// own copy rather than this type becoming fill-specific-in-name-only). Returned with <see cref="Handle"/>
    /// UNCOMPLETED: the caller polls/completes it, then calls <see cref="TakePayload"/> (handle already
    /// completed by the caller — mirrors <c>FillGraphOutput</c>'s own "never dispose/read while in flight"
    /// contract) or <see cref="Dispose"/> (which completes first, for the released-mid-flight / teardown path).
    /// </summary>
    internal struct MeshWriteOutput
    {
        public Mesh.MeshDataArray Mda;
        public NativeArray<float3x2> Bounds;
        public int VertexCount;
        public JobHandle Handle;
        public bool IsCreated;

        /// <summary>Takes the finished payload — reads the worker-computed AABB, disposes the bounds
        /// scratch, rents+resets a pooled <see cref="MeshDataPayload"/> (perf/gc-elimination: never
        /// <c>new</c>s one). The caller must have already completed <see cref="Handle"/>; this does not
        /// complete it itself (mirrors <c>FillGraphOutput</c> — a graph builder never completes its own
        /// handle).</summary>
        internal MeshDataPayload TakePayload(string name, int materialIndex)
        {
            float3x2 b = Bounds[0];
            var bounds = new Bounds(
                new Vector3((b.c0.x + b.c1.x) * 0.5f, (b.c0.y + b.c1.y) * 0.5f, (b.c0.z + b.c1.z) * 0.5f),
                new Vector3(b.c1.x - b.c0.x, b.c1.y - b.c0.y, b.c1.z - b.c0.z));
            Bounds.Dispose();

            MeshDataPayload payload = MeshDataPayloadPool.Rent();
            payload.Reset(Mda, VertexCount, bounds, name, materialIndex);
            IsCreated = false;
            return payload;
        }

        /// <summary><c>Handle.Complete()</c>, then free the array directly (<see cref="MeshDataPayload.DisposeTracked"/>)
        /// — for the released-mid-flight / teardown path, where <see cref="TakePayload"/> was never called,
        /// so there is no payload to route this through.</summary>
        internal void Dispose()
        {
            if (!IsCreated) return;
            Handle.Complete();
            IsCreated = false;

            MeshDataPayload.DisposeTracked(Mda);
            Bounds.Dispose();
        }
    }
}
