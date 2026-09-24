// Lifetime/DisposableContractTests.cs — the two engine-free disposable-contract fixtures (fast lane).
//
// Both compiled verbatim by the Unity EditMode runner and Tools/core-tests; neither references UnityEngine.
//
// Contents:
//   VerifiedDisposableTests  — VerifiedDisposable: idempotent DoDispose, ThrowIfDisposed after disposal.
//   SharedDisposableTests    — SharedDisposable<T>: a refcount over one disposable, disposed by the last Release().

using System;
using NUnit.Framework;
using MapRenderer.Core.Lifetime;
using System.Threading;
using System.Threading.Tasks;


namespace MapRenderer.Tests.Lifetime
{
    /// <summary>Minimal concrete <see cref="VerifiedDisposable"/> used only to exercise the base's contract.</summary>
    internal sealed class TestVerifiedDisposable : VerifiedDisposable
    {
        public int DisposeCount { get; private set; }

        protected override void DoDispose() => DisposeCount++;

        /// <summary>Public wrapper so the test can exercise the protected <see cref="ThrowIfDisposed"/> guard.</summary>
        public void AssertNotDisposed() => ThrowIfDisposed();
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // VerifiedDisposableTests — VerifiedDisposable's shared IDisposable base — idempotent dispose, ThrowIfDisposed
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class VerifiedDisposableTests
    {
        [Test]
        public void Dispose_CalledMultipleTimes_DoDisposeRunsExactlyOnce()
        {
            var d = new TestVerifiedDisposable();

            d.Dispose();
            d.Dispose();
            d.Dispose();

            Assert.AreEqual(1, d.DisposeCount,
                "DoDispose must run exactly once no matter how many times Dispose() is called.");
        }

        [Test]
        public void ThrowIfDisposed_BeforeDispose_DoesNotThrow()
        {
            var d = new TestVerifiedDisposable();
            Assert.DoesNotThrow(() => d.AssertNotDisposed());
        }

        [Test]
        public void ThrowIfDisposed_AfterDispose_ThrowsObjectDisposedException()
        {
            var d = new TestVerifiedDisposable();
            d.Dispose();
            Assert.Throws<ObjectDisposedException>(() => d.AssertNotDisposed());
        }

        [Test]
        public void IsDisposed_ReflectsDisposalState()
        {
            var d = new TestVerifiedDisposable();
            Assert.IsFalse(d.IsDisposed, "Must not be disposed before Dispose() is called.");
            d.Dispose();
            Assert.IsTrue(d.IsDisposed, "Must be disposed after Dispose() is called.");
        }

        [Test]
        public void LeakReporter_IsSettable_AndInvokable()
        {
            Action<string> original = VerifiedDisposable.LeakReporter;
            try
            {
                string captured = null;
                VerifiedDisposable.LeakReporter = msg => captured = msg;

                VerifiedDisposable.LeakReporter("test-leak-message");

                Assert.AreEqual("test-leak-message", captured,
                    "LeakReporter must be swappable and the swapped delegate must be invoked.");
            }
            finally
            {
                // Static hook — restore so this test can't affect any test that runs after it.
                VerifiedDisposable.LeakReporter = original;
            }
        }
    }

    /// <summary>A disposable that counts how many times (thread-safely) it was disposed — the value a
    /// <see cref="SharedDisposable{T}"/> owns, so a test can assert "disposed exactly once, at the last release".</summary>
    internal sealed class CountingDisposable : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SharedDisposableTests — SharedDisposable<T> — a reference count over one disposable, disposed by the last Release()
    // ───────────────────────────────────────────────────────────────────────────────────

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

        // Limitation: acquire-after-zero and release-past-zero are Debug assertions that throw nothing
        // catchable, so like the leak finalizer they are not tested here.

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
