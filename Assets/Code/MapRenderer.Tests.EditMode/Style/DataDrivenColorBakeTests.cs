// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S12 / S60 — data-driven paint evaluation over the real fixture: <see cref="StyleProperty{T}"/>
    /// resolved once per decoded MVT feature, which is what <c>StyledFillTileBuilder</c> and
    /// <c>StyledLineTileBuilder</c> do per feature while meshing a tile.
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
    public class DataDrivenColorBakeTests
    {

        /// <summary>IR C1 P3: the address the committed fixture is decoded at (its buffers are stamped with
        /// it). z0/0/0 — the fixture's own tile.</summary>
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
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
                $"sample-tile.bytes not found. Tried walking up from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static MvtLayer LoadCountries()
        {
            var layer = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
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

        /// <summary>
        /// One evaluation per feature, mirroring the tile builders' inner loop. A failed evaluation
        /// (expression error, wrong type) yields <paramref name="fallback"/> for that feature.
        /// </summary>
        private static List<Color> BakeColors(
            StyleProperty<Color> prop, double zoom, IEnumerable<IFeature> features, Color fallback = default)
        {
            var result = new List<Color>();
            foreach (var feature in features)
                result.Add(prop.TryEvaluate(zoom, feature, out Color c) ? c : fallback);
            return result;
        }

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        private static HashSet<(int r, int g, int b)> DistinctRgb(List<Color> colors)
        {
            var distinct = new HashSet<(int r, int g, int b)>();
            foreach (var c in colors)
                distinct.Add(
                    ((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)));
            return distinct;
        }

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

            List<Color> colors = BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count,
                "BakeColors must return one color per input feature.");

            var distinct = DistinctRgb(colors);

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

            List<Color> colors = BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count, "BakeColors must return one color per feature.");

            var distinct = DistinctRgb(colors);

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

            List<Color> colors = BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count);

            Color expected = colors[0];
            for (int i = 1; i < colors.Count; i++)
                Assert.AreEqual(expected, colors[i],
                    $"Constant-kind evaluator must produce the same color for every feature (index {i} differs).");
        }
    }
}
