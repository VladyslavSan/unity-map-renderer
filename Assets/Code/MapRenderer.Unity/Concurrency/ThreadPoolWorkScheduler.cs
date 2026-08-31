using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Desktop/editor policy: dispatches the body to <see cref="System.Threading.ThreadPool"/>,
    /// reproducing today's <c>UniTask.RunOnThreadPool(configureAwait:false)</c> — completion
    /// (<c>UniTaskCompletionSource.TrySet*</c>) fires on the pool thread and runs the continuation inline
    /// there, so the result stays OFF the PlayerLoop (the §G-1 invariant
    /// <c>TileManager.DrainMeshBuilds</c>/<c>DoDispose</c> depend on).
    ///
    /// <para><b><c>UnsafeQueueUserWorkItem</c>, not <c>QueueUserWorkItem</c>.</b> UniTask's own
    /// <c>SwitchToThreadPool</c> awaiter is an <c>ICriticalNotifyCompletion</c>, so the compiler-generated
    /// state machine calls its <c>UnsafeOnCompleted</c>, which posts via
    /// <c>ThreadPool.UnsafeQueueUserWorkItem</c> (no <see cref="System.Threading.ExecutionContext"/> capture).
    /// The flowing overload posts through the same global pool but adds per-item capture/restore work, so it
    /// is a real dispatch-cost difference from today's path, not just a naming one. Matching the exact
    /// primitive keeps this scheduler dispatch-equivalent to what it replaces, satisfying the stage invariant
    /// by construction rather than by measurement.</para></summary>
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
