// EditMode only (NOT compiled by Tools/core-tests/ — MvtDecoder/MvtModels pull Unity.Collections, and
// layer.Values is now a NativeArray<MvtValueNative>, see core-tests.csproj's tile-decode seam).

using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Mvt
{
    /// <summary>
    /// S39 — MVT property/id decoding tests. Validates:
    ///   1. Key table, value table, and per-feature Properties/Id decoded correctly from the fixture.
    ///   2. All MVT Value variant types (string, float, double, int, uint, sint, bool) decoded from
    ///      synthetic hand-built tile bytes.
    ///   3. Malformed tag input (odd-length, out-of-range index) is skip-tolerant, no throw.
    ///
    /// Fixture numbers probed and pinned 2026-06-20 (see S39 implementation notes).
    /// </summary>
    [TestFixture]
    public class MvtPropertyDecodeTests
    {

        /// <summary>IR C1 P3: the address the committed fixture is decoded at (its buffers are stamped with
        /// it). z0/0/0 — the fixture's own tile.</summary>
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture()
        {
            // Unity batch mode: cwd = project root; dotnet test: AppContext.BaseDirectory ~ bin/Debug/
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

        private static MvtLayer LoadCountries()
        {
            var layer = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present in fixture");
            return layer;
        }

        // ── Key table ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void Countries_KeyTable_HasExpectedKeys()
        {
            var layer = LoadCountries();
            Assert.That(layer.Keys.Count, Is.EqualTo(5), "countries layer has 5 keys");
            CollectionAssert.AreEqual(
                new[] { "fid", "ADM0_A3", "NAME", "ABBREV", "CONTINENT" },
                layer.Keys,
                "Key table order and names must match MVT declaration order");
        }

        // ── Value table ─────────────────────────────────────────────────────────────────────────

        /// <remarks>The sole guard of value-table count correctness this stage: <c>MvtDecodePresizeTests</c>
        /// dropped its own <c>Values</c> capacity arm, since <see cref="MvtLayer.Values"/> is a
        /// <c>NativeArray</c> sized exactly to the decoded count by construction (no capacity to probe) —
        /// see that file's recorded-limitation note.</remarks>
        [Test]
        public void Countries_ValueTable_HasExpectedCount()
        {
            var layer = LoadCountries();
            Assert.That(layer.Values.Length, Is.EqualTo(914), "countries value table has 914 entries");
        }

        [Test]
        public void Countries_ValueTable_ContainsExpectedTypes()
        {
            var layer = LoadCountries();
            int stringCount = 0, numberCount = 0;
            foreach (var v in layer.Values)
            {
                if (v.Type == MapRenderer.Core.Expressions.ValueType.String) stringCount++;
                else if (v.Type == MapRenderer.Core.Expressions.ValueType.Number) numberCount++;
            }
            Assert.That(stringCount, Is.EqualTo(675), "675 string values in table");
            Assert.That(numberCount, Is.EqualTo(239), "239 number values in table (one uint 'fid' per feature)");
        }

        // ── Feature Properties ──────────────────────────────────────────────────────────────────

        [Test]
        public void Aruba_Properties_And_Id()
        {
            var layer = LoadCountries();
            int count = 0;
            MvtFeature arubaFeature = null;
            foreach (var f in layer.Features)
            {
                if (f.Properties.TryGetValue("NAME", out var nameVal) &&
                    nameVal.Type == MapRenderer.Core.Expressions.ValueType.String &&
                    nameVal.AsString() == "Aruba")
                {
                    count++;
                    arubaFeature = f;
                }
            }
            Assert.That(count, Is.EqualTo(1), "Exactly 1 feature named 'Aruba'");
            Assert.IsNotNull(arubaFeature);

            // ADM0_A3
            Assert.IsTrue(arubaFeature.Properties.TryGetValue("ADM0_A3", out var adm0));
            Assert.That(adm0.AsString(), Is.EqualTo("ABW"));

            // HasId and Id (field-1 id, distinct from the 'fid' property)
            Assert.IsTrue(arubaFeature.HasId, "Aruba feature must have HasId=true");
            Assert.That(arubaFeature.Id, Is.EqualTo(182UL), "Aruba feature id (field-1) must be 182");
        }

        [Test]
        public void Afghanistan_Id()
        {
            var layer = LoadCountries();
            int count = 0;
            foreach (var f in layer.Features)
            {
                if (f.Properties.TryGetValue("NAME", out var nameVal) &&
                    nameVal.Type == MapRenderer.Core.Expressions.ValueType.String &&
                    nameVal.AsString() == "Afghanistan")
                {
                    count++;
                    Assert.IsTrue(f.HasId, "Afghanistan must have HasId=true");
                    Assert.That(f.Id, Is.EqualTo(129UL), "Afghanistan feature id (field-1) must be 129");
                }
            }
            Assert.That(count, Is.EqualTo(1), "Exactly 1 feature named 'Afghanistan'");
        }

        [Test]
        public void Continent_FeatureCounts()
        {
            var layer = LoadCountries();
            int africa = 0, europe = 0, asia = 0;
            foreach (var f in layer.Features)
            {
                if (f.Properties.TryGetValue("CONTINENT", out var cv) &&
                    cv.Type == MapRenderer.Core.Expressions.ValueType.String)
                {
                    switch (cv.AsString())
                    {
                        case "Africa":  africa++;  break;
                        case "Europe":  europe++;  break;
                        case "Asia":    asia++;    break;
                    }
                }
            }
            Assert.That(africa, Is.EqualTo(54),  "54 African countries");
            Assert.That(europe, Is.EqualTo(49),  "49 European countries");
            Assert.That(asia,   Is.EqualTo(56),  "56 Asian countries");
        }

        // ── Feature Id presence/distinctness ────────────────────────────────────────────────────

        [Test]
        public void AllFeatures_HaveId_AndDistinctIds()
        {
            var layer = LoadCountries();
            var idSet = new HashSet<ulong>();
            foreach (var f in layer.Features)
            {
                Assert.IsTrue(f.HasId, "Every countries feature must have HasId=true");
                idSet.Add(f.Id);
            }
            Assert.That(idSet.Count, Is.EqualTo(239), "All 239 feature ids are distinct");
        }

        // ── Synthetic variant type tests ─────────────────────────────────────────────────────────
        //
        // The fixture only uses string and uint variants. The tests below cover the remaining
        // variants (float, double, int, sint-negative, bool) via hand-built minimal tile bytes.
        //
        // Tile encoding: each minimal tile is [layer_field3_tag, layer_len, layer_bytes].
        // Layer: [version=15 tag+val, name tag+val, key tag+val, value tag+val, feature tag+val].
        // Feature: [type tag+val, tags tag+val, geometry tag+val (dummy)].
        // Value sub-message varies by variant.

        // Helpers for building minimal protobuf bytes.

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

        // Concatenate arbitrary number of byte arrays.
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

        // Build a minimal tile with one layer, one key ("k"), one value of given bytes, one feature
        // with tag pair [0,0] (keyIdx=0, valIdx=0).
        private static MvtLayer BuildSyntheticLayer(byte[] valueSubMessage)
        {
            // Feature sub-message: type=3 (Polygon), tags=[0,0] (pair: key 0, value 0), geometry=[9,0,0] (one point)
            byte[] featureTags = Cat(Tag(2, 2), LengthDelimited(Cat(Varint(0), Varint(0)))); // packed [0,0]
            byte[] featureType = VarintField(3, 3); // Polygon
            byte[] featureGeom = Cat(Tag(4, 2), LengthDelimited(Cat(Varint(9), Varint(0), Varint(0)))); // minimal geometry
            byte[] featureBody = Cat(featureTags, featureType, featureGeom);

            // Layer sub-message
            byte[] layerVersion = VarintField(15, 2); // version = 2
            byte[] layerName    = StringField(1, "test");
            byte[] layerKey     = StringField(3, "k");
            byte[] layerValue   = Cat(Tag(4, 2), LengthDelimited(valueSubMessage));
            byte[] layerFeature = Cat(Tag(2, 2), LengthDelimited(featureBody));
            byte[] layerBody    = Cat(layerVersion, layerName, layerKey, layerValue, layerFeature);

            // Tile
            byte[] tileBytes = Cat(Tag(3, 2), LengthDelimited(layerBody));

            var tile = MvtDecoder.Decode(FixtureTileId, tileBytes);
            var layer = tile.GetLayer("test");
            Assert.IsNotNull(layer, "synthetic test layer must be present");
            return layer;
        }

        [Test]
        public void Variant_String_DecodesAsValueString()
        {
            // Value string_value (field 1, wire type 2)
            byte[] valMsg = StringField(1, "hello");
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values.Length, Is.EqualTo(1));
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.String));
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsString(), Is.EqualTo("hello"));
            Assert.That(layer.Features[0].Properties["k"].AsString(), Is.EqualTo("hello"));
        }

        [Test]
        public void Variant_Float_DecodesAsValueNumber()
        {
            // Value float_value (field 2, wire type 5 = fixed32 little-endian)
            float f = 3.14f;
            byte[] floatBytes = BitConverter.GetBytes(f);
            if (!BitConverter.IsLittleEndian) Array.Reverse(floatBytes);
            byte[] valMsg = Cat(Tag(2, 5), floatBytes);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.Number));
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsNumber(), Is.EqualTo((double)f).Within(1e-5));
            Assert.That(layer.Features[0].Properties["k"].AsNumber(), Is.EqualTo((double)f).Within(1e-5));
        }

        [Test]
        public void Variant_Double_DecodesAsValueNumber()
        {
            // Value double_value (field 3, wire type 1 = fixed64 little-endian)
            double d = 2.718281828;
            long bits = BitConverter.DoubleToInt64Bits(d);
            byte[] doubleBytes = new byte[8];
            for (int i = 0; i < 8; i++) doubleBytes[i] = (byte)((bits >> (8 * i)) & 0xFF); // LE
            byte[] valMsg = Cat(Tag(3, 1), doubleBytes);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.Number));
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsNumber(), Is.EqualTo(d).Within(1e-12));
        }

        [Test]
        public void Variant_Int_Positive_DecodesAsValueNumber()
        {
            // Value int_value (field 4, wire type 0) — int64 positive
            byte[] valMsg = VarintField(4, 42);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.Number));
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsNumber(), Is.EqualTo(42.0));
        }

        [Test]
        public void Variant_Int_Negative_DecodesAsValueNumber()
        {
            // Value int_value (field 4, wire type 0) — int64 negative (-7 as two's complement ulong varint)
            long neg = -7L;
            ulong raw = (ulong)neg; // two's complement (all bits set for sign extension)
            byte[] valMsg = VarintField(4, raw);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsNumber(), Is.EqualTo(-7.0));
        }

        [Test]
        public void Variant_Uint_DecodesAsValueNumber()
        {
            // Value uint_value (field 5, wire type 0)
            byte[] valMsg = VarintField(5, 999);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.Number));
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsNumber(), Is.EqualTo(999.0));
        }

        [Test]
        public void Variant_Sint_Negative_DecodesAsValueNumber()
        {
            // Value sint_value (field 6, wire type 0) — zigzag(-5): n=5 → (5<<1)^0 = 10; n=-5 → 9
            // zigzag encode: (n << 1) ^ (n >> 63)  →  for -5: (-5<<1)^(-5>>63) = (-10)^(-1) = 9
            long n = -5L;
            ulong zigzag = (ulong)((n << 1) ^ (n >> 63));
            byte[] valMsg = VarintField(6, zigzag);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].ToValue(layer.ValueStrings).AsNumber(), Is.EqualTo(-5.0));
        }

        [Test]
        public void Variant_Bool_True_DecodesAsValueBool()
        {
            // Value bool_value (field 7, wire type 0): 1 = true
            byte[] valMsg = VarintField(7, 1);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.Boolean));
            Assert.IsTrue(layer.Values[0].ToValue(layer.ValueStrings).AsBool());
            Assert.IsTrue(layer.Features[0].Properties["k"].AsBool());
        }

        [Test]
        public void Variant_Bool_False_DecodesAsValueBool()
        {
            // Value bool_value (field 7, wire type 0): 0 = false
            byte[] valMsg = VarintField(7, 0);
            var layer = BuildSyntheticLayer(valMsg);
            Assert.That(layer.Values[0].Type, Is.EqualTo(MapRenderer.Core.Expressions.ValueType.Boolean));
            Assert.IsFalse(layer.Values[0].ToValue(layer.ValueStrings).AsBool());
        }

        // ── Malformed tag input — skip-tolerant ─────────────────────────────────────────────────

        [Test]
        public void MalformedTags_OddLength_DoesNotThrow_PartialProperties()
        {
            // Build a feature with 3 tag uints (odd — last pair is incomplete: [key0,val0, key1])
            // The decoder must process [key0,val0] and ignore the trailing lone key1.
            byte[] featureTags = Cat(Tag(2, 2), LengthDelimited(Cat(Varint(0), Varint(0), Varint(1))));
            byte[] featureType = VarintField(3, 3);
            byte[] featureGeom = Cat(Tag(4, 2), LengthDelimited(Cat(Varint(9), Varint(0), Varint(0))));
            byte[] featureBody = Cat(featureTags, featureType, featureGeom);

            byte[] layerVersion = VarintField(15, 2);
            byte[] layerName    = StringField(1, "malformed");
            byte[] layerKey0    = StringField(3, "k0");
            byte[] layerKey1    = StringField(3, "k1");
            byte[] layerVal0    = Cat(Tag(4, 2), LengthDelimited(StringField(1, "v0")));
            byte[] layerFeature = Cat(Tag(2, 2), LengthDelimited(featureBody));
            byte[] layerBody    = Cat(layerVersion, layerName, layerKey0, layerKey1, layerVal0, layerFeature);
            byte[] tileBytes    = Cat(Tag(3, 2), LengthDelimited(layerBody));

            MvtTile tile = null;
            Assert.DoesNotThrow(() => tile = MvtDecoder.Decode(FixtureTileId, tileBytes), "Odd-length tags must not throw");
            Assert.IsNotNull(tile);
            var layer = tile.GetLayer("malformed");
            Assert.IsNotNull(layer);
            Assert.That(layer.Features.Count, Is.EqualTo(1));
            // key0→val0 pair resolved; trailing lone key1 dropped
            Assert.IsTrue(layer.Features[0].Properties.ContainsKey("k0"), "k0 resolved from complete pair");
            Assert.IsFalse(layer.Features[0].Properties.ContainsKey("k1"), "k1 not in properties (incomplete pair)");
        }

        [Test]
        public void MalformedTags_OutOfRangeKeyIndex_DoesNotThrow_SkipsPair()
        {
            // Feature with tag pair [keyIdx=5, valIdx=0] where keys has only 1 entry (index 0).
            byte[] featureTags = Cat(Tag(2, 2), LengthDelimited(Cat(Varint(5), Varint(0))));
            byte[] featureType = VarintField(3, 3);
            byte[] featureGeom = Cat(Tag(4, 2), LengthDelimited(Cat(Varint(9), Varint(0), Varint(0))));
            byte[] featureBody = Cat(featureTags, featureType, featureGeom);

            byte[] layerVersion = VarintField(15, 2);
            byte[] layerName    = StringField(1, "oob");
            byte[] layerKey0    = StringField(3, "k");
            byte[] layerVal0    = Cat(Tag(4, 2), LengthDelimited(StringField(1, "v")));
            byte[] layerFeature = Cat(Tag(2, 2), LengthDelimited(featureBody));
            byte[] layerBody    = Cat(layerVersion, layerName, layerKey0, layerVal0, layerFeature);
            byte[] tileBytes    = Cat(Tag(3, 2), LengthDelimited(layerBody));

            MvtTile tile = null;
            Assert.DoesNotThrow(() => tile = MvtDecoder.Decode(FixtureTileId, tileBytes), "Out-of-range key index must not throw");
            Assert.IsNotNull(tile);
            var layer = tile.GetLayer("oob");
            Assert.IsNotNull(layer);
            Assert.That(layer.Features[0].Properties.Count, Is.EqualTo(0), "Out-of-range pair skipped, empty properties");
        }

        [Test]
        public void MalformedTags_OutOfRangeValueIndex_DoesNotThrow_SkipsPair()
        {
            // Feature with tag pair [keyIdx=0, valIdx=99] where values has only 1 entry (index 0).
            byte[] featureTags = Cat(Tag(2, 2), LengthDelimited(Cat(Varint(0), Varint(99))));
            byte[] featureType = VarintField(3, 3);
            byte[] featureGeom = Cat(Tag(4, 2), LengthDelimited(Cat(Varint(9), Varint(0), Varint(0))));
            byte[] featureBody = Cat(featureTags, featureType, featureGeom);

            byte[] layerVersion = VarintField(15, 2);
            byte[] layerName    = StringField(1, "oobv");
            byte[] layerKey0    = StringField(3, "k");
            byte[] layerVal0    = Cat(Tag(4, 2), LengthDelimited(StringField(1, "v")));
            byte[] layerFeature = Cat(Tag(2, 2), LengthDelimited(featureBody));
            byte[] layerBody    = Cat(layerVersion, layerName, layerKey0, layerVal0, layerFeature);
            byte[] tileBytes    = Cat(Tag(3, 2), LengthDelimited(layerBody));

            MvtTile tile = null;
            Assert.DoesNotThrow(() => tile = MvtDecoder.Decode(FixtureTileId, tileBytes), "Out-of-range value index must not throw");
            var layer = tile.GetLayer("oobv");
            Assert.That(layer.Features[0].Properties.Count, Is.EqualTo(0), "Out-of-range value pair skipped");
        }

        // ── Feature id = 0 is valid (not treated as absent) ─────────────────────────────────────

        [Test]
        public void FeatureId_Zero_IsValid_HasIdTrue()
        {
            // Build a feature with id field present = 0
            byte[] featureId   = VarintField(1, 0);
            byte[] featureTags = Cat(Tag(2, 2), LengthDelimited(new byte[0])); // empty tags
            byte[] featureType = VarintField(3, 3);
            byte[] featureGeom = Cat(Tag(4, 2), LengthDelimited(Cat(Varint(9), Varint(0), Varint(0))));
            byte[] featureBody = Cat(featureId, featureTags, featureType, featureGeom);

            byte[] layerVersion = VarintField(15, 2);
            byte[] layerName    = StringField(1, "zeroid");
            byte[] layerFeature = Cat(Tag(2, 2), LengthDelimited(featureBody));
            byte[] layerBody    = Cat(layerVersion, layerName, layerFeature);
            byte[] tileBytes    = Cat(Tag(3, 2), LengthDelimited(layerBody));

            var tile = MvtDecoder.Decode(FixtureTileId, tileBytes);
            var layer = tile.GetLayer("zeroid");
            Assert.IsNotNull(layer);
            Assert.That(layer.Features.Count, Is.EqualTo(1));
            Assert.IsTrue(layer.Features[0].HasId, "id=0 must be HasId=true (field-1 was present)");
            Assert.That(layer.Features[0].Id, Is.EqualTo(0UL));
        }
    }
}
