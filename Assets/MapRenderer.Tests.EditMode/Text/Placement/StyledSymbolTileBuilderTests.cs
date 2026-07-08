// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests (StyledSymbolTileBuilder
// and GlyphManager are engine-free despite living under MapRenderer.Unity/Text). Do NOT add UnityEngine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using Sym = MapRenderer.Core.Style.Symbol;
using MapRenderer.Tests; // TestGlyphSource

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S105 Slice 3 (A4) — THE decisive test: a parsed symbol layer + the real fixture tile, run through
    /// <see cref="StyledSymbolTileBuilder"/> (NOT <c>SyntheticLabelSource</c>), produces the expected set of
    /// shaped <see cref="LabelInstance"/>s. Real style + real tile → correct labels, no synthetic stand-in.
    /// </summary>
    [TestFixture]
    public class StyledSymbolTileBuilderTests
    {
        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, Path.Combine(relative));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("fixture not found: " + Path.Combine(relative));
        }

        private const string FontName = "LatinFont";
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static GlyphManager BuildGlyphManager()
        {
            byte[] latin = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latin };
            return new GlyphManager(TestGlyphSource.FromRanges(ranges));
        }

        private static Sym.StyleLayer CentroidsLayer()
            => new Sym.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                LayoutJson = JsonParser.Parse(
                    "{\"text-field\":\"{NAME}\",\"text-size\":16,\"text-font\":[\"" + FontName + "\"]}"),
            };

        [Test]
        public async Task Build_CentroidsLayer_ShapesRealLabels_NoSyntheticSource()
        {
            MvtTile tile = MvtDecoder.Decode(LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var projection = new WebMercatorProjection();
            Sym.StyleLayer layer = CentroidsLayer();

            // Independent extractor pass (Slice 2) gives the ground-truth text/anchor/ordinal per label.
            var extracted = new List<Sym.SymbolLabel>();
            Sym.SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, 0.0, projection, extracted);

            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var labels = new List<LabelInstance>();
            await builder.BuildAsync(tile, FixtureTile, new[] { layer }, 0.0, projection, labels);

            // (a) one LabelInstance per extracted label, in the same order (FeatureIndex tiebreak preserved).
            Assert.AreEqual(extracted.Count, labels.Count, "one shaped LabelInstance per extracted point label");
            Assert.AreEqual(248, labels.Count, "fixture pin: 248 centroids resolve a non-empty NAME");

            for (int i = 0; i < labels.Count; i++)
            {
                Assert.AreEqual(extracted[i].AnchorRender, labels[i].AnchorRender, $"anchor preserved at {i}");
                Assert.AreEqual(extracted[i].FeatureIndex, labels[i].FeatureIndex, $"ordinal preserved at {i}");
                Assert.AreEqual(extracted[i].TileKey, labels[i].TileKey, $"tile key preserved at {i}");
                Assert.AreEqual(16f, labels[i].TextSizePx, 1e-6, $"text-size 16 at {i}");
                Assert.AreEqual(2f, labels[i].PaddingPx, 1e-6, $"text-padding default 2 at {i}");
                Assert.IsNotNull(labels[i].Layout, $"label {i} must be shaped (non-null Layout)");

                // Every pure-ASCII, space-free name shapes to exactly one glyph quad per character (all
                // present in the Latin fixture) — a strong tooth that shaping is REAL, not stubbed empty.
                string t = extracted[i].Text;
                if (IsAsciiNoSpace(t))
                    Assert.AreEqual(t.Length, labels[i].Layout.Quads.Count,
                        $"'{t}' must shape to {t.Length} glyph quads");
            }

            // (b) The specific named feature — Aruba — is the first, shaped to 5 glyphs, at its A3 anchor.
            Assert.AreEqual("Aruba", extracted[0].Text, "feature[0] is Aruba");
            Assert.AreEqual(5, labels[0].Layout.Quads.Count, "Aruba → 5 glyph quads");

            // (c) At least two OTHER named labels match (so a single hard-coded label cannot pass).
            AssertNamedLabel(extracted, labels, "Afghanistan", 11);
            AssertNamedLabel(extracted, labels, "Angola", 6);
        }

        private static void AssertNamedLabel(List<Sym.SymbolLabel> extracted, List<LabelInstance> labels,
            string name, int expectedQuads)
        {
            int idx = extracted.FindIndex(e => e.Text == name);
            Assert.Greater(idx, -1, $"fixture must contain '{name}'");
            Assert.AreEqual(expectedQuads, labels[idx].Layout.Quads.Count, $"'{name}' → {expectedQuads} glyph quads");
        }

        private static bool IsAsciiNoSpace(string s)
        {
            foreach (char c in s)
                if (c <= 32 || c > 126) return false;
            return true;
        }
    }
}
