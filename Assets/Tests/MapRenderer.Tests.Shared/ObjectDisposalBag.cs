using System.Collections.Generic;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A LIFO bag of <see cref="Object"/>s a test constructed and must destroy. Replaces a hand-written
    /// <c>try</c>/<c>finally</c> that calls <c>DestroyImmediate</c> on each one.
    ///
    /// <para>Track only what the test CONSTRUCTED. A loaded asset (<c>AssetDatabase.LoadAssetAtPath</c>,
    /// <c>Resources.Load</c>) is borrowed: destroying one throws from <see cref="Dispose"/> during unwind,
    /// which replaces the body's own exception and hides the real failure.</para>
    ///
    /// <para>Give each rebuilt lifetime — a loop iteration, or a second scene that must not coexist with
    /// the first — its own bag, disposed before the next starts. One bag for the whole test keeps every
    /// iteration alive at once, and nothing asserts "only one is alive", so that failure is silent.</para>
    /// </summary>
    internal sealed class ObjectDisposalBag : System.IDisposable
    {
        private readonly List<Object> _tracked = new();

        /// <summary>Tracks <paramref name="obj"/> for teardown and returns it unchanged, so tracking sits
        /// on the same line as construction.</summary>
        public T Track<T>(T obj) where T : Object
        {
            _tracked.Add(obj);
            return obj;
        }

        /// <summary>Destroys every tracked object in reverse-of-construction (LIFO) order. The null check
        /// covers both a <c>Track(null)</c> and an object the test body already destroyed — Unity's
        /// overloaded <c>==</c> reports the latter as null.</summary>
        public void Dispose()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
                if (_tracked[i] != null)
                    Object.DestroyImmediate(_tracked[i]);
            _tracked.Clear();
        }
    }
}
