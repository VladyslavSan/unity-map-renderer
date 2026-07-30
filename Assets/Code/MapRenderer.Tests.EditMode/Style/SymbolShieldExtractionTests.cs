// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
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
        // ── Walk-up fixture loaders (work in Unity batch mode AND dotnet test) — mirrors LoadFixture in
        //    SymbolFeatureExtractorTests.cs / SymbolFeatureExtractorIconTests.cs. ──
        private static string[] PathParts(string root, string[] rest)
        {
            var parts = new string[rest.Length + 1];
            parts[0] = root;
            Array.Copy(rest, 0, parts, 1, rest.Length);
            return parts;
        }

        private static string LoadUpTextParts(params string[] relSegments)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(PathParts(dir.FullName, relSegments));
                    if (File.Exists(p)) return File.ReadAllText(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("Not found walking up from cwd/AppContext: " + string.Join("/", relSegments));
        }

        private static byte[] LoadUpBytesParts(params string[] relSegments)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(PathParts(dir.FullName, relSegments));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("Not found walking up from cwd/AppContext: " + string.Join("/", relSegments));
        }

        private static string LoadLibertyJson() => LoadUpTextParts("Assets", "StreamingAssets", "Fixtures", "liberty.json");
        private static byte[] LoadBerlinFixtureBytes() => LoadUpBytesParts("Assets", "Fixtures", "boundary-9-274-168.pbf.bytes");

        // ── Real style / real fixture ─────────────────────────────────────────────────────────────────
        private static StyleDocument _libertyDoc;
        private static StyleDocument LibertyDoc() => _libertyDoc ??= StyleParser.Parse(LoadLibertyJson());

        private static SymbolStyle.StyleLayer FindShieldLayer(string id)
        {
            foreach (StyleLayer layer in LibertyDoc().Layers)
                if (layer.Id == id) return layer as SymbolStyle.StyleLayer;
            return null;
        }

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

        // ── §10 D10 negative case: a non-centred icon+text feature (text-offset != 0) stays unpaired and
        //    keeps the PRE-pairing text-then-icon order. ──
        [Test]
        public void NonCentredPair_EmitsTextThenIcon_BothRolesNone()
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
            Assert.AreEqual(LabelKind.Text, labels[0].Kind, "non-centred order stays text-then-icon");
            Assert.AreEqual(LabelKind.Icon, labels[1].Kind);
            Assert.AreEqual(LabelPairRole.None, labels[0].PairRole, "a non-centred text must NOT be paired");
            Assert.AreEqual(LabelPairRole.None, labels[1].PairRole, "a non-centred icon must NOT be paired");
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

        // ── T8 ─────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void MapAlignedLineIconLayer_EmitsNoIcons()
        {
            var handBuilt = new SymbolStyle.StyleLayer
            {
                Id = "shield-map-aligned-icon-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "openmaptiles",
                SourceLayer = "transportation_name",
                Filter = MapRenderer.Core.Json.JsonParser.Parse(
                    "[\"all\",[\"<=\",[\"get\",\"ref_length\"],6],[\"match\",[\"geometry-type\"],[\"LineString\",\"MultiLineString\"],true,false]]"),
                // No rotation-alignment declared -> auto -> resolves MAP under line placement (D3) -> the fence
                // (D4) must hold: a map-aligned line icon never emits, even with an atlas supplied.
                LayoutJson = MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"icon-image\":\"road_3\",\"symbol-placement\":\"line\"}"),
            };
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            IReadOnlyList<ITileFeature> selected = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(handBuilt, BerlinFixtureTile(), 13.0);
            Assert.Greater(selected.Count, 0, "precondition: > 0 features selected");

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolStyle.SymbolFeatureExtractor.Extract(handBuilt, BerlinFixtureTile(), BerlinTile, 13.0, projection, labels, atlas);

            Assert.AreEqual(0, CountIcons(labels), "a map-aligned line icon must never emit (the surviving fence)");
        }

        // ── Shared centred-pair synthetic fixture for T9/T12 (isolates G5/D5 from G1-G4: literal "point"
        //    placement, default centred anchors — unaffected by the step-expression/anchor-emit machinery). ──
        private static List<SymbolStyle.SymbolLabel> ExtractCentredPairLabels()
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
                    "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\"}"), // symbol-placement default = point
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

        // §10 D8 test helper: hand-builds the PointStageInput for one half of a pair from the REAL extractor's
        // own SymbolLabel — mirrors SymbolTileLabelBlockBaker.BuildPointInput's field math (the Unity-only bake
        // step itself can't run here — no Unity.Collections in Tools/core-tests — so this is the engine-free
        // subset: Color/FadeId are placeholders, unasserted by these teeth).
        private static PointStageInput StageInputFor(SymbolStyle.SymbolLabel label, LabelKind atlasKind,
            float2 boundsMin, float2 boundsMax, float2 screenPx, float textSizePx)
            => new PointStageInput
            {
                ScreenPx = screenPx, Depth = 0f, Projected = true,
                BoundsMin = boundsMin, BoundsMax = boundsMax,
                TextSizePx = textSizePx, PaddingPx = label.PaddingPx, SortKey = label.SortKey,
                FeatureIndex = label.FeatureIndex, TileKey = label.TileKey, Slot = 0,
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement,
                TranslatePx = label.TranslatePx, TranslateAnchor = label.TranslateAnchor,
                RotationAlignment = label.RotationAlignment, Color = new float4(1, 1, 1, 1),
                AtlasKind = atlasKind,
            };

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
    }
}
