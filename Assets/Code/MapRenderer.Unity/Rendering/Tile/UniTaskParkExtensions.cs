using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Blocking, park-on-completion waits for a <see cref="UniTask"/>/<see cref="UniTask{T}"/> that
    /// completes OFF the PlayerLoop (RunOnThreadPool / configureAwait:false / SwitchToThreadPool). Replaces
    /// the poll-and-<c>Thread.Sleep</c> pattern with a real kernel-event park; see
    /// <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.AwaitInFlightMeshBuilds"/> for the peer settle
    /// API built on top of it.</summary>
    internal static class UniTaskParkExtensions
    {
        /// <summary>Blocks the calling thread until <paramref name="task"/> completes, parking on a kernel
        /// event instead of polling. Valid ONLY for a task completing OFF the PlayerLoop: a
        /// PlayerLoop-completing task deadlocks here, because this blocks the thread that would run its
        /// continuation. Observes no
        /// result — the caller keeps the single <c>Status</c>/<c>GetResult()</c> read. Returns false on
        /// timeout; never reports success falsely. Non-local invariant: call at most once on a still-pending
        /// task. Registering the continuation consumes the underlying source's single continuation slot.
        /// <c>Preserve()</c> does not make re-parking safe: a preserved task memoizes its result, but still
        /// delegates a pending registration to that one slot. Re-parking a still-pending task throws
        /// <c>InvalidOperationException("Already continuation registered")</c>. A completed task is safe
        /// to re-check: the <c>IsCompleted</c> short-circuit fires first. A <c>false</c> return means a
        /// hang the caller must surface, not retry — see
        /// <see cref="TileManager.AwaitInFlightMeshBuilds"/>, which throws on it.</summary>
        /// <param name="task">The task to wait on; must complete off the PlayerLoop.</param>
        /// <param name="timeoutMs">The maximum time to wait, in milliseconds.</param>
        /// <returns>True if the task completed within <paramref name="timeoutMs"/>; false on timeout.</returns>
        internal static bool WaitOffPlayerLoop(this in UniTask task, int timeoutMs)
        {
            var awaiter = task.GetAwaiter();
            if (awaiter.IsCompleted) return true;
            var done = new ManualResetEventSlim(false, spinCount: 0); // spinCount:0 -> park immediately, no spin
            awaiter.UnsafeOnCompleted(done.Set);
            bool ok = done.Wait(timeoutMs);
            // Dispose ONLY on success: on timeout the continuation is still registered and will call
            // done.Set() later. Set() on a disposed event throws an unobserved ObjectDisposedException on a
            // ThreadPool thread, which can crash the batch run. Leak on timeout; the finalizer reclaims it,
            // and a timeout already fails the caller.
            if (ok) done.Dispose();
            return ok;
        }

        /// <summary>Blocks the calling thread until <paramref name="task"/> completes, parking on a kernel
        /// event instead of polling. Generic-result twin of
        /// <see cref="WaitOffPlayerLoop(in UniTask, int)"/>; see its doc for the completion-shape contract
        /// and the timeout/dispose semantics.</summary>
        /// <param name="task">The task to wait on; must complete off the PlayerLoop.</param>
        /// <param name="timeoutMs">The maximum time to wait, in milliseconds.</param>
        /// <returns>True if the task completed within <paramref name="timeoutMs"/>; false on timeout.</returns>
        internal static bool WaitOffPlayerLoop<T>(this in UniTask<T> task, int timeoutMs)
        {
            var awaiter = task.GetAwaiter();
            if (awaiter.IsCompleted) return true;
            var done = new ManualResetEventSlim(false, spinCount: 0);
            awaiter.UnsafeOnCompleted(done.Set);
            bool ok = done.Wait(timeoutMs);
            if (ok) done.Dispose();
            return ok;
        }
    }
}
