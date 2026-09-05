// Unity EditMode only — NativeList, Burst jobs. NOT registered in core-tests.csproj.
//
// The line twin of FillSizingJobTests' capacity-overrun arm (job-scheduling-design.md §8 stage 6, E.2):
// LineRibbonSizingJob's early return is genuinely unreachable — every ring's growth is floored at 1
// (LineRibbonSizingJob.cs:68-69) — so unlike the fill side, Arm A below CONSTRUCTS the post-early-return
// state directly rather than driving sizing into it. See Arm A's own doc for the one way that differs from
// the real early-return state, and why the delta cannot change what the arm observes.
//
// Claim honestly (par-findings-checklist.md:113-115's brief said the missing arm here "is the gap that
// would have caught" the original fill bug — it is not; fill's identical sizing-only arm did not catch
// fill's identical bug, see FillSizingJobTests.cs's header): Arm B is the parity arm for the
// monotonicity-assertion gap only. It would NOT have caught an out-of-bounds read past a borrowed count —
// Arm A is the one that would.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Lines;
namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class LineRibbonSizingJobTests
    {
        /// <summary>The arm that would have caught the line side's counterpart of the fill bug: the real
        /// <see cref="LineRibbonAggregateJob"/> over a <see cref="LineRibbonBuffers"/> already at the
        /// post-early-return shape (every column length 0), with a real <c>RingFeature</c> standing in for the
        /// borrowed ring count the retired bound would have used.
        ///
        /// <para><b>Constructed, not driven — the one way it differs from the real early-return state, stated
        /// plainly (not "byte-for-byte", which would be false):</b> <see cref="LineRibbonSizingJob"/>
        /// (<c>:62-73</c>) fills <c>RingVertexOffsets</c>/<c>RingIndexOffsets</c> with <c>ringCount + 1</c>
        /// entries BEFORE its monotonicity return (<c>:75-82</c>), whereas <see cref="LineRibbonBuffers.Allocate"/>
        /// leaves both at length 0. That delta cannot change what this arm observes:
        /// <see cref="LineRibbonAggregateJob"/> reads <c>perRingVertexCount[r]</c> before it ever touches
        /// either offset table, and that column is length 0 in BOTH states, so the loop this arm is about
        /// never runs in either. Driving it through sizing to reach the state exactly would require an
        /// impossible non-monotonic table (sizing's growth floor at 1 makes disjointness structural) — not a
        /// degenerate <c>m &lt; 2</c> ring either, since <c>LineRingGatherJob.cs:101</c> filters those out
        /// before sizing ever sees them.</para>
        ///
        /// <para><b>RED recipe — hand-written (LineRibbonAggregateJob has only ever existed in its fixed
        /// form; <c>git log -- LineRibbonAggregateJob.cs</c> returns one commit), executed once by the
        /// developer, reverted immediately.</b> Add <c>[ReadOnly] public NativeList&lt;int&gt; RingSubOffsets;</c>
        /// to the job, replace <c>int ringCount = perRingVertexCount.Length;</c> with
        /// <c>int ringCount = RingSubOffsets.Length - 1;</c>, and hand the tooth a length-3
        /// <c>RingSubOffsets</c>. Observed: RED at <c>perRingVertexCount[r]</c> — recorded in the stage
        /// report, not here (a documented recipe nobody has run is fiction).</para>
        /// </summary>
        [Test]
        public void AggregateOverUnsizedBuffers_ProducesEmptyOutput_AndDoesNotIndexPastThem()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Fail("ENABLE_UNITY_COLLECTIONS_CHECKS is not defined in this test assembly build — " +
                "without it this tooth cannot detect a borrowed-count OOB read in a release-configured build.");
#endif
            LineRibbonBuffers buffers = LineRibbonBuffers.Allocate();
            var ringFeature = new NativeList<int>(Allocator.Persistent);
            ringFeature.Add(0);
            ringFeature.Add(0);

            var outVertices         = new NativeList<LineRibbonVertex>(Allocator.Persistent);
            var outVertexFeatureIdx = new NativeList<int>(Allocator.Persistent);
            var outIndices          = new NativeList<int>(Allocator.Persistent);
            var error               = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new LineRibbonAggregateJob
                {
                    RingFeature = ringFeature,
                    Buffers = buffers,
                    MaxOutputVertices = 1000,
                    OutVertices = outVertices, OutVertexFeatureIdx = outVertexFeatureIdx, OutIndices = outIndices,
                    Error = error,
                }.Run();

                Assert.AreEqual(LineGraphCounts.Ok, error.Value,
                    "an empty (post-early-return) buffers state must not itself be reported as an error");
                Assert.AreEqual(0, outVertices.Length, "no ring to append means no output vertices");
                Assert.AreEqual(0, outVertexFeatureIdx.Length);
                Assert.AreEqual(0, outIndices.Length, "no ring to append means no output indices");

                // Non-vacuity witness: the borrowed ring count the retired bound would have used is still 2 —
                // without this the test would pass identically on an input where there was nothing to
                // iterate in the first place.
                Assert.AreEqual(2, ringFeature.Length);
            }
            finally
            {
                buffers.DisposeAfter(default).Complete();
                ringFeature.Dispose();
                outVertices.Dispose(); outVertexFeatureIdx.Dispose(); outIndices.Dispose();
                error.Dispose();
            }
        }

        /// <summary>The parity arm for the C.2-equivalent monotonicity gap — <c>FillSizingJobTests
        /// .TwoValidPolygons_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag</c>'s shape, one for one. Two
        /// real rings within capacity produce four strictly-increasing offset tables and
        /// <see cref="LineGraphCounts.Ok"/>.
        ///
        /// <para><b>RED recipe, mirroring the fill arm's word for word — executed once, observed, reverted.</b>
        /// Temporarily change <c>LineRibbonSizingJob.cs:71</c>'s <c>ringVertexOffsets.Add(ringVertexOffsets[r]
        /// + maxV);</c> to <c>ringVertexOffsets.Add(r &gt; 0 ? ringVertexOffsets[r] : ringVertexOffsets[r] +
        /// maxV);</c> — ring 1's entry then equals ring 0's (a zero-length slice) — and this test flips from
        /// <see cref="LineGraphCounts.Ok"/> to <see cref="LineGraphCounts.ErrorOffsetTableNotDisjoint"/>.
        /// Revert immediately after observing it.</para>
        ///
        /// <para><b>Claim honestly:</b> this arm catches a non-monotonic offset table. It would NOT have
        /// caught the out-of-bounds read — fill's identical arm did not catch fill's identical bug. Arm A
        /// above is the one that would.</para>
        /// </summary>
        [Test]
        public void TwoValidRings_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag()
        {
            var ringSubOffsets = new NativeList<int>(Allocator.Persistent);
            ringSubOffsets.Add(0); ringSubOffsets.Add(4); ringSubOffsets.Add(9);

            LineRibbonBuffers buffers = LineRibbonBuffers.Allocate();
            var error = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new LineRibbonSizingJob
                {
                    RingSubOffsets = ringSubOffsets,
                    RoundSegments = 8,
                    Buffers = buffers,
                    Error = error,
                }.Run();

                Assert.AreEqual(LineGraphCounts.Ok, error.Value,
                    "two real, in-capacity rings must produce strictly increasing offset tables");

                int[] ringVertexOffsets = buffers.RingVertexOffsets.AsArray().ToArray();
                int[] ringIndexOffsets  = buffers.RingIndexOffsets.AsArray().ToArray();
                for (int r = 0; r < 2; r++)
                {
                    Assert.Greater(ringVertexOffsets[r + 1], ringVertexOffsets[r], $"RingVertexOffsets[{r}] must be strictly increasing");
                    Assert.Greater(ringIndexOffsets[r + 1], ringIndexOffsets[r], $"RingIndexOffsets[{r}] must be strictly increasing");
                }

                Assert.AreEqual(ringVertexOffsets[2], buffers.FlatVertices.Length, "FlatVertices must be resized to the offset table's total");
                Assert.AreEqual(ringIndexOffsets[2], buffers.FlatIndices.Length, "FlatIndices must be resized to the offset table's total");
                Assert.AreEqual(2, buffers.PerRingVertexCount.Length);
                Assert.AreEqual(2, buffers.PerRingIndexCount.Length);
            }
            finally
            {
                ringSubOffsets.Dispose();
                buffers.DisposeAfter(default).Complete();
                error.Dispose();
            }
        }
    }
}
