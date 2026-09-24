// Style/StyleShieldTests.cs — both fixtures exercise SymbolFeatureExtractor.Extract, which depends on
// Unity.Collections; not registered in core-tests.csproj.
//
// Contents:
//   SymbolPairPredicateTests    — the icon+text pairing predicate: both halves resolved, not merely coincident.
//   SymbolShieldExtractionTests — road-shield acceptance over the real Liberty style + Berlin fixture.

using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Style
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolPairPredicateTests — the icon+text pairing predicate: both halves resolved, not merely coincident
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The icon+text PAIRING predicate is "this feature resolved both halves", not "the two halves coincide".
    /// Each tooth sets a non-default anchor or offset (text/icon anchor, text/icon offset, text-radial-offset);
    /// the centred teeth in <c>SymbolShieldExtractionTests</c> cannot tell the two predicates apart.
    /// </summary>
    [TestFixture]
    public class SymbolPairPredicateTests
    {

        /// <summary>A synthetic decoded tile owns <c>Allocator.Persistent</c> buffers, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this guard exists to catch.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private const uint Extent = 4096;
        private static readonly TileId SyntheticTileId = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId PlaceTileId = new TileId { Z = 6, X = 32, Y = 20 };
        private static readonly TileId BerlinTileId = new TileId { Z = 9, X = 274, Y = 168 };

        // ── Synthetic 16x16 sprites for liberty's names (no committed sheet has them); only EXISTENCE and
        //    size matter to these teeth. ──
        private static SpriteAtlasView SyntheticAtlas(params string[] names)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(names[i]).Append('"')
                  .Append(":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1,\"sdf\":false}");
            }
            sb.Append('}');
            return new SpriteAtlasView { Index = SpriteIndex.Parse(sb.ToString()), Size = new int2(64, 64) };
        }

        // ── Synthetic tile plumbing — the local doubles every symbol-extraction test file carries. ──
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        private static uint[] SinglePointGeometry(double2 p)
            => new uint[] { 1u | (1u << 3), ZigZagEncode((long)p.x), ZigZagEncode((long)p.y) };

        /// <summary>One `points` tile carrying a single Point feature with <c>ref = "5"</c>.</summary>
        private static IDecodedTile OnePointTile()
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            return TestDecodedTiles.Of("points", SyntheticTileId, new List<IFeature> { feature }, Extent);
        }

        private static SymbolStyle.StyleLayer PointProbeLayer(string layoutJson)
            => new SymbolStyle.StyleLayer
            {
                Id = "pair-predicate-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout(layoutJson),
            };

        private static List<SymbolStyle.SymbolFeature> ExtractPoint(string layoutJson, SpriteAtlasView atlas)
        {
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(PointProbeLayer(layoutJson), OnePointTile(), SyntheticTileId,
                0.0, new WebMercatorProjection(), symbols, atlas);
            return symbols;
        }

        private static SymbolStyle.SymbolFeature FindByKind(List<SymbolStyle.SymbolFeature> symbols, SymbolKind kind)
        {
            foreach (SymbolStyle.SymbolFeature l in symbols) if (l.Kind == kind) return l;
            return null;
        }

        // ══ Predicate symmetry: EACH retired conjunct, alone, on an otherwise centred layer ══════════
        // One case per conjunct, so dropping only the text-side checks leaves the icon-side cases None.
        [TestCase("\"text-anchor\":\"bottom\"", TestName = "RetiredConjunct_TextAnchor_StillPairs")]
        [TestCase("\"text-offset\":[0,0.6]", TestName = "RetiredConjunct_TextOffset_StillPairs")]
        [TestCase("\"text-radial-offset\":0.5", TestName = "RetiredConjunct_TextRadialOffset_StillPairs")]
        [TestCase("\"icon-anchor\":\"top-left\"", TestName = "RetiredConjunct_IconAnchor_StillPairs")]
        [TestCase("\"icon-offset\":[2,0]", TestName = "RetiredConjunct_IconOffset_StillPairs")]
        public void NonCentredHalf_StillFormsOnePairedInstance(string nonCentringProperty)
        {
            List<SymbolStyle.SymbolFeature> symbols = ExtractPoint(
                "{\"text-field\":\"{ref}\",\"icon-image\":\"road_3\"," + nonCentringProperty + "}",
                SyntheticAtlas("road_3"));

            Assert.AreEqual(2, symbols.Count, "precondition: both halves must be emitted");
            Assert.AreEqual(SymbolKind.Icon, symbols[0].Kind, "the OWNER (icon) is emitted first");
            Assert.AreEqual(SymbolKind.Text, symbols[1].Kind, "the rider text follows");
            Assert.AreEqual(SymbolPairRole.Owner, symbols[0].PairRole, nonCentringProperty + " must still pair");
            Assert.AreEqual(SymbolPairRole.Rider, symbols[1].PairRole, nonCentringProperty + " must still pair");
            Assert.AreEqual(symbols[0].FeatureIndex, symbols[0].PairId, "PairId is the owner's own FeatureIndex");
            Assert.AreEqual(symbols[0].PairId, symbols[1].PairId, "both halves share one PairId");
            Assert.AreEqual(symbols[0].FeatureIndex + 1, symbols[1].FeatureIndex,
                "owner-immediately-then-rider — SymbolPairing's adjacency contract");
        }

        // ══ A non-centred pair's two boxes land where each half's OWN baked bounds say ════════════════
        // Non-local invariant: anchor/offset are folded into each half's anchor-RELATIVE bounds upstream, so
        // StagePointPair places both halves at the OWNER's ScreenPx. Runs the REAL layout + staging chain.
        [Test]
        public void NonCentredPair_EachHalfsBox_IsBuiltFromItsOwnBakedBounds_AndTheyAreDisjoint()
        {
            // text-anchor bottom plus text-offset [0,-2] (y-DOWN, so 2 em UP) clears a 16 px icon; at a zero
            // offset the boxes coincide whatever the staging does.
            const string layoutJson =
                "{\"text-field\":\"{ref}\",\"icon-image\":\"road_3\",\"text-anchor\":\"bottom\",\"text-offset\":[0,-2]}";
            List<SymbolStyle.SymbolFeature> symbols = ExtractPoint(layoutJson, SyntheticAtlas("road_3"));
            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon half must be present");
            Assert.IsNotNull(text, "precondition: a text half must be present");
            Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole, "precondition: the halves must have paired");

            // The REAL text layout, through the REAL options builder over the SAME style layer.
            var glyphAtlas = new GlyphAtlas();
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(SymbolTestFixtures.LoadUpBytes(
                "Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes")).Stacks[0];
            var glyphs = new List<PositionedGlyph>();
            foreach (uint codepoint in new uint[] { 'A', 'b' })
            {
                GlyphAtlasEntry entry = glyphAtlas.Append(latin.Glyphs[codepoint], 0);
                glyphs.Add(new PositionedGlyph
                {
                    AtlasCodepoint = codepoint, XAdvance = entry.Advance, Cluster = glyphs.Count,
                });
            }
            var run = new ShapedRun { Glyphs = glyphs, Direction = TextDirection.LeftToRight };
            TextLayoutOptions options = SymbolStyle.TextLayoutOptionsBuilder.Build(
                PointProbeLayer(layoutJson).Layout, 0.0, null);
            var textLayoutQuads = new List<SymbolQuad>();
            TextLayoutBounds textLayout = TextQuadLayout.Layout(run, glyphAtlas, in options, textLayoutQuads);
            float iconSkirtPx = icon.IconSkirtPx;
            float2 iconSkirtV = new float2(iconSkirtPx, iconSkirtPx);
            float2 iconBoundsMin = math.min(icon.IconQuad.TopLeft, icon.IconQuad.BottomRight) + iconSkirtV;
            float2 iconBoundsMax = math.max(icon.IconQuad.TopLeft, icon.IconQuad.BottomRight) - iconSkirtV;

            var sharedScreenPx = new float2(1000f, 1000f);
            PointStageInput ownerInput = SymbolTestFixtures.StageInputFor(icon, SymbolKind.Icon,
                iconBoundsMin, iconBoundsMax, sharedScreenPx, TextQuadLayout.OneEm);
            PointStageInput riderInput = SymbolTestFixtures.StageInputFor(text, SymbolKind.Text,
                textLayout.Min, textLayout.Max, sharedScreenPx, text.TextSizePx);

            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[64];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;
            var textQuads = new SymbolQuad[textLayoutQuads.Count];
            for (int q = 0; q < textQuads.Length; q++) textQuads[q] = textLayoutQuads[q];
            int staged = SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, textQuads,
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);

            Assert.AreEqual(1, staged, "the pair stages as exactly one candidate");
            Assert.AreEqual(2, candidates[0].BoxCount, "the candidate spans BOTH halves' boxes");
            SymbolBox iconBox = boxes[candidates[0].BoxStart];
            SymbolBox textBox = boxes[candidates[0].BoxStart + 1];

            // Each half's box must be `sharedAnchor + ITS OWN bounds at ITS OWN size`. Collapsing the rider
            // onto the owner's box, or scaling the rider's bounds by the owner's text size, fails here.
            SymbolBox expectedIcon = SymbolBox.Build(sharedScreenPx, iconBoundsMin, iconBoundsMax,
                TextQuadLayout.OneEm, icon.PaddingPx, 0f, 0, 0, 0, false, false);
            SymbolBox expectedText = SymbolBox.Build(sharedScreenPx, textLayout.Min, textLayout.Max,
                text.TextSizePx, text.PaddingPx, 0f, 0, 0, 0, false, false);
            AssertBoxEqual(expectedIcon, iconBox, "the owner's box");
            AssertBoxEqual(expectedText, textBox, "the rider's box");

            // The geometric consequence, stated in its own right: a non-centred pair's boxes are DISJOINT —
            // the halves are one instance without sharing a footprint.
            Assert.IsFalse(SymbolCollision.Overlaps(in iconBox, in textBox),
                "a 2 em offset must separate the halves' boxes — coincident boxes would mean the rider was " +
                "placed at the owner's bounds rather than its own");
            Assert.Greater(textBox.Min.y, iconBox.Max.y,
                "text-anchor bottom + a 2 em upward offset puts the text strictly ABOVE the icon (the bounds " +
                "are y-UP, as TextQuadLayout bakes them)");
        }

        // ══ Liberty's city dot does not outlive its own name ════════════════
        // The dot allows overlap and the text does not; paired, AllowOverlap is the AND, so both drop together.
        [Test]
        public void LibertyLabelCity_TextBlocked_DropsTheDotToo_NoOrphanIcon()
        {
            SymbolStyle.StyleLayer labelCity = SymbolTestFixtures.FindSymbolLayer("label_city");
            Assert.IsNotNull(labelCity, "precondition: label_city must parse as a symbol layer");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            // z6 — BELOW label_city's `["step",["zoom"],"circle_11_black",9,""]`, so the dot exists at all.
            // At z >= 9 the icon-image resolves to "" and there is no icon to pair with, let alone orphan.
            SymbolFeatureExtractor.Extract(labelCity, PlaceFixtureTile(), PlaceTileId, 6.0,
                new WebMercatorProjection(), symbols, SyntheticAtlas("circle_11_black"));

            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon, "precondition: the fixture must carry a class=city place feature with a dot");
            Assert.IsNotNull(text, "precondition: that feature must also resolve a name");
            Assert.IsTrue(icon.AllowOverlap,
                "precondition: liberty sets icon-allow-overlap on the dot — the asymmetry this tooth turns on");
            Assert.IsFalse(text.AllowOverlap, "precondition: the name keeps text-allow-overlap's false default");

            // Stage as the placement system does: ONE pair candidate for a pair, two otherwise. So a broken
            // predicate reads RED as the artefact itself (an orphan dot survives).
            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[8];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            // Synthetic half-bounds + a rider translate: label_city's real -0.1 em offset cannot separate the
            // boxes, so a blocker could not hit the text half alone. The real bounds are pinned above.
            PointStageInput ownerInput = SymbolTestFixtures.StageInputFor(icon, SymbolKind.Icon,
                new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            PointStageInput riderInput = SymbolTestFixtures.StageInputFor(text, SymbolKind.Text,
                new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx);
            riderInput.TranslatePx = new float2(200f, 0f);
            riderInput.TranslateAnchor = TextTranslateAnchor.Viewport;

            int instanceCount = StageInstance(icon, text, in ownerInput, in riderInput,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.Greater(instanceCount, 0, "precondition: the instance must stage");

            SymbolBox textBox = boxes[boxCount - 1]; // the text half is always the LAST box appended above
            var blockerBox = new SymbolBox
            {
                Min = textBox.Min - new float2(1f, 1f), Max = textBox.Max + new float2(1f, 1f),
                SortKey = -1f, FeatureIndex = -1, TileKey = 999, SymbolIndex = 99,
            };
            Assert.IsFalse(SymbolCollision.Overlaps(in blockerBox, in boxes[0]),
                "precondition: the blocker must address the TEXT half alone, never the dot's box");
            candidates[instanceCount] = new SymbolCandidate
            {
                BoxStart = boxCount, BoxCount = 1, EmitStart = emitCount, EmitCount = 0,
                SortKey = -1f, FeatureIndex = -1, TileKey = 999, SymbolIndex = 99,
            };
            boxes[boxCount++] = blockerBox;

            var survivor = new bool[instanceCount + 1];
            NativeCollisionRunner.RunCollision(candidates, instanceCount + 1, boxes, boxCount, survivor);

            Assert.AreEqual(0, SurvivingEmitCount(candidates, survivor, instanceCount + 1, emit, SymbolKind.Icon),
                "the dot must go down with its name — an icon surviving a blocked text IS the orphan-dot artefact");
            Assert.AreEqual(0, SurvivingEmitCount(candidates, survivor, instanceCount + 1, emit, SymbolKind.Text),
                "the blocked name is culled too (this half was never in doubt)");
        }

        // ══ D-PA-3: text-optional does NOT un-pair; it stays a per-BOX verdict inside the pair ═══════
        [Test]
        public void LibertyAirport_TextOptional_StillPairs_AndMarksOnlyTheRiderDroppable()
        {
            SymbolStyle.StyleLayer airport = SymbolTestFixtures.FindSymbolLayer("airport");
            Assert.IsNotNull(airport, "precondition: airport must parse as a symbol layer");
            Assert.IsTrue(airport.Layout.TextOptional,
                "precondition: this tooth turns on liberty's REAL `text-optional: true` — if the style ever " +
                "drops it, the tooth is inert and must be re-pointed, not re-baked");

            // The REAL liberty layer over a hand-built feature: the Berlin fixture's airport sits at x=4929,
            // outside [0, extent), so the clip drops it.
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value>
                {
                    ["iata"] = Value.String("BER"),
                    ["name"] = Value.String("Berlin Brandenburg"),
                },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var tile = TestDecodedTiles.Of(
                "aerodrome_label", BerlinTileId, new List<IFeature> { feature }, Extent);

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(airport, tile, BerlinTileId, 9.0,
                new WebMercatorProjection(), symbols, SyntheticAtlas("airport_11"));

            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon, "precondition: the airport layer must resolve its icon");
            Assert.IsNotNull(text, "precondition: it must also resolve a name");

            // The pair FORMS despite the flag — a "pair only when both optional flags are false" rule would
            // leave these None, and then a text could place with its own required icon culled.
            Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole,
                "text-optional must not un-pair: the flag says this INSTANCE may render icon-only");
            Assert.AreEqual(SymbolPairRole.Rider, text.PairRole, "text-optional must not un-pair the text half");
            Assert.AreEqual(icon.PairId, text.PairId, "both halves share one PairId");
            Assert.IsFalse(icon.PairOptional, "icon-optional is unset — the icon half is REQUIRED");
            Assert.IsTrue(text.PairOptional, "text-optional: true lands on the TEXT half");

            PointStageInput ownerInput = SymbolTestFixtures.StageInputFor(icon, SymbolKind.Icon,
                new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            PointStageInput riderInput = SymbolTestFixtures.StageInputFor(text, SymbolKind.Text,
                new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx);

            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;
            int staged = SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);

            Assert.AreEqual(1, staged, "the pair stages as exactly one candidate");
            Assert.AreEqual(0b10, candidates[0].OptionalBoxMask,
                "optionality is a per-BOX verdict: bit 1 (the rider) droppable, bit 0 (the owner) required");
        }

        // ══ The negatives: the predicate needs BOTH halves, never either ═════════════════════════════
        [Test]
        public void OneSidedFeatures_NeverPair()
        {
            SpriteAtlasView atlas = SyntheticAtlas("road_3");

            List<SymbolStyle.SymbolFeature> textOnly = ExtractPoint("{\"text-field\":\"{ref}\"}", atlas);
            Assert.AreEqual(1, textOnly.Count, "a text-only feature emits one label");
            Assert.AreEqual(SymbolKind.Text, textOnly[0].Kind);
            Assert.AreEqual(SymbolPairRole.None, textOnly[0].PairRole, "there is no icon to pair with");

            List<SymbolStyle.SymbolFeature> iconOnly = ExtractPoint("{\"icon-image\":\"road_3\"}", atlas);
            Assert.AreEqual(1, iconOnly.Count, "an icon-only feature emits one label");
            Assert.AreEqual(SymbolKind.Icon, iconOnly[0].Kind);
            Assert.AreEqual(SymbolPairRole.None, iconOnly[0].PairRole, "there is no text to pair with");

            // A null atlas resolves no icon at all, however loudly the layer asks for one.
            var nullAtlasSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                PointProbeLayer("{\"text-field\":\"{ref}\",\"icon-image\":\"road_3\",\"text-offset\":[0,0.6]}"),
                OnePointTile(), SyntheticTileId, 0.0, new WebMercatorProjection(), nullAtlasSymbols, null);
            Assert.AreEqual(1, nullAtlasSymbols.Count, "a null atlas yields the text half alone");
            Assert.AreEqual(SymbolKind.Text, nullAtlasSymbols[0].Kind);
            Assert.AreEqual(SymbolPairRole.None, nullAtlasSymbols[0].PairRole, "no icon resolved ⇒ no pair");
        }

        // ══ The line branch re-gates on BOTH suppressions, not just the icon's ═══════════════
        // The TEXT leaves for the curved shape; without `&& textAtAnchors` a lone icon is stamped Owner.
        [Test]
        public void MapAlignedTextWithViewportIcon_StampsNoPairRole()
        {
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "mixed-alignment-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "openmaptiles",
                SourceLayer = "transportation_name",
                Filter = MapRenderer.Core.Json.JsonParser.Parse(
                    "[\"all\",[\"<=\",[\"get\",\"ref_length\"],6],[\"match\",[\"geometry-type\"],[\"LineString\",\"MultiLineString\"],true,false]]"),
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"symbol-placement\":\"line\",\"text-field\":[\"to-string\",[\"get\",\"ref\"]]," +
                    "\"icon-image\":\"road_3\",\"text-rotation-alignment\":\"map\"," +
                    "\"icon-rotation-alignment\":\"viewport\"}"),
            };

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, BerlinFixtureTile(), BerlinTileId, 13.0,
                new WebMercatorProjection(), symbols, SyntheticAtlas("road_3"));

            int atAnchorIcons = 0, curvedTexts = 0;
            foreach (SymbolStyle.SymbolFeature l in symbols)
            {
                if (l.Kind == SymbolKind.Icon && l.Placement == SymbolPlacement.Point) atAnchorIcons++;
                if (l.Kind == SymbolKind.Text && l.Placement == SymbolPlacement.Line) curvedTexts++;
                Assert.AreEqual(SymbolPairRole.None, l.PairRole,
                    "the text left for the curved shape, so no at-anchor icon may claim to own a rider");
                Assert.AreEqual(0, l.PairId, "PairId must stay at its unpaired default");
            }

            // Both preconditions matter: without the icons nothing could be mis-stamped Owner, and without
            // the curved texts the mixed alignment never happened.
            Assert.Greater(atAnchorIcons, 0,
                "precondition: the viewport-resolved icon must emit at the anchors — this is what makes " +
                "`iconAtAnchors` true while `textAtAnchors` is false");
            Assert.Greater(curvedTexts, 0, "precondition: the map-aligned text must take the curved shape");
        }

        // ── shared helpers ────────────────────────────────────────────────────────────────────────────────

        private static MvtTile _placeTile;

        /// <summary>A real z6 tile whose `place` layer carries class=city features (liberty's `label_city`).</summary>
        private static MvtTile PlaceFixtureTile()
            => _placeTile ??= MvtDecoder.Decode(PlaceTileId,
                SymbolTestFixtures.LoadUpBytes("Assets", "Fixtures", "water-6-32-20.pbf.bytes"));

        private static MvtTile _berlinTile;

        private static MvtTile BerlinFixtureTile()
            => _berlinTile ??= MvtDecoder.Decode(BerlinTileId,
                SymbolTestFixtures.LoadUpBytes("Assets", "Fixtures", "boundary-9-274-168.pbf.bytes"));

        /// <summary>The cached fixture tiles own native buffers for the whole fixture's life.</summary>
        [OneTimeTearDown]
        public void ReleaseCachedFixtureTiles()
        {
            _placeTile?.Dispose();  _placeTile  = null;
            _berlinTile?.Dispose(); _berlinTile = null;
        }

        // A synthetic quad standing in for a shaped text run; these teeth only need quads.Length > 0 so the half
        // stages (NonCentredPair_EachHalfsBox_IsBuiltFromItsOwnBakedBounds_AndTheyAreDisjoint lays out real text).
        private static SymbolQuad SyntheticTextQuad() => new SymbolQuad
        {
            TopLeft = new float2(-6f, 6f), BottomRight = new float2(6f, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1),
        };

        /// <summary>Stages an icon+text feature the way <c>SymbolPlacementSystem</c> does — ONE pair candidate
        /// when the extractor proposed a pair, two INDEPENDENT candidates when it did not. Returns the number
        /// of candidates written from index 0. Modelling both arms is what lets a tooth assert the placement
        /// OUTCOME (an orphan dot) rather than merely the roles.</summary>
        private static int StageInstance(
            SymbolStyle.SymbolFeature icon, SymbolStyle.SymbolFeature text,
            in PointStageInput ownerInput, in PointStageInput riderInput,
            SymbolBox[] boxes, ref int boxCount, PlacedQuad[] quads, ref int quadCount,
            SymbolCandidate[] candidates, CandidateEmit[] emit, ref int emitCount)
        {
            if (icon.PairRole == SymbolPairRole.Owner && text.PairRole == SymbolPairRole.Rider)
                return SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                    new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                    bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                    boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);

            int staged = SymbolStagingMath.StagePoint(in ownerInput, new[] { icon.IconQuad },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            staged += SymbolStagingMath.StagePoint(in riderInput, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: staged,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            return staged;
        }

        /// <summary>Emits of <paramref name="kind"/> belonging to a SURVIVING candidate, skipping any half
        /// the collision loop dropped (<see cref="SymbolCandidate.DroppedBoxMask"/>).</summary>
        private static int SurvivingEmitCount(SymbolCandidate[] candidates, bool[] survivor, int count,
            CandidateEmit[] emit, SymbolKind kind)
        {
            int n = 0;
            for (int c = 0; c < count; c++)
            {
                if (!survivor[c]) continue;
                SymbolCandidate cand = candidates[c];
                for (int e = 0; e < cand.EmitCount; e++)
                {
                    if (emit[cand.EmitStart + e].AtlasKind != kind) continue;
                    if ((cand.DroppedBoxMask & (1 << e)) != 0) continue;
                    n++;
                }
            }
            return n;
        }

        private static void AssertBoxEqual(in SymbolBox expected, in SymbolBox actual, string what)
        {
            const float eps = 1e-4f;
            Assert.AreEqual(expected.Min.x, actual.Min.x, eps, what + " Min.x");
            Assert.AreEqual(expected.Min.y, actual.Min.y, eps, what + " Min.y");
            Assert.AreEqual(expected.Max.x, actual.Max.x, eps, what + " Max.x");
            Assert.AreEqual(expected.Max.y, actual.Max.y, eps, what + " Max.y");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolShieldExtractionTests — road-shield acceptance over the real Liberty style + Berlin fixture
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Road shields (docs/road-shields-design.md): acceptance teeth over the real Liberty style + real
    /// Berlin fixture (non-US shield layer) plus synthetic tiles/atlas for the two US shield layers,
    /// which the Berlin fixture carries zero of, and the anchor-clip edge cases.
    /// </summary>
    [TestFixture]
    public class SymbolShieldExtractionTests
    {

        /// <summary>A synthetic decoded tile owns <c>Allocator.Persistent</c> buffers, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this guard exists to catch.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        // ── Walk-up fixture loaders + the parsed Liberty style live in SymbolTestFixtures (shared with
        //    SymbolPairPredicateTests); these are the local names this file's call sites already use. ──
        private static byte[] LoadBerlinFixtureBytes()
            => SymbolTestFixtures.LoadUpBytes("Assets", "Fixtures", "boundary-9-274-168.pbf.bytes");

        private static SymbolStyle.StyleLayer FindShieldLayer(string id) => SymbolTestFixtures.FindSymbolLayer(id);

        private static readonly TileId BerlinTile = new TileId { Z = 9, X = 274, Y = 168 };
        private static MvtTile _berlinTile;
        private static MvtTile BerlinFixtureTile() => _berlinTile ??= MvtDecoder.Decode(BerlinTile, LoadBerlinFixtureBytes());

        /// <summary>The cached fixture tile owns native buffers for the whole fixture's life.</summary>
        [OneTimeTearDown]
        public void ReleaseCachedFixtureTile() { _berlinTile?.Dispose(); _berlinTile = null; }

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
        /// us-highway feature — the two networks Berlin's real fixture carries none of
        /// (<c>UsShieldLayers_SelectNothingFromBerlinFixture</c>).</summary>
        private static IDecodedTile SyntheticUsShieldTile()
        {
            var interstate = ShieldFeature("us-interstate", 2, "80", LineStringGeometry(
                new double2(500, 500), new double2(3500, 3500)));
            var usHighway = ShieldFeature("us-highway", 1, "9", LineStringGeometry(
                new double2(500, 1500), new double2(3500, 1500)));
            return TestDecodedTiles.Of(
                "transportation_name", SyntheticTileId, new List<IFeature> { interstate, usHighway }, Extent);
        }

        private static int CountIcons(List<SymbolStyle.SymbolFeature> symbols)
        {
            int n = 0;
            foreach (SymbolStyle.SymbolFeature l in symbols) if (l.Kind == SymbolKind.Icon) n++;
            return n;
        }

        private static int CountTexts(List<SymbolStyle.SymbolFeature> symbols)
        {
            int n = 0;
            foreach (SymbolStyle.SymbolFeature l in symbols) if (l.Kind == SymbolKind.Text) n++;
            return n;
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void PointPlacement_OnLineString_EmitsSymbols()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            Assert.IsNotNull(nonUs, "precondition: highway-shield-non-us must parse as a Symbol.StyleLayer");
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            Assert.Greater(FeatureSelectorCount(nonUs), 0, "precondition: the layer must select > 0 features");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            // z10 — below the layer's z11 step boundary — must evaluate to Point placement.
            SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 10.0, projection, symbols, atlas);

            Assert.Greater(symbols.Count, 0, "point placement on LineString features must emit symbols");
            foreach (SymbolStyle.SymbolFeature l in symbols)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "every label must be Point-placed below the step");

            int icons = CountIcons(symbols), texts = CountTexts(symbols);
            Assert.Greater(icons, 0, "icon count must be > 0");
            Assert.Greater(texts, 0, "text count must be > 0");
            Assert.AreEqual(texts, icons, "icon count must equal text count (one pair per feature)");
        }

        private static int FeatureSelectorCount(StyleLayer layer)
            => MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(layer, BerlinFixtureTile(), 10.0).Count;

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void ShieldIconImages_ResolveFromAtlas_AllThreeLayers()
        {
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            // non-us — real fixture, at z13 (above its z11 step ⇒ line/upright placement, the demo's actual view).
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            var nonUsSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 13.0, projection, nonUsSymbols, atlas);
            int nonUsIcons = CountIcons(nonUsSymbols);
            Assert.Greater(nonUsIcons, 0, "highway-shield-non-us must resolve > 0 icons at z13");
            foreach (SymbolStyle.SymbolFeature l in nonUsSymbols)
                if (l.Kind == SymbolKind.Icon)
                {
                    StringAssert.StartsWith("road_", l.IconImage, "non-us icon name must be road_<ref_length>");
                }

            // interstate + road_shield_us — synthetic tile: Berlin carries no US-network features
            // (UsShieldLayers_SelectNothingFromBerlinFixture).
            IDecodedTile synthTile = SyntheticUsShieldTile();

            SymbolStyle.StyleLayer interstate = FindShieldLayer("highway-shield-us-interstate");
            var interstateSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(interstate, synthTile, SyntheticTileId, 13.0, projection, interstateSymbols, atlas);
            int interstateIcons = CountIcons(interstateSymbols);
            Assert.Greater(interstateIcons, 0, "highway-shield-us-interstate must resolve > 0 icons at z13");
            foreach (SymbolStyle.SymbolFeature l in interstateSymbols)
                if (l.Kind == SymbolKind.Icon)
                    StringAssert.StartsWith("us-interstate_", l.IconImage, "interstate icon name must be us-interstate_<ref_length>");

            SymbolStyle.StyleLayer usShield = FindShieldLayer("road_shield_us");
            var usShieldSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(usShield, synthTile, SyntheticTileId, 13.0, projection, usShieldSymbols, atlas);
            int usShieldIcons = CountIcons(usShieldSymbols);
            Assert.Greater(usShieldIcons, 0, "road_shield_us must resolve > 0 icons at z13");
            foreach (SymbolStyle.SymbolFeature l in usShieldSymbols)
                if (l.Kind == SymbolKind.Icon)
                    Assert.IsTrue(l.IconImage.StartsWith("us-highway_") || l.IconImage.StartsWith("us-state_"),
                        $"road_shield_us icon name must be us-highway_<n>|us-state_<n>, was '{l.IconImage}'");
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void LinePlacement_ViewportAligned_EmitsUprightAnchorSymbols()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 13.0, projection, symbols, atlas);

            Assert.Greater(symbols.Count, 0, "z13 (above the step) must still emit symbols");
            foreach (SymbolStyle.SymbolFeature l in symbols)
            {
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "upright-at-anchor symbols are Point-placed, not curved");
                Assert.IsNull(l.PathRender, "an upright-at-anchor label carries no curved path");
            }

            int icons = CountIcons(symbols), texts = CountTexts(symbols);
            Assert.Greater(icons, 0, "icon count must be > 0 at z13");
            Assert.Greater(texts, 0, "text count must be > 0 at z13");
            Assert.AreEqual(texts, icons, "icon count must equal text count at z13");
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void CentredPair_EmitsAdjacentIconThenText()
        {
            SymbolStyle.StyleLayer nonUs = FindShieldLayer("highway-shield-non-us");
            var projection = new WebMercatorProjection();
            var atlas = SyntheticShieldAtlas();

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(nonUs, BerlinFixtureTile(), BerlinTile, 13.0, projection, symbols, atlas);

            Assert.Greater(symbols.Count, 0, "precondition: some symbols must be emitted");
            Assert.AreEqual(0, symbols.Count % 2, "symbols must form consecutive (icon,text) pairs — even count");

            for (int i = 0; i < symbols.Count; i += 2)
            {
                SymbolStyle.SymbolFeature icon = symbols[i];
                SymbolStyle.SymbolFeature text = symbols[i + 1];
                Assert.AreEqual(SymbolKind.Icon, icon.Kind, $"symbols[{i}] must be the icon (icon emitted first)");
                Assert.AreEqual(SymbolKind.Text, text.Kind, $"symbols[{i + 1}] must be the text");
                Assert.AreEqual(icon.FeatureIndex + 1, text.FeatureIndex, "text's ordinal must immediately follow its icon's");
                Assert.AreEqual(icon.AnchorRender, text.AnchorRender, "icon and text of a pair share the same anchor");
                // The pair is ONE placement instance, so both halves carry their AUTHORED overlap flags
                // (liberty's shields declare neither — default false), not a forced-true passenger flag.
                Assert.IsFalse(text.AllowOverlap, "the rider text carries its AUTHORED AllowOverlap (unset -> false)");
                Assert.IsFalse(text.IgnorePlacement, "the rider text carries its AUTHORED IgnorePlacement (unset -> false)");
                Assert.IsFalse(icon.AllowOverlap, "the icon (pair owner) must NOT set AllowOverlap");
                Assert.IsFalse(icon.IgnorePlacement, "the icon (pair owner) must NOT set IgnorePlacement");

                // The pair is stamped Owner/Rider sharing a PairId.
                Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole, "the icon must be stamped Owner");
                Assert.AreEqual(SymbolPairRole.Rider, text.PairRole, "the text must be stamped Rider");
                Assert.AreEqual(icon.FeatureIndex, icon.PairId, "PairId is the owner's own FeatureIndex");
                Assert.AreEqual(icon.PairId, text.PairId, "both halves of a pair share one PairId");
            }
        }

        // ── A NON-centred icon+text feature (text-offset != 0) PAIRS like any other: the predicate is
        //    "both halves resolved", not "both halves coincide". ──
        [Test]
        public void NonCentredPair_NowEmitsIconThenText_OwnerRider()
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var tile = TestDecodedTiles.Of("points", SyntheticTileId, new List<IFeature> { feature }, Extent);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "non-centred-pair-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\",\"text-offset\":[0,0.6]}"),
            };
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0,
                new WebMercatorProjection(), symbols, SyntheticShieldAtlas());

            Assert.AreEqual(2, symbols.Count, "precondition: text + icon must both be emitted");
            Assert.AreEqual(SymbolKind.Icon, symbols[0].Kind, "a pair emits its OWNER (the icon) first");
            Assert.AreEqual(SymbolKind.Text, symbols[1].Kind, "the rider text follows immediately");
            Assert.AreEqual(SymbolPairRole.Owner, symbols[0].PairRole, "the icon is the pair owner");
            Assert.AreEqual(SymbolPairRole.Rider, symbols[1].PairRole, "the offset text still rides its icon");
            Assert.AreEqual(symbols[0].FeatureIndex, symbols[0].PairId, "PairId is the owner's own FeatureIndex");
            Assert.AreEqual(symbols[0].PairId, symbols[1].PairId, "both halves share one PairId");
            Assert.AreEqual(symbols[0].FeatureIndex + 1, symbols[1].FeatureIndex,
                "the rider's ordinal must immediately follow its owner's (SymbolPairing's adjacency contract)");
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
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
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":[\"to-string\",[\"get\",\"ref\"]],\"text-rotation-alignment\":\"map\",\"symbol-placement\":\"line\"}"),
            };
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            var selected = new List<SelectedTileFeature>();
            MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(
                handBuilt, BerlinFixtureTile().GetLayer(handBuilt.SourceLayer), 13.0, selected);
            Assert.Greater(selected.Count, 0, "precondition: the hand-built filter must select > 0 features");

            // The command streams come from the BYTES (a decoded feature carries none), joined to
            // the selection by layer ORDINAL — which is exactly what the buffer's RingFeatureIdx names.
            MvtFixtureStreams.Layer fixtureStreams =
                MvtFixtureStreams.ReadLayer(LoadBerlinFixtureBytes(), handBuilt.SourceLayer);
            int eligiblePaths = 0;
            foreach (SelectedTileFeature f in selected)
                foreach (List<double2> path in MvtGeometry.Decode(fixtureStreams.Commands[f.Ordinal]))
                    if (path.Count >= 2) eligiblePaths++;
            Assert.Greater(eligiblePaths, 0, "precondition: > 0 eligible (>=2 point) decoded paths");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(handBuilt, BerlinFixtureTile(), BerlinTile, 13.0, projection, symbols, atlas);

            Assert.Greater(symbols.Count, 0, "map-aligned line layer must still emit curved symbols");
            Assert.AreEqual(eligiblePaths, symbols.Count, "one curved label per eligible decoded path — no drops, no dupes");

            for (int i = 0; i < symbols.Count; i++)
            {
                SymbolStyle.SymbolFeature l = symbols[i];
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, $"symbols[{i}] must stay curved (Line), not upright");
                Assert.IsNotNull(l.PathRender, $"symbols[{i}] must carry a projected path");
                Assert.IsNotNull(l.LineAnchors, $"symbols[{i}] must carry its build-time anchors");
                Assert.Greater(l.LineAnchors.Length, 0, $"symbols[{i}] must carry >= 1 anchor");
                Assert.AreEqual(i, l.FeatureIndex, "FeatureIndex ordinals must be contiguous from 0");
                Assert.AreEqual(SymbolKind.Text, l.Kind, "a map-aligned curved label is a text label");
            }

            // Field-for-field, against values derived independently from the layer definition (spec defaults),
            // not a baked baseline.
            SymbolStyle.SymbolFeature first = symbols[0];
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
            Assert.AreEqual(0, CountIcons(symbols), "a text-only layer must emit zero icon symbols");

            // The real shipped Liberty layer (guarded skip if this fixture selects nothing for it).
            SymbolStyle.StyleLayer highwayNameMajor = FindShieldLayer("highway-name-major");
            Assert.IsNotNull(highwayNameMajor, "precondition: highway-name-major must parse");
            IReadOnlyList<IFeature> majorSelected =
                MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(highwayNameMajor, BerlinFixtureTile(), 13.0);
            if (majorSelected.Count == 0)
            {
                Assert.Pass("known coverage gap: highway-name-major selects nothing from the Berlin fixture at z13");
            }
            var majorSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(highwayNameMajor, BerlinFixtureTile(), BerlinTile, 13.0, projection, majorSymbols, atlas);
            Assert.Greater(majorSymbols.Count, 0, "highway-name-major (literal line, unset alignment -> map) must still emit curved symbols");
            foreach (SymbolStyle.SymbolFeature l in majorSymbols)
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "highway-name-major must stay curved");
        }

        // ── A map-resolved line icon emits ─────────────────────────────────────────────────────────────
        // A map-resolved line icon is emitted as a ONE-GLYPH CURVED symbol (the road_one_way_arrow* shape).
        private static SymbolStyle.StyleLayer MapAlignedIconProbeLayer(string extraLayoutJson = "")
            => new SymbolStyle.StyleLayer
            {
                Id = "shield-map-aligned-icon-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "openmaptiles",
                SourceLayer = "transportation_name",
                Filter = MapRenderer.Core.Json.JsonParser.Parse(
                    "[\"all\",[\"<=\",[\"get\",\"ref_length\"],6],[\"match\",[\"geometry-type\"],[\"LineString\",\"MultiLineString\"],true,false]]"),
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"icon-image\":\"road_3\",\"symbol-placement\":\"line\"" + extraLayoutJson + "}"),
            };

        /// <summary>Decoded paths of <paramref name="layer"/>'s selected features that can carry a symbol
        /// (>= 2 points) — the along-line emit shape produces exactly one curved symbol per one of these.
        /// Same count the curved-text tooth above derives inline, over the same fixture.</summary>
        private static int EligiblePathCount(SymbolStyle.StyleLayer layer)
        {
            int eligible = 0;
            var selected = new List<SelectedTileFeature>();
            MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(
                layer, BerlinFixtureTile().GetLayer(layer.SourceLayer), 13.0, selected);
            MvtFixtureStreams.Layer fixtureStreams =
                MvtFixtureStreams.ReadLayer(LoadBerlinFixtureBytes(), layer.SourceLayer);
            foreach (SelectedTileFeature f in selected)
                foreach (List<double2> path in MvtGeometry.Decode(fixtureStreams.Commands[f.Ordinal]))
                    if (path.Count >= 2) eligible++;
            Assert.Greater(eligible, 0, "precondition: > 0 eligible (>=2 point) decoded paths");
            return eligible;
        }

        [Test]
        public void MapAlignedLineIconLayer_EmitsAlongLineIcons()
        {
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            // No rotation-alignment declared -> auto -> resolves MAP under line placement.
            SymbolStyle.StyleLayer mapAligned = MapAlignedIconProbeLayer();
            IReadOnlyList<IFeature> selected =
                MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(mapAligned, BerlinFixtureTile(), 13.0);
            Assert.Greater(selected.Count, 0, "precondition: > 0 features selected");

            // Precondition: the SAME layer with viewport alignment emits POINT icons, so a zero-icon map arm
            // cannot pass because of an unresolvable sprite.
            var viewportSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(",\"icon-rotation-alignment\":\"viewport\""),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, viewportSymbols, atlas);
            Assert.Greater(CountIcons(viewportSymbols), 0,
                "precondition: the viewport-resolved arm must emit icons (the sprite resolves)");
            foreach (SymbolStyle.SymbolFeature l in viewportSymbols)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement,
                    "precondition: a viewport-resolved line icon stays point-shaped (the unchanged path)");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(mapAligned, BerlinFixtureTile(), BerlinTile, 13.0, projection, symbols, atlas);

            int icons = CountIcons(symbols);
            Assert.Greater(icons, 0, "a map-aligned line icon must now emit (the fence is lifted)");

            foreach (SymbolStyle.SymbolFeature l in symbols)
            {
                if (l.Kind != SymbolKind.Icon) continue;
                // A point-shaped icon here would mean the shallow "emit it at the anchors" impl, not the
                // one-glyph curved symbol this stage specifies.
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "an along-line icon carries LINE placement");
                Assert.IsNotNull(l.PathRender, "an along-line icon carries the projected path it rides");
                Assert.Greater(l.PathRender.Length, 1, "the projected path must have >= 1 segment");
                Assert.IsNotNull(l.LineAnchors, "an along-line icon carries the build-time anchors");
                Assert.GreaterOrEqual(l.LineAnchors.Length, 1, "at least one along-line anchor");
                Assert.IsFalse(l.KeepUpright,
                    "icon-keep-upright's spec default is false — an arrow must never flip to stay upright");
                Assert.IsNotNull(l.IconImage, "the resolved sprite name is the icon's cross-tile identity");
                Assert.AreEqual(SymbolPairRole.None, l.PairRole, "a curved label is never half of a centred pair");
            }
        }

        // ── The three-way alignment classification the icon emit shape branches on. ────────────────
        [Test]
        public void IconRotationAlignment_ResolvesToThreeDistinctEmitShapes()
        {
            var atlas = SyntheticShieldAtlas();
            var projection = new WebMercatorProjection();

            // The resolver itself: unset (Auto) under LINE placement is what makes road_one_way_arrow*
            // map-aligned in the first place — the whole reason this stage exists.
            Assert.AreEqual(AlignmentMode.Map, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.Line),
                "auto resolves to map under line placement");
            Assert.AreEqual(AlignmentMode.Viewport, AlignmentResolution.Resolve(AlignmentMode.Auto, SymbolPlacement.Point),
                "auto resolves to viewport under point placement");

            // (a) line + map-resolved -> ALONG-LINE icon (one curved symbol per path).
            var mapSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(MapAlignedIconProbeLayer(),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, mapSymbols, atlas);
            Assert.Greater(CountIcons(mapSymbols), 0, "line + map must emit icons");
            foreach (SymbolStyle.SymbolFeature l in mapSymbols)
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "line + map -> along-line (curved) icon");

            // (b) line + viewport -> the at-anchors icon (point-shaped).
            var viewportSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(",\"icon-rotation-alignment\":\"viewport\""),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, viewportSymbols, atlas);
            Assert.Greater(CountIcons(viewportSymbols), 0, "line + viewport must emit icons");
            foreach (SymbolStyle.SymbolFeature l in viewportSymbols)
            {
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "line + viewport -> point-shaped icon at each anchor");
                Assert.IsNull(l.PathRender, "an at-anchors icon carries no path");
            }
            // Viewport emits one symbol PER ANCHOR, map one per PATH (pinned EXACTLY). The strict inequality
            // needs a path longer than one symbol-spacing (>= 2 anchors), which is asserted, not assumed.
            int eligiblePaths = EligiblePathCount(MapAlignedIconProbeLayer());
            Assert.AreEqual(eligiblePaths, CountIcons(mapSymbols),
                "the along-line arm emits exactly one curved icon per eligible decoded path");
            Assert.Greater(CountIcons(viewportSymbols), eligiblePaths,
                "the at-anchors arm emits per ANCHOR, so with at least one multi-anchor path it must emit " +
                "strictly more icons than there are paths");

            // (c) point placement -> the point icon path, regardless of alignment.
            var pointSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(",\"symbol-placement\":\"point\""),
                BerlinFixtureTile(), BerlinTile, 13.0, projection, pointSymbols, atlas);
            Assert.Greater(CountIcons(pointSymbols), 0, "point placement must emit icons");
            foreach (SymbolStyle.SymbolFeature l in pointSymbols)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "point placement -> point icon");
        }

        // ── The centred-pair predicate uses the UN-suppressed `hasIcon`, so on the line branch it re-gates on
        //    `iconAtAnchors`: an icon that leaves for the along-line shape must not leave a Rider orphaned. ──
        [Test]
        public void ViewportTextWithAlongLineIcon_StampsNoPairRole()
        {
            // Text: explicit VIEWPORT -> at-anchors. Icon: unset -> MAP under line placement -> along-line.
            // Default anchors/offsets make the centred-pair predicate fire.
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                MapAlignedIconProbeLayer(
                    ",\"text-field\":[\"to-string\",[\"get\",\"ref\"]],\"text-rotation-alignment\":\"viewport\""),
                BerlinFixtureTile(), BerlinTile, 13.0, new WebMercatorProjection(), symbols, SyntheticShieldAtlas());

            int atAnchorTexts = 0, alongLineIcons = 0;
            foreach (SymbolStyle.SymbolFeature l in symbols)
            {
                if (l.Kind == SymbolKind.Text && l.Placement == SymbolPlacement.Point) atAnchorTexts++;
                if (l.Kind == SymbolKind.Icon && l.Placement == SymbolPlacement.Line) alongLineIcons++;
                Assert.AreEqual(SymbolPairRole.None, l.PairRole,
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

        // ── icon-rotate is converted ONCE (degrees -> radians) and stamped on the ICON half only. ────────────
        private static List<SymbolStyle.SymbolFeature> ExtractPointPairWithLayout(string layoutJson)
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var tile = TestDecodedTiles.Of("points", SyntheticTileId, new List<IFeature> { feature }, Extent);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "icon-rotate-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout(layoutJson),
            };
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0,
                new WebMercatorProjection(), symbols, SyntheticShieldAtlas());
            return symbols;
        }

        [Test]
        public void IconRotate_IsConvertedToRadiansOnce_AndStampedOnTheIconHalfOnly()
        {
            List<SymbolStyle.SymbolFeature> symbols = ExtractPointPairWithLayout(
                "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\",\"icon-rotate\":90}");
            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon label must be present");
            Assert.IsNotNull(text, "precondition: a text label must be present");

            // Radians, not degrees — a stamped-degrees impl reads 90, three orders of magnitude off.
            Assert.AreEqual(math.PI / 2f, icon.IconRotateRadians, 1e-5f, "icon-rotate: 90 -> pi/2 radians");
            Assert.AreEqual(0f, text.IconRotateRadians, 1e-6f, "icon-rotate never rotates text");

            // Absent -> 0 (the spec default), so every un-rotated icon composes an exact `x + 0f`.
            List<SymbolStyle.SymbolFeature> bare = ExtractPointPairWithLayout(
                "{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\"}");
            Assert.AreEqual(0f, FindByKind(bare, SymbolKind.Icon).IconRotateRadians, 1e-6f,
                "absent icon-rotate -> 0 radians");
        }

        [Test]
        public void IconRotate_180_IsStampedOnAnAlongLineIcon()
        {
            // The road_one_way_arrow_opposite shape: a map-resolved line icon layer with icon-rotate: 180.
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(MapAlignedIconProbeLayer(",\"icon-rotate\":180"),
                BerlinFixtureTile(), BerlinTile, 13.0, new WebMercatorProjection(), symbols, SyntheticShieldAtlas());

            int icons = 0;
            foreach (SymbolStyle.SymbolFeature l in symbols)
            {
                if (l.Kind != SymbolKind.Icon) continue;
                icons++;
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "precondition: the along-line emit shape");
                Assert.AreEqual(math.PI, l.IconRotateRadians, 1e-5f, "icon-rotate: 180 -> pi radians");
            }
            Assert.Greater(icons, 0, "precondition: the layer must emit along-line icons");
        }

        // ── Shared centred-pair fixture: literal "point" placement, default anchors. `extraLayout`
        //    appends further layout members (the optional flags). ──
        private static List<SymbolStyle.SymbolFeature> ExtractCentredPairSymbols(string extraLayout = null)
        {
            var feature = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["ref"] = Value.String("5") },
                geometryType: TileGeometryType.Point,
                geometry: SinglePointGeometry(new double2(2000, 2000)));
            var tile = TestDecodedTiles.Of("points", SyntheticTileId, new List<IFeature> { feature }, Extent);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "centred-pair-probe",
                LayerType = StyleLayerType.Symbol,
                Source = "s",
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"{ref}\",\"icon-image\":\"road_5\"" // symbol-placement default = point
                    + (extraLayout == null ? "" : "," + extraLayout) + "}"),
            };
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0,
                new WebMercatorProjection(), symbols, SyntheticShieldAtlas());
            return symbols;
        }

        private static uint[] SinglePointGeometry(double2 p)
            => new uint[] { 1u | (1u << 3), ZigZagEncode((long)p.x), ZigZagEncode((long)p.y) };

        private static SymbolStyle.SymbolFeature FindByKind(List<SymbolStyle.SymbolFeature> symbols, SymbolKind kind)
        {
            foreach (SymbolStyle.SymbolFeature l in symbols) if (l.Kind == kind) return l;
            return null;
        }

        // Pair test helper (shared with SymbolPairPredicateTests — see SymbolTestFixtures.StageInputFor).
        private static PointStageInput StageInputFor(SymbolStyle.SymbolFeature symbol, SymbolKind atlasKind,
            float2 boundsMin, float2 boundsMax, float2 screenPx, float textSizePx)
            => SymbolTestFixtures.StageInputFor(symbol, atlasKind, boundsMin, boundsMax, screenPx, textSizePx);

        // A single synthetic quad standing in for a shaped text run (SymbolFeature carries no Layout — shaping is
        // Unity-side) — its exact footprint is irrelevant to these teeth, only that quads.Length > 0.
        private static SymbolQuad SyntheticTextQuad() => new SymbolQuad
        {
            TopLeft = new float2(-6f, 6f), BottomRight = new float2(6f, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1),
        };

        // ── P3 ────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void CentredPair_AtomicPlacement_OneCandidateBothBoxes_PairDropsTogether_NoBareNumber()
        {
            List<SymbolStyle.SymbolFeature> symbols = ExtractCentredPairSymbols();
            Assert.AreEqual(2, symbols.Count, "precondition: the centred-pair feature must emit exactly 2 symbols");

            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon label must be present");
            Assert.IsNotNull(text, "precondition: a text label must be present");
            Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole, "precondition: the icon must be the resolved Owner");
            Assert.AreEqual(SymbolPairRole.Rider, text.PairRole, "precondition: the text must be the resolved Rider");
            Assert.IsTrue(SymbolPairing.TryGetRider(symbols, symbols.IndexOf(icon), out int riderIdx));
            Assert.AreEqual(symbols.IndexOf(text), riderIdx, "precondition: SymbolPairing resolves the SAME pair the extractor proposed");

            var ownerInput = StageInputFor(icon, SymbolKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            var riderInput = StageInputFor(text, SymbolKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);

            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            // Candidate 0: a higher-priority blocker sitting where the icon box will land — big enough to
            // cover it regardless of icon-padding's exact magnitude.
            boxes[boxCount++] = new SymbolBox { Min = new float2(900, 900), Max = new float2(1100, 1100) };
            candidates[0] = new SymbolCandidate
            {
                BoxStart = 0, BoxCount = 1, EmitStart = 0, EmitCount = 0,
                SortKey = -1f, FeatureIndex = -1, TileKey = 999, SymbolIndex = 0,
            };

            // Candidate 1: the pair, staged for REAL via StagePointPair (the production code path).
            int staged = SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 1,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged, "the pair stages as exactly one candidate");

            SymbolCandidate pairCand = candidates[1];
            Assert.AreEqual(2, pairCand.BoxCount, "the candidate spans BOTH halves' boxes");
            Assert.AreEqual(2, pairCand.EmitCount, "the candidate spans BOTH halves' emits");
            Assert.AreEqual(SymbolKind.Icon, emit[pairCand.EmitStart].AtlasKind, "emits (Icon, Text) in order");
            Assert.AreEqual(SymbolKind.Text, emit[pairCand.EmitStart + 1].AtlasKind);

            var survivor = new bool[2];
            NativeCollisionRunner.RunCollision(candidates, 2, boxes, boxCount, survivor);

            bool blockerPlaced = false, pairPlaced = false;
            for (int k = 0; k < 2; k++)
            {
                if (candidates[k].SymbolIndex == 0) blockerPlaced = survivor[k];
                else if (candidates[k].SymbolIndex == 1) pairPlaced = survivor[k];
            }
            Assert.IsTrue(blockerPlaced, "the higher-priority blocker must place");
            Assert.IsFalse(pairPlaced,
                "the pair must be DROPPED TOGETHER — this is the inverse of the withdrawn T12's accepted bare number");
        }

        // ── P4 — a PLACED pair blocks through BOTH boxes ─────────────────────────────────────────────────
        [Test]
        public void CentredPair_Placed_BlocksThroughBothBoxes_LaterSymbolOverlappingOnlyTextIsDropped()
        {
            List<SymbolStyle.SymbolFeature> symbols = ExtractCentredPairSymbols();
            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon); Assert.IsNotNull(text);

            var ownerInput = StageInputFor(icon, SymbolKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            var riderInput = StageInputFor(text, SymbolKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);
            // Move the text box away from the icon box (viewport-anchored translate) so a later symbol can
            // overlap ONLY the text half — isolating "the pair blocks through BOTH boxes" from "the icon alone".
            riderInput.TranslatePx = new float2(60f, 0f);
            riderInput.TranslateAnchor = TextTranslateAnchor.Viewport;

            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            int staged = SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged);

            SymbolCandidate pairCand = candidates[0];
            SymbolBox iconBox = boxes[pairCand.BoxStart];
            SymbolBox textBox = boxes[pairCand.BoxStart + 1];
            Assert.IsFalse(SymbolCollision.Overlaps(in iconBox, in textBox),
                "precondition: icon/text boxes must be disjoint, or this tooth cannot isolate the text-only overlap");

            var laterBox = new SymbolBox { Min = textBox.Min - new float2(1, 1), Max = textBox.Max + new float2(1, 1) };
            boxes[boxCount] = laterBox;
            candidates[1] = new SymbolCandidate
            {
                BoxStart = boxCount, BoxCount = 1, EmitStart = emitCount, EmitCount = 0,
                SortKey = pairCand.SortKey + 1f, FeatureIndex = 999, TileKey = 999, SymbolIndex = 1,
            };
            boxCount++;

            var survivor = new bool[2];
            NativeCollisionRunner.RunCollision(candidates, 2, boxes, boxCount, survivor);

            bool pairPlaced = false, laterPlaced = false;
            for (int k = 0; k < 2; k++)
            {
                if (candidates[k].SymbolIndex == 0) pairPlaced = survivor[k];
                else if (candidates[k].SymbolIndex == 1) laterPlaced = survivor[k];
            }
            Assert.IsTrue(pairPlaced, "the pair must place (nothing blocks it)");
            Assert.IsFalse(laterPlaced,
                "a later label overlapping ONLY the text box must still be blocked — the pair blocks through both boxes " +
                "(today's un-fixed extractor forces the passenger's IgnorePlacement=true, so this would incorrectly survive)");
        }

        // ── P5 — one FadeId per pair, from the OWNER ─────────────────────────────────────────────────────
        [Test]
        public void CentredPair_OneFadeId_FromTheOwner_NotTheRider()
        {
            List<SymbolStyle.SymbolFeature> symbols = ExtractCentredPairSymbols();
            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);

            var ownerInput = StageInputFor(icon, SymbolKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            ownerInput.FadeId = 42L;
            var riderInput = StageInputFor(text, SymbolKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);
            riderInput.FadeId = 999L; // DIFFERENT from the owner's — must never leak into the pair's one FadeId

            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            int staged = SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged);
            Assert.AreEqual(2, emitCount, "sanity: two emits were staged"); // guards the next line's premise
            Assert.AreEqual(42L, candidates[0].FadeId, "the pair's ONE FadeId must be the OWNER's — the text contributes no candidate/fade record of its own");
        }

        // ── C2 — the flags land on the right HALF, and the pair still FORMS in every case ─────────────────
        [Test]
        public void IconAndTextOptional_StampTheMatchingHalf_AndThePairStillForms()
        {
            void AssertPair(string extraLayout, bool expectIconOptional, bool expectTextOptional, string what)
            {
                List<SymbolStyle.SymbolFeature> symbols = ExtractCentredPairSymbols(extraLayout);
                Assert.AreEqual(2, symbols.Count, $"{what}: the centred-pair feature must still emit exactly 2 symbols");
                SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
                SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
                Assert.IsNotNull(icon, what); Assert.IsNotNull(text, what);

                // Optionality must NOT un-pair the halves: unpaired, a centred pair's overlapping boxes would
                // make the halves mutually exclusive (the bare-number defect).
                Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole, $"{what}: the icon must STILL be the pair Owner");
                Assert.AreEqual(SymbolPairRole.Rider, text.PairRole, $"{what}: the text must STILL be the pair Rider");
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

        // Shared harness: REAL extractor → StagePointPair → CollisionJob, the rider translated clear so a blocker
        // hits ONE half. A low-priority probe over each half that places proves that half's box was never inserted.
        private struct OptionalPairOutcome
        {
            public bool PairPlaced;
            public byte OptionalBoxMask;
            public byte DroppedBoxMask;
            public bool ProbeOverIconPlaced;
            public bool ProbeOverTextPlaced;
        }

        private static OptionalPairOutcome RunOptionalPairScene(string extraLayout, SymbolKind blockedHalf)
        {
            List<SymbolStyle.SymbolFeature> symbols = ExtractCentredPairSymbols(extraLayout);
            SymbolStyle.SymbolFeature icon = FindByKind(symbols, SymbolKind.Icon);
            SymbolStyle.SymbolFeature text = FindByKind(symbols, SymbolKind.Text);
            Assert.IsNotNull(icon, "precondition: an icon label must be present");
            Assert.IsNotNull(text, "precondition: a text label must be present");
            Assert.AreEqual(SymbolPairRole.Owner, icon.PairRole, "precondition: the pair must form regardless of the flags");
            Assert.AreEqual(SymbolPairRole.Rider, text.PairRole, "precondition: the pair must form regardless of the flags");

            var ownerInput = StageInputFor(icon, SymbolKind.Icon, new float2(-10, -10), new float2(10, 10), new float2(1000, 1000), TextQuadLayout.OneEm);
            var riderInput = StageInputFor(text, SymbolKind.Text, new float2(-6, -6), new float2(6, 6), new float2(1000, 1000), text.TextSizePx > 0f ? text.TextSizePx : TextQuadLayout.OneEm);
            riderInput.TranslatePx = new float2(200f, 0f);
            riderInput.TranslateAnchor = TextTranslateAnchor.Viewport;

            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[4];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            int staged = SymbolStagingMath.StagePointPair(in ownerInput, in riderInput,
                new[] { icon.IconQuad }, new[] { SyntheticTextQuad() },
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
            Assert.AreEqual(1, staged, "precondition: the pair stages as exactly one candidate");
            Assert.AreEqual(2, candidates[0].BoxCount, "precondition: the candidate must span BOTH halves' boxes");

            SymbolBox iconBox = boxes[0];
            SymbolBox textBox = boxes[1];
            Assert.IsFalse(SymbolCollision.Overlaps(in iconBox, in textBox),
                "precondition: the halves' boxes must be disjoint here, or a blocker cannot address one alone");

            SymbolBox target = blockedHalf == SymbolKind.Icon ? iconBox : textBox;
            SymbolBox spared = blockedHalf == SymbolKind.Icon ? textBox : iconBox;
            var blockerBox = new SymbolBox
            {
                Min = new float2(target.Min.x - 5f, target.Min.y), Max = new float2(target.Min.x + 2f, target.Max.y),
            };
            Assert.IsTrue(SymbolCollision.Overlaps(in blockerBox, in target),
                "precondition: the blocker must overlap the targeted half's box");
            Assert.IsFalse(SymbolCollision.Overlaps(in blockerBox, in spared),
                "precondition: the blocker must NOT overlap the other half's box");

            var iconProbeBox = new SymbolBox
            {
                Min = new float2(iconBox.Max.x - 2f, iconBox.Min.y), Max = new float2(iconBox.Max.x + 5f, iconBox.Max.y),
            };
            var textProbeBox = new SymbolBox
            {
                Min = new float2(textBox.Max.x - 2f, textBox.Min.y), Max = new float2(textBox.Max.x + 5f, textBox.Max.y),
            };
            Assert.IsFalse(SymbolCollision.Overlaps(in blockerBox, in iconProbeBox),
                "precondition: the icon probe must be clear of the blocker, so only the PAIR can block it");
            Assert.IsFalse(SymbolCollision.Overlaps(in blockerBox, in textProbeBox),
                "precondition: the text probe must be clear of the blocker");
            Assert.IsFalse(SymbolCollision.Overlaps(in iconProbeBox, in textProbeBox),
                "precondition: the two probes must not block one another");
            Assert.IsFalse(SymbolCollision.Overlaps(in iconProbeBox, in textBox),
                "precondition: the icon probe must address the ICON box only");
            Assert.IsFalse(SymbolCollision.Overlaps(in textProbeBox, in iconBox),
                "precondition: the text probe must address the TEXT box only");

            SymbolCandidate Probe(int boxIndex, float sortKey, int symbolIndex) => new SymbolCandidate
            {
                BoxStart = boxIndex, BoxCount = 1, EmitStart = emitCount, EmitCount = 0,
                SortKey = sortKey, FeatureIndex = 900 + symbolIndex, TileKey = 900 + symbolIndex, SymbolIndex = symbolIndex,
            };

            boxes[boxCount] = blockerBox;   candidates[1] = Probe(boxCount, -1f, 1); boxCount++;
            boxes[boxCount] = iconProbeBox; candidates[2] = Probe(boxCount, 1f, 2);  boxCount++;
            boxes[boxCount] = textProbeBox; candidates[3] = Probe(boxCount, 2f, 3);  boxCount++;

            var survivor = new bool[4];
            NativeCollisionRunner.RunCollision(candidates, 4, boxes, boxCount, survivor);

            var outcome = new OptionalPairOutcome();
            bool blockerPlaced = false;
            for (int k = 0; k < 4; k++)
            {
                switch (candidates[k].SymbolIndex)
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

        // ── C3 — text-optional: the ICON places without its text ──────────────────────────────────────────
        [Test]
        public void TextOptional_TextBoxBlocked_IconStillPlaces_AndTheTextBoxReservesNothing()
        {
            OptionalPairOutcome o = RunOptionalPairScene("\"text-optional\":true", SymbolKind.Text);

            Assert.AreEqual(0b10, o.OptionalBoxMask, "text-optional marks the RIDER (bit 1) droppable, not the owner");
            Assert.IsTrue(o.PairPlaced,
                "the pair must SURVIVE on its icon alone — un-fixed, all-or-nothing drops the whole candidate");
            Assert.AreEqual(0b10, o.DroppedBoxMask, "exactly the text half was dropped");
            Assert.IsTrue(o.ProbeOverTextPlaced,
                "a later label over the DROPPED text box must place — a dropped half reserves nothing");
            Assert.IsFalse(o.ProbeOverIconPlaced,
                "the surviving icon half must still block: only the dropped box is released");
        }

        // ── C4 — icon-optional: the TEXT places without its icon (the mirror of C3) ───────────────────────
        [Test]
        public void IconOptional_IconBoxBlocked_TextStillPlaces_AndTheIconBoxReservesNothing()
        {
            OptionalPairOutcome o = RunOptionalPairScene("\"icon-optional\":true", SymbolKind.Icon);

            Assert.AreEqual(0b01, o.OptionalBoxMask, "icon-optional marks the OWNER (bit 0) droppable, not the rider");
            Assert.IsTrue(o.PairPlaced, "the pair must SURVIVE on its text alone");
            Assert.AreEqual(0b01, o.DroppedBoxMask, "exactly the icon half was dropped");
            Assert.IsTrue(o.ProbeOverIconPlaced,
                "a later label over the DROPPED icon box must place — a dropped half reserves nothing");
            Assert.IsFalse(o.ProbeOverTextPlaced, "the surviving text half must still block");
        }

        // ── C5 — the both-false regression: P3's all-or-nothing is UNCHANGED ──────────────────────────────
        [Test]
        public void NeitherOptional_TextBoxBlocked_TheWholePairDrops_AndReservesNothing()
        {
            OptionalPairOutcome o = RunOptionalPairScene(null, SymbolKind.Text);

            Assert.AreEqual(0, o.OptionalBoxMask, "the spec default leaves NEITHER half optional");
            Assert.IsFalse(o.PairPlaced,
                "with both properties defaulted the pair must still drop TOGETHER (P3) — no bare badge");
            Assert.AreEqual(0, o.DroppedBoxMask, "a dropped candidate records no per-half verdict");
            Assert.IsTrue(o.ProbeOverIconPlaced, "a dropped pair reserves NO box, so both probes place");
            Assert.IsTrue(o.ProbeOverTextPlaced);
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void UsShieldLayers_SelectNothingFromBerlinFixture()
        {
            SymbolStyle.StyleLayer interstate = FindShieldLayer("highway-shield-us-interstate");
            SymbolStyle.StyleLayer usShield = FindShieldLayer("road_shield_us");
            Assert.IsNotNull(interstate); Assert.IsNotNull(usShield);

            int interstateCount = MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(interstate, BerlinFixtureTile(), 13.0).Count;
            int usShieldCount = MapRenderer.Jobs.Tiles.FeatureSelector.SelectFeatures(usShield, BerlinFixtureTile(), 13.0).Count;

            Assert.AreEqual(0, interstateCount, "highway-shield-us-interstate must select 0 features from the Berlin fixture");
            Assert.AreEqual(0, usShieldCount, "road_shield_us must select 0 features from the Berlin fixture");
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void PointAnchor_ClipSemantics()
        {
            var projection = new WebMercatorProjection();

            // (a) mid-arc lands OUTSIDE [0, extent) — the accepted loss.
            {
                var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: LineStringGeometry(new double2(-1000, 500), new double2(100, 600)));
                var tile = TestDecodedTiles.Of("lines", SyntheticTileId, new List<IFeature> { feature }, Extent);
                var styleLayer = new SymbolStyle.StyleLayer
                {
                    Id = "clip-a", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                    Paint = TestStyle.SymbolPaint(),
                    Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"point\"}"),
                };
                var symbols = new List<SymbolStyle.SymbolFeature>();
                SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0, projection, symbols);
                Assert.AreEqual(0, symbols.Count, "a buffer-dominated path (mid-arc outside [0,extent)) must emit ZERO symbols");
            }

            // (b) mid-arc lands INSIDE [0, extent) — exactly one symbol, at the true mid-arc point.
            {
                double2 p0 = new double2(500, 500), p1 = new double2(3500, 3500);
                var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: LineStringGeometry(p0, p1));
                var tile = TestDecodedTiles.Of("lines", SyntheticTileId, new List<IFeature> { feature }, Extent);
                var styleLayer = new SymbolStyle.StyleLayer
                {
                    Id = "clip-b", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                    Paint = TestStyle.SymbolPaint(),
                    Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"point\"}"),
                };
                var symbols = new List<SymbolStyle.SymbolFeature>();
                SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0, projection, symbols);
                Assert.AreEqual(1, symbols.Count, "an in-tile mid-arc must emit exactly one label");

                double2 midTile = (p0 + p1) * 0.5;
                double2 lonLat = SyntheticTileId.ToLonLat(midTile.x, midTile.y, Extent);
                double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                Assert.AreEqual(expected.x, symbols[0].AnchorRender.x, 1e-6, "anchor must be the true mid-arc point");
                Assert.AreEqual(expected.y, symbols[0].AnchorRender.y, 1e-6);
                Assert.AreEqual(expected.z, symbols[0].AnchorRender.z, 1e-6);
            }

            // (c) under LINE placement (viewport-aligned -> upright-at-anchor), only in-tile anchors emit.
            {
                double2 p0 = new double2(-3000, 1000), p1 = new double2(3000, 1000); // length 6000
                var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: LineStringGeometry(p0, p1));
                var tile = TestDecodedTiles.Of("lines", SyntheticTileId, new List<IFeature> { feature }, Extent);
                var styleLayer = new SymbolStyle.StyleLayer
                {
                    Id = "clip-c", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                    Paint = TestStyle.SymbolPaint(),
                    Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"line\",\"text-rotation-alignment\":\"viewport\",\"symbol-spacing\":100}"),
                };
                var symbols = new List<SymbolStyle.SymbolFeature>();
                SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 0.0, projection, symbols);

                Assert.Greater(symbols.Count, 0, "an edge-crossing viewport-aligned line must still emit some upright symbols");
                foreach (SymbolStyle.SymbolFeature l in symbols)
                    Assert.AreEqual(SymbolPlacement.Point, l.Placement, "upright-at-anchor symbols are Point-placed");

                // Independently compute the FULL anchor set (unclipped) and confirm some fall outside [0,extent) —
                // i.e. this scenario genuinely exercises the clip, not a vacuous all-in-tile case.
                double spacingTileUnits = 100.0 * Extent / MapRenderer.Core.Geo.WebMercator.TilePixelSize;
                LineAnchor[] allAnchors = LineAnchorPlacement.Compute(new List<double2> { p0, p1 }, spacingTileUnits, SymbolPlacement.Line);
                int outOfTile = 0;
                foreach (LineAnchor a in allAnchors)
                {
                    // Single-segment path (Segment is always 0): a manual lerp, the local idiom of
                    // SymbolFeatureExtractorTests' anchor resolve.
                    double2 pos = p0 + (p1 - p0) * (double)a.T;
                    if (pos.x < 0.0 || pos.x >= Extent) outOfTile++;
                }
                Assert.Greater(outOfTile, 0, "precondition: this scenario must genuinely have out-of-tile anchors");
                Assert.Less(symbols.Count, allAnchors.Length, "some anchors must have been clipped away");
            }
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void SymbolPlacement_IsEvaluatedAtBuildZoom()
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: LineStringGeometry(new double2(500, 500), new double2(3500, 3500)));
            var tile = TestDecodedTiles.Of("lines", SyntheticTileId, new List<IFeature> { feature }, Extent);
            var styleLayer = new SymbolStyle.StyleLayer
            {
                Id = "step-probe", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":[\"step\",[\"zoom\"],\"point\",11,\"line\"]}"),
            };
            var projection = new WebMercatorProjection();

            var below = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 10.9, projection, below);
            var above = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(styleLayer, tile, SyntheticTileId, 11.0, projection, above);

            Assert.Greater(below.Count, 0, "z10.9 (below the z11 step) must emit the point shape");
            foreach (SymbolStyle.SymbolFeature l in below)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "z10.9 must yield Point placement");

            Assert.Greater(above.Count, 0, "z11.0 (at/above the z11 step) must emit the line shape");
            foreach (SymbolStyle.SymbolFeature l in above)
                Assert.AreEqual(SymbolPlacement.Line, l.Placement, "z11.0 must yield Line placement");
        }

        // ───────────────────────────────────────────────────────────────────────────────────────────────
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

        // ───────────────────────────────────────────────────────────────────────────────────────────────
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

        // ── The along-line anchor clip ─────────────────────────────────────────────────────────────────
        // A horizontal road across a VERTICAL seam, with IDENTICAL local geometry in both tiles (clip plus a
        // 128-unit buffer), so both tiles claim the same world stretch.
        // Non-obvious why: LineAnchor.T is a FLOAT, so vertices AT x == 0 and x == 4096 put every anchor
        // bit-exactly on x == 256k; mid-segment rounding would decide the `< extent` comparison instead.
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

        // Icon arm: unset alignment -> MAP under line placement (road_one_way_arrow*). The text arm declares
        // map explicitly, so it stays curved rather than upright.
        private static SymbolStyle.StyleLayer SeamLayer(SeamArm arm) => new SymbolStyle.StyleLayer
        {
            Id          = "seam-clip-probe",
            LayerType   = StyleLayerType.Symbol,
            Source      = "s",
            SourceLayer = "lines",
            Paint = TestStyle.SymbolPaint(),
            Layout = TestStyle.SymbolLayout(arm == SeamArm.AlongLineIcon
                    ? "{\"icon-image\":\"road_3\",\"symbol-placement\":\"line\",\"symbol-spacing\":32}"
                    : "{\"text-field\":\"L\",\"symbol-placement\":\"line\",\"text-rotation-alignment\":\"map\"," +
                      "\"symbol-spacing\":32}"),
        };

        private static IDecodedTile LineTile(params double2[][] paths)
        {
            var features = new List<IFeature>();
            foreach (double2[] path in paths)
                features.Add(new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: LineStringGeometry(path)));
            return TestDecodedTiles.Of("lines", SyntheticTileId, features, Extent);
        }

        private static List<SymbolStyle.SymbolFeature> ExtractLines(
            SymbolStyle.StyleLayer layer, TileId tileId, params double2[][] paths)
        {
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, LineTile(paths), tileId, 1.0,
                new WebMercatorProjection(), symbols, SyntheticShieldAtlas());
            return symbols;
        }

        /// <summary>The tile-space x an anchor resolves to on <paramref name="path"/> — the same
        /// <c>lerp(v[Segment], v[Segment+1], T)</c> recovery <see cref="LineAnchor"/> documents, computed here
        /// from the test's own vertices so no production code is borrowed to check production code.</summary>
        private static double AnchorLocalX(LineAnchor anchor, double2[] path)
            => math.lerp(path[anchor.Segment], path[anchor.Segment + 1], anchor.T).x;

        /// <summary>Every emitted along-line anchor of <paramref name="symbols"/>, as a world x in tile units
        /// (<c>tileId.X · extent + localX</c>) — one flat list, in emit order.</summary>
        private static List<double> EmittedWorldXs(
            List<SymbolStyle.SymbolFeature> symbols, TileId tileId, double2[] path)
        {
            var world = new List<double>();
            foreach (SymbolStyle.SymbolFeature l in symbols)
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
            List<SymbolStyle.SymbolFeature> a = ExtractLines(SeamLayer(arm), SeamTileA, SeamRoadVertices);
            List<SymbolStyle.SymbolFeature> b = ExtractLines(SeamLayer(arm), SeamTileB, SeamRoadVertices);
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

        // Two-tile: a world position claimed by two tiles is emitted by exactly one of them. The duplicate
        // exists only in the union, so single-tile output cannot show it.
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

        // Anti-over-clip: GREEN before AND after. The surviving set is exactly the positions the geometry
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

        // The boundary SENSE: the seam position is emitted by the tile whose LOCAL coordinate is 0, never
        // `extent`. Only this separates `< extent` from `<= extent` and `>= 0` from `> 0`.
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

        // The SAME property on the Y axis: a vertical road across the HORIZONTAL seam between z1 (0,0) and (0,1),
        // so a predicate that tests `p.x` twice or uses `p.y <= extent` fails here.
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
            foreach ((TileId tile, List<SymbolStyle.SymbolFeature> symbols) in new[]
                     { (tileTop,    ExtractLines(SeamLayer(arm), tileTop,    road)),
                       (tileBottom, ExtractLines(SeamLayer(arm), tileBottom, road)) })
            {
                Assert.Greater(symbols.Count, 0, $"{arm}: precondition: tile {tile.Y} must emit a label");
                foreach (SymbolStyle.SymbolFeature l in symbols)
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

        // The path is NOT clipped: only anchors are filtered. A shallow implementation that clipped the
        // polyline instead would shorten PathRender and turn a join into a cap.
        [Test]
        public void SeamAnchorClip_DoesNotClipThePath(
            [Values(SeamArm.AlongLineIcon, SeamArm.CurvedText)] SeamArm arm)
        {
            var projection = new WebMercatorProjection();
            List<SymbolStyle.SymbolFeature> symbols = ExtractLines(SeamLayer(arm), SeamTileA, SeamRoadVertices);
            Assert.Greater(symbols.Count, 0, $"{arm}: precondition: the seam road must emit a curved label");

            foreach (SymbolStyle.SymbolFeature l in symbols)
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

        // A path whose every anchor belongs to a neighbour emits NOTHING here, rather than an anchor-less
        // symbol that can never place.
        [Test]
        public void SeamAnchorClip_PathWithNoSurvivingAnchor_EmitsNoSymbol(
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

            List<SymbolStyle.SymbolFeature> symbols =
                ExtractLines(SeamLayer(arm), SeamTileA, BufferOnlyRoadVertices);
            Assert.AreEqual(0, symbols.Count,
                $"{arm}: a buffer-only path must emit ZERO symbols here — not one carrying an empty LineAnchors");
        }

        // The at-anchors arm (the shield shape) applies the same predicate; drift between the two copies of
        // the rule shows up here as a shield regression.
        [Test]
        public void SeamAnchorClip_AtAnchorsArmIsUnchanged()
        {
            var projection = new WebMercatorProjection();
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "seam-at-anchors-probe", LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = "lines",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"line\",\"text-rotation-alignment\":\"viewport\"," +
                    "\"symbol-spacing\":32}"),
            };

            List<SymbolStyle.SymbolFeature> symbols = ExtractLines(layer, SeamTileA, SeamRoadVertices);

            // 17 pre-clip anchors at local x = 0 .. 4096; EmitAtAnchor already dropped local 4096 (>= extent).
            Assert.AreEqual(SeamPreClipAnchors - 1, symbols.Count,
                "the at-anchors arm must emit one upright label per IN-TILE anchor — 16, exactly as before");
            foreach (SymbolStyle.SymbolFeature l in symbols)
                Assert.AreEqual(SymbolPlacement.Point, l.Placement, "upright-at-anchor symbols are Point-placed");

            foreach (int k in new[] { 0, SeamPreClipAnchors - 2 })
            {
                double2 lonLat = SeamTileA.ToLonLat(SeamAnchorStride * k, SeamRoadVertices[0].y, Extent);
                double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                Assert.AreEqual(expected.x, symbols[k].AnchorRender.x, 1e-6,
                    $"label {k} must sit at local x == {SeamAnchorStride * k}");
                Assert.AreEqual(expected.y, symbols[k].AnchorRender.y, 1e-6);
                Assert.AreEqual(expected.z, symbols[k].AnchorRender.z, 1e-6);
            }
        }
    }
}
