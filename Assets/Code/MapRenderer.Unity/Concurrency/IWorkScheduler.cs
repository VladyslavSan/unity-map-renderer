namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Execution policy for off-main CPU work that is (today) a plain C# closure. ThreadPool
    /// reproduces today's UniTask.RunOnThreadPool parallelism (desktop/editor); Inline runs the body
    /// synchronously on the calling thread — the WebGL fix (docs/threading-on-web.md), since nothing is
    /// dispatched to a worker that will never run it.
    ///
    /// <para><b>This is a transitional bridge, not the destination.</b> <see cref="Schedule{T}"/> takes a
    /// managed <see cref="System.Func{T,TResult}"/> closure — exactly the shape a native/Burst rewrite
    /// eliminates — so this interface does not survive jobification and is not itself a design to converge
    /// on. The destination is a Burst job struct over native columns, dispatched <c>.Run()</c>, in the shape
    /// <c>NativeFilterEvaluationJob</c> (<c>MapRenderer.Jobs/Mvt/NativeFilterEvaluationJob.cs</c>) already
    /// established. A new off-main site should ask first whether its body can be nativized into that shape;
    /// routing it through <see cref="IWorkScheduler"/> is the fallback for a still-managed body, not the
    /// default move.</para></summary>
    internal interface IWorkScheduler
    {
        /// <summary>Runs <paramref name="body"/> under this policy and hands back a pollable handle.
        /// <paramref name="ct"/> is passed to the body; a body that honors it checks it at its own safe
        /// points (poll, not push — a running closure cannot be interrupted mid-flight).</summary>
        WorkHandle<T> Schedule<T>(System.Func<System.Threading.CancellationToken, T> body,
            System.Threading.CancellationToken ct = default);

        /// <summary>True iff this policy runs the body synchronously on the calling thread before
        /// <see cref="Schedule{T}"/> returns (the WebGL/Inline policy) — false for a policy that dispatches
        /// elsewhere. <b>A decorator over another <see cref="IWorkScheduler"/> MUST forward its inner
        /// scheduler's answer</b> rather than hard-coding one:
        /// <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.WorkScheduler"/>'s and
        /// <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.MeshBuildGateForTest"/>'s mutual-exclusion
        /// guards read this property (not a concrete-type check) precisely so a wrapped Inline scheduler —
        /// e.g. a test spy — still trips the guard, since the deadlock it prevents depends on where the body
        /// actually runs, not on the outermost type.</summary>
        bool RunsInline { get; }
    }
}
