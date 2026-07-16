// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S12 / S60 — <see cref="FeatureColorBaker"/>: bake per-feature colors from the real fixture.
    /// Updated for S60: <c>DataDrivenPaintEvaluator</c> is replaced by <c>StyleProperty&lt;T&gt;</c>.
    ///
    /// The fixture (Assets/Fixtures/sample-tile.bytes) contains the "countries" layer with 239 polygon
    /// features and a "CONTINENT" string property (confirmed: 8 distinct values, including "Asia" and
    /// "South America").
    ///
    /// Teeth:
    ///   1. Distinct-color tooth: a match expression on CONTINENT produces ≥2 distinct colors across
    ///      the 239 baked features (robust against fixture ordering).
    ///   2. Constant-input control: an expression that resolves identically for all features (match on
    ///      a non-existent key → default) produces exactly 1 distinct color across all features.
    ///   3. Constant-kind evaluator: bake with a constant expression → all colors identical.
    /// </summary>
    [TestFixture]
    public class FeatureColorBakerTests
    {
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
                $"sample-tile.bytes not found. Tried walking up from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static MvtLayer LoadCountries()
        {
            var layer = MvtDecoder.Decode(LoadFixture()).GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present in fixture.");
            return layer;
        }

        private static List<IFeature> AdaptFeatures(MvtLayer layer)
        {
            var features = new List<IFeature>(layer.Features.Count);
            foreach (var f in layer.Features)
                features.Add(f); // A6: MvtFeature implements IFeature directly — no adapter
            return features;
        }

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        private const string MatchExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        // ── 1. Distinct-color tooth ───────────────────────────────────────────

        [Test]
        public void BakeColors_DistinctContinent_ProducesDistinctColors()
        {
            var layer    = LoadCountries();
            var features = AdaptFeatures(layer);
            var prop     = ColProp(MatchExpr);

            List<Color> colors = FeatureColorBaker.BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count,
                "BakeColors must return one color per input feature.");

            var distinct = new HashSet<(int r, int g, int b)>();
            foreach (var c in colors)
                distinct.Add(
                    ((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)));

            Assert.GreaterOrEqual(distinct.Count, 2,
                $"A match expression on CONTINENT must produce ≥2 distinct colors (got {distinct.Count}).");
        }

        // ── 2. Constant-input control → exactly 1 distinct color ─────────────

        [Test]
        public void BakeColors_ConstantControl_ProducesUniformColor()
        {
            const string controlExpr =
                "[\"match\",[\"get\",\"__NONEXISTENT_KEY__\"]," +
                "\"x\",[\"rgba\",255,0,0,1]," +
                "[\"rgba\",77,77,77,1]]";

            var layer    = LoadCountries();
            var features = AdaptFeatures(layer);
            var prop     = ColProp(controlExpr);

            List<Color> colors = FeatureColorBaker.BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count, "BakeColors must return one color per feature.");

            var distinct = new HashSet<(int r, int g, int b)>();
            foreach (var c in colors)
                distinct.Add(
                    ((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)));

            Assert.AreEqual(1, distinct.Count,
                $"Constant-input control must produce exactly 1 distinct color (got {distinct.Count}).");
        }

        // ── 3. Constant-kind evaluator → all colors identical ────────────────

        [Test]
        public void BakeColors_ConstantEvaluator_AllIdentical()
        {
            var layer    = LoadCountries();
            var features = AdaptFeatures(layer);
            var prop     = ColProp("\"#3a8f3a\"");

            List<Color> colors = FeatureColorBaker.BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count);

            Color expected = colors[0];
            for (int i = 1; i < colors.Count; i++)
                Assert.AreEqual(expected, colors[i],
                    $"Constant-kind evaluator must produce the same color for every feature (index {i} differs).");
        }
    }
}
