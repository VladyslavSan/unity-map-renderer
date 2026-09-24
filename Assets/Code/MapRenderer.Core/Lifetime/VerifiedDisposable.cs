using System;

namespace MapRenderer.Core.Lifetime
{
    /// <summary>
    /// Template-method <see cref="IDisposable"/> base: one idempotency guard, one
    /// <see cref="ThrowIfDisposed"/>, and a DEBUG/Editor-only leaked-without-Dispose finalizer warning.
    /// Derived types implement <see cref="DoDispose"/>; the non-virtual <see cref="Dispose"/> runs it at most
    /// once. The class adds no locking, so a derived type that needs thread-safe disposal owns that itself.
    /// Engine-free, so both Core and Unity classes derive from it.
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
