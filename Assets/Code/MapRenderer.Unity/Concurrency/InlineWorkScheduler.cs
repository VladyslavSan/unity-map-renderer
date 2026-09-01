using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Runs the body synchronously, on the calling thread, before <see cref="Schedule{T}"/> returns —
    /// the handle is already terminal. Selected on WebGL (no live worker threads —
    /// docs/web-target.md) and the Stage-1 acceptance seam: forcing it in an EditMode test reproduces
    /// "runs correctly with zero dispatch" without a WebGL build.</summary>
    internal sealed class InlineWorkScheduler : IWorkScheduler
    {
        /// <inheritdoc/>
        public bool RunsInline => true;

        public WorkHandle<T> Schedule<T>(System.Func<System.Threading.CancellationToken, T> body,
            System.Threading.CancellationToken ct = default)
        {
            var utcs = new UniTaskCompletionSource<T>();
            // No separate OperationCanceledException catch: UniTaskCompletionSource<T>.TrySetException
            // already redirects an OCE to TrySetCanceled(oce.CancellationToken) internally.
            try { utcs.TrySetResult(body(ct)); }
            catch (System.Exception ex) { utcs.TrySetException(ex); }
            return new WorkHandle<T>(utcs);
        }
    }
}
