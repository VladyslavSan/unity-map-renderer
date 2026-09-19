// Unity EditMode only — exercises the Burst-compiled NativeFilterEvaluationJob (NativeArray/FixedList).
// Not registered in core-tests.csproj: this stage is Burst/Collections, not engine-free.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// The native filter-VM prototype's acceptance teeth: a byte-identical parity oracle against every
    /// liberty filter the VM's op subset covers, zero-managed-lookup on the VM entry, refusal correctness,
    /// and the synthetic error-model tooth the liberty corpus can't exercise on its own (it never triggers
    /// a VM error — see <see cref="All_NonBooleanArg_ExcludesViaNonBooleanError_AndManagedAlsoExcludes"/>).
    /// </summary>
    [TestFixture]
    public class NativeFilterVmTests
    {
        private static MvtTile _tile;

        /// <summary><c>sample-tile.bytes</c>'s one layer ('countries') carries none of
        /// liberty's real match get-keys (<c>class</c>/<c>brunnel</c>), so tooth 1's sweep over it alone
        /// never exercises <c>InStringSet</c>'s membership SCAN for those keys — only the Null/absent-
        /// default case. This second real fixture (croatia-dalmatia, chosen over the fixture corpus for
        /// carrying the most <c>transportation</c> features: 33, with 3 carrying <c>brunnel</c>) is what
        /// closes that gap; see the per-key floors below. (Its 9 <c>ramp</c>-carrying features don't help
        /// here — <c>ramp</c> is excluded from the floors, see the tooth's own doc for why.)</summary>
        private static MvtTile _croatiaTile;

        private static List<JsonValue> _coveredLibertyFilters;
        private static List<string> _refusedLibertyLayerIds;
        private static int _totalFilteredLibertyLayers;

        [OneTimeSetUp]
        public void SetUp()
        {
            _tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            _croatiaTile = MvtDecoder.Decode(new TileId { Z = 9, X = 279, Y = 187 }, File.ReadAllBytes(
                Path.Combine(Application.dataPath, "Fixtures", "water-real-croatia-dalmatia-9-279-187.pbf.bytes")));
            _coveredLibertyFilters = CollectCoveredLibertyFilters();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _tile.Dispose();
            _croatiaTile.Dispose();
        }

        /// <summary>True iff <paramref name="node"/> is exactly <c>["get", <paramref name="key"/>]</c>.</summary>
        private static bool IsGetKey(JsonValue node, string key)
        {
            if (node == null || !node.IsArray) return false;
            IReadOnlyList<JsonValue> items = node.Items;
            return items.Count >= 2 && items[0].AsString() == "get" && items[1].AsString() == key;
        }

        /// <summary>True iff <paramref name="node"/> contains a <c>["match", ["get", <paramref name="key"/>],
        /// ...]</c> sub-tree anywhere — deliberately NARROWER than "uses this key at all": only a
        /// <c>match</c> input compiles to <c>InStringSet</c> (a bare <c>==</c>/<c>!=</c> against the same key
        /// compiles to a different, non-scanning op). Gating the per-key non-vacuity counters on this walk
        /// — not on key presence alone — is what R4 (review) requires: presence over-claims "the membership
        /// scan ran" for a key whose filters are all equality forms.</summary>
        private static bool FilterUsesGetKeyViaMatch(JsonValue node, string key)
        {
            if (node == null || !node.IsArray) return false;
            IReadOnlyList<JsonValue> items = node.Items;
            if (items.Count >= 2 && items[0].AsString() == "match" && IsGetKey(items[1], key))
                return true;
            foreach (JsonValue item in items)
                if (FilterUsesGetKeyViaMatch(item, key)) return true;
            return false;
        }

        /// <summary>Compiles every filtered liberty.json layer, splitting it into the covered filter JSON
        /// (returned, for the parity/refusal teeth below) and the refused layer ids (recorded into
        /// <see cref="_refusedLibertyLayerIds"/>, empty when coverage is complete) plus the total filtered
        /// layer count (<see cref="_totalFilteredLibertyLayers"/>) — a bare count can regress by coincidence
        /// (a filter gained AND one lost still sums to the same total), so the coverage pin below asserts
        /// the refused set by identity, not just its size.</summary>
        private static List<JsonValue> CollectCoveredLibertyFilters()
        {
            var covered = new List<JsonValue>();
            var refused = new List<string>();
            int total = 0;
            foreach (StyleLayer layer in SymbolTestFixtures.LibertyDoc().Layers)
            {
                if (layer.Filter == null) continue;
                total++;
                if (NativeFilterCompiler.TryCompile(layer.Filter, out _))
                    covered.Add(layer.Filter);
                else
                    refused.Add(layer.Id);
            }
            _refusedLibertyLayerIds = refused;
            _totalFilteredLibertyLayers = total;
            return covered;
        }

        // ── coverage pin ────────────────────────────────────────────────────────────────────────

        /// <summary>Pins the measured fast-path coverage (design doc §3): all 105 of liberty.json's
        /// filtered layers compile end-to-end through the accepted op subset (103 before <c>road_link</c>/
        /// <c>road_link_casing</c> gained the compact <c>InStringSet</c> membership path; 93
        /// before <c>has</c> and ordered comparisons were added; 44 before <c>match</c>). The last 2
        /// (<c>road_link</c>/<c>road_link_casing</c>) were previously refused: their old desugaring
        /// (<c>!all(input != a, …)</c>, which re-emits the input <c>get</c> per label) overflowed the VM's
        /// bounded op-count for their 10+ labels; <c>InStringSet</c> compiles them in O(1) ops regardless of
        /// label count. Nothing is left uncovered — asserted by the refused set being empty (a bare count
        /// cannot tell "nothing refused" apart from "one gained, a different one lost").</summary>
        [Test]
        public void CoveredLibertyFilters_Count_Is105()
        {
            Assert.That(_totalFilteredLibertyLayers, Is.EqualTo(105), "precondition: liberty.json's filtered layer count");
            Assert.That(_refusedLibertyLayerIds, Is.Empty,
                "refused layer(s): " + string.Join(", ", _refusedLibertyLayerIds));
            Assert.That(_coveredLibertyFilters.Count, Is.EqualTo(105));
        }

        // ── tooth 1 — parity oracle ─────────────────────────────────────────────────────────────

        /// <summary>
        /// For every covered liberty filter and every feature of every layer of BOTH fixture tiles: the
        /// native VM's include/exclude decision must equal managed <see cref="CompiledFilter.Matches"/>,
        /// bit for bit. Filters are pure functions of the feature, so applying a filter to features from a
        /// differently-named layer is legitimate and widens coverage.
        ///
        /// <para><b>The per-key non-vacuity floors.</b> <c>sample-tile.bytes</c>'s one
        /// layer ('countries') carries none of liberty's real match get-keys (<c>class</c>/<c>brunnel</c>),
        /// so a sweep over it alone never exercises <c>InStringSet</c>'s membership SCAN for those keys —
        /// only the Null/absent-default case (real coverage, but not of the scan itself). The added
        /// <c>_croatiaTile</c> pass is what closes that gap, and the two per-KEY counts below are what
        /// proves it closed rather than merely widening an already-vacuous aggregate: an aggregate floor is
        /// satisfied by <c>class</c> alone (dense on every layer) and would stay green with <c>brunnel</c>
        /// at zero — an oracle is vacuous when the fixture lacks the keys its filters read.
        ///
        /// <para><b><c>ramp</c> is deliberately excluded</b> — not an uncovered gap, an out-of-remit key.
        /// All 18 liberty filters reading it are numeric equality (<c>==</c>/<c>!=</c> against the literal
        /// <c>1</c>); none is a <c>match</c> form (verified: walked every one), so <c>ramp</c> can never
        /// drive <c>InStringSet</c>'s membership scan — the exact mechanism this tooth exists to close the
        /// gap on. A floor on it would be unachievable by construction; re-adding it here would be re-
        /// introducing the vacuous-aggregate shape this edit exists to avoid, just renamed per-key.</para>
        ///
        /// One gap remains and is accepted, not closed here: croatia's <c>brunnel</c> key is present on its
        /// <c>transportation</c> layer only (its <c>waterway</c>/<c>water</c> layers don't carry it) —
        /// liberty's other 4 <c>waterway</c>/<c>water</c>-sourced <c>brunnel</c> filters (of 62 total; the
        /// split is <c>transportation</c> 58 / <c>waterway</c> 3 / <c>water</c> 1) stay uncovered. Croatia
        /// covers the 58. Also accepted: croatia's 3 <c>brunnel</c> features are all <c>tunnel</c>, so the
        /// corpus's only <c>brunnel=bridge</c> case (in <c>water-8-135-80.pbf.bytes</c>) leaves the
        /// match-true "bridge" arm untaken here — recorded rather than adding a third fixture decode.</para>
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForEveryCoveredFilter_EveryFeature_EveryLayer()
        {
            int comparisons = 0;
            int classNonNull = 0, brunnelNonNull = 0;
            MvtTile[] tiles = { _tile, _croatiaTile }; // hoisted out of the per-filter loop (NIT, review)

            foreach (JsonValue filterJson in _coveredLibertyFilters)
            {
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                    "a filter collected as covered must still compile");
                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                bool usesClass = FilterUsesGetKeyViaMatch(filterJson, "class");
                bool usesBrunnel = FilterUsesGetKeyViaMatch(filterJson, "brunnel");

                foreach (MvtTile tile in tiles)
                foreach (MvtLayer layer in tile.Layers)
                {
                    if (layer.DenseKeyResolver == null || layer.Features.Count == 0) continue;
                    // Through the production seam (INativeFilterSource), exactly as FeatureSelector binds it
                    // — the ≥2-id literal-string rebind refusal is a null return here too.
                    INativeFeatureMatcher matcher = ((INativeFilterSource)layer).TryBindNativeFilter(program);
                    if (matcher == null) continue; // duplicate value-string in this layer: legitimately stays managed

                    try
                    {
                        NativeArray<byte> results = matcher.MatchAll();
                        for (int fi = 0; fi < layer.Features.Count; fi++)
                        {
                            bool nativeMatched = results[fi] != 0;
                            IFeature feature = layer.Features[fi];
                            bool managedMatched = managed.Matches(feature, 0.0);
                            comparisons++;
                            Assert.That(nativeMatched, Is.EqualTo(managedMatched),
                                $"layer '{layer.Name}' feature[{fi}]: native/managed disagree for {filterJson}");

                            // Non-vacuity: whether THIS (filter, feature) pair actually drives InStringSet's
                            // membership SCAN — get resolves to a real String, the type both class and
                            // brunnel carry as match/InStringSet inputs.
                            if (usesClass && feature.TryGetProperty("class", out Value cv) && cv.Type == ValueType.String) classNonNull++;
                            if (usesBrunnel && feature.TryGetProperty("brunnel", out Value bv) && bv.Type == ValueType.String) brunnelNonNull++;
                        }
                    }
                    finally
                    {
                        matcher.Dispose();
                    }
                }
            }

            // Migrated from the pre-batching per-feature-evaluator sweep onto MatchAll — the comparison
            // count did not drop: measured 2026-09-05, this exact two-tile corpus, 133350 (was floored at a
            // stale `> 10000`). Floored above half its measured value (a halved count must fail) and
            // comfortably below it (ordinary variation must not flake) — the same rule the per-key floors
            // below already use: 90000 is 67.5% of 133350, safely above the 66675 half-point.
            Assert.That(comparisons, Is.GreaterThan(90000),
                "precondition: the oracle must exercise many (filter, layer, feature) combinations — " +
                "too few and a shallow/broken VM could pass this test vacuously (measured 133350)");

            // Per-key floors, each asserted separately (never summed — an aggregate is satisfied by `class`
            // alone), and each gated on MATCH-FORM participation (FilterUsesGetKeyViaMatch), not mere key
            // presence — R4 (review): a presence-only gate over-claims "the membership scan ran" for a key
            // whose filters are all equality forms (this is exactly what caught `ramp`, and would have let
            // an all-equality `brunnel` corpus pass a presence-based floor vacuously). Measured 2026-09-05
            // over croatia-dalmatia + sample-tile, this exact corpus: class=27864, brunnel=57. Each floor
            // sits above half its measured value (a halved count must fail) and comfortably below it
            // (ordinary variation must not flake): class > 20000 (13932 fails), brunnel > 40 (28 fails).
            Assert.That(classNonNull, Is.GreaterThan(20000),
                "class: InStringSet's membership scan must run densely via match forms (measured 27864)");
            Assert.That(brunnelNonNull, Is.GreaterThan(40),
                "brunnel: InStringSet's membership scan must run via match forms for the croatia " +
                "transportation features carrying this key (measured 57)");
        }

        // ── tooth 1b — explicit match parity (membership + negated) ───────────────────────────────

        /// <summary>
        /// Hand-built <c>match</c> parity teeth, membership and negated, over a real fixture layer —
        /// explicit and self-contained alongside the corpus sweep (tooth 1), which also exercises the 57
        /// covered filters (of all 105) containing a <c>match</c> node anywhere that liberty.json
        /// contributes automatically (against the fixture's one layer, whose <c>get</c> keys don't overlap
        /// liberty's match inputs — see tooth 1's caveat — so THIS tooth is what exercises a real String
        /// <c>get</c> result against real labels). Each case is required to select a proper non-empty subset
        /// of 'countries', or the guard is unsatisfiable.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltMatch_MembershipAndNegated()
        {
            MvtLayer countries = _tile.GetLayer("countries");
            Assert.IsNotNull(countries, "precondition: fixture has a 'countries' layer");

            var names = new List<string>();
            foreach (MvtFeature f in countries.Features)
            {
                if (((IFeature)f).TryGetProperty("NAME", out Value v) && v.Type == ValueType.String
                    && !names.Contains(v.AsString()))
                {
                    names.Add(v.AsString());
                    if (names.Count == 2) break;
                }
            }
            Assert.That(names.Count, Is.EqualTo(2), "precondition: fixture 'countries' needs 2+ distinct NAMEs");

            AssertMatchParity(BuildMatchFilter("NAME", names, member: true), countries);
            AssertMatchParity(BuildMatchFilter("NAME", names, member: false), countries);

            // Scalar (non-array) label — exercises TryEmitMatch's non-IsArray label normalization (now
            // feeding the compact InStringSet path for a get-input string label), which the liberty corpus never
            // hits (all 78 match nodes carry array labels), so a wrong scalar handling would otherwise ship
            // untested.
            AssertMatchParity(BuildScalarMatchFilter("NAME", names[0], member: true), countries);
        }

        /// <summary>Builds <c>["match",["get",key],[labels...],output,default]</c> with complementary
        /// boolean outputs — <paramref name="member"/> selects the membership (labels → true) or negated
        /// (labels → false) form.</summary>
        private static JsonValue BuildMatchFilter(string key, IReadOnlyList<string> labels, bool member)
        {
            var labelItems = new List<JsonValue>();
            foreach (string label in labels) labelItems.Add(JsonValue.OfString(label));
            return JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
                JsonValue.OfArray(labelItems),
                JsonValue.OfBool(member),
                JsonValue.OfBool(!member),
            });
        }

        /// <summary>Builds <c>["match",["get",key],label,output,default]</c> with a SCALAR (non-array)
        /// label — the shape whose single-label branch <see cref="BuildMatchFilter"/>'s array form does not
        /// cover.</summary>
        private static JsonValue BuildScalarMatchFilter(string key, string label, bool member)
            => JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
                JsonValue.OfString(label),
                JsonValue.OfBool(member),
                JsonValue.OfBool(!member),
            });

        // ── tooth 1c — explicit `has` parity ───────────────────────────────────────────────────

        /// <summary>
        /// Hand-built <c>["has","class"]</c> over a synthetic 2-feature layer where <c>class</c> is present
        /// on feature 0 and absent on feature 1 — a proper-subset presence shape the real fixture cannot
        /// supply (every property in the sample tile is present on ALL or NONE of a layer's features, by
        /// inspection). VM must agree with managed <c>FeatureKeyExpression(isHas:true)</c> on both.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltHas()
        {
            MvtLayer layer = BuildHasProperSubsetLayer(
                out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
                out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths);
            try
            {
                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("has"),
                    JsonValue.OfString("class"),
                });
                AssertMatchParity(filter, layer);
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>
        /// <c>["has","class"]</c> where feature 0's <c>class</c> tag points at a value that decoded to
        /// <see cref="MvtValueNative.Null"/> (an empty / unknown-field Value sub-message — legal protobuf a
        /// non-conformant tile can carry, which <c>MvtDecoder.DecodeValue</c> stores as <c>Null</c>). Managed
        /// <c>has</c> reports the key PRESENT (<c>TryGetByKeyIndex</c> returns found the moment the tag pair
        /// exists, whatever the value's type); the VM must agree — presence is tag existence, not
        /// <c>value.Type != Null</c>. RED-verifies against the pre-fix <c>ReadTag(k).Type != Null</c>, which
        /// reported this present-Null key as absent, diverging from managed. (get/Equal do NOT share this — both
        /// sides read Null for a present-Null value, so they never disagreed.)
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltHas_PresentButNullValue()
        {
            var keys = new List<string> { "class", "other" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0, ["other"] = 1 };
            string[] valueStrings = System.Array.Empty<string>();
            // The single value in the table is Null-typed — the present-but-Null case.
            var values = new NativeArray<MvtValueNative>(new[] { MvtValueNative.Null }, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0, 1, 0 }, Allocator.Persistent); // f0:(class,val0=Null); f1:(other,val0)
            var tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 2, 2 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_has_null" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                // has(class): PRESENT on feature 0 (tag exists, value is Null) — managed AND native must
                // select exactly feature 0 (proper subset), not exclude it.
                AssertMatchParity(JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("has"), JsonValue.OfString("class"),
                }), layer);
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>Builds a 2-feature layer (mirroring
        /// <c>FeatureSelectorNativeFilterTests.Rebind_RefusesWhenALiteralMatchesTwoOrMoreValueStrings</c>'s
        /// synthetic-resolver construction) where key <c>class</c> resolves to a value for feature 0's tag
        /// slice; feature 1 carries a tag for the DIFFERENT key <c>other</c> instead (not an empty slice) —
        /// so the absent case exercises <c>ReadTag</c>'s backward scan finding no matching key index, the
        /// real-MVT shape, rather than short-circuiting on a zero-length slice. The proper-subset presence
        /// shape <c>has</c>'s parity tooth needs. Out parameters are the backing native arrays; caller
        /// disposes them.</summary>
        private static MvtLayer BuildHasProperSubsetLayer(
            out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
            out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths)
        {
            var keys = new List<string> { "class", "other" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0, ["other"] = 1 };
            string[] valueStrings = { "residential" };
            values = new NativeArray<MvtValueNative>(new[] { MvtValueNative.String(0) }, Allocator.Persistent);
            tagWords = new NativeArray<uint>(new uint[] { 0, 0, 1, 0 }, Allocator.Persistent); // f0: (class,val0); f1: (other,val0)
            tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            tagLengths = new NativeArray<int>(new[] { 2, 2 }, Allocator.Persistent); // one pair each

            var resolver = new MvtLayerPropertyResolver(
                keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

            var layer = new MvtLayer { Name = "synthetic_has" };
            layer.Features.Add(new MvtFeature
                { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
            layer.Features.Add(new MvtFeature
                { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
            layer.DenseKeyResolver = resolver;
            return layer;
        }

        // ── tooth 1e' — many-label InStringSet evaluation: real membership scan + absent-label sentinel ──

        /// <summary>
        /// A hand-built 15-label <c>match</c> (the <c>road_link</c>-shaped count that forced
        /// <c>InStringSet</c>'s existence — see <see cref="TryCompile_AcceptsManyLabelMatch_ViaInStringSet"/>)
        /// evaluated over a synthetic layer whose <c>class</c> values actually exercise the scan: the
        /// liberty corpus never does this — its match filters key on properties (<c>class</c>/<c>brunnel</c>/
        /// <c>network</c>) absent from the fixture tile's only layer, so every corpus/oracle evaluation of a
        /// real match filter sees a Null <c>get</c> and never enters <c>InStringSet</c>'s scan
        /// (see <see cref="Vm_AgreesWithManagedCompiledFilter_ForEveryCoveredFilter_EveryFeature_EveryLayer"/>'s
        /// doc). This tooth is the sole one exercising: a real String input matched at the first label, mid
        /// list, and the last label (contiguous <c>Binding[start..start+count)</c> scan correctness), and a
        /// real String input distinct from every label while 5 of the 15 labels are themselves absent from
        /// this layer's value-strings (rebind to the -1 sentinel, <see cref="NativeFilterRebind.Rebind"/>) —
        /// the case <c>InStringSet</c>'s byte-identity argument leans on hardest.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltManyLabelMatch_MembershipAndNegated()
        {
            MvtLayer layer = BuildManyLabelInStringSetLayer(
                out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
                out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths);
            try
            {
                // Interleave present (matched) and absent labels so BOTH slot 0 and the LAST slot of the
                // emitted range are live, matched labels. A real StringId is never the -1 an absent label
                // rebinds to, so a TRAILING run of absent labels leaves the top scan slots behaviourally
                // dead — truncating the scan loop there would then go unpinned. class9 sits LAST here and is
                // matched by feature 2, so dropping the top slot fails this tooth.
                var labels = new List<string>
                {
                    "class0",                                                             // slot 0  — feature 0
                    "classA", "classB", "classC", "classD", "classE",                     // absent (-1)
                    "class5",                                                             // slot 6  — feature 1
                    "classF", "classG", "classH", "classI", "classJ", "classK", "classL", // absent (-1)
                    "class9",                                                             // slot 14 — feature 2 (LAST)
                };

                AssertMatchParity(BuildMatchFilter("class", labels, member: true), layer);
                AssertMatchParity(BuildMatchFilter("class", labels, member: false), layer);
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        /// <summary>Builds a 4-feature layer, key <c>class</c>, whose value-strings are <c>class0</c>..
        /// <c>class9</c> plus <c>other_value</c> (11 entries, ids 0..10) — features carry <c>class</c> =
        /// the first label (id 0), a mid-list label (id 5), the last label (id 9), and a value outside the
        /// label set entirely (id 10), a proper non-empty subset for the many-label InStringSet tooth above. Out
        /// parameters are the backing native arrays; caller disposes them.</summary>
        private static MvtLayer BuildManyLabelInStringSetLayer(
            out NativeArray<MvtValueNative> values, out NativeArray<uint> tagWords,
            out NativeArray<int> tagOffsets, out NativeArray<int> tagLengths)
        {
            var keys = new List<string> { "class" };
            var keyIndex = new Dictionary<string, int> { ["class"] = 0 };
            var valueStrings = new string[11];
            var valueList = new List<MvtValueNative>();
            for (int i = 0; i < 10; i++) { valueStrings[i] = "class" + i; valueList.Add(MvtValueNative.String(i)); }
            valueStrings[10] = "other_value";
            valueList.Add(MvtValueNative.String(10));
            values = new NativeArray<MvtValueNative>(valueList.ToArray(), Allocator.Persistent);

            // f0: class0 (first label); f1: class5 (mid list); f2: class9 (last label); f3: other_value
            // (outside the label set) — one (class,val) tag pair each.
            tagWords = new NativeArray<uint>(
                new uint[] { 0, 0, 0, 5, 0, 9, 0, 10 }, Allocator.Persistent);
            tagOffsets = new NativeArray<int>(new[] { 0, 2, 4, 6 }, Allocator.Persistent);
            tagLengths = new NativeArray<int>(new[] { 2, 2, 2, 2 }, Allocator.Persistent);

            var resolver = new MvtLayerPropertyResolver(
                keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

            var layer = new MvtLayer { Name = "synthetic_many_label_in_string_set" };
            for (int fi = 0; fi < 4; fi++)
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, fi) });
            layer.DenseKeyResolver = resolver;
            return layer;
        }

        // ── tooth 1d — explicit numeric-compare parity, both directions ───────────────────────────

        /// <summary>Hand-built <c>["&lt;",["get",key],N]</c> and <c>["&gt;",N,["get",key]]</c> (operand
        /// order reversed) over a fixture numeric property spanning <c>N</c> for a proper subset of
        /// features — VM must agree with managed <c>DecisionOps.CompareValues</c> in both operand
        /// orders.</summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForHandBuiltCompare_BothOperandOrders()
        {
            (MvtLayer layer, string key, double threshold) = FindNumericKeySpanningThreshold();
            Assert.IsNotNull(layer,
                "precondition: fixture must have a numeric property spanning some threshold on some layer");

            JsonValue getVsLiteral = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("<"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
                JsonValue.OfNumber(threshold),
            });
            AssertMatchParity(getVsLiteral, layer);

            JsonValue literalVsGet = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString(">"),
                JsonValue.OfNumber(threshold),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString(key) }),
            });
            AssertMatchParity(literalVsGet, layer);
        }

        /// <summary>Scans every fixture layer's features for a Number-typed property whose values span more
        /// than one distinct value, and returns its midpoint as a threshold guaranteed to split them into a
        /// proper non-empty subset on each side.</summary>
        private static (MvtLayer, string, double) FindNumericKeySpanningThreshold()
        {
            foreach (MvtLayer layer in _tile.Layers)
            {
                if (layer.DenseKeyResolver == null || layer.Features.Count < 2) continue;
                var keys = new HashSet<string>();
                foreach (MvtFeature f in layer.Features)
                    foreach (string k in ((IFeature)f).Properties.Keys)
                        keys.Add(k);

                foreach (string key in keys)
                {
                    var numbers = new List<double>();
                    foreach (MvtFeature f in layer.Features)
                        if (((IFeature)f).TryGetProperty(key, out Value v) && v.Type == ValueType.Number)
                            numbers.Add(v.AsNumber());
                    if (numbers.Count < 2) continue;

                    double min = numbers[0], max = numbers[0];
                    foreach (double n in numbers)
                    {
                        if (n < min) min = n;
                        if (n > max) max = n;
                    }
                    if (max <= min) continue;
                    return (layer, key, (min + max) / 2.0);
                }
            }
            return (null, null, 0.0);
        }

        // ── tooth 1e — type-mismatch compare excludes on both sides ───────────────────────────────

        /// <summary>
        /// <c>["&lt;",["get","NAME"],5]</c> over a "countries" feature: <c>NAME</c> is a STRING, so managed
        /// <c>CompareValues</c>'s type gate throws → <c>CompiledFilter.Matches</c> catches → excludes. The
        /// VM must reach the same exclude via <see cref="NativeFilterError.NonComparable"/>, mirroring
        /// <see cref="All_NonBooleanArg_ExcludesViaNonBooleanError_AndManagedAlsoExcludes"/>'s pattern for
        /// the compare op instead of <c>all</c>.
        /// </summary>
        [Test]
        public void Compare_TypeMismatch_ExcludesOnBothSides_ViaNonComparableError()
        {
            MvtLayer countries = _tile.GetLayer("countries");
            Assert.IsNotNull(countries, "precondition: fixture has a 'countries' layer");

            JsonValue filterJson = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("<"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("NAME") }),
                JsonValue.OfNumber(5),
            });
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program));

            IFeature probe = countries.Features[0];
            Assert.IsTrue(probe.TryGetProperty("NAME", out Value nameValue));
            Assert.That(nameValue.Type, Is.EqualTo(ValueType.String),
                "precondition: fixture's first countries feature must have a STRING NAME");

            var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)countries).TryBindNativeFilter(program);
            Assert.IsNotNull(matcher, "precondition: must native-bind over countries");
            try
            {
                NativeArray<byte> results = matcher.MatchAll();
                bool nativeMatched = results[0] != 0;
                NativeFilterError error = (NativeFilterError)matcher.Errors[0];

                Assert.That(error, Is.EqualTo(NativeFilterError.NonComparable));
                Assert.IsFalse(nativeMatched);

                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                Assert.IsFalse(managed.Matches(countries.Features[0], 0.0),
                    "managed side must also exclude via the CompareValues type-mismatch throw");
            }
            finally
            {
                matcher.Dispose();
            }
        }

        /// <summary>Compiles, native-binds, and per-feature-compares <paramref name="filterJson"/> against
        /// managed <see cref="CompiledFilter"/> over <paramref name="layer"/>; fails loudly (rather than
        /// vacuously) if the filter doesn't compile, doesn't bind, or selects an all/none subset.</summary>
        private static void AssertMatchParity(JsonValue filterJson, MvtLayer layer)
        {
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                $"must compile: {filterJson}");
            INativeFeatureMatcher matcher = ((INativeFilterSource)layer).TryBindNativeFilter(program);
            Assert.IsNotNull(matcher, $"must native-bind over '{layer.Name}': {filterJson}");
            try
            {
                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                NativeArray<byte> results = matcher.MatchAll();
                int selected = 0;
                for (int fi = 0; fi < layer.Features.Count; fi++)
                {
                    bool nativeMatched = results[fi] != 0;
                    bool managedMatched = managed.Matches(layer.Features[fi], 0.0);
                    Assert.That(nativeMatched, Is.EqualTo(managedMatched),
                        $"layer '{layer.Name}' feature[{fi}]: native/managed disagree for {filterJson}");
                    if (nativeMatched) selected++;
                }
                Assert.That(selected, Is.GreaterThan(0).And.LessThan(layer.Features.Count),
                    $"precondition: {filterJson} must select a proper non-empty subset of '{layer.Name}'");
            }
            finally
            {
                matcher.Dispose();
            }
        }

        // ── tooth 2 — no managed property-store lookup on the VM entry (structural) ───────────────

        /// <summary>Structural: the Burst job's field surface is entirely blittable — no string-keyed
        /// lookup type could even appear here (a Burst job could not compile one anyway). Together with the
        /// evaluator taking its native inputs through the <c>INativeFilterColumns</c> capability rather than
        /// a concrete <c>IMvtPropertyStore</c> (a type-level fact — it holds no store reference to call),
        /// this IS the "no managed property lookup on the VM entry" guarantee. It supersedes the earlier
        /// call-counting spy, which only worked while the evaluator reached a feature's store — the coupling
        /// this stage removed.</summary>
        [Test]
        public void NativeFilterEvaluationJob_FieldSurface_IsEntirelyBlittable()
        {
            var fields = typeof(NativeFilterEvaluationJob).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (var f in fields)
                Assert.That(f.FieldType, Is.Not.EqualTo(typeof(string)).And.Not.EqualTo(typeof(object)),
                    $"field '{f.Name}' must stay Burst-blittable");
        }

        // ── tooth 3 — refusal correctness ──────────────────────────────────────────────────────

        [Test]
        public void TryCompile_AcceptsAllCoveredLibertyFilters()
        {
            foreach (JsonValue filterJson in _coveredLibertyFilters)
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out _), filterJson.ToString());
        }

        // ── tooth 3b — many-label match compiles via InStringSet (road_link regression) ────────────────

        /// <summary>
        /// A synthetic <c>match</c> with 15 string labels — the shape that overflowed the VM's bounded
        /// op-count under the old <c>!all(input != a, …)</c> desugaring (3 ops per label + 1 <c>AllStep</c>
        /// per label, which liberty's real <c>road_link</c>/<c>road_link_casing</c> hit at 10+ labels). The
        /// compact <c>InStringSet</c> path emits <c>Get; InStringSet; Not</c> regardless of label
        /// count, so this must now compile.
        /// </summary>
        [Test]
        public void TryCompile_AcceptsManyLabelMatch_ViaInStringSet()
        {
            var labels = new List<JsonValue>();
            for (int i = 0; i < 15; i++) labels.Add(JsonValue.OfString("label" + i));
            JsonValue filterJson = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("class") }),
                JsonValue.OfArray(labels),
                JsonValue.OfBool(true),
                JsonValue.OfBool(false),
            });
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                "a 15-label match must compile via the compact InStringSet path");
            Assert.IsNotNull(program);
        }

        /// <summary>
        /// A match with more labels than the literal cap refuses. InStringSet emits O(1) ops regardless of label
        /// count, so <c>MaxOperations</c> no longer bounds a match's label list — the explicit literal cap does, and
        /// this pins it (the per-feature InStringSet loop, the Rebind scan, and the binding allocation must all stay
        /// bounded — "always bound loops"). Uses only a handful of ops, so <c>MaxOperations</c> is NOT what refuses
        /// it: RED-verify by removing the <c>MaxLiterals</c> guard and this compiles.
        /// </summary>
        [Test]
        public void TryCompile_RefusesMatch_WithMoreLabelsThanTheLiteralCap()
        {
            var labels = new List<JsonValue>();
            for (int i = 0; i < 257; i++) labels.Add(JsonValue.OfString("label" + i)); // > MaxLiterals (256)
            JsonValue filterJson = JsonValue.OfArray(new List<JsonValue>
            {
                JsonValue.OfString("match"),
                JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("class") }),
                JsonValue.OfArray(labels),
                JsonValue.OfBool(true),
                JsonValue.OfBool(false),
            });
            Assert.IsFalse(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                "a match with more labels than the literal cap must refuse (bounds the InStringSet loop / Rebind / binding)");
            Assert.IsNull(program);
        }

        // Multi-arm match: two (label, output) pairs — the restricted single-arm shape requires exactly
        // 5 items ["match", input, label, output, default].
        [TestCase("[\"match\",[\"get\",\"class\"],\"a\",true,\"b\",false,true]", TestName = "Refuses_Match_MultiArm")]
        // Non-boolean output/default: match-widening covers only the boolean-output membership shape.
        [TestCase("[\"match\",[\"get\",\"class\"],[\"x\"],1,0]", TestName = "Refuses_Match_NonBooleanOutput")]
        // Non-complementary booleans: output and default must be exact opposites for the desugar to be
        // a pure membership test.
        [TestCase("[\"match\",[\"get\",\"class\"],[\"x\"],true,true]", TestName = "Refuses_Match_NonComplementaryBooleans")]
        // Computed input: only "get"/"geometry-type" inputs are accepted.
        [TestCase("[\"match\",[\"+\",1,1],[\"x\"],true,false]", TestName = "Refuses_Match_ComputedInput")]
        [TestCase("[\"in\",[\"get\",\"class\"],[\"literal\",[\"a\",\"b\"]]]", TestName = "Refuses_In")]
        [TestCase("[\"has\",[\"get\",\"x\"]]", TestName = "Refuses_Has_DynamicKey")]
        [TestCase("[\"has\",\"a\",\"b\"]", TestName = "Refuses_Has_TwoArg")]
        [TestCase("[\"<\",[\"get\",\"a\"],[\"get\",\"b\"]]", TestName = "Refuses_LessThan_TwoGet")]
        [TestCase("[\"<\",[\"get\",\"a\"],\"str\"]", TestName = "Refuses_LessThan_GetVsString")]
        // Literal-vs-literal must be NESTED under an expression-dialect parent (here "!"), same reasoning
        // as Refuses_NestedLiteralEqualsLiteral below: a bare root ["<",1,2] is legacy dialect (both
        // operands scalar, exactly 3 elements — see FilterDialect.IsExpressionFilter) and gets refused by
        // LegacyFilterTranslator before ever reaching TryEmitCompare's gate.
        [TestCase("[\"!\",[\"<\",1,2]]", TestName = "Refuses_LessThan_LiteralVsLiteral")]
        [TestCase("[\"get\",\"class\"]", TestName = "Refuses_NonBooleanRoot")]
        [TestCase("[\"==\",[\"geometry-type\"],[\"get\",\"x\"]]", TestName = "Refuses_GeometryTypeAgainstDynamicKey")]
        // The two id-vs-byte string-equality shapes: literal==literal (two absent literals both rebind to
        // the -1 sentinel → VM equal, managed unequal) and get==get (dup value-strings at distinct ids →
        // VM unequal, managed equal). Byte-identity holds only for get/geometry-type vs a string literal.
        // The literal==literal case must be NESTED under an expression-dialect parent (here "!"): a bare
        // root ["==","foo","bar"] is legacy dialect and LegacyFilterTranslator rewrites it to the safe
        // ["==",["get","foo"],"bar"] before compilation — translation never reaches under "!".
        [TestCase("[\"!\",[\"==\",\"foo\",\"bar\"]]", TestName = "Refuses_NestedLiteralEqualsLiteral")]
        [TestCase("[\"==\",[\"get\",\"a\"],[\"get\",\"b\"]]", TestName = "Refuses_GetEqualsGet")]
        public void TryCompile_RefusesUnsupportedFilters(string json)
        {
            Assert.IsFalse(NativeFilterCompiler.TryCompile(JsonParser.Parse(json), out NativeFilterProgram program));
            Assert.IsNull(program);
        }

        // ── tooth 4 — error model (synthetic; the liberty corpus never errors) ─────────────────

        /// <summary>
        /// <c>["all",["get","NAME"]]</c> over a "countries" feature: <c>NAME</c> is a non-empty string, so
        /// managed <c>all</c>'s <c>AsBool()</c> throws → <c>CompiledFilter.Matches</c> catches → excludes.
        /// The VM must reach the same exclude via <see cref="NativeFilterError.NonBoolean"/>, not silently
        /// — this is the tooth nothing else in the suite exercises (the liberty corpus never errors).
        /// </summary>
        [Test]
        public void All_NonBooleanArg_ExcludesViaNonBooleanError_AndManagedAlsoExcludes()
        {
            JsonValue filterJson = JsonParser.Parse("[\"all\",[\"get\",\"NAME\"]]");
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program));

            MvtLayer layer = _tile.GetLayer("countries");
            IFeature probe = layer.Features[0];
            Assert.IsTrue(probe.TryGetProperty("NAME", out Value nameValue));
            Assert.That(nameValue.Type, Is.EqualTo(ValueType.String));
            Assert.That(nameValue.AsString().Length, Is.GreaterThan(0),
                "precondition: fixture's first countries feature must have a non-empty NAME");

            var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
            Assert.IsNotNull(matcher, "precondition: must native-bind over countries");
            try
            {
                NativeArray<byte> results = matcher.MatchAll();
                bool nativeMatched = results[0] != 0;
                NativeFilterError error = (NativeFilterError)matcher.Errors[0];

                Assert.That(error, Is.EqualTo(NativeFilterError.NonBoolean));
                Assert.IsFalse(nativeMatched);

                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                Assert.IsFalse(managed.Matches(layer.Features[0], 0.0),
                    "managed side must also exclude via the AsBool error");
            }
            finally
            {
                matcher.Dispose();
            }
        }

        // ── tooth 5 — batched Execute: error independence across features (synthetic, mandatory) ──

        /// <summary>
        /// The liberty corpus never errors (see tooth 4's doc), so no corpus sweep can observe whether a
        /// batched <c>Execute</c> lets one feature's error bleed into the next. Feature 0 forces
        /// <see cref="NativeFilterError.NonComparable"/> (a <c>get</c> resolving to a String compared with
        /// <c>&lt;</c> against a number literal); feature 1 has a genuinely Numeric value that must match.
        /// If <c>Execute</c> failed to re-initialise its error code per feature, feature 0's error would
        /// still be set when feature 1 runs, halting it at <c>programCounter</c> 0 and excluding it wrongly.
        /// </summary>
        [Test]
        public void MatchAll_ErrorOnOneFeature_DoesNotExcludeTheNext()
        {
            var keys = new List<string> { "key" };
            var keyIndex = new Dictionary<string, int> { ["key"] = 0 };
            string[] valueStrings = { "notANumber" };
            // val0 = String("notANumber") — feature 0's compare errors; val1 = Number(2.0) — feature 1's
            // compare (< 5) genuinely matches.
            var values = new NativeArray<MvtValueNative>(
                new[] { MvtValueNative.String(0), MvtValueNative.Number(2.0) }, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0, 0, 1 }, Allocator.Persistent); // f0:(key,val0); f1:(key,val1)
            var tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 2, 2 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_error_independence" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("<"),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("key") }),
                    JsonValue.OfNumber(5),
                });
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program));

                var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
                Assert.IsNotNull(matcher, "precondition: must native-bind");
                try
                {
                    NativeArray<byte> results = matcher.MatchAll();
                    Assert.That((NativeFilterError)matcher.Errors[0], Is.EqualTo(NativeFilterError.NonComparable),
                        "precondition: feature 0 must error (String compared with <)");
                    Assert.That(results[1], Is.Not.EqualTo((byte)0),
                        "feature 1 must be INCLUDED — feature 0's error must not bleed into feature 1's evaluation");
                    Assert.That((NativeFilterError)matcher.Errors[1], Is.EqualTo(NativeFilterError.None));
                }
                finally
                {
                    matcher.Dispose();
                }
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        // ── tooth 6 — batched Execute: operand stack re-initialises per feature (synthetic) ──────

        /// <summary>
        /// <c>["all",["get","a"],["get","b"]]</c> over 2 features: feature 0's <c>a</c> is <c>false</c>, so
        /// its <c>all</c> short-circuits at the FIRST <c>AllStep</c> without ever reading <c>b</c> — the
        /// simplest way to leave the operand stack non-empty going into the next feature if it isn't
        /// re-initialised per feature. Feature 1 has both <c>a</c> and <c>b</c> true, so it runs the program
        /// to completion. If <c>Execute</c> failed to re-initialise its stack per feature, feature 1 would
        /// start with feature 0's leftover element still on it, its end-of-program check
        /// (<c>stack.Length == 1</c>) would see length 2, and it would wrongly report
        /// <see cref="NativeFilterError.StackOverflow"/> instead of matching.
        /// </summary>
        [Test]
        public void MatchAll_StackResidue_DoesNotBleedIntoNextFeature()
        {
            var keys = new List<string> { "a", "b" };
            var keyIndex = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1 };
            string[] valueStrings = System.Array.Empty<string>();
            var values = new NativeArray<MvtValueNative>(
                new[] { MvtValueNative.Bool(false), MvtValueNative.Bool(true) }, Allocator.Persistent);
            // f0: (a,val0=false) — one pair, short-circuits before reading b.
            // f1: (a,val1=true),(b,val1=true) — two pairs, runs to completion.
            var tagWords = new NativeArray<uint>(new uint[] { 0, 0, 0, 1, 1, 1 }, Allocator.Persistent);
            var tagOffsets = new NativeArray<int>(new[] { 0, 2 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 2, 4 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_stack_residue" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Unknown, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("all"),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("a") }),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("get"), JsonValue.OfString("b") }),
                });
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program));

                var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
                Assert.IsNotNull(matcher, "precondition: must native-bind");
                try
                {
                    NativeArray<byte> results = matcher.MatchAll();
                    Assert.That(results[0], Is.EqualTo((byte)0), "precondition: feature 0 (a=false) excludes");
                    Assert.That(results[1], Is.Not.EqualTo((byte)0),
                        "feature 1 (a=true, b=true) must match — feature 0's short-circuit residue must not " +
                        "corrupt feature 1's operand stack");
                    Assert.That((NativeFilterError)matcher.Errors[1], Is.EqualTo(NativeFilterError.None));
                }
                finally
                {
                    matcher.Dispose();
                }
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }

        // ── tooth 7 — batched Execute: geometry kind read per feature (synthetic on both halves) ──

        /// <summary>
        /// P10: liberty.json emits zero <c>$type</c>/<c>geometry-type</c> filters, so no corpus sweep can
        /// ever exercise <see cref="NativeOperation.GeometryEqual"/> — both the filter AND the fixture must
        /// be hand-built. <c>["==",["geometry-type"],"Polygon"]</c> over a layer whose feature 0 is a Point
        /// and feature 1 is a Polygon: if the batched <c>Execute</c> read the geometry-kind column at a
        /// fixed index instead of per feature, both features would evaluate against the SAME kind and stop
        /// disagreeing.
        /// </summary>
        [Test]
        public void MatchAll_GeometryKind_IsReadPerFeature()
        {
            var keys = new List<string>();
            var keyIndex = new Dictionary<string, int>();
            string[] valueStrings = System.Array.Empty<string>();
            var values = new NativeArray<MvtValueNative>(0, Allocator.Persistent);
            var tagWords = new NativeArray<uint>(0, Allocator.Persistent);
            var tagOffsets = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var tagLengths = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            try
            {
                var resolver = new MvtLayerPropertyResolver(
                    keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);
                var layer = new MvtLayer { Name = "synthetic_geometry_kind" };
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Point, Store = new DensePropertyStore(resolver, 0) });
                layer.Features.Add(new MvtFeature
                    { GeometryType = TileGeometryType.Polygon, Store = new DensePropertyStore(resolver, 1) });
                layer.DenseKeyResolver = resolver;

                JsonValue filter = JsonValue.OfArray(new List<JsonValue>
                {
                    JsonValue.OfString("=="),
                    JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("geometry-type") }),
                    JsonValue.OfString("Polygon"),
                });
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filter, out NativeFilterProgram program));

                var matcher = (MvtNativeFeatureMatcher)((INativeFilterSource)layer).TryBindNativeFilter(program);
                Assert.IsNotNull(matcher, "precondition: must native-bind");
                try
                {
                    NativeArray<byte> results = matcher.MatchAll();
                    Assert.That(results[0], Is.EqualTo((byte)0), "feature 0 is a Point: must exclude");
                    Assert.That(results[1], Is.Not.EqualTo((byte)0), "feature 1 is a Polygon: must include");
                }
                finally
                {
                    matcher.Dispose();
                }
            }
            finally
            {
                values.Dispose();
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
            }
        }
    }
}
