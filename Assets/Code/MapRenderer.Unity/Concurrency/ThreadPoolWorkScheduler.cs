using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Desktop/editor policy: dispatches the body to <see cref="System.Threading.ThreadPool"/>.
    /// Non-local invariant: completion (<c>UniTaskCompletionSource.TrySet*</c>) runs the continuation
    /// on the pool thread, so the result stays off the PlayerLoop, as <c>TileManager.DrainMeshBuilds</c>
    /// and <c>DoDispose</c> need. It posts through <c>UnsafeQueueUserWorkItem</c>, as UniTask does,
    /// because the flowing overload adds an <c>ExecutionContext</c> capture.</summary>
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
