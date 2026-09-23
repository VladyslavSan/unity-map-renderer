// The shared delay-job test instrument. A `deps`-parameter job is the production-legitimate seam for
// holding a graph genuinely in-flight — never JobsUtility.JobWorkerCount, which does not hold a scheduled
// job incomplete (see JobGraphInstrumentTests).

using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace MapRenderer.Tests
{
    /// <summary>A job that spins until released, for holding a downstream <c>deps</c> chain genuinely
    /// in-flight under the DEFAULT worker count.
    ///
    /// <para>Guards against two failure modes: a spin Burst folds to a closed form (a silently-instant
    /// "delay", so a caller's in-flight assertions pass while proving nothing), and a loop-invariant hoist
    /// of the gate read (spins forever, or never spins at all). <see cref="Gate"/> is NOT <c>[ReadOnly]</c>
    /// and carries <see cref="NativeDisableContainerSafetyRestrictionAttribute"/> — the releasing thread
    /// writes it while this job is still scheduled, which container safety otherwise rejects as a race. The
    /// accumulator mixes the gate value into a running product every iteration, so the loop cannot reduce to
    /// a closed-form iteration count.</para></summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct SpinUntilGateJob : IJob
    {
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> Gate; // [0] != 0 ⇒ stop. Written by the releasing thread while this job runs.

        // NOT on Out — the releaser must not touch Out while the job is live. Started, like Gate, is read
        // and raced by the releasing thread by design.
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> Started; // [0] set to 1 as the job's FIRST act — a caller spins on this
                                          // instead of a fixed sleep, so "still spinning" is asserted only
                                          // once the job causally IS spinning.

        public NativeArray<int> Out;  // [0] = iterations run, [1] = accumulator — both read by the caller,
                                       // so the loop's result cannot be dead-code-eliminated.
        public int MaxIterations;     // bounded ceiling ("always bound loops") — a forgotten gate release
                                       // cannot hang the suite.

        public void Execute()
        {
            Started[0] = 1;
            int i = 0;
            long acc = 0;
            while (Gate[0] == 0 && i < MaxIterations)
            {
                acc = acc * 1000000007L + Gate[0] + i;
                i++;
            }
            Out[0] = i;
            Out[1] = (int)(acc & 0x7fffffff);
        }
    }

    /// <summary>Bounded spin-wait on <see cref="SpinUntilGateJob.Started"/>.</summary>
    internal static class DelayGateJobInstrument
    {
        /// <summary>Blocks the calling thread until <paramref name="started"/>[0] is non-zero, so a caller
        /// only asserts "still spinning" once the job has causally started, and "ran ≥1 iteration" is
        /// guaranteed rather than a timing guess.</summary>
        internal static void WaitForStart(NativeArray<int> started, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (started[0] == 0)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    Assert.Fail("the delay job never signalled it started within the timeout — the worker " +
                                "pool may be starved, or the job never reached a worker at all");
                System.Threading.Thread.Yield(); // kinder to a loaded machine than a bare busy-spin
            }
        }
    }
}
