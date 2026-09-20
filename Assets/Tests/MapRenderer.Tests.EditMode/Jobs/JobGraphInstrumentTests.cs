// Unity EditMode only — NativeArray, Burst jobs, UnityEngine.Mesh. NOT registered in core-tests.csproj.
//
// job-scheduling-design.md §8 stage 2, Group 0 — the probes the substrate stage is planned against, plus
// the delay-job instrument E2 settled on (option iii: FillMeshGraph.Schedule's existing `deps` parameter,
// not a test-only production hook). Per the design doc's own rule (§3.3, echoed here): the verdict for a
// Burst-job probe is a compilation log grep, not just a green test — see the stage report for the grep
// command and its (empty) result.

using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class JobGraphInstrumentTests
    {
        [BurstCompile(CompileSynchronously = true)]
        private struct WriteOneJob : IJob
        {
            public NativeArray<int> Out;
            public void Execute() => Out[0] = 42;
        }

        // ── The zero-worker-count finding, kept as a PASSING test — job-scheduling-design.md's original
        // assumption for E2's instrument. Originally written expecting IsCompleted == false (a RED,
        // correctly reporting a false premise); renamed and inverted to assert what is actually true, so the
        // gate is green and the knowledge survives (docs/lessons-learned.md doesn't have a slot for "a probe
        // that disproves its own premise" — this is that slot). E2's instrument is the deps-parameter delay
        // job below, not this knob — this test exists only to record why that knob was rejected. ──────────
        //
        // Complete() and Dispose() run UNCONDITIONALLY, before any assertion that could throw — an
        // AssertionException thrown while a container is still registered to a live job would be masked by
        // the finally block's own dispose-time InvalidOperationException (docs/lessons-learned.md: "an
        // exception unwinding through a using whose Dispose() also throws is silently replaced"). This bit
        // twice while this test was being written; both are fixed by completing first, unconditionally.
        [Test]
        public void ZeroWorkerCount_DoesNotHoldAScheduledJobIncomplete()
        {
            int original = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 0;

            var result = new NativeArray<int>(1, Allocator.Persistent);
            JobHandle handle = new WriteOneJob { Out = result }.Schedule();
            JobHandle.ScheduleBatchedJobs();

            bool completedBeforeComplete = handle.IsCompleted; // captured before any risk of an early throw
            handle.Complete(); // unconditional — releases the safety handle regardless of what we find above
            int value = result[0];
            result.Dispose();
            JobsUtility.JobWorkerCount = original; // unconditional, same reason

            Assert.IsTrue(completedBeforeComplete,
                "JobWorkerCount == 0 does NOT hold a scheduled job incomplete — IsCompleted reads true " +
                "immediately after Schedule(), even for a trivial one-field job. This is why the substrate's " +
                "delay instrument uses FillMeshGraph.Schedule's existing `deps` parameter instead of this knob.");
            Assert.AreEqual(42, value);
        }

        // ── The MeshData-view probe — decides FillStreamWriteJob's field set: a SCHEDULED job over a
        // MeshData-derived view (job-scheduling-design.md §9 measurement 5). ─────────────────────────────

        [BurstCompile(CompileSynchronously = true)]
        private struct FillPositionsJob : IJob
        {
            public NativeArray<Vector3> Positions;
            public void Execute()
            {
                for (int i = 0; i < Positions.Length; i++)
                    Positions[i] = new Vector3(i, i, i);
            }
        }

        /// <summary>This probe's own claim does not depend on the worker-count finding above: whether a
        /// scheduled job can safely touch a MeshData-derived view is orthogonal to whether any particular
        /// knob holds it incomplete. Complete()/Apply run unconditionally before any assertion, for the same
        /// masked-exception reason noted above.</summary>
        [Test]
        public void ScheduledJobCanWriteMeshDataStreamViews_AndBeReadBackAfterComplete()
        {
            Mesh.MeshDataArray mda = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData md = mda[0];
            md.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
            NativeArray<Vector3> positions = md.GetVertexData<Vector3>(0);

            JobHandle handle = new FillPositionsJob { Positions = positions }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            var mesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh);
            try
            {
                Vector3[] verts = mesh.vertices;
                Assert.AreEqual(4, verts.Length);
                for (int i = 0; i < 4; i++)
                    Assert.AreEqual(new Vector3(i, i, i), verts[i], $"vertex {i}");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        // ── The deps-seam delay instrument — job-scheduling-design.md E2, option (iii). ──────────────────────
        //
        // FillMeshGraph.Schedule already takes `JobHandle deps = default` and threads it into its first node
        // (a production parameter, not test surface — every node in the design takes `deps`). A test can
        // schedule its own delay job and pass its handle as `deps`, holding the whole downstream chain
        // genuinely incomplete. Calibrated here under the DEFAULT worker count — the finding above is why
        // JobWorkerCount is never touched for this instrument.
        //
        // Two failure modes this calibration exists to catch (both already bit this stage once, per E2's
        // brief): a spin that Burst folds to a closed form (silently-instant "delay" — teeth would pass while
        // proving nothing), and a loop-invariant hoist of the gate read (spins forever, or never spins at
        // all, depending on what gets hoisted). SpinUntilGateJob and WaitForStart now live in
        // MapRenderer.Tests.Shared's SpinUntilGateJob.cs (enclosing-namespace lookup resolves them here
        // unqualified) — promoted once TileManagerBackgroundRegistrationTests needed the same instrument, so
        // there is exactly one copy rather than two maintained in parallel.

        [Test]
        public void DelayJob_HeldByGate_IsGenuinelyInFlight_UnderDefaultWorkerCount()
        {
            // Deliberately does NOT touch JobsUtility.JobWorkerCount — the finding above is why.
            const int maxIterations = 2_000_000_000;
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            try
            {
                JobHandle handle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = maxIterations }.Schedule();
                JobHandle.ScheduleBatchedJobs();

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(handle.IsCompleted,
                    "the gated job must still be spinning right after it signalled it started — if Burst " +
                    "folded the loop or hoisted the gate read, this would already be true");

                gate[0] = 1; // release
                handle.Complete();

                Assert.Greater(outVals[0], 0, "the job must have run at least one real iteration");
                Assert.Less(outVals[0], maxIterations,
                    "the job must have been stopped by the GATE, not by exhausting MaxIterations — otherwise " +
                    "Burst folded the loop, or the ceiling is too low for this machine");
            }
            finally
            {
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary>Proves the mechanism end-to-end over the real production entry point: a
        /// <see cref="FillMeshGraph"/> scheduled with <c>deps</c> set to a still-spinning
        /// delay job's handle stays genuinely incomplete — the shape <c>TileBuildGraph.ScheduleMeasure</c>
        /// uses.
        ///
        /// <para>Drives the SPHERICAL (curved) arm with clip ENABLED — production's actual configuration
        /// (the shipped demo scene runs globe; <c>MapViewConfig.FillTileBufferClip = 0.0</c> decodes to
        /// <c>KeepTileUnits(0.0)</c>, not <c>Disabled</c>). An unset <c>Clip</c> would silently drive the
        /// disabled arm production never takes — the defect class this repo's 2026-09-02 audit catalogued.</para></summary>
        [Test]
        public void DelayJobAsDeps_HoldsAFillMeshGraph_GenuinelyIncomplete()
        {
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);

            TileGeometryBuffers geometry = default;
            NativeArray<int> visitOrder = default;
            FillGraphOutput graphOutput = default;
            try
            {
                geometry = TileGeometryBuffers.Allocate(
                    new TileId { Z = 0, X = 0, Y = 0 }, extent: 4096.0,
                    featureCount: 1, maxRings: 1, maxVertices: 3);
                geometry.FeatureGeometryType[0] = TileGeometryType.Polygon;
                geometry.RingOffsets[0] = 0;
                geometry.Vertices[0] = new double2(0, 0);
                geometry.Vertices[1] = new double2(10, 0);
                geometry.Vertices[2] = new double2(10, 10);
                geometry.RingFeatureIdx[0] = 0;
                geometry.RingOffsets[1] = 3;
                geometry.RingCount = 1;
                geometry.VertexCount = 3;

                visitOrder = new NativeArray<int>(1, Allocator.Persistent);
                visitOrder[0] = 0;

                var input = new FillMeshPipeline.LayerInput
                {
                    Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                    Projection = new SphericalProjection(), Clip = TileBufferClip.KeepTileUnits(0.0),
                };

                JobHandle delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();

                graphOutput = FillMeshGraph.Schedule(input, delayHandle);
                JobHandle.ScheduleBatchedJobs();

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(graphOutput.Handle.IsCompleted,
                    "a graph scheduled with deps = a still-spinning delay job must itself read incomplete");

                gate[0] = 1; // release
                graphOutput.Handle.Complete();

                Assert.IsTrue(graphOutput.Handle.IsCompleted);
                Assert.Greater(graphOutput.TileVertices.Length, 0, "the graph must still have produced real output");
            }
            finally
            {
                graphOutput.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }
    }
}
