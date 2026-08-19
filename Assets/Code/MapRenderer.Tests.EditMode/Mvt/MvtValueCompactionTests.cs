// Unity EditMode only. T3's zero-alloc meter is UnityEngine.TestTools' Is.Not.AllocatingGCMemory() (the
// Recorder-based meter — GC.GetAllocatedBytesForCurrentThread() is DEAD in this runner, see
// DensePropertyStoreTests). T2's size probe uses GC.GetTotalMemory with the same calibration-canary
// technique as DecodeGeometryFlattenAllocTests, since Marshal.SizeOf/UnsafeUtility.SizeOf are illegal on a
// struct holding a managed string reference. NOT registered in core-tests.csproj (MvtValue/MvtModels are
// not compiled there — see core-tests.csproj's tile-decode seam).
//
// Stage C (value-table reduction): MvtLayer.Values shrank from List<Value> (72 B/entry) to
// List<MvtValue> (24 B/entry) — a narrower type carrying only the MVT wire value space (string/number/
// bool/null), reconstituted to the shared expression Value at the read boundary via MvtValue.ToValue().
// See docs/valuetable-plan.md (devloop) for the design; Value.cs/Color.cs are untouched (fence).

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Mvt
{
    [TestFixture]
    public class MvtValueCompactionTests
    {
        // ── T1 — field shape (structural) ──────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="MvtValue"/> must carry no <see cref="Color"/> field (the whole point of giving the
        /// value table its own type instead of reusing <see cref="Value"/>), and its instance field set
        /// must be exactly the compact layout: the <see cref="ValueType"/> tag, a <c>double</c> and a
        /// <c>string</c>. Also pins that <see cref="MvtLayer.Values"/> now holds <see cref="MvtValue"/>,
        /// not <see cref="Value"/> — the actual shrink this stage delivers.
        /// </summary>
        /// <remarks>
        /// RED-verify (two independent injections, both restored before commit):
        /// 1. Add a <c>Color</c> field to <see cref="MvtValue"/> — the "no Color field" assertion fails
        ///    (and T2's size ratio also reds, since a Color field bloats the struct — expected collateral).
        /// 2. Revert <see cref="MvtLayer.Values"/>'s declared type to <c>List&lt;Value&gt;</c>: the element
        ///    type is coupled across DecodeValue / the resolver / both stores (and this fixture's own T3
        ///    setup passes a <c>List&lt;MvtValue&gt;</c> into the resolver ctor), so a faithful revert
        ///    cascades to a COMPILE ERROR rather than a runtime element-type assertion failure — a stronger
        ///    signal, but not the runtime failure literally named. The element-type assertion is therefore
        ///    belt-and-suspenders over the compile-time coupling; T1's live teeth are the no-Color and
        ///    exact-field-set checks (injection 1).
        /// </remarks>
        [Test]
        public void MvtValue_HasCompactFieldShape_AndBacksTheLayerValueTable()
        {
            FieldInfo[] instanceFields = typeof(MvtValue).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(instanceFields.Any(f => f.FieldType == typeof(Color)), Is.False,
                "MvtValue must carry no Color field — that's the reason it exists instead of reusing Value.");

            Type[] actualFieldTypes = instanceFields.Select(f => f.FieldType)
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            Type[] expectedFieldTypes = new[] { typeof(MapRenderer.Core.Expressions.ValueType), typeof(double), typeof(string) }
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expectedFieldTypes, actualFieldTypes,
                "MvtValue's instance fields must be exactly {ValueType, double, string} — no more, no less.");

            FieldInfo valuesField = typeof(MvtLayer).GetField(nameof(MvtLayer.Values));
            Assert.That(valuesField, Is.Not.Null, "precondition: MvtLayer.Values must exist");
            Type elementType = valuesField.FieldType.GetGenericArguments()[0];
            Assert.That(elementType, Is.EqualTo(typeof(MvtValue)),
                "MvtLayer.Values must be a List<MvtValue>, not List<Value> — the field this stage shrinks.");
        }

        // ── T2 — size probe (magnitude) ────────────────────────────────────────────────────────

        // GC.GetTotalMemory's own noise floor (brief: only trustworthy at >= ~100 KB/op) — the calibration
        // canary must clear this by a wide margin to prove the meter is alive in this run.
        private const long CalibrationFloor = 100_000;

        /// <summary>Proves GC.GetTotalMemory is a LIVE meter in this run before the size probe below
        /// trusts it — the same calibration technique as DecodeGeometryFlattenAllocTests.</summary>
        [Test]
        public void Calibration_GetTotalMemory_ReadsALiveAllocation()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(false);
            byte[] block = new byte[8 << 20];
            block[0] = 1; // defeat dead-store elimination
            long after = GC.GetTotalMemory(false);

            Assert.Greater(after - before, CalibrationFloor,
                "GC.GetTotalMemory must read a live 8 MiB allocation well clear of its own noise floor, or " +
                "the meter is dead in this run and the probe below cannot be trusted.");
            GC.KeepAlive(block);
        }

        /// <summary>
        /// MvtValue (24 B) vs Value (72 B), measured as array-allocation bytes rather than
        /// Marshal.SizeOf/UnsafeUtility.SizeOf — both are illegal on a struct holding a managed <c>string</c>
        /// reference. At 200,000 elements the two arrays land at ≈4.8 MB and ≈14.4 MB respectively, both
        /// clear of GC.GetTotalMemory's ~100 KB noise floor by orders of magnitude, so the ~9.6 MB delta is
        /// a robust discriminator.
        /// </summary>
        /// <remarks>RED-verify: bloat MvtValue with filler fields matching Value's layout (bool + Color +
        /// two reference fields) so its measured size regresses toward Value's — the ratio assertion fails.
        /// Restored before commit.</remarks>
        [Test]
        public void MvtValueArray_IsUnderHalfTheSizeOf_ValueArray()
        {
            const int count = 200_000;

            long mvtValueBytes = MeasureArrayAllocationBytes(() => new MvtValue[count]);
            long valueBytes = MeasureArrayAllocationBytes(() => new Value[count]);

            TestContext.WriteLine($"MEASURE MvtValue[{count}]={mvtValueBytes} B, Value[{count}]={valueBytes} B");

            Assert.That(mvtValueBytes, Is.LessThan(valueBytes * 0.5),
                $"MvtValue[{count}] ({mvtValueBytes} B) must be under half the size of Value[{count}] " +
                $"({valueBytes} B) — the 72→24 B/entry shrink this stage delivers.");
        }

        private static long MeasureArrayAllocationBytes(Func<Array> allocate)
        {
            // Warm-up: JIT the delegate before the measured allocation.
            for (int w = 0; w < 3; w++) GC.KeepAlive(allocate());

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);

            Array array = allocate();

            long after = GC.GetTotalMemory(false);
            int collectionsAfter = GC.CollectionCount(0);
            GC.KeepAlive(array);

            Assert.AreEqual(collectionsBefore, collectionsAfter,
                "a Gen0 collection fired inside the measurement window — the byte delta is unreliable here; " +
                "this indicates a flaky run, not a probe result.");

            return after - before;
        }

        // ── T3 — DensePropertyStore.TryGet stays zero-alloc through ToValue() ─────────────────────

        /// <summary>
        /// Guards the exact code this stage adds: <see cref="DensePropertyStore.TryGet"/> now calls
        /// <see cref="MvtValue.ToValue"/> on every hit, and that reconstitution must stay alloc-free for
        /// every variant the wire produces (string, number, bool). Builds the resolver/store directly
        /// (internal types, visible to this assembly) rather than through the decoder, so all three
        /// variants are covered in one feature without a protobuf-encoding round-trip.
        /// </summary>
        /// <remarks>RED-verify: inject a boxing conversion into DensePropertyStore.TryGet before the
        /// <c>ToValue()</c> call — but it must ESCAPE, or the JIT dead-store-eliminates it and the box never
        /// happens (an unused <c>object _ = values[valIdx];</c> silently no-ops and the tooth stays GREEN — a
        /// false pass). Force it live: <c>object _box = values[valIdx]; GC.KeepAlive(_box);</c>. Only then
        /// does the constraint fail — and it fails T3 alone (3/4), confirming isolation. Restored before
        /// commit.</remarks>
        [Test]
        public void DensePropertyStore_TryGet_ThroughToValue_AllocatesNoGCMemory_ForStringNumberAndBool()
        {
            var keys = new List<string> { "s", "n", "b" };
            var values = new List<MvtValue> { MvtValue.String("hello"), MvtValue.Number(42.0), MvtValue.Bool(true) };
            var keyIndex = new Dictionary<string, int> { ["s"] = 0, ["n"] = 1, ["b"] = 2 };
            var resolver = new MvtLayerPropertyResolver(keys, values, keyIndex);
            var rawTags = new uint[] { 0, 0, 1, 1, 2, 2 }; // pairs (keyIdx, valIdx), one per key
            var store = new DensePropertyStore(rawTags, resolver);

            TestDelegate act = () =>
            {
                store.TryGet("s", out Value _);
                store.TryGet("n", out Value _);
                store.TryGet("b", out Value _);
            };
            for (int w = 0; w < 50; w++) act(); // warm the exact measured delegate (JIT its body)

            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "DensePropertyStore.TryGet must not allocate when reconstituting MvtValue.ToValue() for " +
                "string, number or bool — all three are struct-field copies.");
        }
    }
}
