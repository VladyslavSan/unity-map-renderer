// Unity EditMode only. It exercises MapRenderer.Jobs.Tiles/.Mvt (the tile-decode seam, moved out of
// Core), which Tools/core-tests does not compile — this file is not registered there.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// <see cref="FeatureSelector.SelectFeatures"/> returns real non-empty subsets when it filters the fixture
    /// tile by feature properties and feature id. Each legacy filter form has a matching expression form.
    /// A negative control ("Nowhere" → 0) shows the filter is selective. Fixture counts come from a probe run.
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

        // ── Legacy == NAME ───────────────────────────────────────────────────────────────────────

        [Test]
        public void Legacy_Eq_Name_Aruba_Returns1()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"NAME\",\"Aruba\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(1), "Legacy filter NAME==Aruba must return exactly 1 feature");
            Assert.IsTrue(
                result[0].Properties.TryGetValue("NAME", out var v) && v.AsString() == "Aruba",
                "Selected feature must be Aruba");
        }

        [Test]
        public void Expression_Eq_Name_Aruba_Returns1()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"get\",\"NAME\"],\"Aruba\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(1), "Expression filter NAME==Aruba must return exactly 1 feature");
        }

        [Test]
        public void Legacy_Eq_Name_Aruba_SameAs_Expression()
        {
            var legacy = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"NAME\",\"Aruba\"]"), _tile);
            var expr = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"get\",\"NAME\"],\"Aruba\"]"), _tile);
            Assert.That(legacy.Count, Is.EqualTo(expr.Count), "Legacy and expression NAME==Aruba must return same count");
        }

        // ── Continent — Africa (54) and Europe (49) ──────────────────────────────────────────────

        [Test]
        public void Legacy_Eq_Continent_Africa_Returns54()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"CONTINENT\",\"Africa\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(54), "Legacy CONTINENT==Africa must return 54 features");
        }

        [Test]
        public void Expression_Eq_Continent_Africa_Returns54()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(54), "Expression CONTINENT==Africa must return 54 features");
        }

        [Test]
        public void Legacy_And_Expression_Continent_Africa_EqualCount()
        {
            var legacy = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"CONTINENT\",\"Africa\"]"), _tile);
            var expr = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"get\",\"CONTINENT\"],\"Africa\"]"), _tile);
            Assert.That(legacy.Count, Is.EqualTo(expr.Count), "Legacy and expression CONTINENT==Africa must agree");
        }

        [Test]
        public void Legacy_Eq_Continent_Europe_Returns49()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"CONTINENT\",\"Europe\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(49), "Legacy CONTINENT==Europe must return 49 features");
        }

        // ── has / !has ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void Legacy_Has_NAME_Returns239()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"has\",\"NAME\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(239), "has NAME must return all 239 features");
        }

        [Test]
        public void Legacy_NotHas_NoSuchKey_Returns239()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"!has\",\"NoSuchKey\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(239), "!has NoSuchKey must return all 239 features");
        }

        // ── $id filter — live ────────────────────────────────────────────────────────────────────

        [Test]
        public void Legacy_Eq_Id_182_Returns1_And_IsAruba()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"$id\",182]"), _tile);
            Assert.That(result.Count, Is.EqualTo(1), "Legacy $id==182 must return exactly 1 feature");
            Assert.IsTrue(
                result[0].Properties.TryGetValue("NAME", out var v) && v.AsString() == "Aruba",
                "Feature with id=182 must be Aruba");
        }

        [Test]
        public void Legacy_Eq_Id_129_Returns1_And_IsAfghanistan()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"$id\",129]"), _tile);
            Assert.That(result.Count, Is.EqualTo(1), "Legacy $id==129 must return exactly 1 feature");
            Assert.IsTrue(
                result[0].Properties.TryGetValue("NAME", out var v) && v.AsString() == "Afghanistan",
                "Feature with id=129 must be Afghanistan");
        }

        [Test]
        public void Expression_Id_182_Returns1()
        {
            // Expression form: ["==",["id"],182]
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"id\"],182]"), _tile);
            Assert.That(result.Count, Is.EqualTo(1), "Expression [id]==182 must return exactly 1 feature");
        }

        [Test]
        public void Legacy_And_Expression_Id_Aruba_EqualCount()
        {
            var legacy = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"$id\",182]"), _tile);
            var expr = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"id\"],182]"), _tile);
            Assert.That(legacy.Count, Is.EqualTo(expr.Count), "Legacy $id and expression [id] must agree");
        }

        // ── Negative control — proves filter is genuinely selective ──────────────────────────────

        [Test]
        public void Legacy_Eq_Name_Nowhere_Returns0()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",\"NAME\",\"Nowhere\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(0), "NAME==Nowhere must return 0 features (no such name in fixture)");
        }

        [Test]
        public void Expression_Eq_Name_Nowhere_Returns0()
        {
            var result = FeatureSelector.SelectFeatures(
                MakeCountriesLayer("[\"==\",[\"get\",\"NAME\"],\"Nowhere\"]"), _tile);
            Assert.That(result.Count, Is.EqualTo(0));
        }
    }
}
