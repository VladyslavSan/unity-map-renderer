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
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
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

        private static SymbolStyle.StyleLayer CentroidsLayer(string extraLayoutJson = "")
            => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                LayoutJson = JsonParser.Parse(
                    "{\"text-field\":\"{NAME}\",\"text-size\":16,\"text-font\":[\"" + FontName + "\"]" + extraLayoutJson + "}"),
            };

        [Test]
        public async Task Build_CentroidsLayer_ShapesRealLabels_NoSyntheticSource()
        {
            MvtTile tile = MvtDecoder.Decode(LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var projection = new WebMercatorProjection();
            SymbolStyle.StyleLayer layer = CentroidsLayer();

            // Independent extractor pass (Slice 2) gives the ground-truth text/anchor/ordinal per label.
            var extracted = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, 0.0, projection, extracted);

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

        // ── Slice A: the layout-options wiring is LIVE through the builder (guards StyledSymbolTileBuilder's
        //    TextLayoutOptions.Default -> s.LayoutOptions switch — NOT just the Extract/TextQuadLayout seams,
        //    which the engine tests already cover and which stay green even if line 105 is reverted). ──

        private static async Task<List<LabelInstance>> BuildLabels(SymbolStyle.StyleLayer layer, GlyphManager manager)
        {
            MvtTile tile = MvtDecoder.Decode(LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var builder = new StyledSymbolTileBuilder(manager);
            var labels = new List<LabelInstance>();
            await builder.BuildAsync(tile, FixtureTile, new[] { layer }, 0.0, new WebMercatorProjection(), labels);
            return labels;
        }

        [Test]
        public async Task Build_TextOffset_ShiftsEveryQuad_ByEmsTimes24_YDownFlippedToYUp()
        {
            using var manager = BuildGlyphManager();

            List<LabelInstance> baseline = await BuildLabels(CentroidsLayer(), manager);
            // text-offset [1,2] ems in MapLibre's y-DOWN convention.
            List<LabelInstance> shifted = await BuildLabels(CentroidsLayer(",\"text-offset\":[1,2]"), manager);

            Assert.AreEqual(baseline.Count, shifted.Count, "same label set");
            Assert.Greater(baseline.Count, 0, "sanity: fixture yields labels");

            // Center anchor in both (default), so the anchor term cancels and the per-quad delta isolates the
            // offset. ems -> baked px is x24; the y is NEGATED (y-down text-offset -> y-up layout). So every
            // quad shifts by exactly (1*24, -2*24) = (24, -48). A revert of line 105 to Default makes the
            // "shifted" build ignore text-offset -> delta 0 -> this fails. It also pins the y-flip sign.
            var expected = new float2(24f, -48f);
            IReadOnlyList<SymbolQuad> baseQuads = baseline[0].Layout.Quads;   // Aruba
            IReadOnlyList<SymbolQuad> shiftQuads = shifted[0].Layout.Quads;
            Assert.AreEqual(baseQuads.Count, shiftQuads.Count);
            Assert.Greater(baseQuads.Count, 0, "Aruba must shape to >0 quads");
            for (int i = 0; i < baseQuads.Count; i++)
            {
                Assert.AreEqual(expected.x, shiftQuads[i].TopLeft.x - baseQuads[i].TopLeft.x, 1e-3f, $"quad {i} TopLeft.x");
                Assert.AreEqual(expected.y, shiftQuads[i].TopLeft.y - baseQuads[i].TopLeft.y, 1e-3f, $"quad {i} TopLeft.y");
                Assert.AreEqual(expected.x, shiftQuads[i].BottomRight.x - baseQuads[i].BottomRight.x, 1e-3f, $"quad {i} BottomRight.x");
                Assert.AreEqual(expected.y, shiftQuads[i].BottomRight.y - baseQuads[i].BottomRight.y, 1e-3f, $"quad {i} BottomRight.y");
            }
        }

        [Test]
        public async Task Build_TextAnchor_TranslatesBlock_ThroughTheBuilder()
        {
            using var manager = BuildGlyphManager();

            // justify held constant (center) across both so the per-line justify term cancels and the delta
            // isolates the pure anchor translation. Left anchor (hAlign=0) vs Center (hAlign=0.5) pushes the
            // block +x by 0.5*blockWidth, with no vertical change (both vAlign=0.5).
            List<LabelInstance> center = await BuildLabels(CentroidsLayer(",\"text-justify\":\"center\""), manager);
            List<LabelInstance> left = await BuildLabels(CentroidsLayer(",\"text-anchor\":\"left\",\"text-justify\":\"center\""), manager);

            IReadOnlyList<SymbolQuad> centerQuads = center[0].Layout.Quads;   // Aruba, single line
            IReadOnlyList<SymbolQuad> leftQuads = left[0].Layout.Quads;
            Assert.AreEqual(centerQuads.Count, leftQuads.Count);
            Assert.Greater(centerQuads.Count, 0);

            float dx0 = leftQuads[0].TopLeft.x - centerQuads[0].TopLeft.x;
            Assert.Greater(dx0, 0f, "a Left anchor must push the block +x vs Center (anchor is threaded, not dropped)");
            for (int i = 0; i < centerQuads.Count; i++)
            {
                // block-wide translation: same dx for every quad, and no vertical move.
                Assert.AreEqual(dx0, leftQuads[i].TopLeft.x - centerQuads[i].TopLeft.x, 1e-3f, $"quad {i} dx constant");
                Assert.AreEqual(0f, leftQuads[i].TopLeft.y - centerQuads[i].TopLeft.y, 1e-3f, $"quad {i} no vertical move");
            }
        }

        private static void AssertNamedLabel(List<SymbolStyle.SymbolLabel> extracted, List<LabelInstance> labels,
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
