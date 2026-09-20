// Unity EditMode only. It drives SymbolFeatureExtractor.Extract, which since tile-geometry IR B4
// materializes a Waist-1 TileGeometryBuffers and therefore depends on Unity.Collections — so this file left
// Tools/core-tests (no coverage lost, only iteration speed) and must not be re-added to core-tests.csproj.

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
    /// <summary>
    /// P-A — the icon+text PAIRING predicate is "this feature resolved both halves", not "the two halves
    /// coincide". These are the teeth that DISCRIMINATE that change: each one sets a value at which the five
    /// retired conjuncts (text/icon anchor, text/icon offset, text-radial-offset) were the thing saying "no
    /// pair". The pre-existing centred teeth (<c>SymbolShieldExtractionTests</c>,
    /// <c>SymbolFeatureExtractorIconTests</c>) leave every anchor/offset at its default, so the retired code
    /// was inert at their values and they cannot tell the two predicates apart.
    /// </summary>
    [TestFixture]
    public class SymbolPairPredicateTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private const uint Extent = 4096;
        private static readonly TileId SyntheticTileId = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId PlaceTileId = new TileId { Z = 6, X = 32, Y = 20 };
        private static readonly TileId BerlinTileId = new TileId { Z = 9, X = 274, Y = 168 };

        // ── Synthetic sprite sheet: the committed fixtures carry NO sprite sheet for liberty's own names,
        //    so `circle_11_black` / `airport_11` / `road_3` have to be synthesised. 16x16 at the origin —
        //    only the sprite's EXISTENCE and its size matter to these teeth. ──
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
                Paint = SymbolStyle.PaintProperties.Parse(null),
                Layout = SymbolStyle.LayoutProperties.Parse(MapRenderer.Core.Json.JsonParser.Parse(layoutJson)),
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

        // ══ T2 — predicate symmetry: EACH retired conjunct, alone, on an otherwise centred layer ══════════
        // One case per conjunct, so a partial relaxation (the tempting "just drop the text-side checks")
        // cannot pass: the two icon-side cases would still read None.
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

        // ══ T3 — the F1 tooth: a non-centred pair's two boxes land where each half's OWN baked bounds say ══
        // P-A rests on a premise: `text-anchor`/`text-offset`/`text-radial-offset` (and their icon twins) are
        // already folded into each half's anchor-RELATIVE bounds upstream, so StagePointPair appending both
        // halves at the OWNER's ScreenPx displaces them correctly with no per-half placement plumbing. If
        // that premise were false the two boxes would coincide and a non-centred pair would be nonsense.
        // This runs the REAL chain — TextQuadLayout.Layout for the text, IconQuadLayout.Layout (via the
        // extractor) for the icon, StagePointPair for the staging — and pins each box against the box built
        // from that half's OWN bounds at its OWN size.
        [Test]
        public void NonCentredPair_EachHalfsBox_IsBuiltFromItsOwnBakedBounds_AndTheyAreDisjoint()
        {
            // text-anchor bottom puts the text above its anchor; text-offset [0,-2] (y-DOWN as authored, so
            // 2 em UPWARD) pushes it clear of a 16 px icon. The magnitude is the discriminating value: at a
            // zero offset the boxes coincide whatever the staging does, and the tooth would prove nothing.
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

        // ══ T4 — the maintainer's artefact: liberty's city dot no longer outlives its own name ════════════
        // "dots are still visible without text." liberty sets icon-allow-overlap: true on the dot and leaves
        // the text at the default false. Un-paired, the two are independent candidates and the dot's
        // allow-overlap buys it an unconditional place while the name is culled. Paired, the candidate's
        // AllowOverlap is the AND of the halves, so the whole instance drops together. The ASYMMETRY is what
        // makes this discriminating — a layer with both flags false would drop either way.
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

            // Stage the instance the way the placement system does: ONE pair candidate when the extractor
            // proposed a pair, two independent candidates when it did not. Reverting the predicate therefore
            // reads RED as the ARTEFACT (an orphan dot survives), not as a broken precondition.
            var boxes = new SymbolBox[8];
            var quads = new PlacedQuad[8];
            var candidates = new SymbolCandidate[4];
            var emit = new CandidateEmit[8];
            int boxCount = 0, quadCount = 0, emitCount = 0;

            // Synthetic half-bounds + a viewport translate on the rider: label_city's real text-offset is
            // -0.1 em, far too small to separate the boxes, and a blocker that overlaps BOTH halves would not
            // isolate "the text half was blocked". T3 is where the real baked bounds are pinned.
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

        // ══ T5 — D-PA-3: text-optional does NOT un-pair; it stays a per-BOX verdict inside the pair ═══════
        [Test]
        public void LibertyAirport_TextOptional_StillPairs_AndMarksOnlyTheRiderDroppable()
        {
            SymbolStyle.StyleLayer airport = SymbolTestFixtures.FindSymbolLayer("airport");
            Assert.IsNotNull(airport, "precondition: airport must parse as a symbol layer");
            Assert.IsTrue(airport.Layout.TextOptional,
                "precondition: this tooth turns on liberty's REAL `text-optional: true` — if the style ever " +
                "drops it, the tooth is inert and must be re-pointed, not re-baked");

            // The REAL liberty layer over a hand-built aerodrome_label tile. The committed Berlin fixture DOES
            // carry the airport feature, but at tile x=4929 — outside [0, extent), so the single-world clip
            // drops it and the extractor emits nothing. Only the FEATURE is synthetic here; the style
            // (text-optional + text-anchor top + text-offset [0,0.6]) is liberty's own.
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

            // The pair FORMS despite the flag — D11 ("pair only when both optional flags are false") would
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

        // ══ T6 — the negatives: the predicate needs BOTH halves, never either ═════════════════════════════
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

        // ══ T8 — D-PA-4: the line branch re-gates on BOTH suppressions, not just the icon's ═══════════════
        // The mirror of ViewportTextWithAlongLineIcon_StampsNoPairRole: there the ICON left for the
        // along-line shape; here the TEXT leaves for the curved shape, so the at-anchors emit has a lone
        // icon and no rider will ever follow it. Without the `&& textAtAnchors` conjunct the extractor
        // stamps that icon Owner and the "PairedInstance implies both halves" contract is a false claim.
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
                Paint = SymbolStyle.PaintProperties.Parse(null),
                Layout = SymbolStyle.LayoutProperties.Parse(MapRenderer.Core.Json.JsonParser.Parse(
                    "{\"symbol-placement\":\"line\",\"text-field\":[\"to-string\",[\"get\",\"ref\"]]," +
                    "\"icon-image\":\"road_3\",\"text-rotation-alignment\":\"map\"," +
                    "\"icon-rotation-alignment\":\"viewport\"}")),
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

        /// <summary>IR C1 P3: the cached fixture tiles own native buffers for the whole fixture's life.</summary>
        [OneTimeTearDown]
        public void ReleaseCachedFixtureTiles()
        {
            _placeTile?.Dispose();  _placeTile  = null;
            _berlinTile?.Dispose(); _berlinTile = null;
        }

        // A synthetic quad standing in for a shaped text run — the teeth that need REAL text geometry (T3)
        // lay it out for real; these only need quads.Length > 0 so the half actually stages.
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
}
