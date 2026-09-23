namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Execution policy for off-main CPU work that is a plain C# closure. ThreadPool reproduces
    /// the <c>UniTask.RunOnThreadPool</c> parallelism of desktop/editor; Inline runs the body
    /// synchronously on the calling thread — the WebGL policy (<c>docs/web-target.md</c>), where a
    /// dispatched worker never runs. <see cref="Schedule{T}"/> takes a managed
    /// <see cref="System.Func{T,TResult}"/> closure — the fallback for a body that can't yet be
    /// nativized, not the default move.</summary>
    internal interface IWorkScheduler
    {
        /// <summary>Runs <paramref name="body"/> under this policy and hands back a pollable handle.
        /// <paramref name="ct"/> is passed to the body; a body that honors it checks it at its own safe
        /// points (poll, not push — a running closure cannot be interrupted mid-flight).</summary>
        WorkHandle<T> Schedule<T>(System.Func<System.Threading.CancellationToken, T> body,
            System.Threading.CancellationToken ct = default);

        /// <summary>True iff this policy runs the body on the calling thread before
        /// <see cref="Schedule{T}"/> returns (the WebGL/Inline policy). <b>A decorator MUST forward its
        /// inner scheduler's answer</b>: the deadlock this guards against depends on where the body runs,
        /// not on the outermost type, so
        /// <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.WorkScheduler"/> and
        /// <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.MeshBuildGateForTest"/> read this
        /// property instead of checking a concrete type.</summary>
        bool RunsInline { get; }
    }
}
