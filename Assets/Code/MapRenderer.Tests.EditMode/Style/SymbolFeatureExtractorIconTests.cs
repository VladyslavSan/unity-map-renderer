// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

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
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// I3: <see cref="SymbolStyle.SymbolFeatureExtractor.Extract"/>'s icon path — a supplied
    /// <see cref="SpriteAtlasView"/> resolves <c>icon-image</c> per feature and lays out an
    /// <see cref="SymbolQuad"/>-carrying <see cref="SymbolStyle.SymbolLabel"/> (<c>Kind == Icon</c>)
    /// independently of the existing text path (<c>Kind == Text</c>, unchanged). Mirrors
    /// <c>SymbolFeatureExtractorTests</c>'s hand-encoded MultiPoint pattern. Engine-free; runs in both
    /// runners.
    /// </summary>
    [TestFixture]
    public class SymbolFeatureExtractorIconTests
    {
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

        private sealed class FixtureDecodedTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            public FixtureDecodedTile(ITileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
        }

        private sealed class FixtureTileLayer : ITileLayer
        {
            public string Name { get; set; }
            public uint Extent { get; set; }
            public IReadOnlyList<ITileFeature> Features { get; set; }
        }

        private static SymbolStyle.StyleLayer PointLayer(string layoutJson)
            => new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                LayoutJson = JsonParser.Parse(layoutJson),
            };

        private static IDecodedTile OnePointTile(double2 point)
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Point,
                Geometry = SinglePointGeometry(point),
            };
            var layer = new FixtureTileLayer { Name = "points", Extent = Extent, Features = new List<ITileFeature> { feature } };
            return new FixtureDecodedTile(layer);
        }

        [Test]
        public void Extract_IconOnlyFeature_YieldsOneIconLabelWithExactQuad()
        {
            // PRIMARY tooth (RED-verified): a feature with NO text-field but a resolvable icon-image must
            // still emit a label — the pre-I3 extractor would have skipped it entirely on "text==null".
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"icon-image\":\"star\"}");

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), labels, atlas);

            Assert.AreEqual(1, labels.Count, "an icon-only feature must yield exactly one (icon) label");
            SymbolStyle.SymbolLabel label = labels[0];
            Assert.AreEqual(LabelKind.Icon, label.Kind);
            Assert.IsNull(label.Text, "an icon label carries no text");

            SymbolQuad expected = IconQuadLayout.Layout(StarEntry, SheetSize, 1f, TextAnchor.Center, float2.zero);
            AssertQuadEqual(expected, label.IconQuad);
        }

        [Test]
        public void Extract_UnknownSpriteName_YieldsZeroIconLabels_NoThrow()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"does-not-exist\"}");

            var labels = new List<SymbolStyle.SymbolLabel>();
            Assert.DoesNotThrow(() =>
                SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), labels, atlas));

            Assert.AreEqual(1, labels.Count, "the text label still resolves");
            Assert.AreEqual(LabelKind.Text, labels[0].Kind, "an unresolvable sprite name must not emit an icon label");
        }

        [Test]
        public void Extract_TextAndIcon_YieldsTwoLabels_CentredPair_IconFirstThenText()
        {
            // §10 D8/D10 (road-shields, road-shields-design.md — supersedes D5): default text-anchor/icon-anchor
            // (both center) + zero offsets/radial-offset is the CENTRED PAIR predicate — this is not
            // shield-specific, it fires for ANY feature whose text sits centred on its icon. The icon
            // (collision owner) is emitted first, the text rides as its Rider — ONE placement instance
            // downstream (LabelPairing / StagePointPair), so neither half's overlap flags are forced anymore;
            // both carry their AUTHORED text-allow-overlap/text-ignore-placement (default false, unset here).
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"marker\"}");

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), labels, atlas);

            Assert.AreEqual(2, labels.Count, "a feature with both text-field and icon-image yields two labels");
            Assert.AreEqual(LabelKind.Icon, labels[0].Kind, "the icon (pair owner) is emitted first");
            Assert.AreEqual(0, labels[0].FeatureIndex);
            Assert.IsFalse(labels[0].AllowOverlap); Assert.IsFalse(labels[0].IgnorePlacement);
            Assert.AreEqual(LabelKind.Text, labels[1].Kind, "the rider text is emitted second");
            Assert.AreEqual("L", labels[1].Text);
            Assert.AreEqual(1, labels[1].FeatureIndex, "the text label continues the SAME ordinal sequence");
            Assert.IsFalse(labels[1].AllowOverlap, "the D5 forcing is retired — the rider carries its AUTHORED flag (default false)");
            Assert.IsFalse(labels[1].IgnorePlacement, "the D5 forcing is retired — the rider carries its AUTHORED flag (default false)");
            // §10 D10: the pair is stamped Owner/Rider sharing one PairId (the owner's own FeatureIndex).
            Assert.AreEqual(MapRenderer.Core.Text.LabelPairRole.Owner, labels[0].PairRole);
            Assert.AreEqual(MapRenderer.Core.Text.LabelPairRole.Rider, labels[1].PairRole);
            Assert.AreEqual(labels[0].FeatureIndex, labels[0].PairId);
            Assert.AreEqual(labels[0].PairId, labels[1].PairId);
            // Both labels share the same anchor (same point).
            Assert.AreEqual(labels[0].AnchorRender.x, labels[1].AnchorRender.x, 1e-9);
            Assert.AreEqual(labels[0].AnchorRender.y, labels[1].AnchorRender.y, 1e-9);
            Assert.AreEqual(labels[0].AnchorRender.z, labels[1].AnchorRender.z, 1e-9);
        }

        [Test]
        public void Extract_IconSizeOffsetAnchor_AreForwardedToLayout()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer(
                "{\"icon-image\":\"marker\",\"icon-size\":2,\"icon-offset\":[2,0],\"icon-anchor\":\"top-left\"}");

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), labels, atlas);

            Assert.AreEqual(1, labels.Count);
            SymbolQuad expected = IconQuadLayout.Layout(MarkerEntry, SheetSize, 2f, TextAnchor.TopLeft, new float2(2f, 0f));
            AssertQuadEqual(expected, labels[0].IconQuad);
        }

        [Test]
        public void Extract_TextOnly_NullAtlas_YieldsZeroIconLabels_ByteIdenticalToPreI3()
        {
            var tile = OnePointTile(new double2(100, 200));
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"marker\"}");

            // 5-arg (pre-I3) call and the explicit 6-arg call with spriteAtlas: null must agree exactly.
            var preI3Style = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), preI3Style);

            var explicitNull = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), explicitNull, null);

            Assert.AreEqual(1, preI3Style.Count, "a null atlas never emits an icon label, even with icon-image set");
            Assert.AreEqual(1, explicitNull.Count);
            Assert.AreEqual(LabelKind.Text, preI3Style[0].Kind);
            Assert.AreEqual(preI3Style[0].Text, explicitNull[0].Text);
            Assert.AreEqual(preI3Style[0].FeatureIndex, explicitNull[0].FeatureIndex);
            Assert.AreEqual(preI3Style[0].PaddingPx, explicitNull[0].PaddingPx, 1e-9);
        }

        [Test]
        public void Extract_AtlasPresent_TextOnlyLayer_NoIconImage_YieldsZeroIconLabels()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\"}"); // no icon-image at all

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), labels, atlas);

            Assert.AreEqual(1, labels.Count, "an atlas being present doesn't manufacture icons the layer never asked for");
            Assert.AreEqual(LabelKind.Text, labels[0].Kind);
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
