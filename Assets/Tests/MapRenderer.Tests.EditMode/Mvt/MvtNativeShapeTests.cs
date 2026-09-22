// Mvt/MvtNativeShapeTests.cs — the two MVT fixtures that alias Is = UnityEngine.TestTools.Constraints.Is (EditMode).
//
// Split from the other four Mvt files by the using-collision rule, not by size: both here alias
// Is to reach Is.Not.AllocatingGCMemory(); Mvt/MvtDecodeTests.cs's four files use bare NUnit Is
// and must never share a file with this alias.
//
// Contents:
//   DensePropertyStoreTests  — DensePropertyStore's zero-allocation teeth via the Recorder-based Is.Not.AllocatingGCMemory().
//   MvtValueCompactionTests  — MvtLayer.Values shrank from a GC-heap List<MvtValue> to a blittable NativeArray<MvtValueNative>.

using System.Collections.Generic;
using System.IO;
using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;


namespace MapRenderer.Tests.Mvt
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // DensePropertyStoreTests — DensePropertyStore's zero-allocation teeth via Is.Not.AllocatingGCMemory()
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// D1a — dense MVT property storage (<see cref="DensePropertyStore"/>), the GC-eliminating alternative
    /// to an eager per-feature dictionary and the sole production property store. Two things this stage
    /// must prove:
    /// <list type="bullet">
    ///   <item><b>Equivalence</b> — <see cref="DensePropertyStore.TryGet"/> (the backward tag-pair scan
    ///     <see cref="IFeature.TryGetProperty"/> hits) agrees with
    ///     <see cref="MvtLayerPropertyResolver.ResolveToDictionary"/> (the forward walk
    ///     <see cref="IFeature.Properties"/> resolves through), for every layer, every feature, every
    ///     declared key, plus one key guaranteed absent. Two independent implementations of the same
    ///     resolve — not a comparison against a second store.</item>
    ///   <item><b>Zero allocation</b> — the hot single-key <c>TryGetProperty</c> path allocates nothing,
    ///     for both a present and an absent key.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class DensePropertyStoreTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"sample-tile.bytes not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        // ── Equivalence ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The discriminating oracle: for every layer, every feature, every key the layer declares (plus
        /// one key guaranteed absent from every feature), <see cref="IFeature.TryGetProperty"/> (backward
        /// tag-pair scan, <see cref="DensePropertyStore.TryGetByKeyIndex"/>) must agree with
        /// <see cref="IFeature.Properties"/> (forward walk, <see cref="MvtLayerPropertyResolver.ResolveToDictionary"/>)
        /// on both the presence bool and the resolved <see cref="Value"/> — two independent
        /// implementations of the same resolve. An index-math bug in either (wrong keyIdx/valIdx, wrong
        /// pair picked on a duplicate-key feature, off-by-one) fails this test; a store that always returns
        /// <c>false</c> is caught by the per-key presence assertion, not just a value comparison.
        /// RED-verified by swapping the keyIdx/valIdx read order inside
        /// <see cref="DensePropertyStore.TryGetByKeyIndex"/>: every presence assertion for every real
        /// property failed (all reads land on the wrong slot) — restored before committing.
        /// </summary>
        [Test]
        public void TryGetProperty_AgreesWithResolveToDictionary_ForEveryKeyAndEveryFeature_AcrossAllLayers()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture()));

            int comparisons = 0;
            foreach (MvtLayer layer in tile.Layers)
            {
                // Every key this layer declares, plus one name guaranteed absent from every feature.
                var namesToCheck = new List<string>(layer.Keys) { "NoSuchKeyXYZ123" };

                for (int fi = 0; fi < layer.Features.Count; fi++)
                {
                    IFeature feature = layer.Features[fi];
                    // The forward oracle: MvtLayerPropertyResolver.ResolveToDictionary via AsDictionary(),
                    // independent of the backward TryGet scan under test below.
                    IReadOnlyDictionary<string, Value> resolved = feature.Properties;

                    foreach (string name in namesToCheck)
                    {
                        bool resolvedFound = resolved.TryGetValue(name, out Value resolvedValue);
                        bool tryGetFound = feature.TryGetProperty(name, out Value tryGetValue);
                        comparisons++;

                        Assert.That(tryGetFound, Is.EqualTo(resolvedFound),
                            $"layer '{layer.Name}' feature[{fi}] key '{name}': " +
                            "TryGetProperty presence must agree with ResolveToDictionary");
                        if (resolvedFound)
                            Assert.That(tryGetValue, Is.EqualTo(resolvedValue),
                                $"layer '{layer.Name}' feature[{fi}] key '{name}': " +
                                "TryGetProperty value must agree with ResolveToDictionary");
                    }
                }
            }

            Assert.That(comparisons, Is.GreaterThan(1000),
                "precondition: the fixture must exercise many (layer, feature, key) combinations — " +
                "too few and a shallow/broken TryGet implementation could pass this test vacuously");
        }

        // ── Zero allocation ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TryGetProperty_ExistingKey_OnDenseStore_AllocatesNoGCMemory()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            IFeature feature = layer.Features[0]; // every countries feature has NAME (has NAME == 239)

            // Warm the EXACT delegate the constraint invokes (not just the method), so its compiled body is
            // JIT'd before measurement — the mitigation for Is.Not.AllocatingGCMemory's one-shot-lambda
            // false-positive. A real per-call allocation would still be caught (the Recorder sees every
            // GC.Alloc); this only removes the one-time JIT of the measured wrapper from the window.
            TestDelegate act = () => feature.TryGetProperty("NAME", out Value _);
            for (int w = 0; w < 50; w++) act();
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "Dense TryGetProperty on an existing key must not allocate: name→keyIndex is a Dictionary " +
                "lookup (int index, no boxing), the per-feature scan walks an already-decoded uint[], and " +
                "Value is a readonly struct.");
        }

        [Test]
        public void TryGetProperty_MissingKey_OnDenseStore_AllocatesNoGCMemory()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            IFeature feature = layer.Features[0];

            TestDelegate act = () => feature.TryGetProperty("NoSuchKeyXYZ123", out Value _);
            for (int w = 0; w < 50; w++) act(); // warm the exact measured delegate (JIT its body)
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "the missing-key path (hit constantly by !has filters) must also not allocate: a failed " +
                "name→keyIndex lookup returns false without ever touching the per-feature tag array.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtValueCompactionTests — MvtLayer.Values: List<MvtValue> to blittable NativeArray<MvtValueNative>
    // ───────────────────────────────────────────────────────────────────────────────────

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
            var tagOffsets = new NativeArray<int>(new[] { 0 }, Allocator.Persistent); // one feature, ordinal 0
            var tagLengths = new NativeArray<int>(new[] { 6 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var store = new DensePropertyStore(resolver, 0);

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
                tagOffsets.Dispose();
                tagLengths.Dispose();
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
            var tagOffsets = new NativeArray<int>(new[] { 0 }, Allocator.Persistent); // one feature, ordinal 0
            var tagLengths = new NativeArray<int>(new[] { 2 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var store = new DensePropertyStore(resolver, 0);

                // Anti-vacuity: prove the read succeeds WHILE values is alive.
                bool foundWhileAlive = store.TryGetByKeyIndex(0, out Value _);
                Assert.That(foundWhileAlive, Is.True, "precondition: the read must succeed before disposal");

                values.Dispose(); // dispose ONLY the values array — tagWords/columns stay live

                Assert.Throws<ObjectDisposedException>(() => store.TryGetByKeyIndex(0, out Value _),
                    "reading a value after the Values array is disposed must throw ObjectDisposedException — " +
                    "a disposed NativeArray read under collections safety checks, isolated from tagWords.");
            }
            finally
            {
                if (values.IsCreated) values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }
    }
}
