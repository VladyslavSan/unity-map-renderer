// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Road shields (docs/road-shields-design.md, revision 2): G1-G6 acceptance teeth over the real Liberty
    /// style + real Berlin fixture (non-US shield layer) plus synthetic tiles/atlas (the two US shield layers,
    /// which the Berlin fixture carries zero of — T13 — and the anchor-clip edge cases). T1/T6 need the D1/D3
    /// production types and are added in Phase 2/Phase 1 respectively; T10/T11 are Unity-only (Phase 6).
    /// </summary>
    [TestFixture]
    public class SymbolShieldExtractionTests
    {
        // ── Walk-up fixture loaders + the parsed Liberty style live in SymbolTestFixtures (shared with
        //    SymbolPairPredicateTests); these are the local names this file's call sites already use. ──
        private static byte[] LoadBerlinFixtureBytes()
            => SymbolTestFixtures.LoadUpBytes("Assets", "Fixtures", "boundary-9-274-168.pbf.bytes");

        private static SymbolStyle.StyleLayer FindShieldLayer(string id) => SymbolTestFixtures.FindSymbolLayer(id);

        private static readonly TileId BerlinTile = new TileId { Z = 9, X = 274, Y = 168 };
        private static MvtTile _berlinTile;
        private static MvtTile BerlinFixtureTile() => _berlinTile ??= MvtDecoder.Decode(LoadBerlinFixtureBytes());

        // ── Synthetic atlas: road_1..6, us-interstate_1..6, us-highway_1..6, us-state_1..6 ────────────
        private static SpriteAtlasView SyntheticShieldAtlas()
        {
            var sb = new StringBuilder("{");
            string[] prefixes = { "road_", "us-interstate_", "us-highway_", "us-state_" };
            bool first = true;
            foreach (string prefix in prefixes)
            {
                for (int n = 1; n <= 6; n++)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(prefix).Append(n).Append('"');
                    sb.Append(":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1,\"sdf\":false}");
                }
            }
            sb.Append('}');
            return new SpriteAtlasView { Index = SpriteIndex.Parse(sb.ToString()), Size = new int2(64, 64) };
        }

        // ── Synthetic tile plumbing — mirrors SymbolFeatureExtractorTests/SymbolFeatureExtractorIconTests'
        //    FixtureDecodedTile/FixtureTileLayer local doubles (test-only, duplicated per convention). ──
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

        /// <summary>Hand-encodes an N-point LineString MVT command stream: MoveTo(1) + LineTo(N-1),
        /// zigzag-encoded cumulative deltas — mirrors SymbolFeatureExtractorTests.DiagonalLineGeometry
        /// generalized to N points.</summary>
        private static uint[] LineStringGeometry(params double2[] points)
        {
            var stream = new List<uint> { 1u | (1u << 3) };
            long cx = (long)points[0].x, cy = (long)points[0].y;
            stream.Add(ZigZagEncode(cx));
            stream.Add(ZigZagEncode(cy));
            if (points.Length > 1)
            {
                stream.Add(2u | ((uint)(points.Length - 1) << 3));
                for (int i = 1; i < points.Length; i++)
                {
                    long x = (long)points[i].x, y = (long)points[i].y;
                    stream.Add(ZigZagEncode(x - cx));
                    stream.Add(ZigZagEncode(y - cy));
                    cx = x; cy = y;
                }
            }
            return stream.ToArray();
        }

        private static DictionaryFeature ShieldFeature(string network, int refLength, string refValue, uint[] geometry)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value>
                {
                    ["ref"] = Value.String(refValue),
                    ["ref_length"] = Value.Number(refLength),
                    ["network"] = Value.String(network),
                },
                geometryType: TileGeometryType.LineString,
                geometry: geometry);

        private const uint Extent = 4096;
        private static readonly TileId SyntheticTileId = new TileId { Z = 1, X = 0, Y = 0 };

        /// <summary>A synthetic <c>transportation_name</c> tile carrying one us-interstate feature and one
        /// us-highway feature — the two networks Berlin's real fixture carries none of (T13).</summary>
        private static IDecodedTile SyntheticUsShieldTile()
        {
            var interstate = ShieldFeature("us-interstate", 2, "80", LineStringGeometry(
                new double2(500, 500), new double2(3500, 3500)));
            var usHighway = ShieldFeature("us-highway", 1, "9", LineStringGeometry(
                new double2(500, 1500), new double2(3500, 1500)));
            var layer = new FixtureTileLayer
            {
                Name = "transportation_name",
                Extent = Extent,
                Features = new List<ITileFeature> { interstate, usHighway },
            };
            return new FixtureDecodedTile(layer);
        }

        private static int CountIcons(List<SymbolStyle.SymbolLabel> labels)
        {
            int n = 0;
            foreach (SymbolStyle.SymbolLabel l in labels) if (l.Kind == LabelKind.Icon) n++;
            return n;
        }

        private static int CountTexts(List<SymbolStyle.SymbolLabel> labels)
        {
            int n = 0;
            foreach (SymbolStyle.SymbolLabel l in labels) if (l.Kind == LabelKind.Text) n++;
            return n;
        }

        // ── T2 ─────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void PointPlacement_OnLineString_EmitsLabels()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            Assert.IsNotNull(nonUs, "precondition: highway-shield-non-us must parse as a Symbol.StyleLayer");
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            Assert.Greater(FeatureSelectorCount(nonUs), 0, "precondition: the layer must select > 0 features");

            var labels = new List<SymbolStyle.SymbolLabel>();
            // z10 — below the layer's z11 step boundary — must evaluate to Point placement.
            SymbolStyle.SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 10.0, projection, labels, atlas);

            Assert.Greater(labels.Count, 0, "point placement on LineString features must emit labels");
            foreach (SymbolStyle.SymbolLabel l in labels)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "every label must be Point-placed below the step");

            int icons = CountIcons(labels), texts = CountTexts(labels);
            Assert.Greater(icons, 0, "icon count must be > 0");
            Assert.Greater(texts, 0, "text count must be > 0");
            Assert.AreEqual(texts, icons, "icon count must equal text count (one pair per feature)");
        }

        private static int FeatureSelectorCount(StyleLayer layer)
            => MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(layer, BerlinFixtureTile(), 10.0).Count;

        // ── T3 ─────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void ShieldIconImages_ResolveFromAtlas_AllThreeLayers()
        {
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            // non-us — real fixture, at z13 (above its z11 step ⇒ line/upright placement, the demo's actual view).
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            var nonUsLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 13.0, projection, nonUsLabels, atlas);
            int nonUsIcons = CountIcons(nonUsLabels);
            Assert.Greater(nonUsIcons, 0, "highway-shield-non-us must resolve > 0 icons at z13");
            foreach (SymbolStyle.SymbolLabel l in nonUsLabels)
                if (l.Kind == LabelKind.Icon)
                {
                    StringAssert.StartsWith("road_", l.IconImage, "non-us icon name must be road_<ref_length>");
                }

            // interstate + road_shield_us — synthetic tile (Berlin carries no US-network features — T13).
            IDecodedTile synthTile = SyntheticUsShieldTile();

            SymbolStyle.StyleLayer interstate = FindShieldLayer("highway-shield-us-interstate");
            var interstateLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(interstate, synthTile, SyntheticTileId, 13.0, projection, interstateLabels, atlas);
            int interstateIcons = CountIcons(interstateLabels);
            Assert.Greater(interstateIcons, 0, "highway-shield-us-interstate must resolve > 0 icons at z13");
            foreach (SymbolStyle.SymbolLabel l in interstateLabels)
                if (l.Kind == LabelKind.Icon)
                    StringAssert.StartsWith("us-interstate_", l.IconImage, "interstate icon name must be us-interstate_<ref_length>");

            SymbolStyle.StyleLayer usShield = FindShieldLayer("road_shield_us");
            var usShieldLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(usShield, synthTile, SyntheticTileId, 13.0, projection, usShieldLabels, atlas);
            int usShieldIcons = CountIcons(usShieldLabels);
            Assert.Greater(usShieldIcons, 0, "road_shield_us must resolve > 0 icons at z13");
            foreach (SymbolStyle.SymbolLabel l in usShieldLabels)
                if (l.Kind == LabelKind.Icon)
                    Assert.IsTrue(l.IconImage.StartsWith("us-highway_") || l.IconImage.StartsWith("us-state_"),
                        $"road_shield_us icon name must be us-highway_<n>|us-state_<n>, was '{l.IconImage}'");
        }

        // ── T4 ─────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void LinePlacement_ViewportAligned_EmitsUprightAnchorLabels()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 13.0, projection, labels, atlas);

            Assert.Greater(labels.Count, 0, "z13 (above the step) must still emit labels");
            foreach (SymbolStyle.SymbolLabel l in labels)
            {
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "upright-at-anchor labels are Point-placed, not curved");
                Assert.IsNull(l.PathRender, "an upright-at-anchor label carries no curved path");
            }

            int icons = CountIcons(labels), texts = CountTexts(labels);
            Assert.Greater(icons, 0, "icon count must be > 0 at z13");
            Assert.Greater(texts, 0, "text count must be > 0 at z13");
            Assert.AreEqual(texts, icons, "icon count must equal text count at z13");
        }

        // ── T5 ─────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void CentredPair_EmitsAdjacentIconThenText()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 13.0, projection, labels, atlas);

            Assert.Greater(labels.Count, 0, "precondition: some labels must be emitted");
            Assert.AreEqual(0, labels.Count % 2, "labels must form consecutive (icon,text) pairs — even count");

            for (int i = 0; i < labels.Count; i += 2)
            {
                SymbolStyle.SymbolLabel icon = labels[i];
                SymbolStyle.SymbolLabel text = labels[i + 1];
                Assert.AreEqual(LabelKind.Icon, icon.Kind, $"labels[{i}] must be the icon (icon emitted first)");
                Assert.AreEqual(LabelKind.Text, text.Kind, $"labels[{i + 1}] must be the text");
                Assert.AreEqual(icon.FeatureIndex + 1, text.FeatureIndex, "text's ordinal must immediately follow its icon's");
                Assert.AreEqual(icon.AnchorRender, text.AnchorRender, "icon and text of a pair share the same anchor");
                // §10 D8/D9: the D5 forcing is retired — the icon+text pair is ONE placement instance
                // downstream (LabelPairing / StagePointPair), so both halves carry their AUTHORED
                // text-allow-overlap/text-ignore-placement (liberty's shield layers declare neither — default
                // false), not a forced-true passenger flag.
                Assert.IsFalse(text.AllowOverlap, "the rider text carries its AUTHORED AllowOverlap (unset -> false)");
                Assert.IsFalse(text.IgnorePlacement, "the rider text carries its AUTHORED IgnorePlacement (unset -> false)");
                Assert.IsFalse(icon.AllowOverlap, "the icon (pair owner) must NOT set AllowOverlap");
                Assert.IsFalse(icon.IgnorePlacement, "the icon (pair owner) must NOT set IgnorePlacement");

                // §10 D10: the pair is stamped Owner/Rider sharing a PairId.
                Assert.AreEqual(LabelPairRole.Owner, icon.PairRole, "the icon must be stamped Owner");
                Assert.AreEqual(LabelPairRole.Rider, text.PairRole, "the text must be stamped Rider");
                Assert.AreEqual(icon.FeatureIndex, icon.PairId, "PairId is the owner's own FeatureIndex");
                Assert.AreEqual(icon.PairId, text.PairId, "both halves of a pair share one PairId");
            }
        }

        // ── P-A (was §10 D10's negative case): a NON-centred icon+text feature (text-offset != 0) now PAIRS
        //    like any other — the predicate is "both halves resolved", not "both halves coincide". The old
        //    expectation (unpaired, text-then-icon) described the gap that let a city dot place while its own
        //    name was culled; inverted in place rather than deleted, so the discriminating value survives. ──
        [Test]
        public void NonCentredPair_NowEmitsIconThenText_OwnerRider()
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var layer = new FixtureTileLayer
            {
                Name = "points",
                Extent = Extent,
                Features = new List<ITileFeature> { feature },
            };
            var tile = new FixtureDecodedTile(layer);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "non-centred-pair-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\",\"text-offset\":[0,0.6]}"),
            };
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0,
                new WebMercatorProjection(), labels, SyntheticShieldAtlas());

            Assert.AreEqual(2, labels.Count, "precondition: text + icon must both be emitted");
            Assert.AreEqual(LabelKind.Icon, labels[0].Kind, "a pair emits its OWNER (the icon) first");
            Assert.AreEqual(LabelKind.Text, labels[1].Kind, "the rider text follows immediately");
            Assert.AreEqual(LabelPairRole.Owner, labels[0].PairRole, "the icon is the pair owner");
            Assert.AreEqual(LabelPairRole.Rider, labels[1].PairRole, "the offset text still rides its icon");
            Assert.AreEqual(labels[0].FeatureIndex, labels[0].PairId, "PairId is the owner's own FeatureIndex");
            Assert.AreEqual(labels[0].PairId, labels[1].PairId, "both halves share one PairId");
            Assert.AreEqual(labels[0].FeatureIndex + 1, labels[1].FeatureIndex,
                "the rider's ordinal must immediately follow its owner's (LabelPairing's adjacency contract)");
        }

        // ── T7 ─────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void MapAlignedLineLayer_StillCurves_FieldForField()
        {
            var handBuilt = new SymbolStyle.StyleLayer
            {
                Id = "shield-map-aligned-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "openmaptiles",
                SourceLayer = "transportation_name",
                Filter = MapRenderer.Core.Json.JsonParser.Parse(
                    "[\"all\",[\"<=\",[\"get\",\"ref_length\"],6],[\"match\",[\"geometry-type\"],[\"LineString\",\"MultiLineString\"],true,false]]"),
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"text-field\":[\"to-string\",[\"get\",\"ref\"]],\"text-rotation-alignment\":\"map\",\"symbol-placement\":\"line\"}"),
            };
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            IReadOnlyList<ITileFeature> selected = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(handBuilt, BerlinFixtureTile(), 13.0);
            Assert.Greater(selected.Count, 0, "precondition: the hand-built filter must select > 0 features");

            int eligiblePaths = 0;
            foreach (ITileFeature f in selected)
                foreach (List<double2> path in MvtGeometry.Decode(f.Geometry))
                    if (path.Count >= 2) eligiblePaths++;
            Assert.Greater(eligiblePaths, 0, "precondition: > 0 eligible (>=2 point) decoded paths");

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(handBuilt, BerlinFixtureTile(), BerlinTile, 13.0, projection, labels, atlas);

            Assert.Greater(labels.Count, 0, "map-aligned line layer must still emit curved labels");
            Assert.AreEqual(eligiblePaths, labels.Count, "one curved label per eligible decoded path — no drops, no dupes");

            for (int i = 0; i < labels.Count; i++)
            {
                SymbolStyle.SymbolLabel l = labels[i];
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, $"labels[{i}] must stay curved (Line), not upright");
                Assert.IsNotNull(l.PathRender, $"labels[{i}] must carry a projected path");
                Assert.IsNotNull(l.LineAnchors, $"labels[{i}] must carry its build-time anchors");
                Assert.Greater(l.LineAnchors.Length, 0, $"labels[{i}] must carry >= 1 anchor");
                Assert.AreEqual(i, l.FeatureIndex, "FeatureIndex ordinals must be contiguous from 0");
                Assert.AreEqual(LabelKind.Text, l.Kind, "a map-aligned curved label is a text label");
            }

            // Field-for-field, against values derived independently from the layer definition (spec defaults),
            // not a baked baseline.
            SymbolStyle.SymbolLabel first = labels[0];
            Assert.AreEqual(250f, first.SpacingPx, 1e-6, "symbol-spacing default is 250");
            Assert.AreEqual(45f, first.MaxAngleDeg, 1e-6, "text-max-angle default is 45");
            Assert.IsTrue(first.KeepUpright, "text-keep-upright default is true");
            Assert.IsFalse(first.AllowOverlap, "text-allow-overlap default is false");
            Assert.IsFalse(first.IgnorePlacement, "text-ignore-placement default is false");
            Assert.AreEqual(float2.zero, first.TranslatePx, "text-translate default is [0,0]");
            Assert.AreEqual(TextTranslateAnchor.Map, first.TranslateAnchor, "text-translate-anchor default is map");
            Assert.AreEqual(0f, first.SortKey, 1e-6, "symbol-sort-key default is 0");
            Assert.Greater(first.PathRender.Length, 0, "PathRender must be non-empty");

            // No icons at all — this layer declares no icon-image.
            Assert.AreEqual(0, CountIcons(labels), "a text-only layer must emit zero icon labels");

            // The real shipped Liberty layer (guarded skip if this fixture selects nothing for it).
            SymbolStyle.StyleLayer highwayNameMajor = FindShieldLayer("highway-name-major");
            Assert.IsNotNull(highwayNameMajor, "precondition: highway-name-major must parse");
            IReadOnlyList<ITileFeature> majorSelected =
                MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(highwayNameMajor, BerlinFixtureTile(), 13.0);
            if (majorSelected.Count == 0)
            {
                Assert.Pass("known coverage gap: highway-name-major selects nothing from the Berlin fixture at z13");
            }
            var majorLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(highwayNameMajor, BerlinFixtureTile(), BerlinTile, 13.0, projection, majorLabels, atlas);
            Assert.Greater(majorLabels.Count, 0, "highway-name-major (literal line, unset alignment -> map) must still emit curved labels");
            foreach (SymbolStyle.SymbolLabel l in majorLabels)
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "highway-name-major must stay curved");
        }

        // ── A2 (P-B; REVERSES T8) ──────────────────────────────────────────────────────────────────────
        // T8 pinned the D4 fence — "a map-aligned line icon never emits, even with an atlas supplied".
        // P-B LIFTS that fence: a map-resolved line icon is now emitted as a ONE-GLYPH CURVED label (the
        // road_one_way_arrow* shape). The assertion below is T8's inverse, not a re-bake: the old zero-icon
        // expectation described a deliberate gap, and this stage closes it.
        private static SymbolStyle.StyleLayer MapAlignedIconProbeLayer(string extraLayoutJson = "")
            => new SymbolStyle.StyleLayer
            {
                Id = "shield-map-aligned-icon-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "openmaptiles",
                SourceLayer = "transportation_name",
                Filter = MapRenderer.Core.Json.JsonParser.Parse(
                    "[\"all\",[\"<=\",[\"get\",\"ref_length\"],6],[\"match\",[\"geometry-type\"],[\"LineString\",\"MultiLineString\"],true,false]]"),
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"icon-image\":\"road_3\",\"symbol-placement\":\"line\"" + extraLayoutJson + "}"),
            };

        /// <summary>Decoded paths of <paramref name="layer"/>'s selected features that can carry a label
        /// (>= 2 points) — the along-line emit shape produces exactly one curved label per one of these.
        /// Same count the curved-text tooth above derives inline, over the same fixture.</summary>
        private static int EligiblePathCount(SymbolStyle.StyleLayer layer)
        {
            int eligible = 0;
            foreach (ITileFeature f in MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(layer, BerlinFixtureTile(), 13.0))
                foreach (List<double2> path in MvtGeometry.Decode(f.Geometry))
                    if (path.Count >= 2) eligible++;
            Assert.Greater(eligible, 0, "precondition: > 0 eligible (>=2 point) decoded paths");
            return eligible;
        }

        [Test]
        public void MapAlignedLineIconLayer_EmitsAlongLineIconLabels()
        {
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            // No rotation-alignment declared -> auto -> resolves MAP under line placement (D3).
            SymbolStyle.StyleLayer mapAligned = MapAlignedIconProbeLayer();
            IReadOnlyList<ITileFeature> selected =
                MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(mapAligned, BerlinFixtureTile(), 13.0);
            Assert.Greater(selected.Count, 0, "precondition: > 0 features selected");

            // Precondition that the fixture is genuinely ICON-BEARING: the SAME layer with an explicit
            // viewport alignment takes the shipped D4 at-anchors path and emits POINT-shaped icons. Without
            // this, a zero-icon map arm could pass for the wrong reason (an unresolvable sprite).
            var viewportLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(",\"icon-rotation-alignment\":\"viewport\""),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, viewportLabels, atlas);
            Assert.Greater(CountIcons(viewportLabels), 0,
                "precondition: the viewport-resolved arm must emit icons (the sprite resolves)");
            foreach (SymbolStyle.SymbolLabel l in viewportLabels)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement,
                    "precondition: a viewport-resolved line icon stays point-shaped (the unchanged D4 path)");

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(mapAligned, BerlinFixtureTile(), BerlinTile, 13.0, projection, labels, atlas);

            int icons = CountIcons(labels);
            Assert.Greater(icons, 0, "a map-aligned line icon must now emit (the D4 fence is lifted by P-B)");

            foreach (SymbolStyle.SymbolLabel l in labels)
            {
                if (l.Kind != LabelKind.Icon) continue;
                // A point-shaped icon here would mean the shallow "emit it at the anchors" impl, not the
                // one-glyph curved label this stage specifies.
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "an along-line icon carries LINE placement");
                Assert.IsNotNull(l.PathRender, "an along-line icon carries the projected path it rides");
                Assert.Greater(l.PathRender.Length, 1, "the projected path must have >= 1 segment");
                Assert.IsNotNull(l.LineAnchors, "an along-line icon carries the build-time anchors");
                Assert.GreaterOrEqual(l.LineAnchors.Length, 1, "at least one along-line anchor");
                Assert.IsFalse(l.KeepUpright,
                    "icon-keep-upright's spec default is false — an arrow must never flip to stay upright");
                Assert.IsNotNull(l.IconImage, "the resolved sprite name is the icon's cross-tile identity");
                Assert.AreEqual(LabelPairRole.None, l.PairRole, "a curved label is never half of a centred pair");
            }
        }

        // ── A1 (P-B): the three-way alignment classification the icon emit shape now branches on. ──
        [Test]
        public void IconRotationAlignment_ResolvesToThreeDistinctEmitShapes()
        {
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            // The resolver itself: unset (Auto) under LINE placement is what makes road_one_way_arrow*
            // map-aligned in the first place — the whole reason this stage exists.
            Assert.AreEqual(AlignmentMode.Map, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.Line),
                "auto resolves to map under line placement (D3)");
            Assert.AreEqual(AlignmentMode.Viewport, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.Point),
                "auto resolves to viewport under point placement (D3)");

            // (a) line + map-resolved -> ALONG-LINE icon (one curved label per path).
            var mapLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(MapAlignedIconProbeLayer(),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, mapLabels, atlas);
            Assert.Greater(CountIcons(mapLabels), 0, "line + map must emit icons");
            foreach (SymbolStyle.SymbolLabel l in mapLabels)
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "line + map -> along-line (curved) icon");

            // (b) line + viewport -> the shipped D4 at-anchors icon (point-shaped), UNCHANGED by this stage.
            var viewportLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(",\"icon-rotation-alignment\":\"viewport\""),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, viewportLabels, atlas);
            Assert.Greater(CountIcons(viewportLabels), 0, "line + viewport must emit icons");
            foreach (SymbolStyle.SymbolLabel l in viewportLabels)
            {
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "line + viewport -> point-shaped icon at each anchor");
                Assert.IsNull(l.PathRender, "an at-anchors icon carries no path");
            }
            // The two shapes are genuinely different, not the same emit relabelled: the viewport arm produces
            // one label PER ANCHOR, the map arm one per PATH. The map arm's count is pinned EXACTLY — that is
            // the claim, and it needs no premise. The strict inequality does need one the exact count does
            // not: that at least one decoded path is longer than a symbol-spacing (250 px default) and so
            // carries >= 2 anchors. True of this committed fixture, and asserted rather than assumed.
            int eligiblePaths = EligiblePathCount(MapAlignedIconProbeLayer());
            Assert.AreEqual(eligiblePaths, CountIcons(mapLabels),
                "the along-line arm emits exactly one curved icon per eligible decoded path");
            Assert.Greater(CountIcons(viewportLabels), eligiblePaths,
                "the at-anchors arm emits per ANCHOR, so with at least one multi-anchor path it must emit " +
                "strictly more icons than there are paths");

            // (c) point placement -> the point icon path, regardless of alignment.
            var pointLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(",\"symbol-placement\":\"point\""),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, pointLabels, atlas);
            Assert.Greater(CountIcons(pointLabels), 0, "point placement must emit icons");
            foreach (SymbolStyle.SymbolLabel l in pointLabels)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "point placement -> point icon");
        }

        // ── P-B review NIT 2: the centred-pair predicate is computed from the UN-suppressed `hasIcon`, so on
        //    the line branch it must be re-gated on the fence that decides whether an icon reaches the
        //    at-anchors emit at all. P-B widened that gap: before it, `hasIcon` on a line layer implied
        //    `iconAtAnchors`; now the icon can leave for the along-line shape instead, and a centred text
        //    would be stamped Rider against a PairId no emitted label owns. LabelPairing dissolves such an
        //    orphan, so this is about the pairing site telling the truth, not about a visible defect. ──
        [Test]
        public void ViewportTextWithAlongLineIcon_StampsNoPairRole()
        {
            // Text resolves VIEWPORT (explicit) -> at-anchors; the icon's alignment is unset -> auto -> MAP
            // under line placement -> the along-line shape. Anchors/offsets are left at their defaults, which
            // is exactly what makes the centred-pair predicate fire.
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(
                    ",\"text-field\":[\"to-string\",[\"get\",\"ref\"]],\"text-rotation-alignment\":\"viewport\""),
                BerlinFixtureTile(), BerlinTile, 13.0, new WebMercatorProjection(), labels, SyntheticShieldAtlas());

            int atAnchorTexts = 0, alongLineIcons = 0;
            foreach (SymbolStyle.SymbolLabel l in labels)
            {
                if (l.Kind == LabelKind.Text && l.Placement == SymbolPlacement.Point) atAnchorTexts++;
                if (l.Kind == LabelKind.Icon && l.Placement == SymbolPlacement.Line) alongLineIcons++;
                Assert.AreEqual(LabelPairRole.None, l.PairRole,
                    "no half of this feature may claim a pair role: the icon left for the along-line shape, " +
                    "so the at-anchors emit has no owner for a rider to point at");
                Assert.AreEqual(0, l.PairId, "PairId must stay at its unpaired default");
            }

            // Both preconditions matter: without the icons the predicate never fires (vacuous pass), and
            // without the texts there is no half left to mis-stamp.
            Assert.Greater(alongLineIcons, 0,
                "precondition: the icon must resolve AND take the along-line shape — this is what makes " +
                "`hasIcon` true while `iconAtAnchors` is false");
            Assert.Greater(atAnchorTexts, 0, "precondition: the viewport-aligned text must emit at the anchors");
        }

        // ── B2 (P-B): icon-rotate is converted ONCE (degrees -> radians) and stamped on the ICON half only. ──
        private static List<SymbolStyle.SymbolLabel> ExtractPointPairWithLayout(string layoutJson)
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var tile = new FixtureDecodedTile(new FixtureTileLayer
            {
                Name = "points", Extent = Extent, Features = new List<ITileFeature> { feature },
            });
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "icon-rotate-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(layoutJson),
            };
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0,
                new WebMercatorProjection(), labels, SyntheticShieldAtlas());
            return labels;
        }

        [Test]
        public void IconRotate_IsConvertedToRadiansOnce_AndStampedOnTheIconHalfOnly()
        {
            List<SymbolStyle.SymbolLabel> labels = ExtractPointPairWithLayout(
                "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\",\"icon-rotate\":90}");
            SymbolStyle.SymbolLabel icon = FindByKind(labels, LabelKind.Icon);
            SymbolStyle.SymbolLabel text = FindByKind(labels, LabelKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon label must be present");
            Assert.IsNotNull(text, "precondition: a text label must be present");

            // Radians, not degrees — a stamped-degrees impl reads 90, three orders of magnitude off.
            Assert.AreEqual(math.PI / 2f, icon.IconRotateRadians, 1e-5f, "icon-rotate: 90 -> pi/2 radians");
            Assert.AreEqual(0f, text.IconRotateRadians, 1e-6f, "icon-rotate never rotates text");

            // Absent -> 0 (the spec default), so every un-rotated icon composes an exact `x + 0f`.
            List<SymbolStyle.SymbolLabel> bare = ExtractPointPairWithLayout(
                "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\"}");
            Assert.AreEqual(0f, FindByKind(bare, LabelKind.Icon).IconRotateRadians, 1e-6f,
                "absent icon-rotate -> 0 radians");
        }

        [Test]
        public void IconRotate_180_IsStampedOnAnAlongLineIcon()
        {
            // The road_one_way_arrow_opposite shape: a map-resolved line icon layer with icon-rotate: 180.
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(MapAlignedIconProbeLayer(",\"icon-rotate\":180"),
                BerlinFixtureTile(), BerlinTile, 13.0, new WebMercatorProjection(), labels, SyntheticShieldAtlas());

            int icons = 0;
            foreach (SymbolStyle.SymbolLabel l in labels)
            {
                if (l.Kind != LabelKind.Icon) continue;
                icons++;
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "precondition: the along-line emit shape");
                Assert.AreEqual(math.PI, l.IconRotateRadians, 1e-5f, "icon-rotate: 180 -> pi radians");
            }
            Assert.Greater(icons, 0, "precondition: the layer must emit along-line icons");
        }

        // ── Shared centred-pair synthetic fixture for T9/T12 (isolates G5/D5 from G1-G4: literal "point"
        //    placement, default centred anchors — unaffected by the step-expression/anchor-emit machinery).
        //    <paramref name="extraLayout"/> appends further layout members (stage C's optional flags). ──
        private static List<SymbolStyle.SymbolLabel> ExtractCentredPairLabels(string extraLayout = null)
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var layer = new FixtureTileLayer
            {
                Name = "points",
                Extent = Extent,
                Features = new List<ITileFeature> { feature },
            };
            var tile = new FixtureDecodedTile(layer);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "centred-pair-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\"" // symbol-placement default = point
                    + (extraLayout == null ? "" : "," + extraLayout) + "}"),
            };
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0,
                new WebMercatorProjection(), labels, SyntheticShieldAtlas());
            return labels;
        }

        private static uint[] SinglePointGeometry(double2 p)
            => new uint[] { 1u | (1u << 3), ZigZagEncode((long)p.x), ZigZagEncode((long)p.y) };

        private static SymbolStyle.SymbolLabel FindByKind(List<SymbolStyle.SymbolLabel> labels, LabelKind kind)
        {
            foreach (SymbolStyle.SymbolLabel l in labels) if (l.Kind == kind) return l;
            return null;
        }

        // §10 D8 test helper (shared with SymbolPairPredicateTests — see SymbolTestFixtures.StageInputFor).
        private static PointStageInput StageInputFor(SymbolStyle.SymbolLabel label, LabelKind atlasKind,
            float2 boundsMin, float2 boundsMax, float2 screenPx, float textSizePx)
            => SymbolTestFixtures.StageInputFor(label, atlasKind, boundsMin, boundsMax, screenPx, textSizePx);

        // A single synthetic quad standing in for a shaped text run (SymbolLabel carries no Layout — shaping is
        // Unity-side) — its exact footprint is irrelevant to these teeth, only that quads.Length > 0.
        private static SymbolQuad SyntheticTextQuad() => new SymbolQuad
        {
            TopLeft = new float2(-6f, 6f), BottomRight = new float2(6f, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1),
        };

        // ── P3 (§10, REPLACES the withdrawn T9) ───────────────────────────────────────────────────────────
        [Test]
        public void CentredPair_AtomicPlacement_OneCandidateBothBoxes_PairDropsTogether_NoBareNumber()
        {
            List<SymbolStyle.SymbolLabel> labels = ExtractCentredPairLabels();
            Assert.AreEqual(2, labels.Count, "precondition: the centred-pair feature must emit exactly 2 labels");

            SymbolStyle.SymbolLabel icon = FindByKind(labels, LabelKind.Icon);
            SymbolStyle.SymbolLabel text = FindByKind(labels, LabelKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon label must be present");
            Assert.IsNotNull(text, "precondition: a text label must be present");
            Assert.AreEqual(LabelPairRole.Owner, icon.PairRole, "precondition: the icon must be the resolved Owner");
            Assert.AreEqual(LabelPairRole.Rider, text.PairRole, "precondition: the text must be the resolved Rider");
            Assert.IsTrue(LabelPairing.TryGetRider(labels, labels.IndexOf(icon), out int riderIdx));
            Assert.AreEqual(labels.IndexOf(text), riderIdx, "precondition: LabelPairing resolves the SAME pair the extractor proposed");

            var ownerInput = StageInputFor(icon, LabelKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            var riderInput = StageInputFor(text, LabelKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);

            var boxes = new LabelBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new LabelCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            // Candidate 0: a higher-priority blocker sitting where the icon box will land — big enough to
            // cover it regardless of icon-padding's exact magnitude.
            boxes[boxCount++] = new LabelBox { Min = new float2(900, 900), Max = new float2(1100, 1100) };
            candidates[0] = new LabelCandidate
            {
                BoxStart = 0, BoxCount = 1, EmitStart = 0, EmitCount = 0,
                SortKey = -1f, FeatureIndex = -1, TileKey = 999, LabelIndex = 0,
            };

            // Candidate 1: the pair, staged for REAL via StagePointPair (the §10 D8 production code path).
            int staged = LabelStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 1,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged, "the pair stages as exactly one candidate");

            LabelCandidate pairCand = candidates[1];
            Assert.AreEqual(2, pairCand.BoxCount, "the candidate spans BOTH halves' boxes");
            Assert.AreEqual(2, pairCand.EmitCount, "the candidate spans BOTH halves' emits");
            Assert.AreEqual(LabelKind.Icon, emit[pairCand.EmitStart].AtlasKind, "emits (Icon, Text) in order");
            Assert.AreEqual(LabelKind.Text, emit[pairCand.EmitStart + 1].AtlasKind);

            var survivor = new bool[2];
            var grid = new LabelCollisionGrid();
            LabelCollision.SelectSurvivors(candidates, 2, boxes, boxCount, survivor, grid);

            bool blockerPlaced = false, pairPlaced = false;
            for (int k = 0; k < 2; k++)
            {
                if (candidates[k].LabelIndex == 0) blockerPlaced = survivor[k];
                else if (candidates[k].LabelIndex == 1) pairPlaced = survivor[k];
            }
            Assert.IsTrue(blockerPlaced, "the higher-priority blocker must place");
            Assert.IsFalse(pairPlaced,
                "the pair must be DROPPED TOGETHER — this is the inverse of the withdrawn T12's accepted bare number");
        }

        // ── P4 (§10) — a PLACED pair blocks through BOTH boxes ───────────────────────────────────────────
        [Test]
        public void CentredPair_Placed_BlocksThroughBothBoxes_LaterLabelOverlappingOnlyTextIsDropped()
        {
            List<SymbolStyle.SymbolLabel> labels = ExtractCentredPairLabels();
            SymbolStyle.SymbolLabel icon = FindByKind(labels, LabelKind.Icon);
            SymbolStyle.SymbolLabel text = FindByKind(labels, LabelKind.Text);
            Assert.IsNotNull(icon); Assert.IsNotNull(text);

            var ownerInput = StageInputFor(icon, LabelKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            var riderInput = StageInputFor(text, LabelKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);
            // Move the text box away from the icon box (viewport-anchored translate) so a later label can
            // overlap ONLY the text half — isolating "the pair blocks through BOTH boxes" from "the icon alone".
            riderInput.TranslatePx = new float2(60f, 0f);
            riderInput.TranslateAnchor = TextTranslateAnchor.Viewport;

            var boxes = new LabelBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new LabelCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            int staged = LabelStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged);

            LabelCandidate pairCand = candidates[0];
            LabelBox iconBox = boxes[pairCand.BoxStart];
            LabelBox textBox = boxes[pairCand.BoxStart + 1];
            Assert.IsFalse(LabelCollision.Overlaps(in iconBox, in textBox),
                "precondition: icon/text boxes must be disjoint, or this tooth cannot isolate the text-only overlap");

            var laterBox = new LabelBox { Min = textBox.Min - new float2(1, 1), Max = textBox.Max + new float2(1, 1) };
            boxes[boxCount] = laterBox;
            candidates[1] = new LabelCandidate
            {
                BoxStart = boxCount, BoxCount = 1, EmitStart = emitCount, EmitCount = 0,
                SortKey = pairCand.SortKey + 1f, FeatureIndex = 999, TileKey = 999, LabelIndex = 1,
            };
            boxCount++;

            var survivor = new bool[2];
            var grid = new LabelCollisionGrid();
            LabelCollision.SelectSurvivors(candidates, 2, boxes, boxCount, survivor, grid);

            bool pairPlaced = false, laterPlaced = false;
            for (int k = 0; k < 2; k++)
            {
                if (candidates[k].LabelIndex == 0) pairPlaced = survivor[k];
                else if (candidates[k].LabelIndex == 1) laterPlaced = survivor[k];
            }
            Assert.IsTrue(pairPlaced, "the pair must place (nothing blocks it)");
            Assert.IsFalse(laterPlaced,
                "a later label overlapping ONLY the text box must still be blocked — the pair blocks through both boxes " +
                "(today's un-fixed extractor forces the passenger's IgnorePlacement=true, so this would incorrectly survive)");
        }

        // ── P5 (§10) — one FadeId per pair, from the OWNER ───────────────────────────────────────────────
        [Test]
        public void CentredPair_OneFadeId_FromTheOwner_NotTheRider()
        {
            List<SymbolStyle.SymbolLabel> labels = ExtractCentredPairLabels();
            SymbolStyle.SymbolLabel icon = FindByKind(labels, LabelKind.Icon);
            SymbolStyle.SymbolLabel text = FindByKind(labels, LabelKind.Text);

            var ownerInput = StageInputFor(icon, LabelKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            ownerInput.FadeId = 42L;
            var riderInput = StageInputFor(text, LabelKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);
            riderInput.FadeId = 999L; // deliberately DIFFERENT — must never leak into the pair's one FadeId

            var boxes = new LabelBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new LabelCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            int staged = LabelStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged);
            Assert.AreEqual(2, emitCount, "sanity: two emits were staged"); // guards the next line's premise
            Assert.AreEqual(42L, candidates[0].FadeId, "the pair's ONE FadeId must be the OWNER's — the text contributes no candidate/fade record of its own");
        }

        // ── C2 (stage C) — the flags land on the right HALF, and the pair still FORMS in every case ───────
        [Test]
        public void IconAndTextOptional_StampTheMatchingHalf_AndThePairStillForms()
        {
            void AssertPair(string extraLayout, bool expectIconOptional, bool expectTextOptional, string what)
            {
                List<SymbolStyle.SymbolLabel> labels = ExtractCentredPairLabels(extraLayout);
                Assert.AreEqual(2, labels.Count, $"{what}: the centred-pair feature must still emit exactly 2 labels");
                SymbolStyle.SymbolLabel icon = FindByKind(labels, LabelKind.Icon);
                SymbolStyle.SymbolLabel text = FindByKind(labels, LabelKind.Text);
                Assert.IsNotNull(icon, what); Assert.IsNotNull(text, what);

                // The ANTI-D11 assertion: optionality must NOT un-pair the halves. D11's recorded "pair only
                // when both flags are false" would leave these None — and, because a centred pair's boxes
                // overlap by construction, make the two halves mutually exclusive (the bare-number defect).
                Assert.AreEqual(LabelPairRole.Owner, icon.PairRole, $"{what}: the icon must STILL be the pair Owner");
                Assert.AreEqual(LabelPairRole.Rider, text.PairRole, $"{what}: the text must STILL be the pair Rider");
                Assert.AreEqual(icon.PairId, text.PairId, $"{what}: both halves must still share one PairId");

                // icon-optional makes the ICON droppable; text-optional makes the TEXT droppable.
                Assert.AreEqual(expectIconOptional, icon.PairOptional, $"{what}: icon half's PairOptional");
                Assert.AreEqual(expectTextOptional, text.PairOptional, $"{what}: text half's PairOptional");
            }

            AssertPair(null, false, false, "neither property");
            AssertPair("\"text-optional\":true", false, true, "text-optional only (the airport shape)");
            AssertPair("\"icon-optional\":true", true, false, "icon-optional only (the label_* shape)");
            AssertPair("\"icon-optional\":true,\"text-optional\":true", true, true, "both");
        }

        // ── Stage C shared harness (C3/C4/C5) ─────────────────────────────────────────────────────────────
        // The REAL extractor → REAL StagePointPair → REAL SelectSurvivors, with the rider translated clear of
        // the owner so a blocker can address exactly ONE half's box (a centred pair's boxes overlap by
        // construction — the reason the mask is consulted inside test-all-then-insert rather than by
        // un-pairing). Two low-priority PROBES, one over each half and both disjoint from the blocker, then
        // report which boxes the pair actually RESERVED: a probe that places proves its half's box was never
        // inserted.
        private struct OptionalPairOutcome
        {
            public bool PairPlaced;
            public byte OptionalBoxMask;
            public byte DroppedBoxMask;
            public bool ProbeOverIconPlaced;
            public bool ProbeOverTextPlaced;
        }

        private static OptionalPairOutcome RunOptionalPairScene(string extraLayout, LabelKind blockedHalf)
        {
            List<SymbolStyle.SymbolLabel> labels = ExtractCentredPairLabels(extraLayout);
            SymbolStyle.SymbolLabel icon = FindByKind(labels, LabelKind.Icon);
            SymbolStyle.SymbolLabel text = FindByKind(labels, LabelKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon label must be present");
            Assert.IsNotNull(text, "precondition: a text label must be present");
            Assert.AreEqual(LabelPairRole.Owner, icon.PairRole, "precondition: the pair must form regardless of the flags");
            Assert.AreEqual(LabelPairRole.Rider, text.PairRole, "precondition: the pair must form regardless of the flags");

            var ownerInput = StageInputFor(icon, LabelKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            var riderInput = StageInputFor(text, LabelKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);
            riderInput.TranslatePx = new float2(200f, 0f);
            riderInput.TranslateAnchor = TextTranslateAnchor.Viewport;

            var boxes = new LabelBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new LabelCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            int staged = LabelStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged, "precondition: the pair stages as exactly one candidate");
            Assert.AreEqual(2, candidates[0].BoxCount, "precondition: the candidate must span BOTH halves' boxes");

            LabelBox iconBox = boxes[0];
            LabelBox textBox = boxes[1];
            Assert.IsFalse(LabelCollision.Overlaps(in iconBox, in textBox),
                "precondition: the halves' boxes must be disjoint here, or a blocker cannot address one alone");

            LabelBox target = blockedHalf == LabelKind.Icon ? iconBox : textBox;
            LabelBox spared = blockedHalf == LabelKind.Icon ? textBox : iconBox;
            var blockerBox = new LabelBox
            {
                Min = new float2(target.Min.x - 5f, target.Min.y), Max = new float2(target.Min.x + 2f, target.Max.y),
            };
            Assert.IsTrue(LabelCollision.Overlaps(in blockerBox, in target),
                "precondition: the blocker must overlap the targeted half's box");
            Assert.IsFalse(LabelCollision.Overlaps(in blockerBox, in spared),
                "precondition: the blocker must NOT overlap the other half's box");

            var iconProbeBox = new LabelBox
            {
                Min = new float2(iconBox.Max.x - 2f, iconBox.Min.y), Max = new float2(iconBox.Max.x + 5f, iconBox.Max.y),
            };
            var textProbeBox = new LabelBox
            {
                Min = new float2(textBox.Max.x - 2f, textBox.Min.y), Max = new float2(textBox.Max.x + 5f, textBox.Max.y),
            };
            Assert.IsFalse(LabelCollision.Overlaps(in blockerBox, in iconProbeBox),
                "precondition: the icon probe must be clear of the blocker, so only the PAIR can block it");
            Assert.IsFalse(LabelCollision.Overlaps(in blockerBox, in textProbeBox),
                "precondition: the text probe must be clear of the blocker");
            Assert.IsFalse(LabelCollision.Overlaps(in iconProbeBox, in textProbeBox),
                "precondition: the two probes must not block one another");
            Assert.IsFalse(LabelCollision.Overlaps(in iconProbeBox, in textBox),
                "precondition: the icon probe must address the ICON box only");
            Assert.IsFalse(LabelCollision.Overlaps(in textProbeBox, in iconBox),
                "precondition: the text probe must address the TEXT box only");

            LabelCandidate Probe(int boxIndex, float sortKey, int labelIndex) => new LabelCandidate
            {
                BoxStart = boxIndex, BoxCount = 1, EmitStart = emitCount, EmitCount = 0,
                SortKey = sortKey, FeatureIndex = 900 + labelIndex, TileKey = 900 + labelIndex, LabelIndex = labelIndex,
            };

            boxes[boxCount] = blockerBox;   candidates[1] = Probe(boxCount, -1f, 1); boxCount++;
            boxes[boxCount] = iconProbeBox; candidates[2] = Probe(boxCount, 1f, 2);  boxCount++;
            boxes[boxCount] = textProbeBox; candidates[3] = Probe(boxCount, 2f, 3);  boxCount++;

            var survivor = new bool[4];
            var grid = new LabelCollisionGrid();
            LabelCollision.SelectSurvivors(candidates, 4, boxes, boxCount, survivor, grid);

            var outcome = new OptionalPairOutcome();
            bool blockerPlaced = false;
            for (int k = 0; k < 4; k++)
            {
                switch (candidates[k].LabelIndex)
                {
                    case 0:
                        outcome.PairPlaced = survivor[k];
                        outcome.OptionalBoxMask = candidates[k].OptionalBoxMask;
                        outcome.DroppedBoxMask = candidates[k].DroppedBoxMask;
                        break;
                    case 1: blockerPlaced = survivor[k]; break;
                    case 2: outcome.ProbeOverIconPlaced = survivor[k]; break;
                    case 3: outcome.ProbeOverTextPlaced = survivor[k]; break;
                }
            }
            Assert.IsTrue(blockerPlaced, "precondition: the highest-priority blocker must place");
            return outcome;
        }

        // ── C3 (stage C) — text-optional: the ICON places without its text ────────────────────────────────
        [Test]
        public void TextOptional_TextBoxBlocked_IconStillPlaces_AndTheTextBoxReservesNothing()
        {
            OptionalPairOutcome o = RunOptionalPairScene("\"text-optional\":true", LabelKind.Text);

            Assert.AreEqual(0b10, o.OptionalBoxMask, "text-optional marks the RIDER (bit 1) droppable, not the owner");
            Assert.IsTrue(o.PairPlaced,
                "the pair must SURVIVE on its icon alone — un-fixed, all-or-nothing drops the whole candidate");
            Assert.AreEqual(0b10, o.DroppedBoxMask, "exactly the text half was dropped");
            Assert.IsTrue(o.ProbeOverTextPlaced,
                "a later label over the DROPPED text box must place — a dropped half reserves nothing");
            Assert.IsFalse(o.ProbeOverIconPlaced,
                "the surviving icon half must still block: only the dropped box is released");
        }

        // ── C4 (stage C) — icon-optional: the TEXT places without its icon (the mirror of C3) ─────────────
        [Test]
        public void IconOptional_IconBoxBlocked_TextStillPlaces_AndTheIconBoxReservesNothing()
        {
            OptionalPairOutcome o = RunOptionalPairScene("\"icon-optional\":true", LabelKind.Icon);

            Assert.AreEqual(0b01, o.OptionalBoxMask, "icon-optional marks the OWNER (bit 0) droppable, not the rider");
            Assert.IsTrue(o.PairPlaced, "the pair must SURVIVE on its text alone");
            Assert.AreEqual(0b01, o.DroppedBoxMask, "exactly the icon half was dropped");
            Assert.IsTrue(o.ProbeOverIconPlaced,
                "a later label over the DROPPED icon box must place — a dropped half reserves nothing");
            Assert.IsFalse(o.ProbeOverTextPlaced, "the surviving text half must still block");
        }

        // ── C5 (stage C) — the both-false regression: §10 P3's all-or-nothing is UNCHANGED ────────────────
        [Test]
        public void NeitherOptional_TextBoxBlocked_TheWholePairDrops_AndReservesNothing()
        {
            OptionalPairOutcome o = RunOptionalPairScene(null, LabelKind.Text);

            Assert.AreEqual(0, o.OptionalBoxMask, "the spec default leaves NEITHER half optional");
            Assert.IsFalse(o.PairPlaced,
                "with both properties defaulted the pair must still drop TOGETHER (§10 P3) — no bare badge");
            Assert.AreEqual(0, o.DroppedBoxMask, "a dropped candidate records no per-half verdict");
            Assert.IsTrue(o.ProbeOverIconPlaced, "a dropped pair reserves NO box, so both probes place");
            Assert.IsTrue(o.ProbeOverTextPlaced);
        }

        // ── T13 ────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void UsShieldLayers_SelectNothingFromBerlinFixture()
        {
            SymbolStyle.StyleLayer interstate = FindShieldLayer("highway-shield-us-interstate");
            SymbolStyle.StyleLayer usShield = FindShieldLayer("road_shield_us");
            Assert.IsNotNull(interstate); Assert.IsNotNull(usShield);

            int interstateCount = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(interstate, BerlinFixtureTile(), 13.0).Count;
            int usShieldCount = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(usShield, BerlinFixtureTile(), 13.0).Count;

            Assert.AreEqual(0, interstateCount, "highway-shield-us-interstate must select 0 features from the Berlin fixture");
            Assert.AreEqual(0, usShieldCount, "road_shield_us must select 0 features from the Berlin fixture");
        }

        // ── T14 ────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void PointAnchor_ClipSemantics()
        {
            var projection = new WebMercatorProjection();

            // (a) mid-arc lands OUTSIDE [0, extent) — the accepted loss.
            {
                var feature = new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.LineString,
                    Geometry = LineStringGeometry(new double2(-1000, 500), new double2(100, 600)),
                };
                var layer = new FixtureTileLayer { Name = "lines", Extent = Extent, Features = new List<ITileFeature> { feature } };
                var tile = new FixtureDecodedTile(layer);
                var styleLayer = new SymbolStyle.StyleLayer
                {
                    Id = "clip-a", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                    LayoutJson = MapRenderer.Core.Json.JsonParser.Parse("{\"text-field\":\"L\",\"symbol-placement\":\"point\"}"),
                };
                var labels = new List<SymbolStyle.SymbolLabel>();
                SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0, projection, labels);
                Assert.AreEqual(0, labels.Count, "a buffer-dominated path (mid-arc outside [0,extent)) must emit ZERO labels");
            }

            // (b) mid-arc lands INSIDE [0, extent) — exactly one label, at the true mid-arc point.
            {
                double2 p0 = new double2(500, 500), p1 = new double2(3500, 3500);
                var feature = new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.LineString,
                    Geometry = LineStringGeometry(p0, p1),
                };
                var layer = new FixtureTileLayer { Name = "lines", Extent = Extent, Features = new List<ITileFeature> { feature } };
                var tile = new FixtureDecodedTile(layer);
                var styleLayer = new SymbolStyle.StyleLayer
                {
                    Id = "clip-b", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                    LayoutJson = MapRenderer.Core.Json.JsonParser.Parse("{\"text-field\":\"L\",\"symbol-placement\":\"point\"}"),
                };
                var labels = new List<SymbolStyle.SymbolLabel>();
                SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0, projection, labels);
                Assert.AreEqual(1, labels.Count, "an in-tile mid-arc must emit exactly one label");

                double2 midTile = (p0 + p1) * 0.5;
                double2 lonLat = SyntheticTileId.ToLonLat(midTile.x, midTile.y, Extent);
                double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                Assert.AreEqual(expected.x, labels[0].AnchorRender.x, 1e-6, "anchor must be the true mid-arc point");
                Assert.AreEqual(expected.y, labels[0].AnchorRender.y, 1e-6);
                Assert.AreEqual(expected.z, labels[0].AnchorRender.z, 1e-6);
            }

            // (c) under LINE placement (viewport-aligned -> upright-at-anchor), only in-tile anchors emit.
            {
                double2 p0 = new double2(-3000, 1000), p1 = new double2(3000, 1000); // length 6000
                var feature = new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.LineString,
                    Geometry = LineStringGeometry(p0, p1),
                };
                var layer = new FixtureTileLayer { Name = "lines", Extent = Extent, Features = new List<ITileFeature> { feature } };
                var tile = new FixtureDecodedTile(layer);
                var styleLayer = new SymbolStyle.StyleLayer
                {
                    Id = "clip-c", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                    LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                        "{\"text-field\":\"L\",\"symbol-placement\":\"line\",\"text-rotation-alignment\":\"viewport\",\"symbol-spacing\":100}"),
                };
                var labels = new List<SymbolStyle.SymbolLabel>();
                SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0, projection, labels);

                Assert.Greater(labels.Count, 0, "an edge-crossing viewport-aligned line must still emit some upright labels");
                foreach (SymbolStyle.SymbolLabel l in labels)
                    Assert.AreEqual(SymbolPlacement.Point, l.Placement, "upright-at-anchor labels are Point-placed");

                // Independently compute the FULL anchor set (unclipped) and confirm some fall outside [0,extent) —
                // i.e. this scenario genuinely exercises the clip, not a vacuous all-in-tile case.
                double spacingTileUnits = 100.0 * Extent / MapRenderer.Core.Geo.WebMercator.TilePixelSize;
                LineAnchor[] allAnchors = LineAnchorPlacement.Compute(new List<double2> { p0, p1 }, spacingTileUnits, SymbolPlacement.Line);
                int outOfTile = 0;
                foreach (LineAnchor a in allAnchors)
                {
                    // Single-segment path (Segment is always 0) — manual lerp, mirroring
                    // SymbolFeatureExtractorTests' anchor-resolve pattern (math.lerp(double2,double2,double)
                    // DOES exist in the shim — VectorMath.cs:51 — this is just the established local idiom).
                    double2 pos = p0 + (p1 - p0) * (double)a.T;
                    if (pos.x < 0.0 || pos.x >= Extent) outOfTile++;
                }
                Assert.Greater(outOfTile, 0, "precondition: this scenario must genuinely have out-of-tile anchors");
                Assert.Less(labels.Count, allAnchors.Length, "some anchors must have been clipped away");
            }
        }

        // ── T15 ────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void SymbolPlacement_IsEvaluatedAtBuildZoom()
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.LineString,
                Geometry = LineStringGeometry(new double2(500, 500), new double2(3500, 3500)),
            };
            var layer = new FixtureTileLayer { Name = "lines", Extent = Extent, Features = new List<ITileFeature> { feature } };
            var tile = new FixtureDecodedTile(layer);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "step-probe", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"text-field\":\"L\",\"symbol-placement\":[\"step\",[\"zoom\"],\"point\",11,\"line\"]}"),
            };
            var projection = new WebMercatorProjection();

            var below = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 10.9, projection, below);
            var above = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 11.0, projection, above);

            Assert.Greater(below.Count, 0, "z10.9 (below the z11 step) must emit the point shape");
            foreach (SymbolStyle.SymbolLabel l in below)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "z10.9 must yield Point placement");

            Assert.Greater(above.Count, 0, "z11.0 (at/above the z11 step) must emit the line shape");
            foreach (SymbolStyle.SymbolLabel l in above)
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "z11.0 must yield Line placement");
        }

        // ── T1 (Phase 2 / D1) ──────────────────────────────────────────────────────────────────────────
        [Test]
        public void SymbolPlacement_StepExpressions_EvaluatePerBuildZoom()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            SymbolStyle.StyleLayer usShield = FindShieldLayer("road_shield_us");
            SymbolStyle.StyleLayer interstate = FindShieldLayer("highway-shield-us-interstate");
            SymbolStyle.StyleLayer literalLine = FindShieldLayer("highway-name-major");
            Assert.IsNotNull(nonUs); Assert.IsNotNull(usShield); Assert.IsNotNull(interstate); Assert.IsNotNull(literalLine);

            Assert.AreEqual(SymbolPlacement.Point, nonUs.Layout.SymbolPlacement.Evaluate(10.0), "non-us z10 (below its z11 step) -> Point");
            Assert.AreEqual(SymbolPlacement.Line, nonUs.Layout.SymbolPlacement.Evaluate(11.0), "non-us z11 (at its step) -> Line");

            Assert.AreEqual(SymbolPlacement.Point, usShield.Layout.SymbolPlacement.Evaluate(10.0), "road_shield_us z10 -> Point");
            Assert.AreEqual(SymbolPlacement.Line, usShield.Layout.SymbolPlacement.Evaluate(11.0), "road_shield_us z11 -> Line");

            // Interstate's step boundary is z7, NOT z11 — its expression is ["step",["zoom"],"point",7,"line",8,"line"].
            Assert.AreEqual(SymbolPlacement.Point, interstate.Layout.SymbolPlacement.Evaluate(6.0), "interstate z6 (below its z7 step) -> Point");
            Assert.AreEqual(SymbolPlacement.Line, interstate.Layout.SymbolPlacement.Evaluate(7.0), "interstate z7 (at its step) -> Line");

            // A literal "line" layer (not a step expression) must read Line at every zoom.
            Assert.AreEqual(SymbolPlacement.Line, literalLine.Layout.SymbolPlacement.Evaluate(10.0), "highway-name-major z10 -> Line (literal)");
            Assert.AreEqual(SymbolPlacement.Line, literalLine.Layout.SymbolPlacement.Evaluate(11.0), "highway-name-major z11 -> Line (literal)");
        }

        // ── T6 (Phase 1) ───────────────────────────────────────────────────────────────────────────────
        [Test]
        public void AlignmentAuto_ResolvesToMapOnLine()
        {
            Assert.AreEqual(AlignmentMode.Map, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.Line));
            Assert.AreEqual(AlignmentMode.Map, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.LineCenter));
            Assert.AreEqual(AlignmentMode.Viewport, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.Point));

            foreach (SymbolPlacement placement in new[] { SymbolPlacement.Point, SymbolPlacement.Line, SymbolPlacement.LineCenter })
            {
                Assert.AreEqual(AlignmentMode.Map, AlignmentResolution.Resolve(AlignmentMode.Map, placement),
                    "an explicit Map must pass through unchanged");
                Assert.AreEqual(AlignmentMode.Viewport, AlignmentResolution.Resolve(AlignmentMode.Viewport, placement),
                    "an explicit Viewport must pass through unchanged");
            }
        }

        // ── KL-A1: the along-line anchor clip ──────────────────────────────────────────────────────────
        // One horizontal road crossing a VERTICAL seam, with the IDENTICAL local geometry in both tiles —
        // which is what a tiler emits for a road spanning a seam, each tile clipping it to its own box plus a
        // 128-unit buffer. Both tiles therefore compute the same local anchor phase, and the two halves of the
        // road claim the same world stretch twice.
        //
        // The vertices at local x == 0 and x == 4096 are load-bearing, not decoration. LineAnchor.T is a
        // FLOAT, so an anchor resolved in the middle of a long segment lands ~1e-4 tile units off its ideal
        // position — which on a boundary anchor is enough to decide the `< extent` comparison the WRONG way
        // and make the tooth measure float rounding instead of the clip. With a vertex AT each boundary the
        // containing arc ends exactly on that vertex, so LineAnchorPlacement resolves t == 1.0f exactly (and
        // dyadic k/16 in between); every anchor then sits bit-exactly on local x == 256k, and `>= extent` vs
        // `> extent` / `>= 0` vs `> 0` are genuinely discriminated.
        private static readonly double2[] SeamRoadVertices =
        {
            new double2(-128, 2048), new double2(0, 2048), new double2(4096, 2048), new double2(4224, 2048),
        };

        private const double SeamSpacingPx        = 32.0;   // 32 · 4096 / 512 == 256 tile units
        private const double SeamAnchorStride     = 256.0;
        private const int    SeamPreClipAnchors   = 17;     // arcs 256·(k+0.5) <= 4352  =>  k = 0..16
        private static readonly TileId SeamTileA = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId SeamTileB = new TileId { Z = 1, X = 1, Y = 0 };

        /// <summary>A path whose every anchor falls in the buffer strip: length 100 &lt; one spacing, so
        /// <c>Compute</c> falls back to a single centred anchor, at local x == -70.</summary>
        private static readonly double2[] BufferOnlyRoadVertices =
        {
            new double2(-120, 2048), new double2(-20, 2048),
        };

        /// <summary>The two emit shapes that ride the shared along-line anchors — the clip must cover both,
        /// and only running every tooth in both arms proves it.</summary>
        public enum SeamArm { AlongLineIcon, CurvedText }

        // No rotation-alignment on the icon arm: unset -> auto -> MAP under line placement (D3), the
        // road_one_way_arrow* shape. The text arm declares map explicitly so it stays curved rather than
        // resolving to the upright at-anchors shape.
        private static SymbolStyle.StyleLayer SeamLayer(SeamArm arm) => new SymbolStyle.StyleLayer
        {
            Id          = "seam-clip-probe",
            LayerType   = StyleLayerType.Symbol,
            Source      = "s",
            SourceLayer = "lines",
            LayoutJson  = MapRenderer.Core.Json.JsonParser.Parse(
                arm == SeamArm.AlongLineIcon
                    ? "{\"icon-image\":\"road_3\",\"symbol-placement\":\"line\",\"symbol-spacing\":32}"
                    : "{\"text-field\":\"L\",\"symbol-placement\":\"line\",\"text-rotation-alignment\":\"map\"," +
                      "\"symbol-spacing\":32}"),
        };

        private static IDecodedTile LineTile(params double2[][] paths)
        {
            var features = new List<ITileFeature>();
            foreach (double2[] path in paths)
                features.Add(new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.LineString,
                    Geometry     = LineStringGeometry(path),
                });
            return new FixtureDecodedTile(new FixtureTileLayer
            {
                Name = "lines", Extent = Extent, Features = features,
            });
        }

        private static List<SymbolStyle.SymbolLabel> ExtractLines(
            SymbolStyle.StyleLayer layer, TileId tileId, params double2[][] paths)
        {
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(layer, LineTile(paths), tileId, 1.0,
                new WebMercatorProjection(), labels, SyntheticShieldAtlas());
            return labels;
        }

        /// <summary>The tile-space x an anchor resolves to on <paramref name="path"/> — the same
        /// <c>lerp(v[Segment], v[Segment+1], T)</c> recovery <see cref="LineAnchor"/> documents, computed here
        /// from the test's own vertices so no production code is borrowed to check production code.</summary>
        private static double AnchorLocalX(LineAnchor anchor, double2[] path)
            => math.lerp(path[anchor.Segment], path[anchor.Segment + 1], anchor.T).x;

        /// <summary>Every emitted along-line anchor of <paramref name="labels"/>, as a world x in tile units
        /// (<c>tileId.X · extent + localX</c>) — one flat list, in emit order.</summary>
        private static List<double> EmittedWorldXs(
            List<SymbolStyle.SymbolLabel> labels, TileId tileId, double2[] path)
        {
            var world = new List<double>();
            foreach (SymbolStyle.SymbolLabel l in labels)
            {
                if (l.LineAnchors == null) continue;
                foreach (LineAnchor a in l.LineAnchors)
                    world.Add(tileId.X * (double)Extent + AnchorLocalX(a, path));
            }
            return world;
        }

        /// <summary>The world anchor xs both tiles emit for the seam road, restricted to the two tiles' OWN
        /// world territory <c>[0, 8192)</c> — the outer buffer belongs to X=-1 / X=2, which this test does not
        /// extract, so counting it would compare against tiles that are not in the picture.</summary>
        private static List<double> SeamRoadWorldXs(SeamArm arm)
        {
            List<SymbolStyle.SymbolLabel> a = ExtractLines(SeamLayer(arm), SeamTileA, SeamRoadVertices);
            List<SymbolStyle.SymbolLabel> b = ExtractLines(SeamLayer(arm), SeamTileB, SeamRoadVertices);
            Assert.Greater(a.Count, 0, $"{arm}: precondition: tile A must emit a label at all");
            Assert.Greater(b.Count, 0, $"{arm}: precondition: tile B must emit a label at all");
            if (arm == SeamArm.AlongLineIcon)
            {
                Assert.Greater(CountIcons(a), 0, "precondition: the icon arm must resolve its sprite in tile A");
                Assert.Greater(CountIcons(b), 0, "precondition: the icon arm must resolve its sprite in tile B");
            }

            var world = new List<double>();
            world.AddRange(EmittedWorldXs(a, SeamTileA, SeamRoadVertices));
            world.AddRange(EmittedWorldXs(b, SeamTileB, SeamRoadVertices));
            world.RemoveAll(x => x < 0.0 || x >= 2.0 * Extent);
            return world;
        }

        private static int DistinctCount(List<double> values)
        {
            var seen = new HashSet<double>();
            foreach (double v in values) seen.Add(v);
            return seen.Count;
        }

        /// <summary>Anti-vacuity for every seam tooth: the UNCLIPPED anchor set really is 17 anchors landing
        /// bit-exactly on local x == 256k, and the two tiles' unclipped world sets really do collide — so a
        /// tooth that passes is measuring the clip, not a geometry that never reached the seam.</summary>
        private static void AssertSeamGeometryReachesTheSeam()
        {
            double spacingTileUnits = SeamSpacingPx * Extent / MapRenderer.Core.Geo.WebMercator.TilePixelSize;
            Assert.AreEqual(SeamAnchorStride, spacingTileUnits, 1e-12,
                "precondition: symbol-spacing 32 px is 256 tile units at extent 4096");

            LineAnchor[] unclipped = LineAnchorPlacement.Compute(
                new List<double2>(SeamRoadVertices), spacingTileUnits, SymbolPlacement.Line);
            Assert.AreEqual(SeamPreClipAnchors, unclipped.Length,
                "precondition: the seam road must carry 17 pre-clip anchors (arcs 256·(k+0.5) <= 4352)");

            var unclippedWorld = new List<double>();
            for (int k = 0; k < unclipped.Length; k++)
            {
                Assert.AreEqual(SeamAnchorStride * k, AnchorLocalX(unclipped[k], SeamRoadVertices),
                    $"precondition: pre-clip anchor {k} must land EXACTLY on local x == {SeamAnchorStride * k} " +
                    "(a vertex-aligned t — if this drifts, the boundary comparisons stop discriminating)");
                unclippedWorld.Add(SeamAnchorStride * k);                    // tile A, X = 0
                unclippedWorld.Add(Extent + SeamAnchorStride * k);           // tile B, X = 1
            }
            unclippedWorld.RemoveAll(x => x < 0.0 || x >= 2.0 * Extent);
            Assert.AreEqual(33, unclippedWorld.Count,
                "precondition: unclipped, the two tiles emit 33 anchors inside [0, 8192)");
            Assert.AreEqual(32, DistinctCount(unclippedWorld),
                "precondition: exactly one of those world positions (the seam, 4096) is claimed TWICE — " +
                "without this the tooth below could pass over a geometry that never doubled anything");
        }

        // T1 — CENTRAL, two-tile: the defect itself. A world position claimed by two tiles is emitted by
        // exactly one of them. Single-tile output cannot discriminate this — the duplicate exists only in the
        // union.
        [Test]
        public void SeamAnchorClip_TwoTiles_EmitEachWorldPositionExactlyOnce(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            AssertSeamGeometryReachesTheSeam();

            List<double> world = SeamRoadWorldXs(arm);
            Assert.AreEqual(DistinctCount(world), world.Count,
                $"{arm}: a road crossing a seam must yield ONE symbol per world position, not two — " +
                $"emitted {world.Count} anchors over {DistinctCount(world)} distinct world positions");
        }

        // T2 — anti-over-clip: GREEN before AND after. The surviving set is exactly the positions the geometry
        // implies, so "the fix drops symbols that should exist" fails here rather than passing quietly.
        [Test]
        public void SeamAnchorClip_KeepsEveryWorldPositionTheGeometryImplies(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            AssertSeamGeometryReachesTheSeam();

            var expected = new List<double>();
            for (int k = 0; k < 2 * SeamPreClipAnchors - 2; k++) expected.Add(SeamAnchorStride * k); // 0 .. 7936

            List<double> world = SeamRoadWorldXs(arm);
            world.Sort();
            var distinct = new List<double>();
            foreach (double x in world) if (distinct.Count == 0 || distinct[distinct.Count - 1] != x) distinct.Add(x);

            CollectionAssert.AreEqual(expected, distinct,
                $"{arm}: the two tiles together must cover exactly {{256k : k = 0..31}} over [0, 8192) — " +
                "no position orphaned by a too-tight bound, none invented");
        }

        // T3 — the boundary SENSE. The seam position is emitted exactly once and by the tile whose LOCAL
        // coordinate for it is 0, never by the one whose local coordinate is `extent`. This is the only tooth
        // that separates `< extent` from `<= extent` and `>= 0` from `> 0`.
        [Test]
        public void SeamAnchorClip_SeamPositionIsOwnedByTheTileHoldingItAtLocalZero(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            AssertSeamGeometryReachesTheSeam();

            List<double> fromA = EmittedWorldXs(
                ExtractLines(SeamLayer(arm), SeamTileA, SeamRoadVertices), SeamTileA, SeamRoadVertices);
            List<double> fromB = EmittedWorldXs(
                ExtractLines(SeamLayer(arm), SeamTileB, SeamRoadVertices), SeamTileB, SeamRoadVertices);
            Assert.Greater(fromA.Count, 0, $"{arm}: precondition: tile A must emit anchors");
            Assert.Greater(fromB.Count, 0, $"{arm}: precondition: tile B must emit anchors");

            const double seam = 4096.0;
            Assert.AreEqual(0, fromA.FindAll(x => x == seam).Count,
                $"{arm}: tile A holds the seam at local x == extent, which it does NOT own (a `<= extent` " +
                "upper bound would emit it here as well as in B — the duplicate, restored)");
            Assert.AreEqual(1, fromB.FindAll(x => x == seam).Count,
                $"{arm}: tile B holds the seam at local x == 0, which it DOES own (a `> 0` lower bound would " +
                "orphan it — emitted by neither tile)");

            Assert.AreEqual(1, fromA.FindAll(x => x == 0.0).Count,
                $"{arm}: tile A must still emit its own left edge at local x == 0 (`> 0` would drop it)");
        }

        // T7 — the SAME property on the Y axis. Every tooth above runs a horizontal road across a vertical
        // seam, so all four comparisons are exercised only through `p.x`: a predicate that tested `p.x` twice,
        // or that used `p.y <= extent`, passes every one of them. This is the transposed fixture — a vertical
        // road across a HORIZONTAL seam between z1 (0,0) and (0,1) — so the y comparisons carry the assertion.
        // Deliberately only the one discriminating claim; the x teeth already cover the shared machinery.
        [Test]
        public void SeamAnchorClip_TwoTilesStackedVertically_EmitEachWorldPositionExactlyOnce(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            // The x fixture transposed. Vertices sit ON both boundaries for the same float-exactness reason:
            // a 2-vertex path resolves the seam anchor to 4095.9998779 / +4.8e-7 and the duplicate survives.
            double2[] road =
            {
                new double2(2048, -128), new double2(2048, 0), new double2(2048, 4096), new double2(2048, 4224),
            };
            TileId tileTop    = new TileId { Z = 1, X = 0, Y = 0 };
            TileId tileBottom = new TileId { Z = 1, X = 0, Y = 1 };

            List<double> world = new List<double>();
            foreach ((TileId tile, List<SymbolStyle.SymbolLabel> labels) in new[]
                     { (tileTop,    ExtractLines(SeamLayer(arm), tileTop,    road)),
                       (tileBottom, ExtractLines(SeamLayer(arm), tileBottom, road)) })
            {
                Assert.Greater(labels.Count, 0, $"{arm}: precondition: tile {tile.Y} must emit a label");
                foreach (SymbolStyle.SymbolLabel l in labels)
                {
                    if (l.LineAnchors == null) continue;
                    foreach (LineAnchor a in l.LineAnchors)
                    {
                        double localY = math.lerp(road[a.Segment], road[a.Segment + 1], a.T).y;
                        double worldY = tile.Y * (double)Extent + localY;
                        if (worldY >= 0.0 && worldY < 2.0 * Extent) world.Add(worldY);
                    }
                }
            }

            world.Sort();
            var distinct = new List<double>();
            foreach (double y in world) if (distinct.Count == 0 || distinct[distinct.Count - 1] != y) distinct.Add(y);

            Assert.AreEqual(distinct.Count, world.Count,
                $"{arm}: a world position emitted twice means the Y bound duplicates at the seam " +
                "(`p.y <= extent`), or the predicate never tests y at all");
            Assert.AreEqual(2 * Extent / SeamAnchorStride, distinct.Count,
                $"{arm}: every position the geometry implies over [0, 2·extent) must survive — a missing one " +
                "means the Y lower bound orphans the seam (`p.y > 0`)");
        }

        // T4 — the path is NOT clipped: only anchors are filtered. A shallow implementation that clipped the
        // polyline instead would shorten PathRender and turn a join into a cap.
        [Test]
        public void SeamAnchorClip_DoesNotClipThePath(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            var projection = new WebMercatorProjection();
            List<SymbolStyle.SymbolLabel> labels = ExtractLines(SeamLayer(arm), SeamTileA, SeamRoadVertices);
            Assert.Greater(labels.Count, 0, $"{arm}: precondition: the seam road must emit a curved label");

            foreach (SymbolStyle.SymbolLabel l in labels)
            {
                Assert.IsNotNull(l.PathRender, $"{arm}: a curved label carries its projected path");
                Assert.AreEqual(SeamRoadVertices.Length, l.PathRender.Length,
                    $"{arm}: PathRender must keep every DECODED vertex — the clip filters anchors, never the path");

                foreach (int i in new[] { 0, SeamRoadVertices.Length - 1 })
                {
                    double2 lonLat = SeamTileA.ToLonLat(SeamRoadVertices[i].x, SeamRoadVertices[i].y, Extent);
                    double3 expected = projection.Project(
                        new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                    Assert.AreEqual(expected.x, l.PathRender[i].x, 1e-6,
                        $"{arm}: PathRender[{i}] must still project the OUT-OF-TILE endpoint {SeamRoadVertices[i].x}");
                    Assert.AreEqual(expected.y, l.PathRender[i].y, 1e-6);
                    Assert.AreEqual(expected.z, l.PathRender[i].z, 1e-6);
                }
            }
        }

        // T5 — a path whose every anchor belongs to a neighbour emits NOTHING here, rather than an anchor-less
        // label that can never place.
        [Test]
        public void SeamAnchorClip_PathWithNoSurvivingAnchor_EmitsNoLabel(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            // Precondition: the geometry really does produce one anchor, and it really is out of tile.
            LineAnchor[] unclipped = LineAnchorPlacement.Compute(
                new List<double2>(BufferOnlyRoadVertices),
                SeamSpacingPx * Extent / MapRenderer.Core.Geo.WebMercator.TilePixelSize, SymbolPlacement.Line);
            Assert.AreEqual(1, unclipped.Length,
                "precondition: a path shorter than one spacing falls back to a single centred anchor");
            Assert.Less(AnchorLocalX(unclipped[0], BufferOnlyRoadVertices), 0.0,
                "precondition: that anchor must lie in the buffer strip, outside [0, extent)");

            List<SymbolStyle.SymbolLabel> labels =
                ExtractLines(SeamLayer(arm), SeamTileA, BufferOnlyRoadVertices);
            Assert.AreEqual(0, labels.Count,
                $"{arm}: a buffer-only path must emit ZERO labels here — not one carrying an empty LineAnchors");
        }

        // T6 — the at-anchors arm (the shipped D4 shield shape) did not move: it already applied the same
        // predicate at EmitAtAnchor, so hoisting the test upstream is the same comparison on the same inputs.
        // Drift between the two copies of the rule shows up here as a shield regression rather than as nothing.
        [Test]
        public void SeamAnchorClip_AtAnchorsArmIsUnchanged()
        {
            var projection = new WebMercatorProjection();
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "seam-at-anchors-probe", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"text-field\":\"L\",\"symbol-placement\":\"line\",\"text-rotation-alignment\":\"viewport\"," +
                    "\"symbol-spacing\":32}"),
            };

            List<SymbolStyle.SymbolLabel> labels = ExtractLines(layer, SeamTileA, SeamRoadVertices);

            // 17 pre-clip anchors at local x = 0 .. 4096; EmitAtAnchor already dropped local 4096 (>= extent).
            Assert.AreEqual(SeamPreClipAnchors - 1, labels.Count,
                "the at-anchors arm must emit one upright label per IN-TILE anchor — 16, exactly as before");
            foreach (SymbolStyle.SymbolLabel l in labels)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "upright-at-anchor labels are Point-placed");

            foreach (int k in new[] { 0, SeamPreClipAnchors - 2 })
            {
                double2 lonLat = SeamTileA.ToLonLat(SeamAnchorStride * k, SeamRoadVertices[0].y, Extent);
                double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                Assert.AreEqual(expected.x, labels[k].AnchorRender.x, 1e-6,
                    $"label {k} must sit at local x == {SeamAnchorStride * k}");
                Assert.AreEqual(expected.y, labels[k].AnchorRender.y, 1e-6);
                Assert.AreEqual(expected.z, labels[k].AnchorRender.z, 1e-6);
            }
        }
    }
}
