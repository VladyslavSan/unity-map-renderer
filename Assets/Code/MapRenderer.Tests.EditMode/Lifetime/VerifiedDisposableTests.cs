// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
//
// Tests VerifiedDisposable: the shared IDisposable base every concrete disposable class in this codebase
// (TileScheduler, PreparedTileCache, RenderLayerSet, TileManager, the 3 TileRenderer backends) derives from.
// Covers: (a) DoDispose runs exactly once across repeated Dispose() calls (idempotent); (b) ThrowIfDisposed
// throws ObjectDisposedException after Dispose and not before; (c) LeakReporter is settable/invokable.
// Deliberately does NOT try to force the finalizer via GC — that is non-deterministic and would be flaky.

using System;
using NUnit.Framework;
using MapRenderer.Core.Lifetime;

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
}
