// Pure IWorkScheduler/WorkHandle<T> contract tests — no MapView, no TileManager. They pin what the mesh-build
// kick and the symbol dispatch rely on: no body throw escapes, repeatable GetResult, never skip on a token.

using System;
using System.Threading;
using NUnit.Framework;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Concurrency
{
    [TestFixture]
    public class WorkSchedulerContractTests
    {
        /// <summary><c>IWorkScheduler.Schedule</c> never propagates a body exception: both schedulers route a
        /// throw to <c>TrySetException</c>. Non-local invariant: this keeps <c>TileManager.KickMeshBuild</c>'s
        /// single <c>catch { decode.Release(); throw; }</c> an exactly-once release; an escaping throw would
        /// double-release a decoded tile's buffers under a live owner. RED: delete the catch in
        /// <see cref="InlineWorkScheduler.Schedule{T}"/>, where the body runs on the calling thread.</summary>
        [TestCase(false, TestName = "Scheduler_CapturesABodyThrow_AndNeverPropagatesIt_Inline")]
        [TestCase(true,  TestName = "Scheduler_CapturesABodyThrow_AndNeverPropagatesIt_ThreadPool")]
        public void Scheduler_CapturesABodyThrow_AndNeverPropagatesIt(bool useThreadPool)
        {
            IWorkScheduler scheduler = useThreadPool ? new ThreadPoolWorkScheduler() : new InlineWorkScheduler();

            WorkHandle<int> handle = default;
            Assert.DoesNotThrow(
                () => handle = scheduler.Schedule<int>(_ => throw new InvalidOperationException("boom")),
                "Schedule must never propagate a body exception — I-3. A scheduler that let this throw " +
                "escape would make KickMeshBuild's single ownership-guard catch a DOUBLE release (the body's " +
                "own finally plus the kick's outer catch).");

            if (useThreadPool)
                Assert.IsTrue(handle.ToUniTask().WaitOffPlayerLoop(10000),
                    "sanity: the ThreadPool body must complete within the timeout");

            Assert.IsTrue(handle.IsFaulted, "the body's throw must fault the handle, not silently drop it.");
            Assert.IsFalse(handle.IsSucceeded, "a faulted handle must never also report Succeeded.");
            Assert.Throws<InvalidOperationException>(() => handle.GetResult(),
                "GetResult() on a faulted handle must surface the body's own exception, unwrapped.");
        }

        /// <summary>Once terminal, <see cref="WorkHandle{T}.GetResult"/> returns the same outcome on every call,
        /// with no <c>.Preserve()</c> wrapper. <c>TileManager.ConsumeMeshBuild</c> re-fetches the result on every
        /// call of a multi-frame consume. RED: swap <see cref="InlineWorkScheduler"/>'s source for the pooled,
        /// version-tokened <c>AutoResetUniTaskCompletionSource&lt;T&gt;</c>; the second call then throws.</summary>
        [Test]
        public void WorkHandle_GetResultIsRepeatableOnceTerminal()
        {
            const int sentinel = 42;
            var scheduler = new InlineWorkScheduler();
            WorkHandle<int> succeeded = scheduler.Schedule<int>(_ => sentinel);

            Assert.IsTrue(succeeded.IsCompleted);
            Assert.IsTrue(succeeded.IsSucceeded);
            Assert.AreEqual(sentinel, succeeded.GetResult(), "first GetResult() must read the body's value.");
            Assert.AreEqual(sentinel, succeeded.GetResult(), "second GetResult() must repeat the same value.");
            Assert.AreEqual(sentinel, succeeded.GetResult(), "third GetResult() must still repeat it.");
            Assert.IsTrue(succeeded.IsCompleted, "repeated reads must not disturb terminal status.");
            Assert.IsTrue(succeeded.IsSucceeded, "repeated reads must not disturb the Succeeded outcome.");

            WorkHandle<int> faulted =
                scheduler.Schedule<int>(_ => throw new InvalidOperationException("boom"));
            var first  = Assert.Throws<InvalidOperationException>(() => faulted.GetResult());
            var second = Assert.Throws<InvalidOperationException>(() => faulted.GetResult());
            Assert.AreEqual(first.Message, second.Message,
                "a faulted handle must rethrow the SAME exception across successive GetResult() calls, not " +
                "throw once and then report something else (e.g. a version-mismatch error).");
        }

        /// <summary>Unlike <c>UniTask.RunOnThreadPool(cancellationToken:)</c>, <c>IWorkScheduler.Schedule</c> runs
        /// the body even on a pre-cancelled token; the body polls the token. Non-local invariant: in
        /// <c>SymbolSubsystem</c>'s <c>PumpBuilds</c> dispatch the body is the ONLY release of its decode
        /// reference, so a skip leaks it. RED: add an early return on a cancelled <paramref name="ct"/> to
        /// either scheduler's <c>Schedule</c>; <c>bodyRan</c> stays false.</summary>
        [TestCase(false, TestName = "Scheduler_RunsTheBody_EvenWithAnAlreadyCancelledToken_Inline")]
        [TestCase(true,  TestName = "Scheduler_RunsTheBody_EvenWithAnAlreadyCancelledToken_ThreadPool")]
        public void Scheduler_RunsTheBody_EvenWithAnAlreadyCancelledToken(bool useThreadPool)
        {
            IWorkScheduler scheduler = useThreadPool ? new ThreadPoolWorkScheduler() : new InlineWorkScheduler();
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // already cancelled BEFORE Schedule is ever called

            bool bodyRan = false;
            WorkHandle<int> handle = scheduler.Schedule<int>(_ => { bodyRan = true; return 1; }, cts.Token);

            if (useThreadPool)
                Assert.IsTrue(handle.ToUniTask().WaitOffPlayerLoop(10000),
                    "sanity: the ThreadPool body must complete within the timeout");

            Assert.IsTrue(bodyRan,
                "the body must run even though its token was ALREADY cancelled before Schedule was called — " +
                "a scheduler that skipped it here would silently strand any reference the body was the sole " +
                "release for.");
            Assert.IsTrue(handle.IsSucceeded, "…and complete normally (the body itself never checked the token).");
        }
    }
}
