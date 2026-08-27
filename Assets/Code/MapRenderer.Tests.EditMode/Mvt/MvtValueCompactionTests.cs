// Unity EditMode only. NOT registered in core-tests.csproj (MvtValueNative/MvtModels are not compiled there —
// see core-tests.csproj's tile-decode seam). Reaches internal MvtLayer.AdoptValues via InternalsVisibleTo
// ("MapRenderer.Tests.EditMode" from MapRenderer.Jobs).
//
// This stage (value table -> blittable native): MvtLayer.Values shrank from a GC-heap List<MvtValue> (24 B/
// entry, one managed string field) to a blittable NativeArray<MvtValueNative> (16 B/entry, no managed field)
// plus a per-layer managed string[] side table (MvtValueNative.StringId indexes it). See
// native-mvt-storage-stage2-valuetable-plan.md (devloop) for the design; Value.cs/Color.cs are untouched
// (fence).

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Mvt
{
    [TestFixture]
    public class MvtValueCompactionTests
    {
        // ── T1 — blittability (structural) ─────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="MvtValueNative"/> must carry NO managed field (the whole point of the native table:
        /// it lives in a <c>NativeArray</c>, off the GC heap) and must be blittable per
        /// <see cref="UnsafeUtility"/>. Also pins that <see cref="MvtLayer.Values"/>'s element type is
        /// <see cref="MvtValueNative"/> — the actual shape this stage delivers.
        /// </summary>
        /// <remarks>
        /// RED-verify: add a <c>string</c> field to <see cref="MvtValueNative"/> — both the no-managed-field
        /// assertion and the blittability assertion fail. Restored before commit.
        /// </remarks>
        [Test]
        public void MvtValueNative_HasNoManagedField_IsBlittable_AndBacksTheLayerValueTable()
        {
            FieldInfo[] instanceFields = typeof(MvtValueNative).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(instanceFields.Any(f => !f.FieldType.IsValueType), Is.False,
                "MvtValueNative must carry no managed (reference-type) field — that's what makes it fit in " +
                "a NativeArray at all.");

            Assert.That(UnsafeUtility.IsBlittable<MvtValueNative>(), Is.True,
                "MvtValueNative must be blittable — required to live in a NativeArray<MvtValueNative>.");

            PropertyInfo valuesProperty = typeof(MvtLayer).GetProperty(nameof(MvtLayer.Values));
            Assert.That(valuesProperty, Is.Not.Null, "precondition: MvtLayer.Values must exist");
            Assert.That(valuesProperty.PropertyType, Is.EqualTo(typeof(NativeArray<MvtValueNative>)),
                "MvtLayer.Values must be a NativeArray<MvtValueNative> — the field this stage nativizes.");
        }

        // ── T2 — size (magnitude) ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="MvtValueNative"/> must hold its designed 16 B packing (double 8 + <c>ValueType</c> 4 +
        /// int 4, no padding — the deliberate double-first field order the struct documents). This pins the
        /// actual win, not merely "no worse than the 24 B managed <c>MvtValue</c> it replaces". Legal now
        /// that the struct is blittable (<c>UnsafeUtility.SizeOf</c>/<c>Marshal.SizeOf</c> throw on a struct
        /// holding a managed reference, which is why the type this replaces needed a GC-probe instead).
        /// </summary>
        /// <remarks>RED-verify: EITHER add a filler field OR reorder the fields enum-first
        /// (<c>{ValueType, double, int}</c>) — both land the struct at 24 B (a 4 B pad after the lone leading
        /// int-sized field, then the double's 8-byte alignment) and fail the ≤ 16 assertion. The enum-first
        /// case is the one a looser ≤ 24 ceiling would silently pass; this is the tooth that observes the
        /// field-order the struct's own comment calls deliberate. Restored before commit.</remarks>
        [Test]
        public void MvtValueNative_HoldsItsDesigned16BytePacking()
        {
            int size = UnsafeUtility.SizeOf<MvtValueNative>();
            TestContext.WriteLine($"MEASURE sizeof(MvtValueNative)={size} B");

            Assert.That(size, Is.LessThanOrEqualTo(16),
                $"MvtValueNative is {size} B — must hold its designed 16 B packing; an enum-first reorder or " +
                "any added field regresses it to 24 B (see the field-order comment in MvtValueNative).");
        }

        // ── T3 — DensePropertyStore.TryGet stays zero-alloc through ToValue(string[]) ─────────────

        /// <summary>
        /// Guards the exact code this stage adds: <see cref="DensePropertyStore.TryGet"/> now calls
        /// <see cref="MvtValueNative.ToValue"/> on every hit, and that reconstitution must stay alloc-free
        /// for every variant the wire produces (string, number, bool). Builds the resolver/store directly
        /// (internal types, visible to this assembly) rather than through the decoder, so all three variants
        /// are covered in one feature without a protobuf-encoding round-trip.
        /// </summary>
        /// <remarks>RED-verify: inject a boxing conversion into DensePropertyStore.TryGet before the
        /// <c>ToValue(...)</c> call — but it must ESCAPE, or the JIT dead-store-eliminates it and the box
        /// never happens (an unused <c>object _ = values[valIdx];</c> silently no-ops and the tooth stays
        /// GREEN — a false pass). Force it live: <c>object _box = values[valIdx]; GC.KeepAlive(_box);</c>.
        /// Only then does the constraint fail — and it fails T3 alone, confirming isolation. Restored before
        /// commit.</remarks>
        [Test]
        public void DensePropertyStore_TryGet_ThroughToValue_AllocatesNoGCMemory_ForStringNumberAndBool()
        {
            var keys = new List<string> { "s", "n", "b" };
            var valueStrings = new[] { "hello" };
            var values = new NativeArray<MvtValueNative>(
                new[] { MvtValueNative.String(0), MvtValueNative.Number(42.0), MvtValueNative.Bool(true) },
                Allocator.Persistent);
            var keyIndex = new Dictionary<string, int> { ["s"] = 0, ["n"] = 1, ["b"] = 2 };
            var tagWords = new NativeArray<uint>(
                new uint[] { 0, 0, 1, 1, 2, 2 }, Allocator.Persistent); // pairs (keyIdx, valIdx), one per key
            try
            {
                var resolver = new MvtLayerPropertyResolver(keys, values, valueStrings, keyIndex, tagWords);
                var store = new DensePropertyStore(resolver, 0, 6);

                TestDelegate act = () =>
                {
                    store.TryGet("s", out Value _);
                    store.TryGet("n", out Value _);
                    store.TryGet("b", out Value _);
                };
                for (int w = 0; w < 50; w++) act(); // warm the exact measured delegate (JIT its body)

                Assert.That(act, Is.Not.AllocatingGCMemory(),
                    "DensePropertyStore.TryGet must not allocate when reconstituting MvtValueNative.ToValue() " +
                    "for string, number or bool — all three are struct-field copies plus a string-table index.");
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
            }
        }

        // ── NEW — value-array read-after-dispose (white-box UAF) ──────────────────────────────────

        /// <summary>
        /// Isolates the *values* array's own disposed-collections-safety check: a resolver built with a
        /// LIVE <c>tagWords</c> array but a SEPARATELY-disposed <c>Values</c> array. <c>TryGetByKeyIndex</c>
        /// resolves <c>valIdx</c> from the live <c>tagWords</c>, then indexing the disposed <c>values</c>
        /// array must throw. The tile-level read-after-dispose tooth
        /// (<c>NativeTagStorageTests.ReadingProperty_AfterTileDisposed_FailsLoud</c>) cannot observe this: it
        /// disposes the whole layer, and <c>tagWords</c> (read first, inside <c>TryGetByKeyIndex</c>) throws
        /// before <c>values</c> is ever touched — shadowing the values-array check. This tooth constructs the
        /// resolver by hand so it can dispose ONLY <c>values</c>, isolating the check the shadow hides.
        /// </summary>
        /// <remarks>RED-verify: point the resolver at a still-live (undisposed) values array — the
        /// <c>Throws</c> assertion reds (no exception; a value is returned instead). Restored before
        /// commit.</remarks>
        [Test]
        public void TryGetByKeyIndex_AfterValuesArrayDisposed_FailsLoud()
        {
            var keys = new List<string> { "s" };
            var valueStrings = new[] { "hello" };
            var values = new NativeArray<MvtValueNative>(new[] { MvtValueNative.String(0) }, Allocator.Persistent);
            var keyIndex = new Dictionary<string, int> { ["s"] = 0 };
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(keys, values, valueStrings, keyIndex, tagWords);
                var store = new DensePropertyStore(resolver, 0, 2);

                // Anti-vacuity: prove the read succeeds WHILE values is alive.
                bool foundWhileAlive = store.TryGetByKeyIndex(0, out Value _);
                Assert.That(foundWhileAlive, Is.True, "precondition: the read must succeed before disposal");

                values.Dispose(); // dispose ONLY the values array — tagWords stays live

                Assert.Throws<ObjectDisposedException>(() => store.TryGetByKeyIndex(0, out Value _),
                    "reading a value after the Values array is disposed must throw ObjectDisposedException — " +
                    "a disposed NativeArray read under collections safety checks, isolated from tagWords.");
            }
            finally
            {
                if (values.IsCreated) values.Dispose();
                tagWords.Dispose();
            }
        }
    }
}
