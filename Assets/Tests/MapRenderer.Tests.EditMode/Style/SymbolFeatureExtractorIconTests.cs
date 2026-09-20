// Unity EditMode only. It drives SymbolFeatureExtractor.Extract, which since tile-geometry IR B4
// materializes a Waist-1 TileGeometryBuffers and therefore depends on Unity.Collections — so this file left
// Tools/core-tests (no coverage lost, only iteration speed) and must not be re-added to core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// I3: <see cref="SymbolFeatureExtractor.Extract"/>'s icon path — a supplied
    /// <see cref="SpriteAtlasView"/> resolves <c>icon-image</c> per feature and lays out an
    /// <see cref="SymbolQuad"/>-carrying <see cref="SymbolStyle.SymbolFeature"/> (<c>Kind == Icon</c>)
    /// independently of the existing text path (<c>Kind == Text</c>, unchanged). Mirrors
    /// <c>SymbolFeatureExtractorTests</c>'s hand-encoded MultiPoint pattern. Unity EditMode only (see
    /// file header) — it drives a Waist-1 <c>TileGeometryBuffers</c> extraction.
    /// </summary>
    [TestFixture]
    public class SymbolFeatureExtractorIconTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        // Walk-up fixture loader — mirrors SymbolFeatureExtractorTests.LoadFixture.
        private static string LoadSpriteJson()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sprites", "sample-sprite.json");
                    if (File.Exists(p)) return File.ReadAllText(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-sprite.json not found walking up from cwd/AppContext.");
        }

        private static readonly int2 SheetSize = new int2(64, 64);

        private static SpriteAtlasView LoadAtlas()
            => new SpriteAtlasView { Index = SpriteIndex.Parse(LoadSpriteJson()), Size = SheetSize };

        // Fixture sprites (mirrors Assets/Fixtures/sprites/sample-sprite.json — pinned independently in
        // IconQuadLayoutTests too, so a fixture-file edit breaks both, loudly).
        private static readonly SpriteEntry MarkerEntry = new SpriteEntry { X = 0, Y = 0, Width = 16, Height = 16, PixelRatio = 1f, Sdf = false };
        private static readonly SpriteEntry StarEntry = new SpriteEntry { X = 16, Y = 0, Width = 24, Height = 24, PixelRatio = 2f, Sdf = false };

        private const uint Extent = 4096;
        private static readonly TileId TileId0 = new TileId { Z = 1, X = 0, Y = 0 };

        /// <summary>Protobuf zigzag ENcode — mirrors SymbolFeatureExtractorTests.ZigZagEncode.</summary>
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        /// <summary>Hand-encodes a single-point MVT geometry — mirrors SymbolFeatureExtractorTests.MultiPointGeometry
        /// specialized to one point.</summary>
        private static uint[] SinglePointGeometry(double2 p)
            => new uint[]
            {
                1u | (1u << 3),                 // MoveTo, count=1
                ZigZagEncode((long)p.x),
                ZigZagEncode((long)p.y),
            };

        private static SymbolStyle.StyleLayer PointLayer(string layoutJson)
            => new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                Paint = SymbolStyle.PaintProperties.Parse(null),
                Layout = SymbolStyle.LayoutProperties.Parse(JsonParser.Parse(layoutJson)),
            };

        private static IDecodedTile OnePointTile(double2 point)
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Point,
                Geometry = SinglePointGeometry(point),
            };
            return TestDecodedTiles.Of("points", TileId0, new List<IFeature> { feature }, Extent);
        }

        [Test]
        public void Extract_IconOnlyFeature_YieldsOneIconWithExactQuad()
        {
            // PRIMARY tooth (RED-verified): a feature with NO text-field but a resolvable icon-image must
            // still emit a symbol — the pre-I3 extractor would have skipped it entirely on "text==null".
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"icon-image\":\"star\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(1, symbols.Count, "an icon-only feature must yield exactly one (icon) symbol");
            SymbolStyle.SymbolFeature symbol = symbols[0];
            Assert.AreEqual(SymbolKind.Icon, symbol.Kind);
            Assert.IsNull(symbol.Text, "an icon symbol carries no text");

            SymbolQuad expected = IconQuadLayout.Layout(StarEntry, SheetSize, 1f, TextAnchor.Center, float2.zero);
            AssertQuadEqual(expected, symbol.IconQuad);
        }

        [Test]
        public void Extract_UnknownSpriteName_YieldsZeroIcons_NoThrow()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"does-not-exist\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            Assert.DoesNotThrow(() =>
                SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas));

            Assert.AreEqual(1, symbols.Count, "the text symbol still resolves");
            Assert.AreEqual(SymbolKind.Text, symbols[0].Kind, "an unresolvable sprite name must not emit an icon symbol");
        }

        [Test]
        public void Extract_TextAndIcon_YieldsTwoSymbols_IconFirstThenText()
        {
            // §10 D8/D10 (road-shields, road-shields-design.md — supersedes D5): a feature resolving BOTH a
            // text and an icon is ONE placement instance. The icon (collision owner) is emitted first, the
            // text rides as its Rider — ONE placement instance downstream (SymbolPairing / StagePointPair), so
            // neither half's overlap flags are forced anymore; both carry their AUTHORED
            // text-allow-overlap/text-ignore-placement (default false, unset here).
            // NOTE (P-A): this layer leaves every anchor/offset at its default, i.e. the halves are CENTRED —
            // the retired conjuncts were all inert at that value, so this test does NOT discriminate the P-A
            // predicate. SymbolPairPredicateTests carries the teeth that do.
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"marker\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(2, symbols.Count, "a feature with both text-field and icon-image yields two symbols");
            Assert.AreEqual(SymbolKind.Icon, symbols[0].Kind, "the icon (pair owner) is emitted first");
            Assert.AreEqual(0, symbols[0].FeatureIndex);
            Assert.IsFalse(symbols[0].AllowOverlap); Assert.IsFalse(symbols[0].IgnorePlacement);
            Assert.AreEqual(SymbolKind.Text, symbols[1].Kind, "the rider text is emitted second");
            Assert.AreEqual("L", symbols[1].Text);
            Assert.AreEqual(1, symbols[1].FeatureIndex, "the text symbol continues the SAME ordinal sequence");
            Assert.IsFalse(symbols[1].AllowOverlap, "the D5 forcing is retired — the rider carries its AUTHORED flag (default false)");
            Assert.IsFalse(symbols[1].IgnorePlacement, "the D5 forcing is retired — the rider carries its AUTHORED flag (default false)");
            // §10 D10: the pair is stamped Owner/Rider sharing one PairId (the owner's own FeatureIndex).
            Assert.AreEqual(MapRenderer.Core.Text.SymbolPairRole.Owner, symbols[0].PairRole);
            Assert.AreEqual(MapRenderer.Core.Text.SymbolPairRole.Rider, symbols[1].PairRole);
            Assert.AreEqual(symbols[0].FeatureIndex, symbols[0].PairId);
            Assert.AreEqual(symbols[0].PairId, symbols[1].PairId);
            // Both symbols share the same anchor (same point).
            Assert.AreEqual(symbols[0].AnchorRender.x, symbols[1].AnchorRender.x, 1e-9);
            Assert.AreEqual(symbols[0].AnchorRender.y, symbols[1].AnchorRender.y, 1e-9);
            Assert.AreEqual(symbols[0].AnchorRender.z, symbols[1].AnchorRender.z, 1e-9);
        }

        [Test]
        public void Extract_IconSizeOffsetAnchor_AreForwardedToLayout()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer(
                "{\"icon-image\":\"marker\",\"icon-size\":2,\"icon-offset\":[2,0],\"icon-anchor\":\"top-left\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(1, symbols.Count);
            SymbolQuad expected = IconQuadLayout.Layout(MarkerEntry, SheetSize, 2f, TextAnchor.TopLeft, new float2(2f, 0f));
            AssertQuadEqual(expected, symbols[0].IconQuad);
        }

        [Test]
        public void Extract_TextOnly_NullAtlas_YieldsZeroIcons_ByteIdenticalToPreI3()
        {
            var tile = OnePointTile(new double2(100, 200));
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"marker\"}");

            // 5-arg (pre-I3) call and the explicit 6-arg call with spriteAtlas: null must agree exactly.
            var preI3Style = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), preI3Style);

            var explicitNull = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), explicitNull, null);

            Assert.AreEqual(1, preI3Style.Count, "a null atlas never emits an icon symbol, even with icon-image set");
            Assert.AreEqual(1, explicitNull.Count);
            Assert.AreEqual(SymbolKind.Text, preI3Style[0].Kind);
            Assert.AreEqual(preI3Style[0].Text, explicitNull[0].Text);
            Assert.AreEqual(preI3Style[0].FeatureIndex, explicitNull[0].FeatureIndex);
            Assert.AreEqual(preI3Style[0].PaddingPx, explicitNull[0].PaddingPx, 1e-9);
        }

        [Test]
        public void Extract_AtlasPresent_TextOnlyLayer_NoIconImage_YieldsZeroIcons()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\"}"); // no icon-image at all

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(1, symbols.Count, "an atlas being present doesn't manufacture icons the layer never asked for");
            Assert.AreEqual(SymbolKind.Text, symbols[0].Kind);
        }

        private static void AssertQuadEqual(in SymbolQuad expected, in SymbolQuad actual)
        {
            const float eps = 1e-5f;
            Assert.AreEqual(expected.TopLeft.x, actual.TopLeft.x, eps, "TopLeft.x");
            Assert.AreEqual(expected.TopLeft.y, actual.TopLeft.y, eps, "TopLeft.y");
            Assert.AreEqual(expected.BottomRight.x, actual.BottomRight.x, eps, "BottomRight.x");
            Assert.AreEqual(expected.BottomRight.y, actual.BottomRight.y, eps, "BottomRight.y");
            Assert.AreEqual(expected.UvTopLeft.x, actual.UvTopLeft.x, eps, "UvTopLeft.x");
            Assert.AreEqual(expected.UvTopLeft.y, actual.UvTopLeft.y, eps, "UvTopLeft.y");
            Assert.AreEqual(expected.UvBottomRight.x, actual.UvBottomRight.x, eps, "UvBottomRight.x");
            Assert.AreEqual(expected.UvBottomRight.y, actual.UvBottomRight.y, eps, "UvBottomRight.y");
        }
    }
}
