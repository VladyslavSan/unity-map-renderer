// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using System.IO;
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
using MapRenderer.Tests; // TestGlyphSource
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// P2 REQUIRED-1 — the `Up` CARRIER CHAIN from Block A, driven end to end through the REAL
    /// <see cref="StyledSymbolTileBuilder.BuildAsync"/> (which itself calls the real
    /// <see cref="SymbolStyle.SymbolFeatureExtractor.Extract"/>), asserted against a closed form written
    /// out here — never obtained from <see cref="IProjection.ProjectPoint"/> or any production sampler.
    ///
    /// <para><b>Why this exists as its own tooth.</b> T-3 (<c>WorldSurfaceUpPopulationTests</c>) hand-builds
    /// a <see cref="LabelInstance"/> directly, so it starts DOWNSTREAM of every Block-A edit (the extractor's
    /// four <c>UpRender</c>/<c>PathUpRender</c> assignments and <see cref="StyledSymbolTileBuilder"/>'s four
    /// carry-through sites). Deleting any one of those eight lines leaves T-3 — and the rest of the gate —
    /// green, because nothing else reads <c>Up</c> yet (the byte-identical invariant). Only a tooth that
    /// starts at REAL extraction, mirroring <c>IconSkirtCarrierChainTests</c>' identical reasoning for the
    /// icon skirt, can see it.</para>
    ///
    /// <para><see cref="SphericalProjection"/>, not <see cref="WebMercatorProjection"/>: Mercator's `Up` is
    /// the constant <c>(0,1,0)</c>, so a dropped assignment there would default to `(0,1,0)` too — a
    /// plausible-looking pass. The globe's per-anchor `Up` makes a dropped assignment read as
    /// <see cref="double3.zero"/>, unmistakably wrong.</para>
    /// </summary>
    [TestFixture]
    public class SymbolUpCarrierChainTests
    {
        private static readonly TileId TileId0 = new TileId { Z = 10, X = 500, Y = 500 };
        private const uint Extent = 4096;
        private static readonly SphericalProjection Projection = new SphericalProjection();
        private const string FontName = "LatinFont";

        // Closed form, hand-written — see WorldSurfaceUpPopulationTests for the same discipline. Render
        // axes are (X,Z,Y) per SphericalProjection.cs's documented ECEF axis-swap, so Up.y carries sinφ.
        private static float3 ClosedFormUp(double latDeg, double lonDeg)
        {
            double phi = latDeg * math.PI_DBL / 180.0;
            double lambda = lonDeg * math.PI_DBL / 180.0;
            return new float3(
                (float)(math.cos(phi) * math.cos(lambda)), (float)math.sin(phi), (float)(math.cos(phi) * math.sin(lambda)));
        }

        private static float3 ExpectedUpAtTilePoint(double2 tilePoint)
        {
            double2 lonLat = TileId0.ToLonLat(tilePoint.x, tilePoint.y, Extent);
            return ClosedFormUp(lonLat.y, lonLat.x);
        }

        private static void AssertUp(float3 expected, double3 actual, string message)
        {
            Assert.AreEqual(expected.x, (float)actual.x, 1e-5f, $"{message} (x)");
            Assert.AreEqual(expected.y, (float)actual.y, 1e-5f, $"{message} (y)");
            Assert.AreEqual(expected.z, (float)actual.z, 1e-5f, $"{message} (z)");
            Assert.AreNotEqual(double3.zero, actual, $"{message}: must not be the dropped-assignment zero");
        }

        private static GlyphManager BuildGlyphManager()
        {
            string[] starts = { System.AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
            byte[] latin = null;
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
                    if (File.Exists(p)) { latin = File.ReadAllBytes(p); break; }
                    dir = dir.Parent;
                }
                if (latin != null) break;
            }
            Assert.IsNotNull(latin, "fixture glyph range must be found walking up from cwd/AppContext");
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latin };
            return new GlyphManager(TestGlyphSource.FromRanges(ranges));
        }

        private static SpriteAtlasView OneSpriteAtlas(string name)
            => new SpriteAtlasView
            {
                Index = SpriteIndex.Parse("{\"" + name + "\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}"),
                Size = new int2(32, 32),
            };

        // ── fixtures: a single Point feature, or a single 2-vertex LineString feature ───────────────────

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

        private static readonly double2 LineFrom = new double2(500, 500);
        private static readonly double2 LineTo = new double2(3500, 3500);

        private static IDecodedTile OneLineTile()
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.LineString,
                Geometry = new uint[]
                {
                    1u | (1u << 3), ZigZagEncode((long)LineFrom.x), ZigZagEncode((long)LineFrom.y),
                    2u | (1u << 3), ZigZagEncode((long)(LineTo.x - LineFrom.x)), ZigZagEncode((long)(LineTo.y - LineFrom.y)),
                },
            };
            return new FixtureDecodedTile(new FixtureTileLayer
            {
                Name = "roads", Extent = Extent, Features = new List<ITileFeature> { feature },
            });
        }

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

        // ── (1) point TEXT, unpaired — SymbolFeatureExtractor.EmitTextLabel + StyledSymbolTileBuilder :257 ──

        [Test]
        public async Task PointText_ThroughRealExtractionAndBuild_UpRenderMatchesClosedForm()
        {
            var point = new double2(2000, 2000);
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "labels", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "points",
                LayoutJson = JsonParser.Parse("{\"text-field\":\"L\",\"text-font\":[\"" + FontName + "\"]}"),
            };

            using GlyphManager manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var labels = new List<LabelInstance>();
            await builder.BuildAsync(OnePointTile(point), TileId0, new[] { layer }, 0.0, Projection, labels);

            Assert.AreEqual(1, labels.Count, "one point-text label");
            Assert.AreEqual(LabelKind.Text, labels[0].Kind);
            AssertUp(ExpectedUpAtTilePoint(point), labels[0].UpRender, "point-text UpRender");
        }

        // ── (2) point ICON+TEXT PAIR — both EmitIconLabel(Owner)/EmitTextLabel(Rider) + builder :179/:257 ──

        [Test]
        public async Task PointIconTextPair_ThroughRealExtractionAndBuild_BothHalvesUpRenderMatchClosedForm()
        {
            var point = new double2(2000, 2000);
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "labels", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "points",
                LayoutJson = JsonParser.Parse(
                    "{\"text-field\":\"L\",\"text-font\":[\"" + FontName + "\"],\"icon-image\":\"marker\"}"),
            };

            using GlyphManager manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var labels = new List<LabelInstance>();
            await builder.BuildAsync(OnePointTile(point), TileId0, new[] { layer }, 0.0, Projection, labels,
                spriteAtlas: OneSpriteAtlas("marker"));

            Assert.AreEqual(2, labels.Count, "a feature resolving both icon and text emits a paired instance (icon then text)");
            LabelInstance icon = labels[0], text = labels[1];
            Assert.AreEqual(LabelKind.Icon, icon.Kind); Assert.AreEqual(LabelPairRole.Owner, icon.PairRole);
            Assert.AreEqual(LabelKind.Text, text.Kind); Assert.AreEqual(LabelPairRole.Rider, text.PairRole);

            float3 expected = ExpectedUpAtTilePoint(point);
            AssertUp(expected, icon.UpRender, "paired icon (owner) UpRender");
            AssertUp(expected, text.UpRender, "paired text (rider) UpRender");
        }

        // ── (3) curved TEXT — SymbolFeatureExtractor's curved-text PathUpRender (:321) + builder :287 ──

        [Test]
        public async Task CurvedText_ThroughRealExtractionAndBuild_PathUpRenderIsIndexParallelAndMatchesClosedFormAtBothEnds()
        {
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "lines", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "roads",
                LayoutJson = JsonParser.Parse(
                    "{\"text-field\":\"L\",\"text-font\":[\"" + FontName + "\"],\"symbol-placement\":\"line-center\"}"),
            };

            using GlyphManager manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var labels = new List<LabelInstance>();
            await builder.BuildAsync(OneLineTile(), TileId0, new[] { layer }, 0.0, Projection, labels);

            Assert.AreEqual(1, labels.Count, "one curved text label");
            LabelInstance curved = labels[0];
            Assert.AreEqual(SymbolPlacement.LineCenter, curved.Placement);
            Assert.IsNotNull(curved.PathRender);
            Assert.IsNotNull(curved.PathUpRender, "PathUpRender must not be null on a curved label");
            Assert.AreEqual(curved.PathRender.Length, curved.PathUpRender.Length,
                "PathUpRender must be index-parallel to PathRender (same length)");

            // Subdivision (LineCurvatureSubdivision.Subdivide) always preserves the ORIGINAL endpoints
            // exactly (dense[0] = input[0], dense[last] = input[last]) regardless of how many points it
            // inserts between them — so element 0 / element[last] are safe to check independent of whether
            // the globe subdivided this span.
            AssertUp(ExpectedUpAtTilePoint(LineFrom), curved.PathUpRender[0], "curved text PathUpRender[0] (start)");
            AssertUp(ExpectedUpAtTilePoint(LineTo), curved.PathUpRender[curved.PathUpRender.Length - 1], "curved text PathUpRender[last] (end)");
        }

        // ── (4) along-line ICON — SymbolFeatureExtractor.EmitAlongLineIcon's PathUpRender (:605) + builder :216 ──

        [Test]
        public async Task AlongLineIcon_ThroughRealExtractionAndBuild_PathUpRenderIsIndexParallelAndMatchesClosedFormAtBothEnds()
        {
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "lines", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "roads",
                // icon-rotation-alignment unset -> resolves auto -> map for line placement -> the P-B
                // one-glyph curved-icon emit shape (mirrors IconSkirtCarrierChainTests.AlongLineIconLayer).
                LayoutJson = JsonParser.Parse("{\"icon-image\":\"arrow\",\"symbol-placement\":\"line\"}"),
            };

            // An icon-only layer must never touch the glyph/shaper machinery (mirrors
            // IconSkirtCarrierChainTests.IconOnlyGlyphManager).
            using var manager = new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));
            var builder = new StyledSymbolTileBuilder(manager);
            var labels = new List<LabelInstance>();
            await builder.BuildAsync(OneLineTile(), TileId0, new[] { layer }, 0.0, Projection, labels,
                spriteAtlas: OneSpriteAtlas("arrow"));

            Assert.AreEqual(1, labels.Count, "one along-line icon label");
            LabelInstance icon = labels[0];
            Assert.AreEqual(LabelKind.Icon, icon.Kind);
            Assert.AreEqual(1, icon.CurvedGlyphs.Count, "an along-line icon is a ONE-glyph curved label");
            Assert.IsNotNull(icon.PathRender);
            Assert.IsNotNull(icon.PathUpRender, "PathUpRender must not be null on an along-line icon");
            Assert.AreEqual(icon.PathRender.Length, icon.PathUpRender.Length,
                "PathUpRender must be index-parallel to PathRender (same length)");

            AssertUp(ExpectedUpAtTilePoint(LineFrom), icon.PathUpRender[0], "along-line icon PathUpRender[0] (start)");
            AssertUp(ExpectedUpAtTilePoint(LineTo), icon.PathUpRender[icon.PathUpRender.Length - 1], "along-line icon PathUpRender[last] (end)");
        }
    }
}
