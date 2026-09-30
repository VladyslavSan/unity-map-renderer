using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The one meter every zero-alloc assertion here uses. It counts the <c>GC.Alloc</c> events of the CALLING thread
    /// only, so an allocation another thread makes inside the window never reds a tooth. It warms the measured
    /// delegate first (docs/gc-and-allocation-design.md § 6). Pass <c>warmUp: false</c> when the FIRST call IS the
    /// tested state. A failure re-measures once, says whether the allocation repeated, and prints the context.
    /// </summary>
    public static class AllocationDiagnostics
    {
        /// <summary>Warm-up calls before every measurement — enough to clear a one-time first-use cost.</summary>
        private const int WarmUpIterations = 3;

        /// <summary>Receives the calibration allocation, so the JIT cannot drop it.</summary>
        private static object _calibrationSink;

        /// <summary>
        /// Counts the GC.Alloc events the calling thread makes inside <paramref name="act"/>. A calibration window
        /// first proves the recorder is live: it must see one known allocation. The caller warms
        /// <paramref name="act"/> itself, so the delegate's own JIT compile falls outside the window.
        /// </summary>
        /// <exception cref="InvalidOperationException">The recorder did not see the calibration allocation.</exception>
        public static int CountOnCallingThread(TestDelegate act)
        {
            Calibrate();
            return CountWindow(act);
        }

        /// <summary>Proves the recorder is live: a window with one known allocation counts one or more events.</summary>
        private static void Calibrate()
        {
            var recorder = Recorder.Get("GC.Alloc");
            Measure(recorder, () => _calibrationSink = new object());
            if (recorder.sampleBlockCount < 1)
                throw new InvalidOperationException(
                    "calibration: the GC.Alloc recorder saw no allocation in its calibration window.");
        }

        /// <summary>Counts the calling thread's events inside one window around <paramref name="act"/>.</summary>
        private static int CountWindow(TestDelegate act)
        {
            var recorder = Recorder.Get("GC.Alloc");
            Measure(recorder, act);
            return recorder.sampleBlockCount;
        }

        /// <summary>Runs <paramref name="act"/> inside one recorder window filtered to this thread. The count is ready
        /// only after the disable. <c>CollectFromAllThreads</c> must not run first: it would undo the filter.</summary>
        private static void Measure(Recorder recorder, TestDelegate act)
        {
            recorder.enabled = false; // flushes the recorder's own creation-time samples
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try { act(); }
            finally { recorder.enabled = false; }
        }

        /// <summary>As <see cref="AssertNotAllocating(TestDelegate, string, bool)"/>, with a generic message.</summary>
        public static void AssertNotAllocating(TestDelegate act)
            => AssertNotAllocating(act, "must not allocate GC memory.");

        /// <summary>Asserts <paramref name="act"/> allocates no GC memory on this thread; prints context if not.</summary>
        /// <param name="warmUp">False when <paramref name="act"/>'s first call is itself the state under
        /// test — skips the warm-up instead of consuming that first call before the measured one.</param>
        public static void AssertNotAllocating(TestDelegate act, string message, bool warmUp = true)
        {
            if (warmUp)
                for (int w = 0; w < WarmUpIterations; w++) act();

            int collectionsBefore = GC.CollectionCount(0);
            int frameBefore = Time.frameCount;
            int allocations = CountOnCallingThread(act);
            if (allocations == 0) return;

            int collectionsAfter = GC.CollectionCount(0);
            int frameAfter = Time.frameCount;

            // Re-measure the warm delegate itself, only to say if the allocation repeats. The test fails either way.
            Calibrate(); // a dead recorder is a failure of its own, never relabelled as "the measured call threw"
            string remeasureLine;
            try
            {
                int second = CountWindow(act);
                remeasureLine = second == 0
                    ? "  re-measure: the allocation did NOT repeat (one-off)\n"
                    : $"  re-measure: the allocation REPEATED (per-call, {second} event(s))\n";
            }
            catch (Exception ex)
            {
                remeasureLine = $"  re-measure: the measured call threw {ex.GetType().Name}, so no second reading\n";
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
                $"  measured thread: {allocations} GC.Alloc event(s)\n" +
                remeasureLine +
                $"  GC.CollectionCount(0): before={collectionsBefore} after={collectionsAfter}\n" +
                $"  Time.frameCount: before={frameBefore} after={frameAfter}\n" +
                $"  ThreadPool: {threadPoolLine}\n" +
                $"  managed thread id={Thread.CurrentThread.ManagedThreadId}");
            Assert.Fail($"{message} The measured thread made {allocations} GC.Alloc event(s).");
        }
    }
}
