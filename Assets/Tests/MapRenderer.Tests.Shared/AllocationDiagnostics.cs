using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The wrapper every zero-alloc assertion here uses in place of <c>Is.Not.AllocatingGCMemory()</c> directly.
    /// It warms the measured delegate first (docs/gc-and-allocation-design.md § 6). Pass <c>warmUp: false</c> when
    /// the FIRST call IS the tested state. A failure re-measures once and says whether the allocation repeated,
    /// then prints gen-0 collections, frame and ThreadPool load. It prints no allocation count: under <c>Is.Not</c>
    /// the failure message carries none, and the recorder is shared. The test fails either way.
    /// </summary>
    public static class AllocationDiagnostics
    {
        /// <summary>Warm-up calls before every measurement — enough to clear a one-time first-use cost.</summary>
        private const int WarmUpIterations = 3;

        /// <summary>As <see cref="AssertNotAllocating(TestDelegate, string, bool)"/>, with a generic message.</summary>
        public static void AssertNotAllocating(TestDelegate act) => AssertNotAllocating(act, "must not allocate GC memory.");

        /// <summary>Asserts <paramref name="act"/> allocates no GC memory, printing diagnostic context on failure.</summary>
        /// <param name="warmUp">False when <paramref name="act"/>'s first call is itself the state under
        /// test — skips the warm-up instead of consuming that first call before the measured one.</param>
        public static void AssertNotAllocating(TestDelegate act, string message, bool warmUp = true)
        {
            if (warmUp)
                for (int w = 0; w < WarmUpIterations; w++) act();

            int collectionsBefore = GC.CollectionCount(0);
            int frameBefore = Time.frameCount;
            try
            {
                Assert.That(act, Is.Not.AllocatingGCMemory(), message);
            }
            catch (AssertionException)
            {
                int collectionsAfter = GC.CollectionCount(0);
                int frameAfter = Time.frameCount;

                // Re-measure once, only to say whether the allocation repeats. The test fails whatever this finds.
                Exception actFailure = null;
                TestDelegate remeasured = () =>
                {
                    try { act(); }
                    catch (Exception ex) { actFailure = ex; throw; }
                };
                string remeasureLine;
                try
                {
                    Assert.That(remeasured, Is.Not.AllocatingGCMemory());
                    remeasureLine = "  re-measure: the allocation did NOT repeat (one-off)\n";
                }
                catch (Exception ex)
                {
                    Exception threw = actFailure ?? (ex is AssertionException ? null : ex);
                    remeasureLine = threw != null
                        ? $"  re-measure: the measured call threw {threw.GetType().Name}, so there is no second reading\n"
                        : "  re-measure: the allocation REPEATED (per-call)\n";
                }

                string threadPoolLine;
                try
                {
                    ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
                    ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
                    threadPoolLine = $"busyWorkers={maxWorker - availWorker}/{maxWorker} " +
                                     $"busyIoThreads={maxIo - availIo}/{maxIo}";
                }
                catch (Exception ex)
                {
                    threadPoolLine = $"unavailable ({ex.GetType().Name})";
                }

                TestContext.WriteLine(
                    $"allocation diagnostic — {message}\n" +
                    remeasureLine +
                    $"  GC.CollectionCount(0): before={collectionsBefore} after={collectionsAfter}\n" +
                    $"  Time.frameCount: before={frameBefore} after={frameAfter}\n" +
                    $"  ThreadPool: {threadPoolLine}\n" +
                    $"  managed thread id={Thread.CurrentThread.ManagedThreadId}");
                throw;
            }
        }
    }
}
