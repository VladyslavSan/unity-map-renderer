// Before (S89 Stage B): the tile pipeline populated a Mesh.MeshData from a RAW UniTask.RunOnThreadPool
// worker thread (NOT a blessed Unity Job worker), then applied on the main thread — this test was that
// contract guard, load-bearing for the allocate-at-kick / write-on-worker / apply-at-consume choreography.
//
// After (job-scheduling-design.md §8 stage 4 Group B, §9 row 5): nothing writes Mesh.MeshData from a raw
// managed worker any more. The write step is main-allocate (Mesh.AllocateWritableMeshData at kick) +
// FillStreamWriteJob/FillExtrusionStreamWriteJob (a Burst IJob, scheduled through Unity's job system —
// a DIFFERENT code path with its own safety semantics, not the RunOnThreadPool one this spike probes).
//
// Kept anyway, retitled as a standalone Unity-API capability probe rather than a production-path guard: if
// a future feature ever wants the raw-worker-write pattern again, this is still the test that would catch a
// Unity upgrade making it throw (GetVertexData's AtomicSafetyHandle rejecting off-job access). It proves
// nothing about the CURRENT production write path — that is FillStreamWriteJob's own tests
// (StyledFillExtrusionGraphWriteTests, TileBuildGraphTests).
//
// (Established once as a spike: writing works off-thread, but AllocateWritableMeshData /
// ApplyAndDisposeWritableMeshData are main-thread only — "CreateNewMeshDatas can only be called from the
// main thread" — which is why allocation happens at kick and apply at consume, both on the main thread.)

using System;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class MeshDataThreadWriteSpikeTests
    {
        [Test]
        public void WriteMeshData_FromRunOnThreadPoolWorker_RoundTrips()
        {
            var mda = Mesh.AllocateWritableMeshData(1);   // MAIN THREAD (kick-time allocation)
            Exception workerEx = null;
            bool wrote = false;

            var task = UniTask.RunOnThreadPool(() =>
            {
                try
                {
                    Mesh.MeshData md = mda[0];
                    md.SetVertexBufferParams(3,
                        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0));

                    var pos = md.GetVertexData<Vector3>(0);   // AtomicSafetyHandle must permit off-job write
                    pos[0] = new Vector3(1f, 2f, 3f);
                    pos[1] = new Vector3(4f, 5f, 6f);
                    pos[2] = new Vector3(7f, 8f, 9f);

                    md.SetIndexBufferParams(3, IndexFormat.UInt16);
                    var idx = md.GetIndexData<ushort>();
                    idx[0] = 0; idx[1] = 1; idx[2] = 2;

                    md.subMeshCount = 1;
                    md.SetSubMesh(0, new SubMeshDescriptor(0, 3, MeshTopology.Triangles),
                        MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
                    wrote = true;
                }
                catch (Exception e) { workerEx = e; }
            }, configureAwait: false).Preserve();

            task.WaitOffPlayerLoop(10000);

            if (workerEx != null)
            {
                mda.Dispose(); // never applied — dispose to avoid a native leak
                Assert.Fail("Writing MeshData from a RunOnThreadPool worker THREW — Stage B's off-thread write " +
                            $"is broken (Unity upgrade?). Exception: {workerEx}");
                return;
            }
            Assert.IsTrue(wrote, "worker did not complete the write");

            var mesh = new Mesh();
            try
            {
                Mesh.ApplyAndDisposeWritableMeshData(mda, mesh);   // MAIN THREAD (apply-at-consume; disposes mda)
                Assert.AreEqual(3, mesh.vertexCount, "vertex count must round-trip from the worker-written MeshData.");
                Vector3[] verts = mesh.vertices;
                Assert.AreEqual(new Vector3(4f, 5f, 6f), verts[1],
                    "a known worker-written vertex value must round-trip through ApplyAndDisposeWritableMeshData.");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
