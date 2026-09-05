namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Execution policy for off-main CPU work that is (today) a plain C# closure. ThreadPool
    /// reproduces today's UniTask.RunOnThreadPool parallelism (desktop/editor); Inline runs the body
    /// synchronously on the calling thread — the WebGL fix (docs/web-target.md), since nothing is
    /// dispatched to a worker that will never run it.
    ///
    /// <para><b>A bridge for the managed bodies that remain — not a design to converge on, but not going
    /// away soon either.</b> <see cref="Schedule{T}"/> takes a managed
    /// <see cref="System.Func{T,TResult}"/> closure, exactly the shape a native/Burst rewrite eliminates, so
    /// a body that can be nativized should be, and routing a new site through here is the fallback rather
    /// than the default move.</para>
    ///
    /// <para><b>Corrected 2026-09-04, twice over.</b> This paragraph used to name the destination as a Burst
    /// job "dispatched <c>.Run()</c>". That is wrong for the tile path: the job system schedules from the
    /// main thread only, so the destination shape is a <b>scheduled graph node</b>
    /// (<c>docs/job-scheduling-design.md</c> §3.2 and §4 rule 1), not a synchronous run. It also claimed
    /// this interface "does not survive jobification". It survives as long as any managed body does, and the
    /// remaining ones are not close to gone: tile-layer selection still runs a per-feature evaluator loop,
    /// paint bake needs a native expression VM that does not exist and is its own project, and the symbol
    /// pass rides the kick regardless. Treat this as the dispatch policy for residual managed bodies, with
    /// one platform switch, and stop scheduling its deletion.</para></summary>
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
