using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The wrapper every zero-alloc assertion in this codebase uses in place of
    /// <c>Is.Not.AllocatingGCMemory()</c> directly — see docs/gc-and-allocation-design.md § 6 for why it
    /// warms the measured delegate itself before measuring. Pass <c>warmUp: false</c> when the FIRST call
    /// IS the tested state (a fresh buffer, a transition's settling frame). A failure prints forensic
    /// context: the recorder's count, <c>GC.CollectionCount(0)</c>, the frame count, and pending ThreadPool work.
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
                Recorder recorder = Recorder.Get("GC.Alloc");

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
                    $"  Recorder(\"GC.Alloc\") sampleBlockCount (as left by the constraint)={recorder.sampleBlockCount}\n" +
                    $"  GC.CollectionCount(0): before={collectionsBefore} after={collectionsAfter}\n" +
                    $"  Time.frameCount: before={frameBefore} after={frameAfter}\n" +
                    $"  ThreadPool: {threadPoolLine}\n" +
                    $"  managed thread id={Thread.CurrentThread.ManagedThreadId}");
                throw;
            }
        }
    }
}
