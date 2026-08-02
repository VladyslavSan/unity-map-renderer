// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// The icon skirt's CARRIER CHAIN, driven end to end from a genuinely padded
    /// <see cref="SpriteAtlasView"/>: <c>SymbolFeatureExtractor</c> (computes
    /// <c>IconQuadLayout.SkirtPx</c>) → <c>SymbolLabel.IconSkirtPx</c> → <see cref="StyledSymbolTileBuilder"/>
    /// → the point label's <c>Layout.Bounds*</c> and the along-line label's <c>CurvedGlyph.CellSkirt</c>.
    ///
    /// <para><b>Why this exists as its own tooth.</b> Every other skirt test calls the two ends directly —
    /// <c>ToLayoutResult(quad, SkirtPx(...))</c> or <c>BuildRotatedGlyph(..., skirt: 3f)</c> — so all of them
    /// stay green against an implementation that never computes the skirt during extraction, drops one of
    /// the <c>IconSkirtPx</c> assignments, or emits <c>CellSkirt = 0</c>. The render snapshots cannot see it
    /// either: they draw the PADDED quad, which is unchanged by a lost skirt. Only the collision footprint
    /// moves, and only a test that starts at extraction can observe that.</para>
    ///
    /// <para>The atlas is built by running the real <c>SpriteSheetPadder</c> over a raw parsed index rather
    /// than by hand-setting <c>Padding</c>, so the entries under test are the ones production would bind.</para>
    /// </summary>
    [TestFixture]
    public class IconSkirtCarrierChainTests
    {
        private const int Padding = 1;
        private const int SpriteExtent = 16;
        private const float IconSize = 2f;
        private static readonly int2 SourceSheetSize = new int2(64, 64);
        private static readonly TileId TileId0 = new TileId { Z = 1, X = 0, Y = 0 };
        private const uint Extent = 4096;

        /// <summary>Two 16×16 abutting sprites — the real sheet's shape — run through the production planner.</summary>
        private static SpritePadPlan PaddedPlan() => SpriteSheetPadder.Plan(
            SpriteIndex.Parse(
                "{\"marker\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"arrow\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}"),
            SourceSheetSize, Padding);

        private static SpriteAtlasView PaddedAtlas(SpritePadPlan plan)
            => new SpriteAtlasView { Index = plan.Index, Size = plan.Size };

        /// <summary>The same sprite as it would be WITHOUT the repack — the reference the collision footprint
        /// must still equal. Only Width/Height/PixelRatio reach the box (X/Y are UV-only), so this is the
        /// content-rect entry, byte-identical to a raw parse.</summary>
        private static readonly SpriteEntry BareEntry = new SpriteEntry
        {
            X = 0, Y = 0, Width = SpriteExtent, Height = SpriteExtent, PixelRatio = 1f,
        };

        // ── The point path: Layout.Bounds must be the UNPADDED content box ────────────────────────────

        [Test]
        public async Task PointIcon_ThroughRealExtraction_CollidesOnTheContentBox_NotThePaddedQuad()
        {
            SpritePadPlan plan = PaddedPlan();
            SpriteAtlasView atlas = PaddedAtlas(plan);
            Assert.IsTrue(atlas.Index.TryGetSprite("marker", out SpriteEntry padded));
            Assert.AreEqual(Padding, padded.Padding,
                "precondition: the planner must actually have padded this sprite, or the tooth is vacuous");

            var labels = new List<LabelInstance>();
            using (GlyphManager manager = IconOnlyGlyphManager())
            {
                var builder = new StyledSymbolTileBuilder(manager);
                await builder.BuildAsync(
                    OnePointTile(new double2(100, 200)), TileId0,
                    new[] { PointIconLayer() }, 0.0, new WebMercatorProjection(), labels,
                    spriteAtlas: atlas);
            }

            Assert.AreEqual(1, labels.Count, "the icon-only feature must emit exactly one label");
            LabelInstance icon = labels[0];
            Assert.AreEqual(LabelKind.Icon, icon.Kind);

            // The reference: the very same layout with NO border at all. That box is what collision saw
            // before the repack and must still see after it.
            SymbolQuad bareQuad = IconQuadLayout.Layout(
                BareEntry, atlas.Size, IconSize, TextAnchor.Center, float2.zero);

            const float eps = 1e-5f;
            Assert.AreEqual(math.min(bareQuad.TopLeft.x, bareQuad.BottomRight.x), icon.Layout.BoundsMin.x, eps, "BoundsMin.x");
            Assert.AreEqual(math.min(bareQuad.TopLeft.y, bareQuad.BottomRight.y), icon.Layout.BoundsMin.y, eps, "BoundsMin.y");
            Assert.AreEqual(math.max(bareQuad.TopLeft.x, bareQuad.BottomRight.x), icon.Layout.BoundsMax.x, eps, "BoundsMax.x");
            Assert.AreEqual(math.max(bareQuad.TopLeft.y, bareQuad.BottomRight.y), icon.Layout.BoundsMax.y, eps, "BoundsMax.y");

            // Non-vacuity: the DRAWN quad must be strictly bigger than the collision box, by exactly the
            // skirt. Without this, an atlas that silently lost its padding would satisfy everything above.
            float expectedSkirt = IconQuadLayout.SkirtPx(padded, IconSize);
            Assert.AreEqual(Padding * IconSize, expectedSkirt, eps, "precondition: a 1-texel border at icon-size 2 is 2px");
            SymbolQuad drawn = icon.Layout.Quads[0];
            Assert.AreEqual(icon.Layout.BoundsMin.x - expectedSkirt, drawn.TopLeft.x, eps,
                "the RENDER quad must keep the skirt the collision box removed — the two representations " +
                "part company here, and only here.");
            Assert.AreEqual(icon.Layout.BoundsMax.x + expectedSkirt, drawn.BottomRight.x, eps);
        }

        // ── The along-line path: CurvedGlyph.CellSkirt must reach the rotated collision box ────────────

        [Test]
        public async Task AlongLineIcon_ThroughRealExtraction_RotatedBoxEqualsTheUnpaddedCell()
        {
            SpritePadPlan plan = PaddedPlan();
            SpriteAtlasView atlas = PaddedAtlas(plan);
            Assert.IsTrue(atlas.Index.TryGetSprite("arrow", out SpriteEntry padded));
            Assert.AreEqual(Padding, padded.Padding, "precondition: the planner must actually have padded this sprite");

            var labels = new List<LabelInstance>();
            using (GlyphManager manager = IconOnlyGlyphManager())
            {
                var builder = new StyledSymbolTileBuilder(manager);
                await builder.BuildAsync(
                    OneLineTile(new double2(500, 500), new double2(3500, 3500)), TileId0,
                    new[] { AlongLineIconLayer() }, 0.0, new WebMercatorProjection(), labels,
                    spriteAtlas: atlas);
            }

            Assert.AreEqual(1, labels.Count, "the map-aligned line icon must emit exactly one curved label");
            LabelInstance icon = labels[0];
            Assert.AreEqual(LabelKind.Icon, icon.Kind);
            Assert.AreEqual(1, icon.CurvedGlyphs.Count, "an along-line icon is a ONE-glyph curved label");
            CurvedGlyph glyph = icon.CurvedGlyphs[0];

            float expectedSkirt = IconQuadLayout.SkirtPx(padded, IconSize);
            Assert.Greater(expectedSkirt, 0f, "precondition: a padded sprite has a non-zero skirt");
            Assert.AreEqual(expectedSkirt, glyph.CellSkirt, 1e-5f,
                "the extractor's skirt must reach CurvedGlyph.CellSkirt — an emit that hard-codes 0 leaves " +
                "every along-line icon colliding on its transparent border.");

            // The consequence, not just the carried number: the rotated collision box built from the padded
            // cell + its skirt must equal the one built from the BARE cell with no skirt at all.
            SymbolQuad bareCell = IconQuadLayout.Layout(
                BareEntry, atlas.Size, IconSize, TextAnchor.Center, float2.zero);
            var anchor = new float2(120f, -40f);
            const float rotation = 0.7f;

            LabelBox actual = LabelBox.BuildRotatedGlyph(
                anchor, glyph.Cell, TextQuadLayout.OneEm, rotation, paddingPx: 0f, cellSkirt: glyph.CellSkirt);
            LabelBox expected = LabelBox.BuildRotatedGlyph(
                anchor, bareCell, TextQuadLayout.OneEm, rotation, paddingPx: 0f, cellSkirt: 0f);

            const float eps = 1e-4f;
            Assert.AreEqual(expected.Min.x, actual.Min.x, eps, "rotated Min.x");
            Assert.AreEqual(expected.Min.y, actual.Min.y, eps, "rotated Min.y");
            Assert.AreEqual(expected.Max.x, actual.Max.x, eps, "rotated Max.x");
            Assert.AreEqual(expected.Max.y, actual.Max.y, eps, "rotated Max.y");

            // Non-vacuity: the padded cell with skirt 0 must be a DIFFERENT box, or the comparison above
            // could not discriminate a lost skirt.
            LabelBox unshrunk = LabelBox.BuildRotatedGlyph(
                anchor, glyph.Cell, TextQuadLayout.OneEm, rotation, paddingPx: 0f, cellSkirt: 0f);
            Assert.Greater(math.abs(unshrunk.Min.x - expected.Min.x), 10f * eps,
                "precondition: dropping the skirt must visibly change the box, or this tooth is vacuous");
        }

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>A glyph manager whose source serves nothing. Legitimate here: an icon-only layer never
        /// requests a range (pass 1 skips <c>LabelKind.Icon</c>) and never builds a font-stack resolver, so
        /// touching it at all would itself be the defect.</summary>
        private static GlyphManager IconOnlyGlyphManager()
            => new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));

        private static SymbolStyle.StyleLayer PointIconLayer()
            => new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                LayoutJson = JsonParser.Parse("{\"icon-image\":\"marker\",\"icon-size\":2}"),
            };

        /// <summary>`symbol-placement: line` with `icon-rotation-alignment` unset ⇒ resolves `auto → map`,
        /// which is the P-B one-glyph-curved-label emit shape.</summary>
        private static SymbolStyle.StyleLayer AlongLineIconLayer()
            => new SymbolStyle.StyleLayer
            {
                Id = "roads",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "roads",
                LayoutJson = JsonParser.Parse(
                    "{\"icon-image\":\"arrow\",\"icon-size\":2,\"symbol-placement\":\"line\"}"),
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

        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        private static IDecodedTile OnePointTile(double2 point)
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Point,
                Geometry = new[] { 1u | (1u << 3), ZigZagEncode((long)point.x), ZigZagEncode((long)point.y) },
            };
            return new FixtureDecodedTile(new FixtureTileLayer
            {
                Name = "points", Extent = Extent, Features = new List<ITileFeature> { feature },
            });
        }

        private static IDecodedTile OneLineTile(double2 from, double2 to)
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.LineString,
                Geometry = new[]
                {
                    1u | (1u << 3), ZigZagEncode((long)from.x), ZigZagEncode((long)from.y),
                    2u | (1u << 3), ZigZagEncode((long)(to.x - from.x)), ZigZagEncode((long)(to.y - from.y)),
                },
            };
            return new FixtureDecodedTile(new FixtureTileLayer
            {
                Name = "roads", Extent = Extent, Features = new List<ITileFeature> { feature },
            });
        }
    }
}
