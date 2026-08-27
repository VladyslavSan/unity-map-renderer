// Unity EditMode only — exercises the Burst-compiled NativeFilterEvalJob (NativeArray/FixedList).
// Not registered in core-tests.csproj: this stage is Burst/Collections, not engine-free.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Mvt;
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
        private static List<JsonValue> _coveredLibertyFilters;

        [OneTimeSetUp]
        public void SetUp()
        {
            _tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            _coveredLibertyFilters = CollectCoveredLibertyFilters();
        }

        [OneTimeTearDown]
        public void TearDown() => _tile.Dispose();

        private static List<JsonValue> CollectCoveredLibertyFilters()
        {
            var covered = new List<JsonValue>();
            foreach (StyleLayer layer in SymbolTestFixtures.LibertyDoc().Layers)
            {
                if (layer.Filter == null) continue;
                if (NativeFilterCompiler.TryCompile(layer.Filter, out _))
                    covered.Add(layer.Filter);
            }
            return covered;
        }

        // ── coverage pin ────────────────────────────────────────────────────────────────────────

        /// <summary>Pins the measured fast-path coverage (design doc §3): 93 of liberty.json's 105
        /// filtered layers compile end-to-end through the accepted op subset (44 before match-widening).
        /// The remaining 12 stay managed: 10 use ops outside the subset (<c>has</c>, ordered comparisons),
        /// and 2 (<c>road_link</c>/<c>road_link_casing</c>) are match filters whose desugaring
        /// (<c>!all(input != a, …)</c>, which re-emits the input <c>get</c> per label) exceeds the VM's
        /// bounded op-count — correctly refused, not a bug; a get-once desugaring would fit them (deferred).</summary>
        [Test]
        public void CoveredLibertyFilters_Count_Is93()
        {
            Assert.That(_coveredLibertyFilters.Count, Is.EqualTo(93));
        }

        // ── tooth 1 — parity oracle ─────────────────────────────────────────────────────────────

        /// <summary>
        /// For every covered liberty filter and every feature of every layer in the fixture tile: the
        /// native VM's include/exclude decision must equal managed <see cref="CompiledFilter.Matches"/>,
        /// bit for bit. Filters are pure functions of the feature, so applying a filter to features from a
        /// differently-named layer is legitimate and widens coverage.
        /// </summary>
        [Test]
        public void Vm_AgreesWithManagedCompiledFilter_ForEveryCoveredFilter_EveryFeature_EveryLayer()
        {
            int comparisons = 0;
            using var evaluator = new NativeFilterEvaluator(Allocator.Persistent);

            foreach (JsonValue filterJson in _coveredLibertyFilters)
            {
                Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                    "a filter collected as covered must still compile");
                CompiledFilter managed = CompiledFilter.Compile(filterJson);

                foreach (MvtLayer layer in _tile.Layers)
                {
                    if (layer.DenseKeyResolver == null || layer.Features.Count == 0) continue;
                    if (!program.Rebind(layer.DenseKeyResolver, Allocator.TempJob, out NativeArray<int> binding))
                        continue; // duplicate value-string in this layer: legitimately stays managed

                    try
                    {
                        for (int fi = 0; fi < layer.Features.Count; fi++)
                        {
                            evaluator.Evaluate(program, binding, layer, fi, out bool nativeMatched, out _);
                            bool managedMatched = managed.Matches(layer.Features[fi], 0.0);
                            comparisons++;
                            Assert.That(nativeMatched, Is.EqualTo(managedMatched),
                                $"layer '{layer.Name}' feature[{fi}]: native/managed disagree for {filterJson}");
                        }
                    }
                    finally
                    {
                        binding.Dispose();
                    }
                }
            }

            Assert.That(comparisons, Is.GreaterThan(10000),
                "precondition: the oracle must exercise many (filter, layer, feature) combinations — " +
                "too few and a shallow/broken VM could pass this test vacuously");
        }

        // ── tooth 1b — explicit match parity (membership + negated) ───────────────────────────────

        /// <summary>
        /// Hand-built <c>match</c> parity teeth, membership and negated, over a real fixture layer —
        /// explicit and self-contained alongside the corpus sweep (tooth 1), which also exercises the
        /// 51 match filters liberty.json contributes automatically. Each case is required to select a
        /// proper non-empty subset of 'countries', or the guard is unsatisfiable.
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

            // Scalar (non-array) label — exercises TryEmitMatch's non-IsArray branch, which the liberty
            // corpus never hits (all 78 match nodes carry array labels), so a wrong scalar rewrite would
            // otherwise ship untested.
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

        /// <summary>Compiles, native-binds, and per-feature-compares <paramref name="filterJson"/> against
        /// managed <see cref="CompiledFilter"/> over <paramref name="layer"/>; fails loudly (rather than
        /// vacuously) if the filter doesn't compile, doesn't bind, or selects an all/none subset.</summary>
        private static void AssertMatchParity(JsonValue filterJson, MvtLayer layer)
        {
            Assert.IsTrue(NativeFilterCompiler.TryCompile(filterJson, out NativeFilterProgram program),
                $"must compile: {filterJson}");
            Assert.IsTrue(program.Rebind(layer.DenseKeyResolver, Allocator.TempJob, out NativeArray<int> binding),
                $"must native-bind over '{layer.Name}': {filterJson}");
            try
            {
                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                using var evaluator = new NativeFilterEvaluator(Allocator.Persistent);
                int selected = 0;
                for (int fi = 0; fi < layer.Features.Count; fi++)
                {
                    evaluator.Evaluate(program, binding, layer, fi, out bool nativeMatched, out _);
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
                binding.Dispose();
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
        public void NativeFilterEvalJob_FieldSurface_IsEntirelyBlittable()
        {
            var fields = typeof(NativeFilterEvalJob).GetFields(
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
        [TestCase("[\"has\",\"class\"]", TestName = "Refuses_Has")]
        [TestCase("[\"<\",[\"get\",\"admin_level\"],2]", TestName = "Refuses_LessThan")]
        [TestCase("[\"in\",[\"get\",\"class\"],[\"literal\",[\"a\",\"b\"]]]", TestName = "Refuses_In")]
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

            Assert.IsTrue(program.Rebind(layer.DenseKeyResolver, Allocator.TempJob, out NativeArray<int> binding));
            try
            {
                using var evaluator = new NativeFilterEvaluator(Allocator.Persistent);
                evaluator.Evaluate(program, binding, layer, 0, out bool nativeMatched, out NativeFilterError error);

                Assert.That(error, Is.EqualTo(NativeFilterError.NonBoolean));
                Assert.IsFalse(nativeMatched);

                CompiledFilter managed = CompiledFilter.Compile(filterJson);
                Assert.IsFalse(managed.Matches(layer.Features[0], 0.0),
                    "managed side must also exclude via the AsBool error");
            }
            finally
            {
                binding.Dispose();
            }
        }
    }
}
