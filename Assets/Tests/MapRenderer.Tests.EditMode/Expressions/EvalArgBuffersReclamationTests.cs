// EditMode only: reaches the internal EvalArgBuffers via Core's InternalsVisibleTo("MapRenderer.Tests.EditMode").
// NOT registered in core-tests.csproj (that runner's assembly is not granted Core internals access).

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// H4 — a pooled argument buffer must not root the reference-typed <see cref="Value"/>s it last held (a
    /// <see cref="Value"/> can carry a string / list / dictionary). Without clearing on return, the buffer —
    /// and especially its oversized tail (slots past the last request's arity, hidden by the caller's
    /// <c>AsSpan(0, count)</c>) — would retain a feature-owned graph for the worker thread's lifetime, quietly
    /// recreating the GC pressure this stage removes. Both review arms flagged it.
    /// </summary>
    [TestFixture]
    public class EvalArgBuffersReclamationTests
    {
        [Test]
        public void Return_ClearsReferenceTypedSlots_SoThePoolRetainsNoArgument()
        {
            Value[] buffer = EvalArgBuffers.Rent(2);
            buffer[0] = Value.String("must-not-be-retained");
            buffer[1] = Value.Array(new List<Value> { Value.Number(1) });

            EvalArgBuffers.Return(buffer);

            // Return clears in place, so the pool roots neither the string nor the list. RED without the
            // Array.Clear in Return.
            Assert.That(buffer[0].Type, Is.EqualTo(ValueType.Null),
                "Return must clear the string slot so the pool roots no argument string.");
            Assert.That(buffer[1].Type, Is.EqualTo(ValueType.Null),
                "Return must clear the array slot so the pool roots no argument collection.");
        }

        [Test]
        public void Return_ClearsEverySlot_NotJustTheUsedPrefix()
        {
            // Return must clear the whole array, not just a prefix: a later AsSpan(0, N) reuse hides the tail,
            // which would otherwise retain a reference. The test inspects the array it filled.
            Value[] buffer = EvalArgBuffers.Rent(4);
            for (int i = 0; i < buffer.Length; i++) buffer[i] = Value.String("tail-ref-" + i);

            EvalArgBuffers.Return(buffer);

            for (int i = 0; i < buffer.Length; i++)
                Assert.That(buffer[i].Type, Is.EqualTo(ValueType.Null),
                    $"Return must clear slot {i} — the whole array, not only the last-used prefix (the " +
                    "oversized tail is exactly the lifetime-retention hazard).");
        }
    }
}
