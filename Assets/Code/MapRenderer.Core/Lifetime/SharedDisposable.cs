using System;
using System.Diagnostics;
using System.Threading;

namespace MapRenderer.Core.Lifetime
{
    /// <summary>
    /// A reference count over a single <typeparamref name="T"/> shared by several independent owners: the
    /// value is disposed EXACTLY ONCE, by the last <see cref="Release"/>. The creator's reference is born with
    /// the wrapper (<c>_refs == 1</c>), so the value is never ownerless; every further owner takes one with
    /// <see cref="Acquire"/> and drops it with <see cref="Release"/>.
    ///
    /// <para><b>The reference you hold is the guarantee.</b> While you hold a reference the count is ≥ 1, so
    /// the value is alive — that is why <see cref="Value"/> hands it back with no check and no lock. The only
    /// synchronised state is the counter itself (<see cref="Acquire"/>/<see cref="Release"/> run from different
    /// threads — the decode pool thread mints it, main-thread consumers and a job <c>finally</c> drop it), and
    /// it is kept atomic with <see cref="Interlocked"/>, not a lock.</para>
    ///
    /// <para><b>No per-acquire token.</b> Each reference must reach EXACTLY ONE <see cref="Release"/>: dispose
    /// fires on the single transition to zero, so an extra release is inert in release builds, while an
    /// <see cref="Acquire"/> after the count has hit zero resurrects a freed value. Both are contract
    /// violations, caught by <see cref="Debug"/> assertions in DEBUG/Editor builds and prevented in production
    /// by a single-release-site discipline. Deliberately NOT <see cref="IDisposable"/>: the operation is
    /// <see cref="Release"/>, so <c>using</c> can not single-owner-free a shared value. Sibling of
    /// <see cref="VerifiedDisposable"/> — a refcount instead of a one-shot bool — sharing its
    /// <see cref="VerifiedDisposable.LeakReporter"/> channel.</para>
    /// </summary>
    /// <typeparam name="T">The shared, disposable value.</typeparam>
    public sealed class SharedDisposable<T> where T : class, IDisposable
    {
        private readonly T _value;     // set once; the last Release disposes it, never nulls it
        private int _refs = 1;         // the creator's reference — born with the wrapper

        private static long _liveCount;
        private static long _negativeObservations;

        /// <summary>Net live <see cref="SharedDisposable{T}"/> instances for this closed <typeparamref name="T"/>
        /// — incremented at construction, decremented only after <see cref="Release"/> disposes the value (a
        /// throwing <c>Dispose</c> leaves the count elevated: it reads disposed, not merely released). The
        /// finalizer never decrements it — adding one for symmetry would silently disarm every delta tooth
        /// over this counter, with no test going RED. Test-only (Core grants InternalsVisibleTo).</summary>
        internal static long DebugLiveCount => Interlocked.Read(ref _liveCount);

        /// <summary>Non-zero iff <see cref="Release"/>'s decrement ever took <see cref="DebugLiveCount"/> below
        /// zero — a "back to baseline" reading is only honest when this is also zero, since a negative
        /// decrement can wrap back through zero on a later leak and read as clean.</summary>
        internal static long DebugNegativeObservations => Interlocked.Read(ref _negativeObservations);

        /// <param name="value">The value this wrapper shares and disposes at the last release. Never null.</param>
        public SharedDisposable(T value)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
            Interlocked.Increment(ref _liveCount); // after the null check — a throwing ctor doesn't count
        }

        /// <summary>The shared value. <b>BORROWED</b> — never dispose it, and never retain it past your own
        /// <see cref="Release"/>; the last release disposes it. A read after the last release is a
        /// use-after-free; a DEBUG/Editor <see cref="Debug"/> assertion catches it, but the release build
        /// leaves the read a bare field access (the reference you hold is what keeps the value alive) — the
        /// same DEBUG-only posture as the <see cref="Acquire"/>/<see cref="Release"/> contract checks.</summary>
        public T Value
        {
            get
            {
                Debug.Assert(Volatile.Read(ref _refs) > 0,
                    "Value read after the last Release() — use-after-free; the wrapped value's resources are already freed. Read only while you hold a reference.");
                return _value;
            }
        }

        /// <summary>Takes one additional reference. Call only while you already hold one — acquiring after the
        /// last release resurrects a freed value.</summary>
        public void Acquire()
        {
            int now = Interlocked.Increment(ref _refs);
            // A legitimate acquire raises a count that was ≥ 1, so `now` is ≥ 2; `now <= 1` means we came from
            // zero (or below) and just resurrected a value the last Release already freed.
            Debug.Assert(now > 1, "Acquire() after the last Release() — resurrecting a freed value. Acquire only while you hold a reference.");
        }

        /// <summary>Drops one reference; the one that brings the count to zero disposes the value, exactly once.
        /// A release past zero is unbalanced — inert in release builds, caught by a DEBUG/Editor assertion.</summary>
        public void Release()
        {
            int remaining = Interlocked.Decrement(ref _refs);
            if (remaining == 0)
            {
                _value.Dispose();
                long after = Interlocked.Decrement(ref _liveCount);
                if (after < 0) Interlocked.Increment(ref _negativeObservations);
                GC.SuppressFinalize(this);   // drained cleanly — no leak to report
            }
            else
            {
                Debug.Assert(remaining > 0, "Release() with no live reference — released more times than acquired.");
            }
        }

#if DEBUG || UNITY_EDITOR
        /// <summary>DEBUG/Editor-only: a wrapper finalized while it still holds live references never reached
        /// its last <see cref="Release"/> — the value's resources leaked. Reports via
        /// <see cref="VerifiedDisposable.LeakReporter"/> (the Unity layer wires it to the Editor/Player log);
        /// never frees anything, since a finalizer does not run on demand. A constructor that threw leaves
        /// <c>_value</c> null, so it is not mistaken for a leak.</summary>
        ~SharedDisposable()
        {
            if (_refs > 0 && _value != null)
                VerifiedDisposable.LeakReporter(
                    $"{nameof(SharedDisposable<T>)}<{typeof(T).Name}> finalized with {_refs} live reference(s) — " +
                    "the value's resources leaked (a Release() was missed).");
        }
#endif
    }
}
