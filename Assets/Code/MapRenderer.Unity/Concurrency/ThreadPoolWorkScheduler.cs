using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Desktop/editor policy: dispatches the body to <see cref="System.Threading.ThreadPool"/>,
    /// reproducing today's <c>UniTask.RunOnThreadPool(configureAwait:false)</c> — completion
    /// (<c>UniTaskCompletionSource.TrySet*</c>) fires on the pool thread and runs the continuation inline
    /// there, so the result stays OFF the PlayerLoop — the invariant
    /// <c>TileManager.DrainMeshBuilds</c>/<c>DoDispose</c> depend on.
    ///
    /// <para><b><c>UnsafeQueueUserWorkItem</c>, not <c>QueueUserWorkItem</c>.</b> UniTask's
    /// <c>SwitchToThreadPool</c> awaiter is an <c>ICriticalNotifyCompletion</c>, so the state machine posts
    /// through <c>ThreadPool.UnsafeQueueUserWorkItem</c> — no
    /// <see cref="System.Threading.ExecutionContext"/> capture. The flowing overload uses the same global
    /// pool but adds per-item capture/restore work, so the choice is a dispatch-cost difference, not a
    /// naming one.</para></summary>
    internal sealed class ThreadPoolWorkScheduler : IWorkScheduler
    {
        /// <inheritdoc/>
        public bool RunsInline => false;

        public WorkHandle<T> Schedule<T>(System.Func<System.Threading.CancellationToken, T> body,
            System.Threading.CancellationToken ct = default)
        {
            var utcs = new UniTaskCompletionSource<T>();
            // No separate OperationCanceledException catch: UniTaskCompletionSource<T>.TrySetException
            // already redirects an OCE to TrySetCanceled(oce.CancellationToken) internally.
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { utcs.TrySetResult(body(ct)); }
                catch (System.Exception ex) { utcs.TrySetException(ex); }
            }, null);
            return new WorkHandle<T>(utcs);
        }
    }
}
