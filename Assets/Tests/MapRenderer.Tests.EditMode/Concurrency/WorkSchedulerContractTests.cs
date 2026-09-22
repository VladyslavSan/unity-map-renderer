// Stage-2 acceptance: pure IWorkScheduler/WorkHandle<T> contract teeth — no MapView, no TileManager. These
// pin the properties the mesh-build kick migration and the symbol dispatch migration (SymbolSubsystem.cs
// :643/:899) lean on: I-3 (a body throw never propagates out of Schedule, which is what keeps
// KickMeshBuild's single ownership-guard catch exactly-once under both policies), repeatable-GetResult (why
// the migration drops .Preserve() rather than replacing it), and never-skips-on-token (poll, not push — why
// SymbolSubsystem's parked-drain dispatch needs no cancellationToken:-style skip guard the way
// UniTask.RunOnThreadPool did).

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
        /// <summary>I-3: <c>IWorkScheduler.Schedule</c> never propagates a body exception — both shipped
        /// schedulers wrap the body in their own try/catch and route a throw to
        /// <c>TrySetException</c>, so <c>Schedule</c> itself can only throw for a dispatch failure. This is
        /// what lets <c>TileManager.KickMeshBuild</c>'s single <c>catch { decode.Release(); throw; }</c>
        /// stay an exactly-once release under EITHER policy — a scheduler that let
        /// a body throw escape would double-release (the body's own <c>finally</c> plus the kick's outer
        /// catch), silently disposing a decoded tile's buffers under a live owner.
        ///
        /// <para><b>RED injection:</b> delete the <c>catch (Exception ex) { utcs.TrySetException(ex); }</c>
        /// from <see cref="InlineWorkScheduler.Schedule{T}"/> — <c>Assert.DoesNotThrow</c> fails because the
        /// body's throw now propagates out of <c>Schedule</c> uncaught. Inline is the right injection target:
        /// it is the policy where the body runs INSIDE <c>Schedule</c>, on the calling thread, so it is the
        /// one where a missing catch actually escapes to the caller.</para></summary>
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

        /// <summary>Repeatable-GetResult: once terminal, <see cref="WorkHandle{T}.GetResult"/> is callable
        /// any number of times with the same outcome — no version-token invalidation, no
        /// <c>.Preserve()</c> needed. This is the property the migration relies on instead of the
        /// <c>.Preserve()</c> wrapper it removes: <c>TileManager.ConsumeMeshBuild</c> re-fetches the result on
        /// every call across a resumable multi-frame consume.
        ///
        /// <para><b>RED injection:</b> in <see cref="InlineWorkScheduler"/>, swap
        /// <c>UniTaskCompletionSource&lt;T&gt;</c> for the pooled, version-tokened
        /// <c>AutoResetUniTaskCompletionSource&lt;T&gt;</c> — the second <c>GetResult()</c> then throws on a
        /// token-version mismatch instead of repeating the value.</para></summary>
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

        /// <summary>Never-skips-on-token: unlike <c>UniTask.RunOnThreadPool(cancellationToken:)</c>, which
        /// skips the delegate entirely when the token is already cancelled at dispatch time,
        /// <c>IWorkScheduler.Schedule</c> ALWAYS runs the body — the token is handed to the body to poll at
        /// its own safe points ("poll, not push" — <see cref="IWorkScheduler.Schedule{T}"/>'s own doc). This
        /// is the property <c>SymbolSubsystem</c>'s parked-drain dispatch (<c>PumpBuilds</c>) depends on: the
        /// dispatched body is the ONLY release for its decode reference, so a scheduler that skipped it on a
        /// pre-cancelled token would leak exactly as the old <c>cancellationToken:</c> argument would have.
        ///
        /// <para><b>RED injection:</b> add an early-return-without-running-body guard on a cancelled
        /// <paramref name="ct"/> to either scheduler's <c>Schedule</c> (mirroring
        /// <c>UniTask.RunOnThreadPool</c>'s skip) — <c>bodyRan</c> stays false.</para></summary>
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
