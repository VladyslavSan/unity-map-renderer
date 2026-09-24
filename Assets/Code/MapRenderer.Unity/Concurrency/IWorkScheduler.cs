namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Execution policy for off-main CPU work that is a plain C# closure. ThreadPool gives the
    /// desktop/editor parallelism; Inline runs the body synchronously on the calling thread, the WebGL
    /// policy (<c>docs/web-target.md</c>), where a dispatched worker never runs. A managed closure is the
    /// fallback for a body that is not native, not the default move.</summary>
    internal interface IWorkScheduler
    {
        /// <summary>Runs <paramref name="body"/> under this policy and hands back a pollable handle.
        /// <paramref name="ct"/> is passed to the body; a body that honors it checks it at its own safe
        /// points (poll, not push — a running closure cannot be interrupted mid-flight).</summary>
        WorkHandle<T> Schedule<T>(System.Func<System.Threading.CancellationToken, T> body,
            System.Threading.CancellationToken ct = default);

        /// <summary>True iff this policy runs the body on the calling thread before
        /// <see cref="Schedule{T}"/> returns (the WebGL/Inline policy). Non-local invariant: a
        /// decorator must forward its inner scheduler's answer, because the test-gate deadlock checks in
        /// <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.WorkScheduler"/>
        /// read this property, not a type.</summary>
        bool RunsInline { get; }
    }
}
