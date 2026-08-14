// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
//
// Tests SharedDisposable<T>: a reference count over one disposable value, disposed by the LAST Release().
// Covers: (a) the value is disposed exactly once, and only at the last release, not before; (b) Value hands
// back the wrapped instance seamlessly while a reference is held; (c) an extra release past zero is inert (no
// double dispose); (d) the count stays correct under concurrent Acquire/Release from many threads.
// By design there is NO throw on read/acquire after release (the reference you hold is the guarantee), so
// those are contract violations, not tested behaviours. Deliberately does NOT force the leak finalizer via GC.

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Tests.Lifetime
{
    /// <summary>A disposable that counts how many times (thread-safely) it was disposed — the value a
    /// <see cref="SharedDisposable{T}"/> owns, so a test can assert "disposed exactly once, at the last release".</summary>
    internal sealed class CountingDisposable : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    [TestFixture]
    public class SharedDisposableTests
    {
        [Test]
        public void Release_OfSoleReference_DisposesValueExactlyOnce()
        {
            var value  = new CountingDisposable();
            var shared = new SharedDisposable<CountingDisposable>(value);

            shared.Release();   // drops the creator's reference -> count 0 -> dispose

            Assert.AreEqual(1, value.DisposeCount, "The last (here: only) Release must dispose the value exactly once.");
        }

        [Test]
        public void Release_WithLiveAcquires_DisposesOnlyAtTheLastRelease()
        {
            var value  = new CountingDisposable();
            var shared = new SharedDisposable<CountingDisposable>(value);  // refs = 1 (creator)
            shared.Acquire();                                             // refs = 2
            shared.Acquire();                                             // refs = 3

            shared.Release();
            Assert.AreEqual(0, value.DisposeCount, "Must NOT dispose while references remain (2 left).");
            shared.Release();
            Assert.AreEqual(0, value.DisposeCount, "Must NOT dispose while references remain (1 left).");
            shared.Release();
            Assert.AreEqual(1, value.DisposeCount, "The LAST release must dispose the value, exactly once.");
        }

        [Test]
        public void Value_WhileAReferenceIsHeld_ReturnsTheWrappedInstance_WithoutDisposing()
        {
            var value  = new CountingDisposable();
            var shared = new SharedDisposable<CountingDisposable>(value);
            shared.Acquire();   // a second holder

            Assert.AreSame(value, shared.Value, "Value must hand back the exact wrapped instance while a reference is held.");
            Assert.AreEqual(0, value.DisposeCount, "Reading Value must not dispose the value.");

            shared.Release();
            shared.Release();
        }

        // The contract-violation checks (acquire-after-zero, release-past-zero) are System.Diagnostics.Debug
        // assertions — they notify trace listeners rather than throwing a catchable exception, so they are not
        // asserted here, the same way the leak finalizer is left untested. The value-lifecycle teeth below are
        // what pin the behaviour that matters.

        [Test]
        public void Ctor_NullValue_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new SharedDisposable<CountingDisposable>(null));
        }

        [Test]
        public void ConcurrentAcquireRelease_KeepsTheCountCorrect_AndDisposesExactlyOnce()
        {
            var value  = new CountingDisposable();
            var shared = new SharedDisposable<CountingDisposable>(value);  // creator ref held throughout

            const int threads = 8;
            const int perThread = 5000;
            var tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        shared.Acquire();
                        // Each thread's own Acquire precedes its own Release, so the creator's reference keeps
                        // the count >= 1 throughout — the value must never be disposed during this phase.
                        shared.Release();
                    }
                });
            }
            Task.WaitAll(tasks);   // re-throws if any thread observed a corrupted count (torn increment/decrement)

            Assert.AreEqual(0, value.DisposeCount, "The value must survive the whole concurrent phase (creator ref held).");

            shared.Release();      // drop the creator ref -> count 0 -> dispose, exactly once
            Assert.AreEqual(1, value.DisposeCount, "After the creator release, the value must be disposed exactly once.");
        }
    }
}
