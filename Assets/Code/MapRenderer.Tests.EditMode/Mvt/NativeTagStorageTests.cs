// Unity EditMode only — uses NativeArray / internal MvtLayer.FeatureTagWords (InternalsVisibleTo). NOT
// registered in core-tests.csproj (MvtDecoder pulls Unity.Collections — see core-tests.csproj's tile-decode
// seam, mirrors DecodeGeometryFlattenAllocTests' header).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Mvt
{
    /// <summary>
    /// The per-layer flattened tag-word buffer this stage adds: <see cref="DensePropertyStore"/> drops its
    /// own <c>uint[]</c> for a <c>(offset, count)</c> view into <see cref="MvtLayer.FeatureTagWords"/>, one
    /// shared <c>Allocator.Persistent</c> buffer per layer instead of one managed array per feature.
    /// </summary>
    [TestFixture]
    public class NativeTagStorageTests
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

        // ── Tooth #1c — the discriminator ──────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="DensePropertyStore"/>'s instance field set must be EXACTLY
        /// <c>{MvtLayerPropertyResolver, int, int}</c> — no <c>uint[]</c> (the pre-flatten representation)
        /// and no <c>NativeArray&lt;uint&gt;</c> (the bloated per-feature-handle design the plan rejects:
        /// a 48 B handle per store, in the Editor, erases the whole stage's win). Only an exact field-set
        /// assertion catches the bloated design — a looser "no uint[]" check would pass it.
        /// </summary>
        [Test]
        public void Store_HasExactFieldSet_ResolverAndTwoInts()
        {
            FieldInfo[] instanceFields = typeof(DensePropertyStore).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Type[] actualFieldTypes = instanceFields.Select(f => f.FieldType)
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            Type[] expectedFieldTypes = new[] { typeof(MvtLayerPropertyResolver), typeof(int), typeof(int) }
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expectedFieldTypes, actualFieldTypes,
                "DensePropertyStore's instance fields must be EXACTLY {MvtLayerPropertyResolver, int, int} " +
                "— no uint[] (the pre-flatten representation) and no NativeArray<uint> (a per-store handle, " +
                "which in the Editor costs 48 B and erases the whole stage's win across ~495 stores/tile).");
        }

        // ── Tooth #1a ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every non-empty layer owns exactly one tag-words buffer. "Non-empty" here means
        /// <c>Features.Count &gt; 0</c>, not "has tags" — a layer whose features carry zero tag pairs between
        /// them would still adopt (a zero-length buffer), but this tooth does not separately probe that
        /// boundary; it holds on <c>sample-tile.bytes</c> because every non-empty layer there has at least
        /// one tagged feature.
        /// </summary>
        [Test]
        public void EachLayer_OwnsExactlyOneTagWordsBuffer()
        {
            MvtTile tile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture()));

            Assert.That(tile.Layers.Count, Is.GreaterThan(0), "precondition: the fixture decodes at least one layer");

            bool anyNonEmpty = false;
            foreach (MvtLayer layer in tile.Layers)
            {
                if (layer.Features.Count == 0) continue;
                anyNonEmpty = true;
                Assert.That(layer.FeatureTagWords.IsCreated, Is.True,
                    $"layer '{layer.Name}' has {layer.Features.Count} features but FeatureTagWords is not created.");
            }

            Assert.That(anyNonEmpty, Is.True,
                "precondition: the fixture must decode at least one non-empty layer, or this tooth is vacuous");
        }

        // ── Tooth #4 — ownership/leak ───────────────────────────────────────────────────────────

        /// <summary>Frees the shared tag-words buffer exactly once, and a second <c>Dispose</c> is a no-op
        /// (idempotent write-back), not a throw.</summary>
        [Test]
        public void DecodedTile_FreesTagWords_ExactlyOnce()
        {
            MvtTile tile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture()));
            MvtLayer layer = tile.Layers.First(l => l.Features.Count > 0);
            Assert.That(layer.FeatureTagWords.IsCreated, Is.True, "precondition: the buffer is alive before disposal");

            tile.Dispose();
            Assert.That(layer.FeatureTagWords.IsCreated, Is.False,
                "MvtLayer.Dispose must free FeatureTagWords — IsCreated should now be false.");

            Assert.DoesNotThrow(() => tile.Dispose(),
                "a second Dispose must be a no-op (idempotent write-back), not a throw.");
        }

        // ── Value-table ownership teeth (this stage) ────────────────────────────────────────────

        /// <summary>Every layer whose value table is non-empty owns exactly one native
        /// <see cref="MvtLayer.Values"/> buffer — mirrors <see cref="EachLayer_OwnsExactlyOneTagWordsBuffer"/>.
        /// Scoped to non-empty tables: a value-less layer adopts <c>default</c> (<c>IsCreated == false</c>),
        /// same qualification the tag-words tooth carries.</summary>
        [Test]
        public void EachLayer_OwnsExactlyOneValuesBuffer()
        {
            MvtTile tile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture()));

            Assert.That(tile.Layers.Count, Is.GreaterThan(0), "precondition: the fixture decodes at least one layer");

            bool anyNonEmpty = false;
            foreach (MvtLayer layer in tile.Layers)
            {
                if (layer.Values.Length == 0) continue;
                anyNonEmpty = true;
                Assert.That(layer.Values.IsCreated, Is.True,
                    $"layer '{layer.Name}' has a non-empty value table but Values is not created.");
            }

            Assert.That(anyNonEmpty, Is.True,
                "precondition: the fixture must decode at least one non-empty value table, or this tooth is vacuous");
        }

        /// <summary>The actual leak guard for the value table: frees <see cref="MvtLayer.Values"/> exactly
        /// once, and a second <c>Dispose</c> is a no-op. <b>This is the tooth that catches a missed free</b> —
        /// if <see cref="MvtLayer.Dispose"/> forgot to free <c>Values</c>, the values-array read-after-dispose
        /// UAF tooth would stay GREEN (its resolver's <c>tagWords</c> read throws first, shadowing the
        /// values-array check — see <c>MvtValueCompactionTests.TryGetByKeyIndex_AfterValuesArrayDisposed_FailsLoud</c>'s
        /// doc), so free-exactly-once is the only tooth that observes a missed free.</summary>
        [Test]
        public void DecodedTile_FreesValues_ExactlyOnce()
        {
            MvtTile tile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture()));
            MvtLayer layer = tile.Layers.First(l => l.Values.Length > 0);
            Assert.That(layer.Values.IsCreated, Is.True, "precondition: the buffer is alive before disposal");

            tile.Dispose();
            Assert.That(layer.Values.IsCreated, Is.False,
                "MvtLayer.Dispose must free Values — IsCreated should now be false.");

            Assert.DoesNotThrow(() => tile.Dispose(),
                "a second Dispose must be a no-op (idempotent write-back), not a throw.");
        }

        // ── Tooth #5 — §0.1 use-after-free bound ───────────────────────────────────────────────

        /// <summary>
        /// Reading a property after the owning tile is disposed must fail LOUD — the disposed-<c>NativeArray</c>
        /// safety check (<c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, always on in the Editor gate), not a silent
        /// stale read. Warms the live read first (present key, real feature) so the post-dispose failure can
        /// only be the disposed-access check, never a first-touch/JIT artifact.
        /// </summary>
        [Test]
        public void ReadingProperty_AfterTileDisposed_FailsLoud()
        {
            MvtTile tile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture()));
            MvtLayer countries = tile.GetLayer("countries");
            Assert.That(countries, Is.Not.Null, "precondition: the fixture has a 'countries' layer");
            IFeature feature = countries.Features[0];

            // Anti-vacuity: prove the read succeeds WHILE the tile is alive, on a key guaranteed present
            // (every countries feature has NAME — see DensePropertyStoreTests). A missing key would return
            // false at the TryGetKeyIndex gate before ever touching the tag-words buffer, so it would not
            // exercise — or falsify — the read this tooth is about.
            bool foundWhileAlive = feature.TryGetProperty("NAME", out Value _);
            Assert.That(foundWhileAlive, Is.True, "precondition: NAME must resolve while the tile is alive");

            tile.Dispose();

            // Strict form first (brief: don't degrade to Assert.Catch without first establishing the exact
            // type reds). If a future Collections version changes the thrown type, tighten/loosen here —
            // see the dev report for the type observed on the pinned version.
            Assert.Throws<ObjectDisposedException>(() => feature.TryGetProperty("NAME", out Value _),
                "reading a property after MvtLayer.Dispose must throw ObjectDisposedException — a disposed " +
                "NativeArray read under collections safety checks, not a silent stale value.");
        }

        // ── Repeated tags field — last-wins + lazy parse (deliberate; see DecodeFeature's doc) ────

        // Minimal hand-rolled protobuf byte builders — mirrors MvtPropertyDecodeTests.cs's technique
        // verbatim (same helper shapes), so a synthetic malformed-tile fixture stays consistent across
        // both files rather than growing a second construction style.
        private static byte[] Varint(ulong v)
        {
            var list = new List<byte>();
            while (v >= 0x80) { list.Add((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            list.Add((byte)v);
            return list.ToArray();
        }

        private static byte[] LengthDelimited(byte[] data)
        {
            var len = Varint((ulong)data.Length);
            var result = new byte[len.Length + data.Length];
            Array.Copy(len, 0, result, 0, len.Length);
            Array.Copy(data, 0, result, len.Length, data.Length);
            return result;
        }

        private static byte[] Cat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            var result = new byte[total];
            int pos = 0;
            foreach (var p in parts) { Array.Copy(p, 0, result, pos, p.Length); pos += p.Length; }
            return result;
        }

        private static byte[] Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

        private static byte[] StringField(int field, string val)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(val);
            return Cat(Tag(field, 2), LengthDelimited(bytes));
        }

        private static byte[] VarintField(int field, ulong val) => Cat(Tag(field, 0), Varint(val));

        /// <summary>
        /// A repeated <c>FeatureTags</c> field (MVT Feature field 2 appearing twice) is last-wins AND
        /// lazily parsed — see <see cref="MvtDecoder"/>'s <c>DecodeFeature</c> doc. Only the LAST
        /// occurrence's bytes are ever read, so a malformed EARLIER occurrence (an unterminated packed
        /// varint that would throw if it were ever parsed) must not throw, and the resolved property must
        /// come from the LAST occurrence.
        /// </summary>
        [Test]
        public void RepeatedTagsField_MalformedEarlierOccurrence_DoesNotThrow_ResolvesFromLastOccurrence()
        {
            // Two occurrences of the tags field (field 2, wire type 2) inside one feature message:
            //   1st: length=1, data=[0x80] — a single continuation byte with no terminator; parsing this
            //        as a packed varint throws "varint overruns buffer".
            //   2nd: length=2, data=[0x00,0x00] — a valid pair (keyIdx=0, valIdx=0).
            byte[] malformedTagsOccurrence = Cat(Tag(2, 2), LengthDelimited(new byte[] { 0x80 }));
            byte[] validTagsOccurrence     = Cat(Tag(2, 2), LengthDelimited(Cat(Varint(0), Varint(0))));
            byte[] featureTags = Cat(malformedTagsOccurrence, validTagsOccurrence);
            byte[] featureType = VarintField(3, 3); // Polygon
            byte[] featureGeom = Cat(Tag(4, 2), LengthDelimited(Cat(Varint(9), Varint(0), Varint(0))));
            byte[] featureBody = Cat(featureTags, featureType, featureGeom);

            byte[] layerVersion = VarintField(15, 2);
            byte[] layerName    = StringField(1, "repeatedtags");
            byte[] layerKey     = StringField(3, "k");
            byte[] layerValue   = Cat(Tag(4, 2), LengthDelimited(StringField(1, "resolved-from-last")));
            byte[] layerFeature = Cat(Tag(2, 2), LengthDelimited(featureBody));
            byte[] layerBody    = Cat(layerVersion, layerName, layerKey, layerValue, layerFeature);
            byte[] tileBytes    = Cat(Tag(3, 2), LengthDelimited(layerBody));

            MvtTile tile = null;
            Assert.DoesNotThrow(
                () => tile = TestDecodedTiles.Track(
                    MvtDecoder.Decode(FixtureTileId, tileBytes)),
                "a malformed EARLIER occurrence of a repeated tags field must not throw — only the LAST " +
                "occurrence is ever parsed.");

            MvtLayer layer = tile.GetLayer("repeatedtags");
            Assert.That(layer, Is.Not.Null, "precondition: the synthetic layer must decode");
            IFeature feature = layer.Features[0];

            bool found = feature.TryGetProperty("k", out Value value);
            Assert.That(found, Is.True, "the LAST occurrence's pair (keyIdx=0, valIdx=0) must resolve");
            // Anti-vacuity: the resolved value can only have come from the LAST occurrence — the malformed
            // earlier one was never parsed at all, not parsed-and-discarded.
            Assert.That(value.AsString(), Is.EqualTo("resolved-from-last"),
                "the resolved property must be the value only the LAST tags occurrence can produce.");
        }
    }
}
