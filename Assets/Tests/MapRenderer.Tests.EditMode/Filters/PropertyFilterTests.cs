// Unity EditMode only. It exercises MapRenderer.Unity.Jobs.Tiles/.Mvt (the tile-decode seam, moved out of
// Core), which Tools/core-tests does not compile — this file is not registered there.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using MapRenderer.Core.Expressions;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Jobs.Mvt;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// <see cref="FeatureSelector.SelectFeatures"/> returns real non-empty subsets when it filters the fixture
    /// tile by feature properties and feature id. Each legacy filter form has a matching expression form.
    /// A negative control ("Nowhere" → 0) shows the filter is selective.
    /// </summary>
    [TestFixture]
    public class PropertyFilterTests
    {
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

        private static MvtTile _tile;

        [OneTimeSetUp]
        public void SetUp()
        {
            _tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, LoadFixture());
        }

        private static StyleLayer MakeCountriesLayer(string filterJson)
        {
            return new StyleLayer
            {
                Id          = "test",
                Source      = "fixture",
                SourceLayer = "countries",
                Filter      = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };
        }

        // ── Count-only selections ────────────────────────────────────────────────────────────────

        /// <summary>Each filter form's exact feature count over the fixture: expression forms of name/id
        /// equality, and the negative control (a name that matches nothing, legacy and expression).</summary>
        [Test]
        [TestCase("[\"==\",[\"get\",\"NAME\"],\"Aruba\"]", 1, TestName = "SelectFeatures_ReturnsCount(Expression_Eq_Name_Aruba)")]
        [TestCase("[\"==\",[\"id\"],182]", 1, TestName = "SelectFeatures_ReturnsCount(Expression_Id_182)")]
        [TestCase("[\"==\",\"NAME\",\"Nowhere\"]", 0, TestName = "SelectFeatures_ReturnsCount(Legacy_Eq_Name_Nowhere)")]
        [TestCase("[\"==\",[\"get\",\"NAME\"],\"Nowhere\"]", 0, TestName = "SelectFeatures_ReturnsCount(Expression_Eq_Name_Nowhere)")]
        public void SelectFeatures_ReturnsCount(string filterJson, int expectedCount)
        {
            var result = FeatureSelector.SelectFeatures(MakeCountriesLayer(filterJson), _tile);
            Assert.That(result.Count, Is.EqualTo(expectedCount),
                $"filter {filterJson} must return exactly {expectedCount} feature(s)");
        }

        // ── has / !has on an always-present key ──────────────────────────────────────────────────

        /// <summary>An always-present key's <c>has</c> (and the complementary <c>!has</c> on a key that is
        /// never present) selects every feature in the layer. The expected count is computed independently
        /// per row (features carrying/lacking the named key), not assumed equal to the layer's total.</summary>
        [Test]
        [TestCase("[\"has\",\"NAME\"]", "NAME", true, TestName = "Has_AlwaysPresentKey_ReturnsEveryFeature(Legacy_Has_NAME)")]
        [TestCase("[\"!has\",\"NoSuchKey\"]", "NoSuchKey", false, TestName = "Has_AlwaysPresentKey_ReturnsEveryFeature(Legacy_NotHas_NeverPresentKey)")]
        public void Has_AlwaysPresentKey_ReturnsEveryFeature(string filterJson, string key, bool expectPresent)
        {
            var result = FeatureSelector.SelectFeatures(MakeCountriesLayer(filterJson), _tile);
            var features = _tile.GetLayer("countries").Features;
            int expected = features.Count(f => f.Properties.ContainsKey(key) == expectPresent);

            Assert.That(expected, Is.EqualTo(features.Count),
                $"precondition: ContainsKey('{key}') == {expectPresent} must hold for every feature, or " +
                "this filter does not select the whole layer.");
            Assert.That(result.Count, Is.EqualTo(expected),
                $"filter {filterJson} must select every feature in the layer.");
        }

        // ── Property equality selects a proper subset ────────────────────────────────────────────

        /// <summary>A property-equality filter must be selective on its own — a real subset, neither empty
        /// nor the whole layer — independent of the legacy/expression agreement check below.</summary>
        [Test]
        public void Legacy_Eq_Continent_Europe_ReturnsAProperSelectiveSubset()
        {
            var result = FeatureSelector.SelectFeatures(MakeCountriesLayer("[\"==\",\"CONTINENT\",\"Europe\"]"), _tile);
            int total = _tile.GetLayer("countries").Features.Count;
            Assert.That(result.Count, Is.GreaterThan(0).And.LessThan(total),
                "CONTINENT == Europe must select a real subset, not none and not everything.");
        }

        // ── Count + identity ─────────────────────────────────────────────────────────────────────

        /// <summary>A single-feature selection also names the right feature, not just the right count.</summary>
        [Test]
        [TestCase("[\"==\",\"NAME\",\"Aruba\"]", "Aruba", TestName = "SelectFeatures_ReturnsCountAndIdentity(Legacy_Eq_Name_Aruba)")]
        [TestCase("[\"==\",\"$id\",182]", "Aruba", TestName = "SelectFeatures_ReturnsCountAndIdentity(Legacy_Eq_Id_182)")]
        [TestCase("[\"==\",\"$id\",129]", "Afghanistan", TestName = "SelectFeatures_ReturnsCountAndIdentity(Legacy_Eq_Id_129)")]
        public void SelectFeatures_ReturnsCountAndIdentity(string filterJson, string expectedName)
        {
            var result = FeatureSelector.SelectFeatures(MakeCountriesLayer(filterJson), _tile);
            Assert.That(result.Count, Is.EqualTo(1), $"filter {filterJson} must return exactly 1 feature");
            Assert.IsTrue(
                result[0].Properties.TryGetValue("NAME", out var v) && v.AsString() == expectedName,
                $"the selected feature must be {expectedName}");
        }

        // ── Legacy vs expression agreement ───────────────────────────────────────────────────────

        /// <summary>A legacy filter and its hand-written expression equivalent select the same COUNT (not
        /// necessarily by identical mechanism) over the fixture.</summary>
        [Test]
        [TestCase("[\"==\",\"NAME\",\"Aruba\"]", "[\"==\",[\"get\",\"NAME\"],\"Aruba\"]",
            TestName = "LegacyAndExpression_AgreeOnCount(Name_Aruba)")]
        [TestCase("[\"==\",\"CONTINENT\",\"Africa\"]", "[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]",
            TestName = "LegacyAndExpression_AgreeOnCount(Continent_Africa)")]
        [TestCase("[\"==\",\"$id\",182]", "[\"==\",[\"id\"],182]",
            TestName = "LegacyAndExpression_AgreeOnCount(Id_Aruba)")]
        public void LegacyAndExpression_AgreeOnCount(string legacyJson, string expressionJson)
        {
            var legacy = FeatureSelector.SelectFeatures(MakeCountriesLayer(legacyJson), _tile);
            var expr = FeatureSelector.SelectFeatures(MakeCountriesLayer(expressionJson), _tile);
            Assert.That(legacy.Count, Is.EqualTo(expr.Count),
                $"legacy {legacyJson} and expression {expressionJson} must return the same count");
        }
    }
}
