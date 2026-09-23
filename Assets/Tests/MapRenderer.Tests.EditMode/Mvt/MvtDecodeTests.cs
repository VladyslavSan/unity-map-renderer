// Mvt/MvtDecodeTests.cs — MVT tag/property/geometry decode teeth, plain bare-NUnit-Is group (EditMode).
//
// Split from Mvt/MvtNativeShapeTests.cs by the using-collision rule: these four use bare NUnit Is
// and must never share a file with that pair's Is = UnityEngine.TestTools.Constraints.Is alias.
//
// Contents:
//   MvtDecodeTagColumnHardeningTests  — MvtDecoder.FlattenFeatureColumn sizes both tag-slice columns to the feature count.
//   MvtPropertyDecodeTests            — key table, value table and per-feature Properties/Id decoded correctly from the fixture and from synthetic tile bytes.
//   MvtDecodePresizeTests             — the decoded value table is sized to the exact decoded count, no growth over-allocation.
//   KeyBindingHoistTests              — FeatureSelector's per-layer bind step resolves a filter's constant-key names once per call, not once per feature.
//   NativeTagStorageTests             — DensePropertyStore's flattened tag-word buffer: a shared Allocator.Persistent MvtLayer.FeatureTagWords view.
//   DecodeGeometryFlattenAllocTests   — the decode-geometry-flatten allocation tooth, calibrated against a live GC.GetTotalMemory canary.

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Jobs.Mvt;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using System.Linq;
using System.Reflection;


namespace MapRenderer.Tests.Mvt
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtDecodeTagColumnHardeningTests — MvtDecoder.FlattenFeatureColumn's tag-slice columns at the feature count
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression tooth for
    /// <see cref="MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount"/>: the tag-slice columns' feature-
    /// count lockstep check must both (a) actually throw on a mismatch and (b) run BEFORE
    /// <c>MvtLayer.AdoptGeometry</c> inside <c>MvtDecoder.DecodeLayer</c> — the ordering that keeps a
    /// mismatch from leaking the just-adopted geometry buffer (the layer is not yet reachable from
    /// <c>tile.Layers</c> at that point, so <c>Decode</c>'s catch cannot free it; see the call site's
    /// comment in <c>MvtDecoder.cs</c>).
    ///
    /// <para>A REAL mismatch is unreachable through <see cref="MvtDecoder.Decode"/> — <c>FlattenFeatureColumn</c>
    /// sizes both tag-slice columns to the feature count — so (a) is pinned directly
    /// against the check (broadened to <c>internal</c> for exactly this), and (b) is pinned by scanning
    /// <c>MvtDecoder.cs</c>'s own source for the two calls' relative order, the same source-scan technique
    /// <c>Structure/NeutralGeometryPathTests</c> already uses for a fence a reflection-only assertion
    /// cannot express.</para>
    /// </summary>
    [TestFixture]
    public class MvtDecodeTagColumnHardeningTests
    {
        // ── (a) the check itself throws on mismatch, not on match ──────────────────────────────

        [Test]
        public void ValidateTagSliceColumnsMatchFeatureCount_ThrowsOnMismatch()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount(
                    tagOffsetsLength: 3, tagLengthsLength: 3, featCount: 4, layerName: "roads"));
            StringAssert.Contains("roads", ex.Message);
        }

        [Test]
        public void ValidateTagSliceColumnsMatchFeatureCount_ThrowsWhenOnlyLengthsColumnMismatches()
        {
            Assert.Throws<InvalidOperationException>(() =>
                MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount(
                    tagOffsetsLength: 4, tagLengthsLength: 3, featCount: 4, layerName: "roads"));
        }

        [Test]
        public void ValidateTagSliceColumnsMatchFeatureCount_DoesNotThrow_WhenBothColumnsMatch()
        {
            Assert.DoesNotThrow(() =>
                MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount(
                    tagOffsetsLength: 4, tagLengthsLength: 4, featCount: 4, layerName: "roads"));
        }

        // ── (b) the check runs BEFORE AdoptGeometry, in DecodeLayer's own source ───────────────

        /// <summary>Non-vacuity + the ordering claim in one: both calls must actually be found (proving the
        /// scan is looking at the real method), and the validate call's position must precede the adopt
        /// call's. RED-verify: temporarily move the <c>ValidateTagSliceColumnsMatchFeatureCount(...)</c>
        /// statement to after <c>layer.AdoptGeometry(...)</c> in <c>MvtDecoder.cs</c> — this test goes red;
        /// restore and it is green again.</summary>
        [Test]
        public void DecodeLayer_ValidatesTagSliceColumns_BeforeAdoptingGeometry()
        {
            string decoderSource = ReadMvtDecoderSource();

            // The exact declaration text, not just "DecodeLayer(" — that substring also matches the call
            // site inside Decode(), which appears EARLIER in the file than the method it calls.
            string decodeLayerBody = ExtractMethodBody(decoderSource, "static MvtLayer DecodeLayer(");
            Assert.IsNotEmpty(decodeLayerBody, "precondition: DecodeLayer's body must be found in the source scan");

            int validateIndex = decodeLayerBody.IndexOf("ValidateTagSliceColumnsMatchFeatureCount(", StringComparison.Ordinal);
            int adoptGeometryIndex = decodeLayerBody.IndexOf(".AdoptGeometry(", StringComparison.Ordinal);

            Assert.That(validateIndex, Is.GreaterThanOrEqualTo(0),
                "precondition: DecodeLayer must call ValidateTagSliceColumnsMatchFeatureCount — if this " +
                "scan finds nothing, the ordering assertion below asserts about nothing");
            Assert.That(adoptGeometryIndex, Is.GreaterThanOrEqualTo(0),
                "precondition: DecodeLayer must call AdoptGeometry — same non-vacuity concern");

            Assert.That(validateIndex, Is.LessThan(adoptGeometryIndex),
                "the tag-slice column validation must run BEFORE AdoptGeometry: a throw after AdoptGeometry " +
                "would leak the just-adopted geometry buffer, because the layer is not yet reachable from " +
                "tile.Layers and Decode's catch can only free what IS reachable");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        private static string ReadMvtDecoderSource()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            FileAssert.Exists(path);
            return File.ReadAllText(path);
        }

        /// <summary>Extracts the brace-matched body of the first method whose declaration contains
        /// <paramref name="declarationText"/> — a plain brace counter from the method's own opening brace,
        /// good enough for a single well-formed C# source file with no string/char literals containing
        /// unbalanced braces (true of this file). The caller passes enough of the declaration (return type
        /// + name + open paren) to distinguish it from a mere call site earlier in the file.</summary>
        private static string ExtractMethodBody(string source, string declarationText)
        {
            int nameIndex = source.IndexOf(declarationText, StringComparison.Ordinal);
            if (nameIndex < 0) return string.Empty;

            int openBrace = source.IndexOf('{', nameIndex);
            if (openBrace < 0) return string.Empty;

            int depth = 0;
            for (int i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(openBrace, i - openBrace + 1);
                }
            }
            return string.Empty;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtPropertyDecodeTests — key/value table and per-feature Properties/Id decoding
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MVT property/id decoding tests. Validates:
    ///   1. Key table, value table, and per-feature Properties/Id decoded correctly from the fixture.
    ///   2. All MVT Value variant types (string, float, double, int, uint, sint, bool) decoded from
    ///      synthetic hand-built tile bytes.
    ///   3. Malformed tag input (odd-length, out-of-range index) is skip-tolerant, no throw.
    ///
    /// Fixture numbers probed and pinned 2026-06-20.
    /// </summary>
    [TestFixture]
    public class MvtPropertyDecodeTests
    {

        /// <summary>The address the committed fixture is decoded at (its buffers are stamped with
        /// it). z0/0/0 — the fixture's own tile.</summary>
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>A decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
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
        /// <c>NativeArray</c> sized exactly to the decoded count (no capacity to probe) —
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

            // Id (field-1 id, distinct from the 'fid' property)
            Assert.That(arubaFeature.Id, Is.EqualTo(Value.Number(182)), "Aruba feature id (field-1) must be 182");
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
                    Assert.That(f.Id, Is.EqualTo(Value.Number(129)), "Afghanistan feature id (field-1) must be 129");
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
            var idSet = new HashSet<Value>();
            foreach (var f in layer.Features)
            {
                Assert.That(f.Id.IsNull, Is.False, "Every countries feature must have an id");
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
        public void FeatureId_Zero_IsValid_NotNull()
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
            Assert.That(layer.Features[0].Id.IsNull, Is.False, "id=0 must not be Value.Null (field-1 was present)");
            Assert.That(layer.Features[0].Id, Is.EqualTo(Value.Number(0)));
        }

        // ── Feature id absent decodes to Value.Null, not a zero Value.Number ────────────────────

        [Test]
        public void FeatureId_AbsentField1_DecodesAsValueNull()
        {
            // BuildSyntheticLayer's feature never emits field 1 (id) at all.
            var layer = BuildSyntheticLayer(StringField(1, "hello"));
            Assert.That(layer.Features[0].Id.IsNull, Is.True,
                "an absent field-1 must decode to Value.Null, never a zero Value.Number.");
        }
    }

// EditMode only (decodes a real MVT fixture through MvtDecoder, which lives in MapRenderer.Jobs and is not
// compiled by the Tools/core-tests seam).

    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtDecodePresizeTests — the decoded value table is sized to the exact decoded count
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// H1 (safe subset) — <see cref="MvtDecoder"/> pre-sizes each layer's <c>Features</c> / <c>Keys</c> lists
    /// (and its two per-feature scratch lists) from a read-only counting pass, so the streaming decode never
    /// grows them by doubling and discarding a chain of backing arrays. Tooth: after decode, every list's
    /// <c>Capacity</c> equals its <c>Count</c> exactly — no growth over-allocation.
    /// </summary>
    /// <remarks>
    /// RED without the pre-size: a <see cref="System.Collections.Generic.List{T}"/> grown by doubling lands on
    /// the next power of two ≥ Count, strictly greater whenever Count is not itself a reached power of two.
    /// The fixture-has-a-non-power-of-two guard keeps the tooth from passing vacuously on a layer whose count
    /// a doubling build would coincidentally land on.
    ///
    /// <para><b>Recorded limitation — the <c>Values</c> arm is dropped.</b> This stage moved
    /// <see cref="MvtLayer.Values"/> off <c>List&lt;MvtValue&gt;</c> onto <c>NativeArray&lt;MvtValueNative&gt;</c>,
    /// sized to the exact decoded count (no <c>Capacity</c> to over-allocate) — the
    /// "no growth over-allocation" property this file pins now belongs to the DECODE's transient
    /// <c>List&lt;MvtValueNative&gt;</c> scratch, which falls out of scope before this test can observe it.
    /// Value-table COUNT correctness (as opposed to allocation shape) is still pinned — by
    /// <c>MvtPropertyDecodeTests.Countries_ValueTable_HasExpectedCount</c> (914, via <c>.Length</c>) — so this
    /// is a narrowing of what this file observes, not a silently weakened tooth.</para>
    /// </remarks>
    [TestFixture]
    public class MvtDecodePresizeTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        [Test]
        public void DecodedLayerLists_HaveCapacityEqualToCount_NoGrowthOverAllocation()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture()));

            Assert.That(tile.Layers.Count, Is.GreaterThan(0), "fixture sanity: at least one layer decoded.");

            bool sawNonPowerOfTwo = false;
            foreach (MvtLayer layer in tile.Layers)
            {
                Assert.That(layer.Features.Capacity, Is.EqualTo(layer.Features.Count),
                    $"layer '{layer.Name}': Features must be pre-sized exactly (no doubling reallocation).");
                Assert.That(layer.Keys.Capacity, Is.EqualTo(layer.Keys.Count),
                    $"layer '{layer.Name}': Keys must be pre-sized exactly.");

                // A 0-count layer is Capacity==Count==0 with or without the fix — not a discriminator — so it
                // must not satisfy the guard; require a non-empty layer whose count a doubling build overshoots.
                if (layer.Features.Count > 0 && !IsPowerOfTwo(layer.Features.Count)) sawNonPowerOfTwo = true;
            }

            // The Capacity==Count check only discriminates the fix when at least one count is not a power of
            // two (a doubling build would otherwise coincidentally land on Count). Fail loudly if the fixture
            // ever loses that property rather than let the tooth pass vacuously.
            Assert.That(sawNonPowerOfTwo, Is.True,
                "fixture must contain a layer whose feature count is not a power of two for this tooth to bite.");
        }

        private static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

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
    }

// Unity EditMode only — MvtDecoder decodes into NativeArray-backed buffers. NOT registered in
// core-tests.csproj (see core-tests.csproj's tile-decode-seam note).

    // ───────────────────────────────────────────────────────────────────────────────────
    // KeyBindingHoistTests — FeatureSelector's per-layer constant-key bind step resolves once per call
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T1b + T2 (string→id key hoist) — the integration proof, over the real fixture, that
    /// <see cref="FeatureSelector"/>'s per-layer bind step resolves a filter's constant-key <c>get</c>/<c>has</c>
    /// names ONCE per <see cref="FeatureSelector.SelectFeatures(StyleLayer, ITileLayer, double, List{SelectedTileFeature})"/>
    /// call — not once per feature — and that the hoisted int-key binding path (via the
    /// <see cref="ITileLayer"/> overload) and the plain string-lookup path (via the
    /// <see cref="FeatureSelector.SelectFeatures(StyleLayer, IReadOnlyList{IFeature}, double, List{SelectedTileFeature})"/>
    /// features-list overload, which never probes for <see cref="IIndexedFeatureSource"/> capability and so
    /// always evaluates by name) select the same features over the SAME decoded feature set, exactly as
    /// before the hoist.
    /// </summary>
    [TestFixture]
    public class KeyBindingHoistTests
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

        private static StyleLayer MakeCountriesLayer(string filterJson) => new StyleLayer
        {
            Id          = "test",
            Source      = "fixture",
            SourceLayer = "countries",
            Filter      = JsonParser.Parse(filterJson),
        };

        /// <summary>Counts <see cref="IFeatureKeyResolver.TryResolveKey"/> calls, forwarding to a real
        /// resolver — installed via <see cref="CountingIndexedTileLayer"/> so the count rides the seam
        /// itself, with zero production instrumentation.</summary>
        private sealed class CountingKeyResolver : IFeatureKeyResolver
        {
            private readonly IFeatureKeyResolver _inner;
            public int CallCount { get; private set; }
            public CountingKeyResolver(IFeatureKeyResolver inner) => _inner = inner;
            public bool TryResolveKey(string name, out int keyIndex)
            {
                CallCount++;
                return _inner.TryResolveKey(name, out keyIndex);
            }
        }

        /// <summary>A thin <see cref="ITileLayer"/>/<see cref="IIndexedFeatureSource"/> adapter over a real
        /// (already-decoded) <see cref="MvtLayer"/>: forwards <see cref="Features"/> as given (so a test can
        /// vary N independently of the layer's own feature count) and <see cref="Geometry"/> straight from
        /// the real layer (borrowed, never disposed here — the tracked <see cref="MvtTile"/> owns it), and
        /// answers <see cref="KeyResolver"/> with a <see cref="CountingKeyResolver"/> wrapping the real
        /// layer's Dense resolver.</summary>
        private sealed class CountingIndexedTileLayer : ITileLayer, IIndexedFeatureSource
        {
            private readonly MvtLayer _real;
            public readonly CountingKeyResolver Resolver;

            public CountingIndexedTileLayer(MvtLayer real, IReadOnlyList<IFeature> features)
            {
                _real = real;
                Features = features;
                Resolver = new CountingKeyResolver(real.DenseKeyResolver);
            }

            public string Name => _real.Name;
            public uint Extent => _real.Extent;
            public IReadOnlyList<IFeature> Features { get; }
            public TileGeometryBuffers Geometry => _real.Geometry;
            public IFeatureKeyResolver KeyResolver => Resolver;
        }

        // ── T1b: O(layers), not O(features) ──────────────────────────────────────────────────────

        [Test]
        public void SelectFeatures_ResolvesKeysOncePerCall_NotPerFeature_AndSelectionIsUnchanged()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            Assert.That(layer, Is.Not.Null, "precondition: fixture must have a 'countries' layer");
            Assert.That(layer.Features.Count, Is.GreaterThan(1),
                "precondition: need >1 feature to distinguish O(layers) from O(features)");

            StyleLayer styleLayer = MakeCountriesLayer("[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]");

            // An independent oracle via the STRING path (bypasses the binding entirely), so the
            // "selection unchanged" assertions below don't just trust the code under test.
            bool StringPathMatchesAfrica(IFeature f) =>
                f.TryGetProperty("CONTINENT", out Value v)
                && v.Type == MapRenderer.Core.Expressions.ValueType.String
                && v.AsString() == "Africa";

            var into = new List<SelectedTileFeature>();

            // N = 1 feature.
            var oneLayerAdapter = new CountingIndexedTileLayer(layer, new List<IFeature> { layer.Features[0] });
            FeatureSelector.SelectFeatures(styleLayer, oneLayerAdapter, 0.0, into);
            int oneFeatureResolveCount = oneLayerAdapter.Resolver.CallCount;
            int oneFeatureSelected = into.Count;

            // N = all features.
            var allLayerAdapter = new CountingIndexedTileLayer(layer, layer.Features);
            FeatureSelector.SelectFeatures(styleLayer, allLayerAdapter, 0.0, into);
            int allFeaturesResolveCount = allLayerAdapter.Resolver.CallCount;
            int allFeaturesSelected = into.Count;

            // The filter has exactly one get-node -> exactly one TryResolveKey call per SelectFeatures call,
            // however many features it scans. A per-feature lookup (the un-hoisted behaviour) would instead
            // scale with N: 1 at N=1, layer.Features.Count at N=all.
            Assert.That(oneFeatureResolveCount, Is.EqualTo(1),
                "one get-node -> exactly one TryResolveKey call, even at N=1");
            Assert.That(allFeaturesResolveCount, Is.EqualTo(1),
                "one get-node -> exactly one TryResolveKey call, however many features (this is the hoist)");
            Assert.That(allFeaturesResolveCount, Is.EqualTo(oneFeatureResolveCount),
                "resolve count must be IDENTICAL at N=1 and N=all — O(layers), not O(features)");

            // Selection must be unchanged and non-vacuous — guards against a tooth that passes by resolving
            // nothing (tooth-membership-is-not-coverage).
            bool expectedOne = StringPathMatchesAfrica(layer.Features[0]);
            int expectedAll = 0;
            foreach (IFeature f in layer.Features) if (StringPathMatchesAfrica(f)) expectedAll++;

            Assert.That(oneFeatureSelected, Is.EqualTo(expectedOne ? 1 : 0));
            Assert.That(allFeaturesSelected, Is.EqualTo(expectedAll));
            Assert.That(expectedAll, Is.EqualTo(54),
                "precondition: CONTINENT==Africa is pinned at 54 features elsewhere (PropertyFilterTests) — " +
                "if this drifts, the fixture changed underneath both tests, not just this one");
        }

        // ── T2: hoisted (int-key) and string-lookup selection still agree ───────────────────────────

        [TestCase("[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]")]
        [TestCase("[\"has\",\"NAME\"]")]
        [TestCase("[\"==\",\"NAME\",\"Aruba\"]")] // legacy form — also routes through the constant-key node
        public void Hoisted_And_StringPath_SelectFeatures_AgreeOnSelectedOrdinals(string filterJson)
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");

            StyleLayer styleLayer = MakeCountriesLayer(filterJson);

            var hoistedInto = new List<SelectedTileFeature>();
            var stringPathInto = new List<SelectedTileFeature>();
            // ITileLayer overload: probes IIndexedFeatureSource, binds the hoisted int-key path.
            FeatureSelector.SelectFeatures(styleLayer, (ITileLayer)layer, 0.0, hoistedInto);
            // Features-list overload: no ITileLayer to probe, so always evaluates by name (see its own doc).
            FeatureSelector.SelectFeatures(styleLayer, (IReadOnlyList<IFeature>)layer.Features, 0.0, stringPathInto);

            Assert.That(stringPathInto.Count, Is.GreaterThan(0), "precondition: filter must select something");
            Assert.That(hoistedInto.Count, Is.EqualTo(stringPathInto.Count),
                $"the hoisted int-key path and the string-lookup path must select the same COUNT for " +
                $"filter {filterJson}");

            var stringPathOrdinals = new HashSet<int>();
            foreach (var s in stringPathInto) stringPathOrdinals.Add(s.Ordinal);
            var hoistedOrdinals = new HashSet<int>();
            foreach (var s in hoistedInto) hoistedOrdinals.Add(s.Ordinal);
            Assert.That(hoistedOrdinals.SetEquals(stringPathOrdinals),
                $"the hoisted int-key path and the string-lookup path must select the SAME ordinals for " +
                $"filter {filterJson}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // NativeTagStorageTests — DensePropertyStore's flattened tag-word buffer, one Allocator.Persistent view per layer
    // ───────────────────────────────────────────────────────────────────────────────────

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
        /// <c>{MvtLayerPropertyResolver, int}</c> — the resolver plus its feature ordinal, nothing more.
        /// The per-feature <c>(offset,count)</c> slice now lives in the layer's native columns (read by
        /// ordinal through <see cref="MvtLayerPropertyResolver.TryGetFeatureSlice"/>), so the store holds no
        /// copy of it — leaner than the earlier two-int shape. Still no <c>uint[]</c> (the pre-flatten
        /// representation) and no <c>NativeArray&lt;uint&gt;</c> (a 48 B per-store handle in the Editor that
        /// would erase the whole stage's win across ~495 stores/tile). Only an exact field-set assertion
        /// catches a bloated design — a looser "no uint[]" check would pass it.
        /// </summary>
        [Test]
        public void Store_HasExactFieldSet_ResolverAndOrdinal()
        {
            FieldInfo[] instanceFields = typeof(DensePropertyStore).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Type[] actualFieldTypes = instanceFields.Select(f => f.FieldType)
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            Type[] expectedFieldTypes = new[] { typeof(MvtLayerPropertyResolver), typeof(int) }
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expectedFieldTypes, actualFieldTypes,
                "DensePropertyStore's instance fields must be EXACTLY {MvtLayerPropertyResolver, int} — the " +
                "resolver plus its ordinal; the (offset,count) slice lives in the layer's columns, not here. " +
                "No uint[] and no NativeArray<uint> (a 48 B per-store handle that erases the win over ~495 stores/tile).");
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

        /// <summary>The leak guard for the per-feature (offset,count) columns the native-column decoupling
        /// added: <see cref="MvtLayer.Dispose"/> frees both <see cref="MvtLayer.FeatureTagOffsets"/> and
        /// <see cref="MvtLayer.FeatureTagLengths"/> exactly once, and a second Dispose is a no-op.</summary>
        [Test]
        public void DecodedTile_FreesFeatureTagColumns_ExactlyOnce()
        {
            MvtTile tile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture()));
            MvtLayer layer = tile.Layers.First(l => l.FeatureTagOffsets.Length > 0);
            Assert.That(layer.FeatureTagOffsets.IsCreated && layer.FeatureTagLengths.IsCreated, Is.True,
                "precondition: the columns are alive before disposal");

            tile.Dispose();
            Assert.That(layer.FeatureTagOffsets.IsCreated, Is.False,
                "MvtLayer.Dispose must free FeatureTagOffsets.");
            Assert.That(layer.FeatureTagLengths.IsCreated, Is.False,
                "MvtLayer.Dispose must free FeatureTagLengths.");

            Assert.DoesNotThrow(() => tile.Dispose(),
                "a second Dispose must be a no-op (idempotent write-back), not a throw.");
        }

        // ── Tooth #5 — use-after-free bound ────────────────────────────────────────────────────

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

    // ───────────────────────────────────────────────────────────────────────────────────
    // DecodeGeometryFlattenAllocTests — the decode-geometry-flatten allocation tooth, GC.GetTotalMemory calibrated
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class DecodeGeometryFlattenAllocTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        // GC.GetTotalMemory's own noise floor (brief: only trustworthy at >= ~100 KB/op) — the calibration
        // canary must clear this by a wide margin to prove the meter is alive.
        private const long CalibrationFloor = 100_000;

        // Ceiling between the RE-MEASURED green (~49,200 B/tile) and the per-feature-uint[] RED-verify
        // (356,966 B/tile, see the file header): ~101 KB above green, ~207 KB below red — both clear the
        // ~100 KB meter noise floor and the observed run-to-run variance (49,152 B / 49,356 B across
        // repeated green runs on this tree).
        private const long Ceiling = 150_000;

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

        /// <summary>Proves GC.GetTotalMemory is a LIVE meter in this run before the decode tooth below trusts
        /// it — GC.GetAllocatedBytesForCurrentThread is dead in this same Mono runner, and a silently-dead
        /// meter would make the assertion below vacuous.</summary>
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
                "the meter is dead in this run and the tooth below cannot be trusted.");
            GC.KeepAlive(block);
        }

        /// <summary>Bytes/decode over a warmed loop, guarded against a Gen0 collection firing inside the
        /// measurement window. The decoded tile is disposed INSIDE the loop: its layers mint
        /// Allocator.Persistent native buffers that GC.GetTotalMemory cannot see, but leaking them across N
        /// iterations still costs real process memory, so each iteration must clean up after itself.</summary>
        private static long BytesPerDecode(byte[] bytes, int iterations)
        {
            // Warm-up: JIT compilation and any one-shot first-touch allocation must not land in the window.
            for (int w = 0; w < 3; w++)
            {
                var warm = MvtDecoder.Decode(FixtureTileId, bytes);
                warm.Dispose();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);

            for (int i = 0; i < iterations; i++)
            {
                var tile = MvtDecoder.Decode(FixtureTileId, bytes);
                tile.Dispose();
            }

            long after = GC.GetTotalMemory(false);
            int collectionsAfter = GC.CollectionCount(0);

            Assert.AreEqual(collectionsBefore, collectionsAfter,
                "a Gen0 collection fired inside the measurement window — the byte delta is unreliable here; " +
                "this indicates a flaky run, not a decode result.");

            return (after - before) / iterations;
        }

        /// <summary>
        /// The flatten's headline tooth: MvtDecoder.Decode's per-feature geometry uint[] was the single
        /// biggest decode allocation site (see the file header for the green/RED-verify figures).
        /// RED-verify by reinstating an equivalent per-feature uint[] copy of the flattened commands inside
        /// MvtDecoder.DecodeLayer, before the materializer runs.
        /// </summary>
        [Test]
        public void Decode_SampleTile_AllocatesUnderCeilingPerTile()
        {
            byte[] bytes = LoadFixture();

            long bytesPerDecode = BytesPerDecode(bytes, iterations: 20);
            TestContext.WriteLine($"MEASURE bytesPerDecode={bytesPerDecode}");

            Assert.LessOrEqual(bytesPerDecode, Ceiling,
                $"MvtDecoder.Decode allocated {bytesPerDecode} B/tile on sample-tile.bytes — must stay under " +
                $"the {Ceiling} B ceiling. 2a removes the per-feature geometry uint[] (the largest single " +
                "site); the residual is tags/Value-table/husks/strings, left to a later retention-pooling stage.");
        }
    }
}
