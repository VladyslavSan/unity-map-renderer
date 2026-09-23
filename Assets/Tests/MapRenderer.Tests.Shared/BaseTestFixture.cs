using NUnit.Framework;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Root of the test fixture hierarchy: a per-test <c>[SetUp]</c>/<c>[TearDown]</c> pair that calls the
    /// overridable <see cref="OnSetUp"/>/<see cref="OnTearDown"/> hooks, plus a per-test
    /// <see cref="ObjectDisposalBag"/> every fixture reaches through <see cref="Track{T}"/>. This file
    /// compiles for EditMode AND PlayMode, so it must never reference <c>UnityEditor</c>.
    ///
    /// <para>Non-local invariant: the hooks are protected (NUnit never invokes a PRIVATE attributed method
    /// on an abstract base — measured on standalone NUnit 3.14 and Unity's vendored 3.5.0.0) and
    /// non-virtual, so a subclass cannot shadow or skip them; the bag destroys its
    /// objects AFTER <see cref="OnTearDown"/> runs, so a test that needs a narrower lifetime declares its
    /// own <c>ObjectDisposalBag</c> in its own brace block.</para>
    /// </summary>
    public abstract class BaseTestFixture
    {
        private readonly ObjectDisposalBag _bag = new();

        [SetUp]
        protected void DoSetUp() => OnSetUp();

        [TearDown]
        protected void DoTearDown()
        {
            try { OnTearDown(); }
            finally { _bag.Dispose(); }
        }

        /// <summary>Tracks an object this test CONSTRUCTED and returns it unchanged, so the call composes
        /// at the construction site: <c>var mat = Track(new Material(shader));</c>. Destroyed after
        /// <see cref="OnTearDown"/> runs, in reverse-of-construction order — see <see cref="ObjectDisposalBag"/>.
        /// Never track what the test LOADED (<c>AssetDatabase.LoadAssetAtPath</c>, <c>Resources.Load</c>).</summary>
        protected T Track<T>(T obj) where T : UnityEngine.Object => _bag.Track(obj);

        /// <summary>Override to acquire per-test state. A derived override must call
        /// <c>base.OnSetUp()</c> FIRST, so an outer layer's state is in place before an inner layer
        /// builds on it.</summary>
        protected virtual void OnSetUp() { }

        /// <summary>Override to release state <see cref="OnSetUp"/> acquired. NUnit runs
        /// <c>[TearDown]</c> even when <c>[SetUp]</c> throws, so a derived override does its OWN
        /// cleanup first, then calls <c>base.OnTearDown()</c> LAST inside a <c>finally</c>.</summary>
        protected virtual void OnTearDown() { }
    }
}
