using System;

namespace MapRenderer.Core.Lifetime
{
    /// <summary>
    /// Template-method <see cref="IDisposable"/> base: one idempotency guard, one
    /// <see cref="ThrowIfDisposed"/>, and a (DEBUG/Editor-only) leaked-without-Dispose finalizer warning —
    /// avoiding the <c>_disposed</c> field / guard / <see cref="GC.SuppressFinalize"/> boilerplate a
    /// hand-rolled concrete disposable class would otherwise duplicate.
    ///
    /// <para>Derived types implement <see cref="DoDispose"/> instead of <c>Dispose()</c> — the base's
    /// <see cref="Dispose"/> is sealed (non-virtual) and guarantees <see cref="DoDispose"/> runs AT MOST ONCE,
    /// even under repeated/concurrent <see cref="Dispose"/> calls (single-threaded idempotency; this class does
    /// not add its own locking — a derived type that needs disposal to be thread-safe still owns that).</para>
    ///
    /// <para>Engine-free (lives in <c>MapRenderer.Core</c>, <c>System.*</c> only) so both Core classes
    /// (e.g. <c>TileScheduler</c>) and Unity classes can derive from it without Core taking a
    /// <c>MapRenderer.Unity</c>/<c>UnityEngine</c> dependency.</para>
    /// </summary>
    public abstract class VerifiedDisposable : IDisposable
    {
        /// <summary>
        /// Invoked by the (DEBUG/Editor-only) finalizer when an instance is garbage-collected without ever
        /// having been <see cref="Dispose"/>d — a resource leak. Core's default is a no-op (engine-free);
        /// the Unity layer wires this to <c>UnityEngine.Debug.LogError</c> once at startup so a leak surfaces
        /// in the Editor/Player log.
        /// </summary>
        public static Action<string> LeakReporter = static _ => { };

        /// <summary>True once <see cref="Dispose"/> has run (or is running). Public — callers/tests probe it
        /// directly (BCL idiom), matching every hand-rolled <c>IsDisposed</c> accessor this base replaces.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>Idempotent — <see cref="DoDispose"/> runs at most once no matter how many times this is called.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            DoDispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>Throws <see cref="ObjectDisposedException"/> if this instance has already been disposed.</summary>
        protected void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(GetType().Name);
        }

        /// <summary>Derived-type teardown logic. Called exactly once, by <see cref="Dispose"/>.</summary>
        protected abstract void DoDispose();

#if DEBUG || UNITY_EDITOR
        ~VerifiedDisposable()
        {
            if (!IsDisposed) LeakReporter($"{GetType().Name} was finalized without Dispose() — resource leak.");
        }
#endif
    }
}
