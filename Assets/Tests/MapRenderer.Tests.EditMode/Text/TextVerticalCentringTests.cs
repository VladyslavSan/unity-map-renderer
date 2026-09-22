// Text/TextVerticalCentringTests.cs — text/glyph shaping, layout, and rendering teeth: alignment, curved text, font-stack fallback, glyph atlas/PBF decode, icon quads, RTL, wrap, and vertical centring (fast lane: engine-free, compiled by Tools/core-tests too).
//
// Grouped by what they exercise: alignment resolution, then the two curved-text fixtures, then font-stack fallback, then the four glyph-atlas/PBF/SDF fixtures, then icon-quad layout, then the text-layout fixtures (anchor/offset/justify, quad layout, RTL, shaping, vertical centring, wrap).
//
// Contents:
//   AlignmentResolutionTests      — pins ResolvePitch: the spec's pitch-alignment auto resolves against the RESOLVED rotation alignment (never the raw one), an explicit pitch value always wins, and the two enum arguments are not interchangeable.
//   CurvedTextCentringTests       — A curved (along-line) text cell is centred VERTICALLY on the path, by the same optical (cap-band) metric a centred point symbol uses (docs/road-shields-design.md).
//   CurvedTextLayoutTests         — CurvedTextLayout maps a shaped single-line run to per-glyph (arc-center, path-centred cell), the build-time half of curved along-line text.
//   FontStackFallbackTests        — Font-stack fallback.
//   GlyphAtlasFixedSizeTests      — The FIXED-size glyph atlas (a big pre-allocated atlas whose Size never changes as glyphs append).
//   GlyphAtlasTests               — Atlas packing + CPU blit, against the same committed glyph-PBF fixture the decode tests use (Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes).
//   GlyphManagerTests             — GlyphManager's fetch/decode/cache/atlas wiring, exercised against a fake in-memory TestGlyphSource serving the committed Assets/Fixtures/glyphs/NotoSansRegular/*.bytes fixtures (never the network).
//   GlyphPbfDecodeTests           — Glyph-PBF decode against a real fixture (demotiles "Noto Sans Regular" 0-255 range, committed as Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes).
//   IconQuadLayoutTests           — Layout over the committed sample-sprite.json fixture's own numbers (sheet 64×64 — marker 16×16@1x, star 24×24@2x, dot 8×8@1x at (0,32)), hand-pinned rather than re-derived, so a formula regression (e.g.
//   SdfDistanceFieldTests         — The decoded glyph-PBF bitmap is a REAL signed distance field, not a coverage bitmap masquerading as one, against the same committed fixture (Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes) Slice 1's decode tests use.
//   TextAnchorOffsetJustifyTests  — T2 (text-anchor shifts the whole multi-line block bbox, H and V), T3 (text-offset / text-radial-offset, ems -&gt; baked px), T4 (text-justify incl.
//   TextQuadLayoutTests           — T1 (THE decisive per-glyph quad golden: buffer + UV + pen-advance + anchor, element-by-element) and T8 (structural: no text-size parameter; SymbolQuad is blittable).
//   TextRtlLayoutTests            — RTL single-line correctness.
//   TextShapingTests              — THE decisive Model-A/Option-Y shaping test.
//   TextVerticalCentringTests     — docs/road-shields-design.md — a centred text block is centred on its INK, not on a line box.
//   TextWrapTests                 — T5 (greedy word-wrap, golden line assignment) and T6 (whitespace advances the pen but emits no quad).

using NUnit.Framework;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Tests.Style; // SymbolTestFixtures lives in the Style test folder
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using System.Threading.Tasks;
using MapRenderer.Unity.Text;
using System.Security.Cryptography;
using MapRenderer.Core.Text.Sprites;
using System.Linq;
using System.Reflection;


namespace MapRenderer.Tests.Text
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // AlignmentResolutionTests — ResolvePitch resolves against the RESOLVED rotation alignment
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins <see cref="AlignmentResolution.ResolvePitch"/>: the spec's
    /// pitch-alignment <c>auto</c> resolves against the RESOLVED rotation alignment (never the raw one), an
    /// explicit pitch value always wins, and the two enum arguments are not interchangeable. Engine-free;
    /// runs in both runners.
    /// </summary>
    [TestFixture]
    public class AlignmentResolutionTests
    {
        private static readonly AlignmentMode[] AllModes =
            { AlignmentMode.Auto, AlignmentMode.Map, AlignmentMode.Viewport };

        private static readonly SymbolPlacement[] AllPlacements =
            { SymbolPlacement.Point, SymbolPlacement.Line, SymbolPlacement.LineCenter };

        // T1 — the exhaustive 27-row resolution table (the core tooth).
        [Test]
        public void ResolvePitch_ExhaustiveTable_MatchesSpecChain()
        {
            foreach (AlignmentMode pitch in AllModes)
            foreach (AlignmentMode rotation in AllModes)
            foreach (SymbolPlacement placement in AllPlacements)
            {
                AlignmentMode expected = ExpectedFor(pitch, rotation, placement);
                AlignmentMode actual = AlignmentResolution.ResolvePitch(pitch, rotation, placement);
                Assert.AreEqual(expected, actual,
                    $"pitch={pitch}, rotation={rotation}, placement={placement}");
            }
        }

        // Hand-derivation of the table in the plan/spec — kept separate from the production switch so the
        // test doesn't just restate the implementation.
        private static AlignmentMode ExpectedFor(AlignmentMode pitch, AlignmentMode rotation, SymbolPlacement placement)
        {
            if (pitch == AlignmentMode.Map) return AlignmentMode.Map;
            if (pitch == AlignmentMode.Viewport) return AlignmentMode.Viewport;
            // pitch == Auto
            if (rotation == AlignmentMode.Map) return AlignmentMode.Map;
            if (rotation == AlignmentMode.Viewport) return AlignmentMode.Viewport;
            // rotation == Auto too: resolves by placement, exactly like Resolve().
            return placement == SymbolPlacement.Point ? AlignmentMode.Viewport : AlignmentMode.Map;
        }

        // T2 — never Auto, over the same 27 rows.
        [Test]
        public void ResolvePitch_NeverReturnsAuto()
        {
            foreach (AlignmentMode pitch in AllModes)
            foreach (AlignmentMode rotation in AllModes)
            foreach (SymbolPlacement placement in AllPlacements)
            {
                AlignmentMode actual = AlignmentResolution.ResolvePitch(pitch, rotation, placement);
                Assert.AreNotEqual(AlignmentMode.Auto, actual,
                    $"ResolvePitch's contract is a RESOLVED value — pitch={pitch}, rotation={rotation}, placement={placement}");
            }
        }

        // T3 — the argument order is not symmetric; kills a swapped-parameter implementation.
        [Test]
        public void ResolvePitch_ArgumentOrder_IsNotSymmetric()
        {
            Assert.AreEqual(AlignmentMode.Map,
                AlignmentResolution.ResolvePitch(AlignmentMode.Map, AlignmentMode.Viewport, SymbolPlacement.Point),
                "an explicit Map pitch wins over a Viewport rotation");
            Assert.AreEqual(AlignmentMode.Viewport,
                AlignmentResolution.ResolvePitch(AlignmentMode.Viewport, AlignmentMode.Map, SymbolPlacement.Point),
                "an explicit Viewport pitch wins over a Map rotation");
        }

        // T5 — grounded on shipped liberty layers, in both directions.
        [Test]
        public void ResolvePitch_Liberty_TextLineLayers_ResolveMap()
        {
            // highway-name-path/-minor/-major: text-rotation-alignment is EXPLICIT 'map' (not auto), and
            // text-pitch-alignment is absent (auto). This grounds the explicit-rotation pass-through
            // (Resolve(Map, *) = Map regardless of placement) on real shipped data — it is NOT the
            // auto-auto-placement chain (that genuine witness is road_one_way_arrow* below, where BOTH
            // keys are absent).
            foreach (string id in new[] { "highway-name-path", "highway-name-minor", "highway-name-major" })
            {
                SymbolStyle.StyleLayer layer = SymbolTestFixtures.FindSymbolLayer(id);
                Assert.IsNotNull(layer, $"liberty must ship a symbol layer '{id}'");

                LayoutProperties layout = layer.Layout;
                SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(14.0, null, out SymbolPlacement evaluated)
                    ? evaluated : SymbolPlacement.Point;

                AlignmentMode resolved = AlignmentResolution.ResolvePitch(
                    layout.TextPitchAlignment, layout.TextRotationAlignment, placement);
                Assert.AreEqual(AlignmentMode.Map, resolved,
                    $"{id}: text-pitch-alignment absent (auto), text-rotation-alignment EXPLICIT map " +
                    "-> pitch resolves map via the explicit-rotation pass-through");
            }
        }

        [Test]
        public void ResolvePitch_Liberty_RoadOneWayArrow_IconResolvesMap()
        {
            // road_one_way_arrow / road_one_way_arrow_opposite: symbol-placement:line, BOTH icon-rotation-alignment
            // and icon-pitch-alignment absent (auto/auto) -- the full pitch-auto -> rotation-auto -> placement
            // chain, the single highest-value row in the stage.
            foreach (string id in new[] { "road_one_way_arrow", "road_one_way_arrow_opposite" })
            {
                SymbolStyle.StyleLayer layer = SymbolTestFixtures.FindSymbolLayer(id);
                Assert.IsNotNull(layer, $"liberty must ship a symbol layer '{id}'");

                LayoutProperties layout = layer.Layout;
                SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(14.0, null, out SymbolPlacement evaluated)
                    ? evaluated : SymbolPlacement.Point;

                AlignmentMode resolved = AlignmentResolution.ResolvePitch(
                    layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
                Assert.AreEqual(AlignmentMode.Map, resolved,
                    $"{id}: icon-pitch-alignment and icon-rotation-alignment both absent (auto), placement line " +
                    "-> pitch resolves map via the full auto-auto chain");
            }
        }

        [Test]
        public void ResolvePitch_Liberty_HighwayShieldNonUs_IconResolvesViewport()
        {
            // Contrast row: highway-shield-non-us sets icon-rotation-alignment:viewport EXPLICITLY, so icon
            // pitch resolves viewport regardless of placement -- without this row, T5 could pass on an
            // implementation that returns Map unconditionally. symbol-placement is a step expression
            // (point below z11, line at/above); zoom 12 (>= 11) picked so placement is 'line', proving the
            // explicit rotation value still wins over the line-placement auto-auto default.
            SymbolStyle.StyleLayer layer = SymbolTestFixtures.FindSymbolLayer("highway-shield-non-us");
            Assert.IsNotNull(layer, "liberty must ship a symbol layer 'highway-shield-non-us'");

            LayoutProperties layout = layer.Layout;
            SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(12.0, null, out SymbolPlacement evaluated)
                ? evaluated : SymbolPlacement.Point;
            Assert.AreEqual(SymbolPlacement.Line, placement, "z12 is at/above the step's z11 boundary -> line");

            AlignmentMode resolved = AlignmentResolution.ResolvePitch(
                layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
            Assert.AreEqual(AlignmentMode.Viewport, resolved,
                "icon-rotation-alignment:viewport is explicit -> icon pitch resolves viewport even under line placement");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // CurvedTextCentringTests — a curved text cell centres vertically by the same optical metric
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A curved (along-line) text cell is centred VERTICALLY on the path, by the same optical
    /// (cap-band) metric a centred point symbol uses (<c>docs/road-shields-design.md</c>). The
    /// curved producer used to leave every cell baseline-relative, so a road symbol rendered a fixed offset
    /// above/below the road it was drawn along — at every tilt, tilt 0 included.
    ///
    /// <para><b>Oracle hygiene.</b> No tooth here takes <c>TextQuadLayout.OpticalCentreBelowReferencePx</c>
    /// as its expected value — that is the code under test, and an oracle derived from it is vacuous.
    /// T1 measures the ink band the committed fixture's own glyph metrics predict, T2 measures a
    /// per-glyph-vs-per-symbol distinction that needs no magnitude at all, and T3 uses the POINT path
    /// (fixed separately, pinned by <c>TextVerticalCentringTests</c>) as an
    /// independent reference.</para>
    /// </summary>
    [TestFixture]
    public class CurvedTextCentringTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(params uint[] codepoints)
        {
            var list = new List<PositionedGlyph>(codepoints.Length);
            for (int i = 0; i < codepoints.Length; i++)
                list.Add(new PositionedGlyph { AtlasCodepoint = codepoints[i], XAdvance = 0f, Cluster = i });
            return new ShapedRun { Glyphs = list, Direction = TextDirection.LeftToRight };
        }

        /// <summary>Bare (unpadded) glyph height in baked px — the SDF cell less its symmetric buffer border.</summary>
        private static int BareHeight(in GlyphAtlasEntry entry) => entry.CellSize.y - 2 * GlyphSdf.Buffer;

        // =========================================================================================
        // T1 — the headline invariant, in cell coordinates: a baseline-resting CAP glyph's ink band
        // straddles the anchor. Cell y=0 is the point on the path (both consumers map the cell linearly
        // and homogeneously about the anchor — BillboardMath.BuildWorldQuad, SymbolBox.BuildRotatedGlyph),
        // so "on the road" is exactly "ink band symmetric about y=0".
        //
        // The preconditions come FIRST and from the fixture's own entry, the V9 pattern: this tooth's
        // exactness rests on '5' being a baseline-resting glyph whose bare height IS the nominal cap
        // height. A regenerated fixture baked against a different ascent must fail loudly here rather
        // than drift silently through a still-green suite.
        // =========================================================================================
        [Test]
        public void CurvedCell_InkBand_IsSymmetricAboutTheAnchor_ForACapGlyph()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);

            int bareHeight = BareHeight(entryFive);
            Assert.AreEqual(17, bareHeight,
                $"precondition: the fixture's '5' must be a {GlyphSdf.NominalCapHeightEm * TextQuadLayout.OneEm} " +
                $"baked-px cap-height glyph; measured {bareHeight}");
            Assert.AreEqual(-9, entryFive.Top,
                $"precondition: the fixture's '5' must rest on the baseline (bare height - Top == " +
                $"{GlyphSdf.BaselineBelowReferencePx}); measured Top {entryFive.Top}, height {bareHeight}");

            IReadOnlyList<CurvedGlyph> glyphs = CurvedTextLayout.Layout(MakeRun((uint)'5'), atlas);
            Assert.AreEqual(1, glyphs.Count, "'5' is a single visible glyph");

            // Strip the symmetric SDF buffer border to recover the INK band from the padded cell.
            SymbolQuad cell = glyphs[0].Cell;
            float inkTop = cell.TopLeft.y - GlyphSdf.Buffer;
            float inkBottom = cell.BottomRight.y + GlyphSdf.Buffer;
            float centre = 0.5f * (inkTop + inkBottom);

            Assert.LessOrEqual(math.abs(centre), 1e-3f,
                $"a cap glyph's ink band must straddle the path it is drawn along: measured " +
                $"[{inkBottom}, {inkTop}], centre {centre} baked px (0 expected exactly). " +
                $"No vertical centring at all reads -{GlyphSdf.BaselineBelowReferencePx - 0.5f * GlyphSdf.NominalCapHeightEm * TextQuadLayout.OneEm}.");
        }

        // =========================================================================================
        // T2 — the shift is ONE CONSTANT PER LABEL, not per glyph. A run's internal typography must
        // survive intact: 'A', 'g' and '5' keep their relative offsets, descenders still descend.
        //
        // What this tooth does and does NOT observe, stated honestly: the residual is constant under BOTH
        // the old code (residual == Buffer) and the correct fix (residual == Buffer + the shift), so
        // T2 cannot see MAGNITUDE — T1 does that. What T2 alone catches is the REJECTED per-glyph
        // metric (centring each cell on its own box, the obvious "just centre it" fix), which makes the
        // residual vary with each glyph's own Top/Height while leaving a symmetric cap glyph like '5'
        // looking perfectly correct. T1 and T2 are a pair; neither alone is the tooth.
        // =========================================================================================
        [Test]
        public void CurvedCells_ShiftIsOnePerSymbolConstant_NotPerGlyph()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            uint[] codepoints = { (uint)'A', (uint)'g', (uint)'5' };
            var entries = new GlyphAtlasEntry[codepoints.Length];
            for (int i = 0; i < codepoints.Length; i++) entries[i] = atlas.Append(latin.Glyphs[codepoints[i]], 0);

            // Vacuity guard: a run whose glyphs share their metrics could not distinguish per-glyph from
            // per-symbol centring at all.
            Assert.AreNotEqual(BareHeight(entries[0]), BareHeight(entries[1]),
                $"vacuity guard: 'A' and 'g' must differ in bare height on this fixture " +
                $"({BareHeight(entries[0])} vs {BareHeight(entries[1])})");
            Assert.AreNotEqual(entries[0].Top, entries[1].Top,
                $"vacuity guard: 'A' and 'g' must differ in Top on this fixture " +
                $"({entries[0].Top} vs {entries[1].Top})");

            IReadOnlyList<CurvedGlyph> glyphs = CurvedTextLayout.Layout(MakeRun(codepoints), atlas);
            Assert.AreEqual(codepoints.Length, glyphs.Count, "all three glyphs are visible");

            float residualZero = glyphs[0].Cell.TopLeft.y - entries[0].Top;
            for (int i = 1; i < codepoints.Length; i++)
            {
                float residual = glyphs[i].Cell.TopLeft.y - entries[i].Top;
                Assert.AreEqual(residualZero, residual, 1e-4f,
                    $"every cell in a run must be shifted by the SAME amount — '{(char)codepoints[i]}' " +
                    $"moved by {residual} where '{(char)codepoints[0]}' moved by {residualZero}. A residual " +
                    $"that tracks the glyph's own metrics (Top {entries[i].Top}, bare height " +
                    $"{BareHeight(entries[i])}) means each glyph was centred on its own ink, which makes " +
                    $"ascenders and descenders bob relative to one another along the road.");
            }
        }

        // =========================================================================================
        // T3 — cross-check against the POINT path, which already centres correctly and is untouched by
        // this stage (pinned by TextVerticalCentringTests). Same glyph, same atlas, two
        // independent producers: their cells' VERTICAL band must agree. x legitimately differs — the point
        // path centres the whole block, the curved path centres each glyph on its own arc centre.
        //
        // The equality holds because the point path's centre shift carries a (lineCount - 1) line-spacing
        // term that vanishes for the single line a curved symbol always is; LineCount is asserted as a
        // precondition so a future multi-line change cannot silently make this compare two different things.
        // =========================================================================================
        [Test]
        public void CurvedCell_VerticalBand_MatchesThePointPath_ForTheSameGlyph()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            atlas.Append(latin.Glyphs[(uint)'5'], 0);
            ShapedRun run = MakeRun((uint)'5');

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Center,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var pointQuads = new List<SymbolQuad>();
            TextLayoutBounds point = TextQuadLayout.Layout(run, atlas, in options, pointQuads);
            Assert.AreEqual(1, point.LineCount, "precondition: the point twin must be single-line, or the " +
                "block's (lineCount - 1) line-spacing term stops vanishing and the two paths are no longer comparable");
            Assert.AreEqual(1, pointQuads.Count, "the point path emits one quad for '5'");

            IReadOnlyList<CurvedGlyph> curved = CurvedTextLayout.Layout(run, atlas);
            Assert.AreEqual(1, curved.Count, "the curved path emits one cell for '5'");

            SymbolQuad pointQuad = pointQuads[0];
            SymbolQuad curvedCell = curved[0].Cell;

            Assert.AreEqual(pointQuad.TopLeft.y, curvedCell.TopLeft.y, 1e-3f,
                $"a curved cell's top must sit where a centre-anchored point quad's does: point " +
                $"{pointQuad.TopLeft.y}, curved {curvedCell.TopLeft.y} baked px");
            Assert.AreEqual(pointQuad.BottomRight.y, curvedCell.BottomRight.y, 1e-3f,
                $"…and likewise its bottom: point {pointQuad.BottomRight.y}, curved {curvedCell.BottomRight.y} baked px");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // CurvedTextLayoutTests — CurvedTextLayout maps a shaped run to per-glyph path-centred cells
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CurvedTextLayout maps a shaped single-line run to per-glyph (arc-center, path-centred cell),
    /// the build-time half of curved along-line text. Expectations are recomputed from the atlas entries,
    /// then pinned. The vertical centring itself is pinned by <c>CurvedTextCentringTests</c>; what moves
    /// here is only the positional restatement.
    /// </summary>
    [TestFixture]
    public class CurvedTextLayoutTests
    {
        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException($"{fileName} not found walking up from {Directory.GetCurrentDirectory()}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(params uint[] codepoints)
        {
            var list = new List<PositionedGlyph>(codepoints.Length);
            for (int i = 0; i < codepoints.Length; i++)
                list.Add(new PositionedGlyph { AtlasCodepoint = codepoints[i], XAdvance = 0f, Cluster = i });
            return new ShapedRun { Glyphs = list, Direction = TextDirection.LeftToRight };
        }

        [Test]
        public void TwoGlyphs_ArcCentersAreCumulativeAdvanceMidpoints_CellsCenteredHorizontally()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun((uint)'A', (uint)'a');

            IReadOnlyList<CurvedGlyph> glyphs = CurvedTextLayout.Layout(run, atlas);
            Assert.AreEqual(2, glyphs.Count, "both glyphs are visible");

            // ArcCenter = cumulative advance to each glyph's own advance-midpoint.
            float centerA = entryA.Advance * 0.5f;
            float centerLowerA = entryA.Advance + entryLowerA.Advance * 0.5f;
            Assert.AreEqual(centerA, glyphs[0].ArcCenter, 1e-4f);
            Assert.AreEqual(centerLowerA, glyphs[1].ArcCenter, 1e-4f);

            // Glyph 0's cell: same TOP-referenced box as TextQuadLayout, shifted -arcCenter in x and lifted
            // in y so the run's optical (cap-band) centre lands on the path. The shift is restated here as
            // the derivation it is — baseline offset less half a cap height — NOT read
            // off TextQuadLayout's own constant, which is the code under test.
            float opticalCentreShift = GlyphSdf.BaselineBelowReferencePx - 0.5f * GlyphSdf.NominalCapHeightEm * TextQuadLayout.OneEm;
            SymbolQuad cell0 = glyphs[0].Cell;
            Assert.AreEqual(entryA.Left - GlyphSdf.Buffer - centerA, cell0.TopLeft.x, 1e-4f, "cell x is centered on the arc-center");
            Assert.AreEqual(entryA.Top + GlyphSdf.Buffer + opticalCentreShift, cell0.TopLeft.y, 1e-4f, "cell y is centered on the path's optical centre");
            Assert.AreEqual(entryA.Left - GlyphSdf.Buffer + entryA.CellSize.x - centerA, cell0.BottomRight.x, 1e-4f);
            Assert.AreEqual(entryA.Top + GlyphSdf.Buffer + opticalCentreShift - entryA.CellSize.y, cell0.BottomRight.y, 1e-4f);

            // Glyph 1's cell y, by the SAME derivation — and 'a' is the discriminating half of this pair.
            // 'A' is a baseline-resting CAP glyph, for which centring the run on its optical centre and
            // centring each glyph on its own box happen to give the identical answer (they coincide exactly
            // when bare height == the nominal cap height), so cell0 alone cannot tell the correct per-LABEL
            // rule from the rejected per-GLYPH one. 'a' is an x-height glyph and separates them by 2.0 baked
            // px, which is 20 000x this tolerance.
            SymbolQuad cell1 = glyphs[1].Cell;
            Assert.AreEqual(entryLowerA.Top + GlyphSdf.Buffer + opticalCentreShift, cell1.TopLeft.y, 1e-4f,
                "every glyph in the run shifts by the same per-label constant — an x-height glyph included");
            Assert.AreEqual(entryLowerA.Top + GlyphSdf.Buffer + opticalCentreShift - entryLowerA.CellSize.y, cell1.BottomRight.y, 1e-4f);

            // UVs unchanged from the atlas entry.
            float2 atlasSize = atlas.Size;
            Assert.AreEqual(((float2)entryA.AtlasOrigin / atlasSize).x, cell0.UvTopLeft.x, 1e-6f);
            Assert.AreEqual((((float2)entryA.AtlasOrigin + (float2)entryA.CellSize) / atlasSize).x, cell0.UvBottomRight.x, 1e-6f);
        }

        [Test]
        public void Whitespace_EmitsNoGlyph_ButStillAdvancesTheArc()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun((uint)'A', (uint)' ', (uint)'a');

            IReadOnlyList<CurvedGlyph> glyphs = CurvedTextLayout.Layout(run, atlas);
            Assert.AreEqual(2, glyphs.Count, "the space emits no CurvedGlyph");

            // The second visible glyph's arc-center includes the (skipped) space's advance.
            float expected = entryA.Advance + entrySpace.Advance + entryLowerA.Advance * 0.5f;
            Assert.AreEqual(expected, glyphs[1].ArcCenter, 1e-4f, "arc still advances through whitespace");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FontStackFallbackTests — font-stack fallback
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Font-stack fallback. <see cref="FontStackResolver"/> resolves a codepoint
    /// against an ordered <see cref="FontStack"/>, trying each font's decoded range (via
    /// <see cref="GlyphCache"/>) in order; the first font that has the codepoint wins. A codepoint in
    /// no font of the stack yields the defined not-found outcome (notdef/skip, never throw).
    /// </summary>
    [TestFixture]
    public class FontStackFallbackTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs BuildHandGlyphs(string name, int rangeStart, int rangeEnd, uint codepoint, int advance)
        {
            var glyphs = new Dictionary<uint, SdfGlyph>
            {
                [codepoint] = new SdfGlyph
                {
                    Codepoint = codepoint,
                    Width = 1,
                    Height = 1,
                    Left = 0,
                    Top = 0,
                    Advance = advance,
                    Bitmap = null,
                },
            };
            return new FontStackGlyphs { Name = name, RangeStart = rangeStart, RangeEnd = rangeEnd, Glyphs = glyphs };
        }

        // =========================================================================================
        // Real-fixture fallback direction: a codepoint present ONLY in the second font's decoded
        // range (the first font has no decoded range at all covering this codepoint's bucket) must
        // resolve from the second font.
        // =========================================================================================
        [Test]
        public void Resolve_CodepointMissingFromFirstFont_ResolvesFromSecondFont()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            FontStackGlyphs arabic = GlyphPbfDecoder.Decode(LoadFixture("1536-1791.pbf.bytes")).Stacks[0];

            var cache = new GlyphCache();
            cache.Store("LatinFont", latin.RangeStart, latin);     // only covers range 0 (bucket for codepoint 1536 is 1536)
            cache.Store("ArabicFont", arabic.RangeStart, arabic);  // covers range 1536

            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };
            var resolver = new FontStackResolver(stack, cache);

            // U+0600 (Arabic Number Sign) — present in the Arabic fixture, absent from the Latin one,
            // and the Latin font's cache has no decoded range at all for this codepoint's bucket.
            GlyphResolution resolution = resolver.Resolve(0x0600u);

            Assert.IsTrue(resolution.Found, "codepoint present in the fallback font must resolve");
            Assert.AreEqual("ArabicFont", resolution.ResolvedFontName, "must resolve from the SECOND (fallback) font, not the first");
            Assert.AreEqual(0x0600u, resolution.Glyph.Codepoint);
        }

        // =========================================================================================
        // Real-fixture: a codepoint present in the FIRST font resolves from the first font. (The
        // second font's cache has no entry at all for this codepoint's range bucket, so a resolver
        // that incorrectly skipped straight to fallback would return not-found here, not "LatinFont".)
        // =========================================================================================
        [Test]
        public void Resolve_CodepointPresentInFirstFont_ResolvesFromFirstFont()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

            var cache = new GlyphCache();
            cache.Store("LatinFont", latin.RangeStart, latin);
            // Deliberately: no cache entry for "ArabicFont" at range 0 — fallback has nothing to offer here.

            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };
            var resolver = new FontStackResolver(stack, cache);

            GlyphResolution resolution = resolver.Resolve((uint)'A');

            Assert.IsTrue(resolution.Found);
            Assert.AreEqual("LatinFont", resolution.ResolvedFontName);
            Assert.AreEqual((uint)'A', resolution.Glyph.Codepoint);
        }

        // =========================================================================================
        // Priority discriminator (the case a disjoint-fixture test cannot exercise): the SAME
        // codepoint exists in BOTH fonts at the same range, with distinguishable metrics. Only stack
        // order — first-font-wins — can be correct; a resolver that iterated in reverse, or picked
        // "any" match, would return Font2's advance here.
        // =========================================================================================
        [Test]
        public void Resolve_CodepointPresentInBothFonts_FirstFontInStackWins()
        {
            FontStackGlyphs font1Range0 = BuildHandGlyphs("Font1", 0, 255, 65u, advance: 11);
            FontStackGlyphs font2Range0 = BuildHandGlyphs("Font2", 0, 255, 65u, advance: 21);

            var cache = new GlyphCache();
            cache.Store("Font1", 0, font1Range0);
            cache.Store("Font2", 0, font2Range0);

            var stack = new FontStack { Names = new[] { "Font1", "Font2" } };
            var resolver = new FontStackResolver(stack, cache);

            GlyphResolution resolution = resolver.Resolve(65u);

            Assert.IsTrue(resolution.Found);
            Assert.AreEqual("Font1", resolution.ResolvedFontName, "the FIRST font in the stack must win, not the last/any match");
            Assert.AreEqual(11, resolution.Glyph.Advance, "the resolved glyph must be Font1's (advance 11), not Font2's (advance 21)");
        }

        // =========================================================================================
        // Locked policy: a codepoint absent from every font in the stack is a defined not-found
        // outcome (notdef/skip) — never a throw.
        // =========================================================================================
        [Test]
        public void Resolve_CodepointInNoFont_ReturnsNotFound_NeverThrows()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            FontStackGlyphs arabic = GlyphPbfDecoder.Decode(LoadFixture("1536-1791.pbf.bytes")).Stacks[0];

            var cache = new GlyphCache();
            cache.Store("LatinFont", latin.RangeStart, latin);
            cache.Store("ArabicFont", arabic.RangeStart, arabic);

            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };
            var resolver = new FontStackResolver(stack, cache);

            // codepoint 5000 -> bucket 4864, a range neither font's cache has anything for.
            GlyphResolution resolution = default;
            Assert.DoesNotThrow(() => resolution = resolver.Resolve(5000u));

            Assert.IsFalse(resolution.Found);
            Assert.IsNull(resolution.ResolvedFontName);
        }

        // =========================================================================================
        // Range math: rangeStart = (codepoint / 256) * 256 — the glyph-PBF 256-codepoint bucketing.
        // =========================================================================================
        [Test]
        public void ComputeRangeStart_Buckets256AlignedBase()
        {
            Assert.AreEqual(0, FontStackResolver.ComputeRangeStart(65u), "'A' (65) is in the 0-255 range");
            Assert.AreEqual(256, FontStackResolver.ComputeRangeStart(300u), "300 is in the 256-511 range");
            Assert.AreEqual(1536, FontStackResolver.ComputeRangeStart(1541u), "1541 is in the 1536-1791 range");
        }

        // =========================================================================================
        // IGlyphMetricsProvider delegation: a resolver plugs directly into ShapingRequest.Metrics,
        // with the same fallback-aware resolution (0 / false for a not-found codepoint).
        // =========================================================================================
        [Test]
        public void TryResolveGlyph_DelegatesToFallbackAwareResolve()
        {
            FontStackGlyphs font1Range0 = BuildHandGlyphs("Font1", 0, 255, 65u, advance: 11);
            var cache = new GlyphCache();
            cache.Store("Font1", 0, font1Range0);

            var stack = new FontStack { Names = new[] { "Font1" } };
            IGlyphMetricsProvider metrics = new FontStackResolver(stack, cache);

            Assert.IsTrue(metrics.TryResolveGlyph(65u, out float advance, out _));
            Assert.AreEqual(11f, advance);

            Assert.IsFalse(metrics.TryResolveGlyph(66u, out float missingAdvance, out _));
            Assert.AreEqual(0f, missingAdvance);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphAtlasFixedSizeTests — the fixed-size glyph atlas
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The FIXED-size glyph atlas (a big pre-allocated atlas whose <c>Size</c> never
    /// changes as glyphs append). This is what keeps per-tile incremental layout correct: a constant
    /// <c>Size</c> means an early tile's baked UVs (<c>origin / Size</c>) stay valid when a later tile adds
    /// glyphs. Overflow degrades gracefully (no throw, counted).
    /// </summary>
    [TestFixture]
    public class GlyphAtlasFixedSizeTests
    {
        private static SdfGlyph Glyph(uint codepoint, int w = 10, int h = 10)
            => new SdfGlyph
            {
                Codepoint = codepoint,
                Width = w,
                Height = h,
                Left = 0,
                Top = h,
                Advance = w + 2,
                Bitmap = new byte[(w + 6) * (h + 6)], // CellSize = (w+2*buffer, h+2*buffer), buffer = 3
            };

        [Test]
        public void FixedAtlas_SizeIsConstant_AcrossAppends()
        {
            var atlas = new GlyphAtlas(width: 256, fixedHeight: 256);

            var before = atlas.Size;
            Assert.AreEqual(new int2(256, 256), before, "a fixed atlas reports its full size from the start");
            Assert.AreEqual(256 * 256, atlas.Pixels.Length, "the pixel buffer is pre-allocated to the full fixed size");

            for (uint c = 65; c < 90; c++) atlas.Append(Glyph(c), 0);

            Assert.AreEqual(before, atlas.Size,
                "Size MUST NOT change as glyphs append — the whole point of the fixed atlas (UV stability)");
            Assert.AreEqual(0, atlas.OverflowCount, "25 small glyphs fit a 256x256 atlas with no overflow");
        }

        // =========================================================================================
        // A glyph that no longer fits the CURRENT page now opens a NEW page instead of being
        // dropped — this is the multi-page capacity behaviour M-T2 pins end-to-end (layout/vertex/render);
        // this test is the atlas-level slice of it.
        // =========================================================================================
        [Test]
        public void FixedAtlas_PageOverflow_OpensNewPage_InsteadOfDropping()
        {
            // 16 wide (1 cell per shelf) x 32 tall: a 16-tall cell fits at y=0 and y=16; the third would
            // start at y=32 on page 0 — page 1 opens for it instead of dropping it.
            var atlas = new GlyphAtlas(width: 16, fixedHeight: 32);

            GlyphAtlasEntry first = atlas.Append(Glyph(65, w: 10, h: 10), 0); // cell 16x16 → page 0, y=0
            GlyphAtlasEntry second = atlas.Append(Glyph(66, w: 10, h: 10), 0); // cell 16x16 → page 0, y=16
            Assert.AreEqual(0, atlas.OverflowCount, "two 16px cells fit a 32px-tall page");
            Assert.AreEqual(1, atlas.PageCount, "no new page needed yet");
            Assert.AreEqual(0, first.Page);
            Assert.AreEqual(0, second.Page);

            GlyphAtlasEntry third = atlas.Append(Glyph(67, w: 10, h: 10), 0); // would start at y=32 on page 0
            Assert.AreEqual(0, atlas.OverflowCount,
                "No longer a drop — the glyph lands on a fresh page instead");
            Assert.AreEqual(2, atlas.PageCount, "the third glyph forced a second page open");
            Assert.AreEqual(1, third.Page, "the third glyph packed onto page 1");
            Assert.AreEqual(new int2(0, 0), third.AtlasOrigin, "page 1 is a fresh packer — starts at its own origin");
            Assert.IsTrue(atlas.TryGetEntry(0, 67, out GlyphAtlasEntry roundTrip), "the page-1 glyph round-trips through TryGetEntry");
            Assert.AreEqual(1, roundTrip.Page);
            Assert.AreEqual(new int2(16, 32), atlas.Size, "Size (per-page) is unchanged by paging");
        }

        // =========================================================================================
        // Genuine overflow: a cell that doesn't fit even a BRAND-NEW empty page
        // (taller than the fixed page height) is still dropped and counted — paging only helps a cell that
        // fits A page, just not the current one.
        // =========================================================================================
        [Test]
        public void FixedAtlas_CellTallerThanPage_StillOverflows_EvenOnAFreshPage()
        {
            var atlas = new GlyphAtlas(width: 16, fixedHeight: 32);

            GlyphAtlasEntry entry = atlas.Append(Glyph(1, w: 10, h: 40), 0); // cell 16x46 — taller than any page

            Assert.AreEqual(1, atlas.OverflowCount, "a cell taller than the fixed page height never fits, even fresh");
            Assert.AreEqual(1, atlas.PageCount, "no page was opened for a cell that can't fit any page");
            Assert.IsFalse(atlas.TryGetEntry(0, 1, out _), "a genuinely overflowed glyph gets NO entry");
            Assert.AreEqual(default(GlyphAtlasEntry).Page, entry.Page, "default(GlyphAtlasEntry) returned on overflow");
        }

        [Test]
        public void FixedAtlas_EarlyGlyphOrigin_UnaffectedByLaterAppends()
        {
            var atlas = new GlyphAtlas(width: 256, fixedHeight: 512);

            GlyphAtlasEntry first = atlas.Append(Glyph(65), 0);
            int2 originAtFirst = first.AtlasOrigin;
            int2 sizeAtFirst = atlas.Size;

            for (uint c = 66; c < 120; c++) atlas.Append(Glyph(c), 0);

            Assert.IsTrue(atlas.TryGetEntry(0, 65, out GlyphAtlasEntry stillFirst));
            Assert.AreEqual(originAtFirst, stillFirst.AtlasOrigin, "an early glyph's atlas origin never moves");
            Assert.AreEqual(sizeAtFirst, atlas.Size,
                "and the UV denominator (Size) is unchanged → its baked UVs stay valid");
        }

        // =========================================================================================
        // Robustness: a cell WIDER than the fixed page must be surfaced as
        // OverflowCount overflow, NOT throw. GlyphAtlasPacker.TryPack throws for an over-wide cell, so
        // without the width preflight in TryPackFixed a single over-wide glyph aborts the whole build.
        // =========================================================================================
        [Test]
        public void FixedAtlas_CellWiderThanPage_CountsAsOverflow_DoesNotThrow()
        {
            var atlas = new GlyphAtlas(width: 32, fixedHeight: 64);

            // Cell width = 40 + 2*3 = 46 > 32 page width. Pre-fix this threw ArgumentOutOfRangeException.
            GlyphAtlasEntry entry = default;
            Assert.DoesNotThrow(() => entry = atlas.Append(Glyph(1, w: 40, h: 10), 0),
                "an over-wide cell must be counted as overflow, never throw and abort the build");

            Assert.AreEqual(1, atlas.OverflowCount, "the over-wide glyph is counted as overflow");
            Assert.AreEqual(1, atlas.PageCount, "no page was opened for a cell that can't fit any page");
            Assert.IsFalse(atlas.TryGetEntry(0, 1, out _), "an over-wide (overflowed) glyph gets NO entry");
            Assert.AreEqual(default(GlyphAtlasEntry).Page, entry.Page, "default(GlyphAtlasEntry) returned on overflow");

            // A subsequently appended glyph that DOES fit still packs normally — the atlas is not broken.
            GlyphAtlasEntry ok = atlas.Append(Glyph(2, w: 10, h: 10), 0);
            Assert.IsTrue(atlas.TryGetEntry(0, 2, out _), "a fitting glyph still packs after an over-wide overflow");
            Assert.AreEqual(0, ok.Page);
        }

        // =========================================================================================
        // Robustness: page growth is capped at GlyphAtlas.MaxPages. Once the cap
        // is reached, a glyph that would need a NEW page is surfaced as OverflowCount overflow instead of
        // allocating unboundedly (OOM / Texture2DArray layer limit). The existing pages keep their content.
        // =========================================================================================
        [Test]
        public void FixedAtlas_HittingMaxPages_SurfacesOverflow_InsteadOfGrowingUnbounded()
        {
            // A page sized to exactly ONE 16px cell (16 wide, 16 tall) → every appended glyph opens a fresh
            // page, so page count == glyph count until the cap bites.
            var atlas = new GlyphAtlas(width: 16, fixedHeight: 16);

            // Fill exactly MaxPages pages (one glyph each). None overflow.
            for (uint c = 0; c < GlyphAtlas.MaxPages; c++)
            {
                GlyphAtlasEntry e = atlas.Append(Glyph(c, w: 10, h: 10), 0);
                Assert.AreEqual((int)c, e.Page, $"glyph {c} lands on its own fresh page");
            }
            Assert.AreEqual(GlyphAtlas.MaxPages, atlas.PageCount, "exactly MaxPages pages allocated");
            Assert.AreEqual(0, atlas.OverflowCount, "nothing overflowed while filling up to the cap");

            // The next glyph would need page MaxPages (index == MaxPages) — capped → overflow, no new page.
            GlyphAtlasEntry over = atlas.Append(Glyph(999, w: 10, h: 10), 0);
            Assert.AreEqual(1, atlas.OverflowCount, "the glyph past the page cap is counted as overflow");
            Assert.AreEqual(GlyphAtlas.MaxPages, atlas.PageCount, "the page count is CAPPED — no unbounded growth");
            Assert.IsFalse(atlas.TryGetEntry(0, 999, out _), "the capped-out glyph gets no entry");
            Assert.AreEqual(default(GlyphAtlasEntry).Page, over.Page);

            // An earlier page's glyph is untouched by the overflow (the cap doesn't corrupt existing pages).
            Assert.IsTrue(atlas.TryGetEntry(0, 0, out GlyphAtlasEntry firstStill));
            Assert.AreEqual(0, firstStill.Page);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphAtlasTests — atlas packing + CPU blit against the committed glyph-PBF fixture
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atlas packing + CPU blit, against the same committed glyph-PBF fixture the
    /// decode tests use (<c>Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes</c>).
    /// </summary>
    [TestFixture]
    public class GlyphAtlasTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs LoadLatinStack()
            => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        // =========================================================================================
        // (b) + (c) + (d): a single bitmap-bearing glyph packs a correctly-sized cell, blits its bytes
        //     row-major at the packed origin, and round-trips through TryGetEntry.
        // =========================================================================================
        /// <summary>
        /// <b>The atlas must key on the FONT as well as the codepoint.</b> One shared
        /// <see cref="GlyphAtlas"/> serves every layer (<c>SymbolSubsystem</c> builds exactly one), and a
        /// Liberty-style document mixes faces — <c>label_city</c> asks for Noto Sans Regular while
        /// <c>water_name_*</c> and <c>poi_r1</c> ask for Noto Sans Italic. Keyed on the codepoint alone,
        /// whichever face decodes first claims 'B' forever and <c>GlyphManager.AppendToAtlas</c>'s
        /// "already present" skip silently discards every other face's 'B' — so the whole map renders in one
        /// arbitrary face. That is a wrong-glyph bug, not a styling nicety: it rendered Berlin in italic.
        /// </summary>
        [Test]
        public void Append_SameCodepointFromTwoFonts_KeepsThemApart()
        {
            SdfGlyph regular = LoadLatinStack().Glyphs[66u]; // 'B'
            // A second face's 'B': same codepoint, deliberately different metrics so a collision is visible
            // as a WRONG entry rather than merely a missing one.
            SdfGlyph italic = new SdfGlyph
            {
                Codepoint = 66u,
                Width     = regular.Width,
                Height    = regular.Height,
                Left      = regular.Left + 3,
                Top       = regular.Top + 5,
                Advance   = regular.Advance + 7,
                Bitmap    = regular.Bitmap,
            };

            var atlas = new GlyphAtlas();
            int regularFont = atlas.FontId("Noto Sans Regular");
            int italicFont  = atlas.FontId("Noto Sans Italic");

            atlas.Append(regular, regularFont);
            atlas.Append(italic, italicFont);

            Assert.IsTrue(atlas.TryGetEntry(regularFont, 66u, out GlyphAtlasEntry fromRegular),
                "the regular face's 'B' must be retrievable under its OWN font");
            Assert.IsTrue(atlas.TryGetEntry(italicFont, 66u, out GlyphAtlasEntry fromItalic),
                "the italic face's 'B' must not be swallowed by the regular one already occupying codepoint 66");

            Assert.AreEqual(regular.Advance, fromRegular.Advance, "regular 'B' kept its own advance");
            Assert.AreEqual(italic.Advance, fromItalic.Advance,
                "italic 'B' read back the REGULAR face's advance — the two faces share one atlas slot");
            Assert.AreNotEqual(fromRegular.AtlasOrigin, fromItalic.AtlasOrigin,
                "two faces' glyphs must occupy two cells; one origin for both means one bitmap for both");
        }

        [Test]
        public void Append_BitmapGlyph_PacksCellBlitsBytesAndRoundTripsLookup()
        {
            SdfGlyph a = LoadLatinStack().Glyphs[65u]; // 'A'
            var atlas = new GlyphAtlas();

            GlyphAtlasEntry entry = atlas.Append(a, 0);

            Assert.AreEqual(65u, entry.Codepoint);
            Assert.AreEqual(new int2(a.Width + 6, a.Height + 6), entry.CellSize,
                "CellSize == (Width + 2*buffer, Height + 2*buffer)");
            Assert.AreEqual(a.Left, entry.Left);
            Assert.AreEqual(a.Top, entry.Top);
            Assert.AreEqual(a.Advance, entry.Advance);

            int2 origin = entry.AtlasOrigin;
            int2 cell = entry.CellSize;
            int2 atlasSize = atlas.Size;
            byte[] pixels = atlas.Pixels;
            Assert.AreEqual(atlasSize.x * atlasSize.y, pixels.Length, "Pixels is exactly Size.x * Size.y bytes");

            for (int row = 0; row < cell.y; row++)
            {
                for (int col = 0; col < cell.x; col++)
                {
                    byte expected = a.Bitmap[row * cell.x + col];
                    byte actual = pixels[(origin.y + row) * atlasSize.x + origin.x + col];
                    Assert.AreEqual(expected, actual, $"pixel mismatch at cell-local ({col},{row})");
                }
            }

            Assert.IsTrue(atlas.TryGetEntry(0, 65u, out GlyphAtlasEntry roundTrip), "'A' must round-trip through TryGetEntry");
            Assert.AreEqual(entry.Codepoint, roundTrip.Codepoint);
            Assert.AreEqual(entry.AtlasOrigin, roundTrip.AtlasOrigin);
            Assert.AreEqual(entry.CellSize, roundTrip.CellSize);

            Assert.IsFalse(atlas.TryGetEntry(0, 0xFFFFu, out _), "an unappended codepoint must not be found");
        }

        // =========================================================================================
        // (e): a no-bitmap glyph (space, codepoint 32) still gets an entry, but blits nothing -- its
        //      packed cell region in Pixels stays all-zero.
        // =========================================================================================
        [Test]
        public void Append_NoBitmapGlyph_GetsEntryButBlitsNothing()
        {
            SdfGlyph space = LoadLatinStack().Glyphs[32u];
            Assert.IsFalse(space.HasBitmap, "fixture precondition: space carries no bitmap");
            var atlas = new GlyphAtlas();

            GlyphAtlasEntry entry = atlas.Append(space, 0);

            Assert.AreEqual(32u, entry.Codepoint);
            Assert.AreEqual(new int2(space.Width + 6, space.Height + 6), entry.CellSize);
            Assert.IsTrue(atlas.TryGetEntry(0, 32u, out _), "a no-bitmap glyph still gets an entry");

            int2 origin = entry.AtlasOrigin;
            int2 cell = entry.CellSize;
            int2 atlasSize = atlas.Size;
            byte[] pixels = atlas.Pixels;
            for (int row = 0; row < cell.y; row++)
            {
                for (int col = 0; col < cell.x; col++)
                {
                    Assert.AreEqual(0, pixels[(origin.y + row) * atlasSize.x + origin.x + col],
                        "a no-bitmap glyph's cell must stay unblitted (zero)");
                }
            }
        }

        // =========================================================================================
        // (a) + whole-range invariant: appending every glyph in the 0-255 range packs each into a
        //     non-overlapping cell fully inside Size, and every cell's blitted bytes (when it has a
        //     bitmap) match its source glyph's bitmap.
        // =========================================================================================
        [Test]
        public void Append_WholeRange_PacksNonOverlappingCellsWithinSizeAndBlitsCorrectly()
        {
            FontStackGlyphs stack = LoadLatinStack();
            var atlas = new GlyphAtlas();
            var entries = new List<(GlyphAtlasEntry entry, SdfGlyph source)>();

            foreach (var kv in stack.Glyphs)
            {
                entries.Add((atlas.Append(kv.Value, 0), kv.Value));
            }

            int2 atlasSize = atlas.Size;
            byte[] pixels = atlas.Pixels;
            Assert.AreEqual(stack.Glyphs.Count, entries.Count);
            Assert.AreEqual(atlasSize.x * atlasSize.y, pixels.Length);

            foreach (var (entry, source) in entries)
            {
                Assert.GreaterOrEqual(entry.AtlasOrigin.x, 0);
                Assert.GreaterOrEqual(entry.AtlasOrigin.y, 0);
                Assert.LessOrEqual(entry.AtlasOrigin.x + entry.CellSize.x, atlasSize.x,
                    $"codepoint {entry.Codepoint} overflows atlas width");
                Assert.LessOrEqual(entry.AtlasOrigin.y + entry.CellSize.y, atlasSize.y,
                    $"codepoint {entry.Codepoint} overflows atlas height");

                if (source.HasBitmap)
                {
                    for (int row = 0; row < entry.CellSize.y; row++)
                    {
                        for (int col = 0; col < entry.CellSize.x; col++)
                        {
                            byte expected = source.Bitmap[row * entry.CellSize.x + col];
                            byte actual = pixels[(entry.AtlasOrigin.y + row) * atlasSize.x + entry.AtlasOrigin.x + col];
                            Assert.AreEqual(expected, actual,
                                $"codepoint {entry.Codepoint} cell-local ({col},{row}) byte mismatch");
                        }
                    }
                }
            }

            // No two cells overlap (pairwise AABB-overlap check across the whole appended set).
            for (int i = 0; i < entries.Count; i++)
            {
                for (int j = i + 1; j < entries.Count; j++)
                {
                    Assert.IsFalse(RectanglesOverlap(entries[i].entry, entries[j].entry),
                        $"codepoints {entries[i].entry.Codepoint} and {entries[j].entry.Codepoint} overlap");
                }
            }
        }

        private static bool RectanglesOverlap(GlyphAtlasEntry a, GlyphAtlasEntry b)
        {
            bool separateX = a.AtlasOrigin.x + a.CellSize.x <= b.AtlasOrigin.x || b.AtlasOrigin.x + b.CellSize.x <= a.AtlasOrigin.x;
            bool separateY = a.AtlasOrigin.y + a.CellSize.y <= b.AtlasOrigin.y || b.AtlasOrigin.y + b.CellSize.y <= a.AtlasOrigin.y;
            return !(separateX || separateY);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphManagerTests — GlyphManager's fetch/decode/cache/atlas wiring over a fake glyph source
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="GlyphManager"/>'s fetch/decode/cache/atlas
    /// wiring, exercised against a fake in-memory <see cref="TestGlyphSource"/> serving the committed
    /// <c>Assets/Fixtures/glyphs/NotoSansRegular/*.bytes</c> fixtures (never the network).
    /// </summary>
    [TestFixture]
    public class GlyphManagerTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        // =========================================================================================
        // Single-range fetch/decode/cache/atlas wiring: EnsureFontRangeAsync populates BOTH the
        // GlyphCache (decoded range, keyed by fontName+rangeStart) AND the shared GlyphAtlas (per-glyph
        // entries), from the real decoded fixture bytes -- and does not re-fetch on a repeat call.
        // =========================================================================================
        [Test]
        public async Task EnsureFontRangeAsync_FetchesDecodesCachesAndAppendsGlyphs()
        {
            byte[] latinBytes = LoadFixture("0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [("Noto Sans Regular", 0)] = latinBytes };
            var source = TestGlyphSource.FromRanges(ranges);
            var manager = new GlyphManager(source);

            await manager.EnsureFontRangeAsync("Noto Sans Regular", 0);

            Assert.IsTrue(manager.Cache.TryGet("Noto Sans Regular", 0, out FontStackGlyphs glyphs),
                "the decoded range must be stored in the cache under (fontName, rangeStart)");
            Assert.IsTrue(glyphs.Glyphs.TryGetValue(65u, out SdfGlyph expectedA), "fixture precondition: 'A' must decode");

            Assert.IsTrue(manager.Atlas.TryGetEntry(0, 65u, out GlyphAtlasEntry entry),
                "the decoded glyph must be appended to the shared atlas");
            Assert.AreEqual(expectedA.Advance, entry.Advance);
            Assert.AreEqual(expectedA.Left, entry.Left);
            Assert.AreEqual(expectedA.Top, entry.Top);

            Assert.AreEqual(1, source.FetchCount);

            // Repeat call: already cached -> must NOT re-fetch (keep-all-per-session).
            await manager.EnsureFontRangeAsync("Noto Sans Regular", 0);
            Assert.AreEqual(1, source.FetchCount, "an already-cached (fontName, rangeStart) must not be refetched");
        }

        // =========================================================================================
        // Policy via GlyphManager: a range the source reports absent is cached as an empty range
        // (never throws) so it is not refetched on every subsequent request.
        // =========================================================================================
        [Test]
        public async Task EnsureFontRangeAsync_AbsentRange_CachesEmptyAndDoesNotRefetch()
        {
            var source = TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()); // nothing served
            var manager = new GlyphManager(source);

            await manager.EnsureFontRangeAsync("Missing Font", 0);

            Assert.IsTrue(manager.Cache.TryGet("Missing Font", 0, out FontStackGlyphs glyphs),
                "an absent range must still be cached (as empty), not left unrecorded");
            Assert.AreEqual(0, glyphs.Glyphs.Count);
            Assert.AreEqual(1, source.FetchCount);

            await manager.EnsureFontRangeAsync("Missing Font", 0);
            Assert.AreEqual(1, source.FetchCount, "a cached-absent range must not be refetched");
        }

        // =========================================================================================
        // Font-stack fetch model: EnsureFontStackRangeAsync fetches PER
        // INDIVIDUAL FONT NAME. A two-font stack where only the second font's fixture covers the
        // requested codepoint's range still resolves correctly via FontStackResolver afterward (T5,
        // exercised through the GlyphManager wiring rather than a hand-built GlyphCache).
        // =========================================================================================
        [Test]
        public async Task EnsureFontStackRangeAsync_TwoFontStack_FetchesEachNameAndResolvesWithFallback()
        {
            byte[] latinBytes = LoadFixture("0-255.pbf.bytes");
            byte[] arabicBytes = LoadFixture("1536-1791.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]>
            {
                [("LatinFont", 0)] = latinBytes,
                [("ArabicFont", 1536)] = arabicBytes,
            };
            var source = TestGlyphSource.FromRanges(ranges);
            var manager = new GlyphManager(source);
            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };

            // 'A' (65) -> range 0: LatinFont has it, ArabicFont's range-0 fetch reports absent.
            await manager.EnsureFontStackRangeAsync(stack, (uint)'A');
            // U+0600 (Arabic Number Sign) -> range 1536: ArabicFont has it, LatinFont's range-1536 fetch reports absent.
            await manager.EnsureFontStackRangeAsync(stack, 0x0600u);

            Assert.AreEqual(4, source.FetchCount, "two codepoints x two stack font names = 4 individual fetches");

            FontStackResolver resolver = manager.CreateResolver(stack);

            GlyphResolution latinResolution = resolver.Resolve((uint)'A');
            Assert.IsTrue(latinResolution.Found);
            Assert.AreEqual("LatinFont", latinResolution.ResolvedFontName);

            GlyphResolution arabicResolution = resolver.Resolve(0x0600u);
            Assert.IsTrue(arabicResolution.Found, "the fallback font's glyph must resolve");
            Assert.AreEqual("ArabicFont", arabicResolution.ResolvedFontName);

            // Under each font's OWN id — the atlas is shared by every layer, so a glyph filed under the
            // wrong face is a glyph the layer that asked for it will never see.
            int latinFont  = manager.Atlas.FontId("LatinFont");
            int arabicFont = manager.Atlas.FontId("ArabicFont");
            Assert.AreEqual(latinFont, manager.Atlas.FontId(latinResolution.ResolvedFontName),
                "the resolver and the atlas must agree on LatinFont's id");
            Assert.AreEqual(arabicFont, manager.Atlas.FontId(arabicResolution.ResolvedFontName),
                "the resolver and the atlas must agree on ArabicFont's id");
            Assert.AreNotEqual(latinFont, arabicFont, "two font names must intern to two ids");

            Assert.IsTrue(manager.Atlas.TryGetEntry(latinFont, (uint)'A', out _), "LatinFont's glyph must be in the shared atlas");
            Assert.IsTrue(manager.Atlas.TryGetEntry(arabicFont, 0x0600u, out _), "ArabicFont's fallback glyph must be in the shared atlas");
            Assert.IsFalse(manager.Atlas.TryGetEntry(arabicFont, (uint)'A', out _),
                "'A' was decoded for LatinFont only — finding it under ArabicFont means the atlas ignores the font half of its key");

            // Repeat: every (name, rangeStart) pair involved is now cached (including the two absent
            // misses) -> zero additional fetches.
            await manager.EnsureFontStackRangeAsync(stack, (uint)'A');
            await manager.EnsureFontStackRangeAsync(stack, 0x0600u);
            Assert.AreEqual(4, source.FetchCount, "every (fontName, rangeStart) pair -- hit or cached-absent -- must not be refetched");
        }

        // =========================================================================================
        // A codepoint absent from EVERY font's fetched range resolves to the defined not-found
        // outcome, never a throw, once GlyphManager has ensured the relevant ranges.
        // =========================================================================================
        [Test]
        public async Task EnsureFontStackRangeAsync_CodepointInNoFont_ResolverReturnsNotFound()
        {
            byte[] latinBytes = LoadFixture("0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [("LatinFont", 0)] = latinBytes };
            var source = TestGlyphSource.FromRanges(ranges);
            var manager = new GlyphManager(source);
            var stack = new FontStack { Names = new[] { "LatinFont" } };

            await manager.EnsureFontStackRangeAsync(stack, (uint)'A');

            FontStackResolver resolver = manager.CreateResolver(stack);
            GlyphResolution resolution = default;
            Assert.DoesNotThrow(() => resolution = resolver.Resolve(0x1F600u)); // an emoji codepoint, in no ensured range
            Assert.IsFalse(resolution.Found);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphPbfDecodeTests — glyph-PBF decode against a real fixture
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Glyph-PBF decode against a real fixture (demotiles "Noto Sans Regular" 0-255
    /// range, committed as <c>Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes</c>).
    ///
    /// Golden values below were pinned by decoding the committed fixture with an independent (Python,
    /// non-Unity) generic protobuf walker — not by round-tripping through the decoder under test.
    /// </summary>
    [TestFixture]
    public class GlyphPbfDecodeTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static GlyphPbfRange DecodeLatinRange() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes"));

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // =========================================================================================
        // 0. Fontstack / range envelope decodes.
        // =========================================================================================
        [Test]
        public void Decode_ProducesOneFontStack_WithParsedRange()
        {
            GlyphPbfRange result = DecodeLatinRange();

            Assert.AreEqual(1, result.Stacks.Count, "the 0-255.pbf fixture carries one fontstack");
            FontStackGlyphs stack = result.Stacks[0];
            StringAssert.StartsWith("Noto Sans Regular", stack.Name);
            Assert.AreEqual(0, stack.RangeStart, "range string '0-255' parses to start=0");
            Assert.AreEqual(255, stack.RangeEnd, "range string '0-255' parses to end=255");
            Assert.AreEqual(223, stack.Glyphs.Count, "223 glyph records present in the 0-255 range fixture");
        }

        // =========================================================================================
        // 1. Exact metrics for known codepoints ('A', 'a') — both carry a bitmap, non-negative Left.
        // =========================================================================================
        [Test]
        public void Decode_ExactMetrics_UppercaseA()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(65u, out SdfGlyph a), "codepoint 65 ('A') present");

            Assert.AreEqual(65u, a.Codepoint);
            Assert.AreEqual(15, a.Width);
            Assert.AreEqual(17, a.Height);
            Assert.AreEqual(0, a.Left);
            Assert.AreEqual(-9, a.Top);
            Assert.AreEqual(15, a.Advance);
            Assert.IsTrue(a.HasBitmap);
        }

        [Test]
        public void Decode_ExactMetrics_LowercaseA()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(97u, out SdfGlyph a), "codepoint 97 ('a') present");

            Assert.AreEqual(97u, a.Codepoint);
            Assert.AreEqual(11, a.Width);
            Assert.AreEqual(13, a.Height);
            Assert.AreEqual(1, a.Left);
            Assert.AreEqual(-13, a.Top);
            Assert.AreEqual(13, a.Advance);
            Assert.IsTrue(a.HasBitmap);
        }

        // =========================================================================================
        // 1b. SDF interior peak — DIAGNOSTIC for the "0.75 edge renders nearly transparent" finding.
        // The fill shader treats distSample > _SdfEdge as inside; the fontnik convention puts the glyph
        // OUTLINE at 191/255 ≈ 0.75 and the interior should climb well above it (toward 255) for any stroke
        // more than a couple px thick. If a standard glyph's interior barely clears 0.75, either the glyph
        // source encodes a shallow field OR our decode compresses the range. This measures the committed
        // NotoSans fixture (a known-standard fontnik bake) to tell those apart.
        //
        // MEASURED, both sources, 0-255 range: byte-for-byte the same encoding — global max 255, MEDIAN
        // per-glyph interior peak 224 (0.878), p10 219 (0.859). So openfreemap serves the same fontnik bake
        // as the fixture and the "other source encodes a shallower field" arm is NOT what was happening.
        // A lower _SdfEdge is therefore NOT per-source calibration here: at the 0.75 iso a median stroke
        // still clears the threshold by ~1 atlas texel, and every 0.01 below it dilates each edge by
        // 0.08 texels (_SdfRangeTexels 8). The material ran at 0.60 for a long time, which is ~1.2 texels of
        // dilation PER EDGE — visibly bold text, mistaken for a font-weight problem.
        // =========================================================================================
        [Test]
        public void SdfInteriorPeak_StandardFontnikGlyphsClimbWellAbove075()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];

            int globalMax = 0;
            uint globalMaxCp = 0;
            foreach (System.Collections.Generic.KeyValuePair<uint, SdfGlyph> kv in stack.Glyphs)
            {
                byte[] bmp = kv.Value.Bitmap;
                if (bmp == null) continue;
                for (int i = 0; i < bmp.Length; i++)
                    if (bmp[i] > globalMax) { globalMax = bmp[i]; globalMaxCp = kv.Value.Codepoint; }
            }

            // Per-glyph peak + how much of the glyph is "solid" at the 0.75 iso, for thick-stroke letters.
            string report = "";
            foreach (uint cp in new uint[] { 66u /*B*/, 77u /*M*/, 111u /*o*/, 46u /*.*/ })
            {
                if (!stack.Glyphs.TryGetValue(cp, out SdfGlyph g) || g.Bitmap == null) continue;
                int max = 0, above075 = 0, nonZero = 0;
                foreach (byte b in g.Bitmap)
                {
                    if (b > max) max = b;
                    if (b >= 191) above075++;
                    if (b > 0) nonZero++;
                }
                report += $"  '{(char)cp}' cp{cp}: max={max} ({max / 255f:F2}), " +
                          $"{above075}/{nonZero} inked px ≥0.75 ({(nonZero > 0 ? 100f * above075 / nonZero : 0):F0}%)\n";
            }

            TestContext.WriteLine($"[SdfPeak] globalMax={globalMax} ({globalMax / 255f:F3}) at cp{globalMaxCp}\n{report}");

            // A standard fontnik SDF must reach near-255 somewhere (thick stroke centers). This is the
            // decisive tooth: if the committed fixture ITSELF peaks low, the pipeline compresses the field.
            Assert.GreaterOrEqual(globalMax, 240,
                $"standard fontnik SDF interiors should approach 255 (1.0); global max byte = {globalMax} " +
                $"(cp {globalMaxCp} = {globalMax / 255f:F3}). If this is low, our decode/atlas compresses the " +
                $"field. Per-glyph peaks:\n{report}");
        }

        // =========================================================================================
        // 2. Space (codepoint 32) has no bitmap in the PBF — decoder must tolerate the absent
        //    `bitmap` field (whitespace/zero-advance glyphs) rather than throwing or fabricating one.
        // =========================================================================================
        [Test]
        public void Decode_Space_HasNoBitmap()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(32u, out SdfGlyph space), "codepoint 32 (space) present");

            Assert.AreEqual(0, space.Width);
            Assert.AreEqual(0, space.Height);
            Assert.AreEqual(-26, space.Top);
            Assert.AreEqual(6, space.Advance);
            Assert.IsNull(space.Bitmap, "space carries no bitmap field in the PBF");
            Assert.IsFalse(space.HasBitmap);
        }

        // =========================================================================================
        // 3. Zigzag-vs-plain-varint discriminator: 'J' (codepoint 74) has a NEGATIVE Left (-2).
        //    The wire byte for this field is the zigzag encoding of -2 (raw varint value 3); reading
        //    it as a plain (non-zigzag) varint would yield 3, not -2 — a different, wrong value.
        //    'A'/'a' above (Left = 0 / 1) do not discriminate this bug since small non-negative zigzag
        //    values coincide with small positive raw varints.
        // =========================================================================================
        [Test]
        public void Decode_NegativeLeftAndTop_RequireZigzagDecode()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            Assert.IsTrue(stack.Glyphs.TryGetValue(74u, out SdfGlyph j), "codepoint 74 ('J') present");

            Assert.AreEqual(6, j.Width);
            Assert.AreEqual(22, j.Height);
            Assert.AreEqual(-2, j.Left, "zigzag-decoded Left must be -2; plain-varint decode would yield 3");
            Assert.AreEqual(-9, j.Top);
            Assert.AreEqual(6, j.Advance);
            Assert.IsTrue(j.HasBitmap);
        }

        // =========================================================================================
        // 4. Structural guard: decoded bitmap length == (Width + 2*Buffer) * (Height + 2*Buffer).
        //    A "width = bitmap.Length / height" shortcut that ignores the buffer fails this.
        // =========================================================================================
        [Test]
        public void Decode_BitmapLength_MatchesBufferedCellDimensions()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];

            foreach (uint codepoint in new uint[] { 65u, 97u, 74u })
            {
                SdfGlyph g = stack.Glyphs[codepoint];
                int expected = (g.Width + 2 * GlyphSdf.Buffer) * (g.Height + 2 * GlyphSdf.Buffer);
                Assert.AreEqual(expected, g.Bitmap.Length,
                    $"codepoint {codepoint}: bitmap length must equal (width+2*buffer)*(height+2*buffer)");
                Assert.AreEqual(new int2(g.Width + 6, g.Height + 6), g.CellSize);
            }
        }

        // =========================================================================================
        // 5. Exact byte-match (via SHA-256 golden hash) of 'A''s decoded SDF bitmap — pinned against
        //    the committed fixture with an independent, non-Unity protobuf walker.
        // =========================================================================================
        [Test]
        public void Decode_UppercaseA_BitmapMatchesGoldenHash()
        {
            FontStackGlyphs stack = DecodeLatinRange().Stacks[0];
            SdfGlyph a = stack.Glyphs[65u];

            Assert.AreEqual(483, a.Bitmap.Length);
            Assert.AreEqual(
                "06abcc9e85c98ceefdc40b0081d8b6770a1b08037abc03655cb81b6e96569c32",
                Sha256Hex(a.Bitmap),
                "decoded 'A' bitmap bytes must match the golden hash exactly (row-major, buffer-padded)");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // IconQuadLayoutTests — layout over the committed sample-sprite fixture's own numbers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IconQuadLayout.Layout"/> over the committed <c>sample-sprite.json</c> fixture's own
    /// numbers (sheet 64×64 — <c>marker</c> 16×16@1x, <c>star</c> 24×24@2x, <c>dot</c> 8×8@1x at (0,32)),
    /// hand-pinned rather than re-derived, so a formula regression (e.g. UV accidentally divided by
    /// <see cref="SpriteEntry.PixelRatio"/>) is caught by a literal mismatch. Engine-free; runs in both
    /// runners.
    ///
    /// <para>Every UV expectation below is the sprite's PADDED rect — its content rect grown by
    /// <see cref="SpriteEntry.Padding"/> texels on each side — divided by the sheet size, with NO inset.
    /// The half-texel inset the earlier expectations carried is retired: what keeps the sheet's bilinear
    /// filtering off the neighbouring sprite is now the one-texel transparent border <c>SpriteSheet</c>'s
    /// repack lays down, and DRAWING that border is what antialiases the icon's silhouette (see
    /// <see cref="IconQuadLayout"/>). The fixture entries below carry <c>Padding = 0</c> — they mirror the
    /// committed sprite JSON, which is a RAW parsed index — so their UVs are the plain rect; the padded
    /// cases live in the teeth at the bottom of this file. Each expectation carries its own derivation in a
    /// trailing comment, and they remain hand-written literals ON PURPOSE: re-deriving them from
    /// <c>entry.X / sheetSize</c> would just restate the implementation and could not fail.</para>
    /// </summary>
    [TestFixture]
    public class IconQuadLayoutTests
    {
        private static readonly int2 SheetSize = new int2(64, 64);

        // Fixture sprites (mirrors Assets/Fixtures/sprites/sample-sprite.json).
        private static readonly SpriteEntry Marker = new SpriteEntry { X = 0, Y = 0, Width = 16, Height = 16, PixelRatio = 1f, Sdf = false };
        private static readonly SpriteEntry Star = new SpriteEntry { X = 16, Y = 0, Width = 24, Height = 24, PixelRatio = 2f, Sdf = false };
        private static readonly SpriteEntry Dot = new SpriteEntry { X = 0, Y = 32, Width = 8, Height = 8, PixelRatio = 1f, Sdf = false };

        private const float Eps = 1e-5f;

        [Test]
        public void Star_CenterAnchor_IconSize1_LogicalSizeAndUvBothCorrect()
        {
            // The UV/pixelRatio RED tooth: star is @2x, so logical size (24/2=12) and the UV rect (spanning
            // the raw 24px extent, NEVER divided by pixelRatio) must diverge from each other — asserting
            // both in one test catches an implementation that mistakenly divides the UV rect too. The UV
            // extent below is 24/64, the sprite's full 24 texels, nowhere near the 12px a pixelRatio
            // division would produce.
            SymbolQuad quad = IconQuadLayout.Layout(Star, SheetSize, 1f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-6f, quad.TopLeft.x, Eps);
            Assert.AreEqual(6f, quad.TopLeft.y, Eps);
            Assert.AreEqual(6f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-6f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0.25f,   quad.UvTopLeft.x, Eps);     // 16/64
            Assert.AreEqual(0f,      quad.UvTopLeft.y, Eps);     //  0/64
            Assert.AreEqual(0.625f,  quad.UvBottomRight.x, Eps); // 40/64
            Assert.AreEqual(0.375f,  quad.UvBottomRight.y, Eps); // 24/64
        }

        [Test]
        public void Marker_CenterAnchor_IconSize1_UvIsTheRawRect()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-8f, quad.TopLeft.x, Eps);
            Assert.AreEqual(8f, quad.TopLeft.y, Eps);
            Assert.AreEqual(8f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-8f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0f,    quad.UvTopLeft.x, Eps);     //  0/64
            Assert.AreEqual(0f,    quad.UvTopLeft.y, Eps);     //  0/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.x, Eps); // 16/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.y, Eps); // 16/64
        }

        [Test]
        public void Dot_TopLeftAnchor_IconSize1_QuadAndUvCorrect()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Dot, SheetSize, 1f, TextAnchor.TopLeft, float2.zero);

            // Top-left anchor: the box's own top-left corner sits at the anchor (0,0), extending +x/-y.
            Assert.AreEqual(0f, quad.TopLeft.x, Eps);
            Assert.AreEqual(0f, quad.TopLeft.y, Eps);
            Assert.AreEqual(8f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-8f, quad.BottomRight.y, Eps);

            Assert.AreEqual(0f,      quad.UvTopLeft.x, Eps);     //  0/64
            Assert.AreEqual(0.5f,    quad.UvTopLeft.y, Eps);     // 32/64
            Assert.AreEqual(0.125f,  quad.UvBottomRight.x, Eps); //  8/64
            Assert.AreEqual(0.625f,  quad.UvBottomRight.y, Eps); // 40/64
        }

        [Test]
        public void Marker_CenterAnchor_IconSize2_ScalesExtentButNotUv()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, float2.zero);

            Assert.AreEqual(-16f, quad.TopLeft.x, Eps);
            Assert.AreEqual(16f, quad.TopLeft.y, Eps);
            Assert.AreEqual(16f, quad.BottomRight.x, Eps);
            Assert.AreEqual(-16f, quad.BottomRight.y, Eps);

            // UV is unaffected by icon-size — it indexes the sheet, not the drawn extent. Byte-identical to
            // the iconSize-1 case above, which is the whole point of this test.
            Assert.AreEqual(0f,    quad.UvTopLeft.x, Eps);     //  0/64
            Assert.AreEqual(0f,    quad.UvTopLeft.y, Eps);     //  0/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.x, Eps); // 16/64
            Assert.AreEqual(0.25f, quad.UvBottomRight.y, Eps); // 16/64
        }

        [Test]
        public void Marker_CenterAnchor_IconSize2_OffsetShiftsByOffsetTimesIconSize()
        {
            SymbolQuad unshifted = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, float2.zero);
            SymbolQuad shifted = IconQuadLayout.Layout(Marker, SheetSize, 2f, TextAnchor.Center, new float2(2f, 0f));

            // icon-offset.x(2) * icon-size(2) == +4 on every corner's x; y untouched (offset.y == 0).
            Assert.AreEqual(unshifted.TopLeft.x + 4f, shifted.TopLeft.x, Eps);
            Assert.AreEqual(unshifted.BottomRight.x + 4f, shifted.BottomRight.x, Eps);
            Assert.AreEqual(unshifted.TopLeft.y, shifted.TopLeft.y, Eps);
            Assert.AreEqual(unshifted.BottomRight.y, shifted.BottomRight.y, Eps);
        }

        [Test]
        public void Marker_OffsetY_IsNegatedOnceLikeTextOffset()
        {
            // Style icon-offset is y-DOWN as authored; TextLayoutOptionsBuilder negates text-offset.y exactly
            // once — icon must match (never double-flip).
            SymbolQuad unshifted = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, float2.zero);
            SymbolQuad shifted = IconQuadLayout.Layout(Marker, SheetSize, 1f, TextAnchor.Center, new float2(0f, 3f));

            // A positive (down) icon-offset.y must DECREASE the quad's y (move down in the y-up frame).
            Assert.AreEqual(unshifted.TopLeft.y - 3f, shifted.TopLeft.y, Eps);
            Assert.AreEqual(unshifted.BottomRight.y - 3f, shifted.BottomRight.y, Eps);
        }

        // 16x16 marker at iconSize 1: minX=-hAlign*16, maxX=(1-hAlign)*16, maxY=vAlign*16, minY=-(1-vAlign)*16
        // — the SAME hAlign/vAlign convention TextQuadLayout.ResolveAlignFactors uses (Left/Right/Top/Bottom
        // 0 or 1, Center/unset 0.5), so icon and text anchors agree.
        [TestCase(TextAnchor.Center, -8f, 8f, 8f, -8f)]
        [TestCase(TextAnchor.Left, 0f, 8f, 16f, -8f)]
        [TestCase(TextAnchor.Right, -16f, 8f, 0f, -8f)]
        [TestCase(TextAnchor.Top, -8f, 0f, 8f, -16f)]
        [TestCase(TextAnchor.Bottom, -8f, 16f, 8f, 0f)]
        [TestCase(TextAnchor.TopLeft, 0f, 0f, 16f, -16f)]
        [TestCase(TextAnchor.TopRight, -16f, 0f, 0f, -16f)]
        [TestCase(TextAnchor.BottomLeft, 0f, 16f, 16f, 0f)]
        [TestCase(TextAnchor.BottomRight, -16f, 16f, 0f, 0f)]
        public void AnchorSweep_MatchesHAlignVAlignConvention(
            TextAnchor anchor, float expectedTopLeftX, float expectedTopLeftY, float expectedBottomRightX, float expectedBottomRightY)
        {
            SymbolQuad quad = IconQuadLayout.Layout(Marker, SheetSize, 1f, anchor, float2.zero);
            Assert.AreEqual(expectedTopLeftX, quad.TopLeft.x, Eps, $"anchor {anchor}: TopLeft.x");
            Assert.AreEqual(expectedTopLeftY, quad.TopLeft.y, Eps, $"anchor {anchor}: TopLeft.y");
            Assert.AreEqual(expectedBottomRightX, quad.BottomRight.x, Eps, $"anchor {anchor}: BottomRight.x");
            Assert.AreEqual(expectedBottomRightY, quad.BottomRight.y, Eps, $"anchor {anchor}: BottomRight.y");
        }

        // The inline bounds formula (min/max corner ± skirt) wraps a single laid-out icon quad into
        // the same caller-owned quad list + TextLayoutBounds shape the point-text path produces, so
        // StyledSymbolTileBuilder's Pass 2 can emit an icon down the SAME point-placement path.
        [Test]
        public void IconBounds_WrapsSingleQuad_BoundsAreItsOwnMinMaxCorners()
        {
            SymbolQuad quad = IconQuadLayout.Layout(Star, SheetSize, 1f, TextAnchor.TopLeft, new float2(3f, -1f));

            float skirtPx = IconQuadLayout.SkirtPx(Star, 1f);
            var quads = new List<SymbolQuad> { quad };
            float2 skirtV = new float2(skirtPx, skirtPx);
            float2 boundsMin = math.min(quad.TopLeft, quad.BottomRight) + skirtV;
            float2 boundsMax = math.max(quad.TopLeft, quad.BottomRight) - skirtV;

            Assert.AreEqual(1, quads.Count, "a sprite is exactly one quad");
            AssertQuadEqual(quad, quads[0]);
            Assert.AreEqual(math.min(quad.TopLeft.x, quad.BottomRight.x), boundsMin.x, Eps);
            Assert.AreEqual(math.min(quad.TopLeft.y, quad.BottomRight.y), boundsMin.y, Eps);
            Assert.AreEqual(math.max(quad.TopLeft.x, quad.BottomRight.x), boundsMax.x, Eps);
            Assert.AreEqual(math.max(quad.TopLeft.y, quad.BottomRight.y), boundsMax.y, Eps);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The padded-repack teeth. SpriteSheet hands every drawable sprite a one-texel transparent border
        // and reports it as SpriteEntry.Padding; IconQuadLayout draws that border (which is what gives the
        // silhouette a ramp bilinear can antialias) and grows the quad by exactly its drawn size.
        //
        // Padded twins of the fixture sprites — SAME rect, Padding = 1.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private static readonly SpriteEntry PaddedMarker = new SpriteEntry { X = 5, Y = 5, Width = 16, Height = 16, PixelRatio = 1f, Padding = 1 };
        private static readonly SpriteEntry PaddedStar = new SpriteEntry { X = 30, Y = 7, Width = 24, Height = 24, PixelRatio = 2f, Padding = 1 };
        private static readonly SpriteEntry PaddedDot = new SpriteEntry { X = 1, Y = 40, Width = 8, Height = 8, PixelRatio = 1f, Padding = 1 };

        /// <summary>
        /// C1 — the CONTENT box is exactly what it was before the border existed, for every anchor. The
        /// literals are the same nine rows the un-padded <see cref="AnchorSweep_MatchesHAlignVAlignConvention"/>
        /// pins for a 16×16 @1x marker at icon-size 1, which is the point: adding a border must not move or
        /// resize a single anchor's box. This is the ink-size invariant AND the collision-box invariant in
        /// one, and it is what makes "icons cannot silently grow" checkable rather than merely intended.
        /// </summary>
        [TestCase(TextAnchor.Center, -8f, 8f, 8f, -8f)]
        [TestCase(TextAnchor.Left, 0f, 8f, 16f, -8f)]
        [TestCase(TextAnchor.Right, -16f, 8f, 0f, -8f)]
        [TestCase(TextAnchor.Top, -8f, 0f, 8f, -16f)]
        [TestCase(TextAnchor.Bottom, -8f, 16f, 8f, 0f)]
        [TestCase(TextAnchor.TopLeft, 0f, 0f, 16f, -16f)]
        [TestCase(TextAnchor.TopRight, -16f, 0f, 0f, -16f)]
        [TestCase(TextAnchor.BottomLeft, 0f, 16f, 16f, 0f)]
        [TestCase(TextAnchor.BottomRight, -16f, 16f, 0f, 0f)]
        public void PaddedAnchorSweep_ContentBoxIsUnchangedByTheBorder(
            TextAnchor anchor, float expectedTopLeftX, float expectedTopLeftY,
            float expectedBottomRightX, float expectedBottomRightY)
        {
            SymbolQuad quad = IconQuadLayout.Layout(PaddedMarker, SheetSize, 1f, anchor, float2.zero);
            float skirtPx = IconQuadLayout.SkirtPx(PaddedMarker, 1f);
            float2 skirtV = new float2(skirtPx, skirtPx);
            float2 boundsMin = math.min(quad.TopLeft, quad.BottomRight) + skirtV;
            float2 boundsMax = math.max(quad.TopLeft, quad.BottomRight) - skirtV;

            Assert.AreEqual(math.min(expectedTopLeftX, expectedBottomRightX), boundsMin.x, Eps, $"anchor {anchor}: BoundsMin.x");
            Assert.AreEqual(math.min(expectedTopLeftY, expectedBottomRightY), boundsMin.y, Eps, $"anchor {anchor}: BoundsMin.y");
            Assert.AreEqual(math.max(expectedTopLeftX, expectedBottomRightX), boundsMax.x, Eps, $"anchor {anchor}: BoundsMax.x");
            Assert.AreEqual(math.max(expectedTopLeftY, expectedBottomRightY), boundsMax.y, Eps, $"anchor {anchor}: BoundsMax.y");
        }

        /// <summary>C1, the general case: for every fixture sprite × icon-size × anchor × offset, the padded
        /// entry's CONTENT box equals the un-padded entry's quad exactly. A single formula error anywhere in
        /// the grow/un-grow pair shows up here.</summary>
        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void PaddedContentBox_EqualsTheUnpaddedQuad_ForEverySpriteAndAnchor(float iconSize)
        {
            var pairs = new[] { (Marker, PaddedMarker), (Star, PaddedStar), (Dot, PaddedDot) };
            var anchors = new[]
            {
                TextAnchor.Center, TextAnchor.Left, TextAnchor.Right, TextAnchor.Top, TextAnchor.Bottom,
                TextAnchor.TopLeft, TextAnchor.TopRight, TextAnchor.BottomLeft, TextAnchor.BottomRight,
            };
            var offset = new float2(3f, -2f);

            foreach ((SpriteEntry bare, SpriteEntry padded) in pairs)
            {
                foreach (TextAnchor anchor in anchors)
                {
                    SymbolQuad bareQuad = IconQuadLayout.Layout(bare, SheetSize, iconSize, anchor, offset);
                    SymbolQuad paddedQuad = IconQuadLayout.Layout(padded, SheetSize, iconSize, anchor, offset);
                    float skirtPx = IconQuadLayout.SkirtPx(padded, iconSize);
                    float2 skirtV = new float2(skirtPx, skirtPx);
                    float2 boundsMin = math.min(paddedQuad.TopLeft, paddedQuad.BottomRight) + skirtV;
                    float2 boundsMax = math.max(paddedQuad.TopLeft, paddedQuad.BottomRight) - skirtV;

                    string what = $"{bare.Width}x{bare.Height}@{bare.PixelRatio} {anchor} size {iconSize}";
                    Assert.AreEqual(math.min(bareQuad.TopLeft.x, bareQuad.BottomRight.x), boundsMin.x, Eps, $"{what}: min.x");
                    Assert.AreEqual(math.min(bareQuad.TopLeft.y, bareQuad.BottomRight.y), boundsMin.y, Eps, $"{what}: min.y");
                    Assert.AreEqual(math.max(bareQuad.TopLeft.x, bareQuad.BottomRight.x), boundsMax.x, Eps, $"{what}: max.x");
                    Assert.AreEqual(math.max(bareQuad.TopLeft.y, bareQuad.BottomRight.y), boundsMax.y, Eps, $"{what}: max.y");
                }
            }
        }

        /// <summary>C2 — the UV rect covers the sprite's PADDED rect exactly (no inset, no half-texel), and
        /// the quad's extent is the padded rect's own logical size. "Pad the atlas but leave the quad
        /// nominal" fails the second half; "grow the quad but leave the UV on the content" fails the
        /// first.</summary>
        [Test]
        public void PaddedSprite_UvRectAndQuadExtentBothSpanTheWholeCell()
        {
            const float iconSize = 1.5f;
            SymbolQuad quad = IconQuadLayout.Layout(PaddedStar, SheetSize, iconSize, TextAnchor.Center, float2.zero);

            float uvTexelsX = (quad.UvBottomRight.x - quad.UvTopLeft.x) * SheetSize.x;
            float uvTexelsY = (quad.UvBottomRight.y - quad.UvTopLeft.y) * SheetSize.y;
            Assert.AreEqual(PaddedStar.Width + 2 * PaddedStar.Padding, uvTexelsX, Eps, "UV must span the padded width");
            Assert.AreEqual(PaddedStar.Height + 2 * PaddedStar.Padding, uvTexelsY, Eps, "UV must span the padded height");

            float quadWidth = quad.BottomRight.x - quad.TopLeft.x;
            float quadHeight = quad.TopLeft.y - quad.BottomRight.y;
            Assert.AreEqual((PaddedStar.Width + 2 * PaddedStar.Padding) / PaddedStar.PixelRatio * iconSize, quadWidth, Eps);
            Assert.AreEqual((PaddedStar.Height + 2 * PaddedStar.Padding) / PaddedStar.PixelRatio * iconSize, quadHeight, Eps);
        }

        /// <summary>
        /// C3 — the identity the whole design hangs on: <b>texels-per-drawn-pixel is the same for the border
        /// as for the content</b>, i.e. <c>uvWidth × sheetWidth / quadWidth == PixelRatio / iconSize</c>,
        /// independent of the sprite and of the padding. It catches BOTH shallow implementations in one
        /// assertion — a quad grown without widening the UV rect makes the ratio too small (fewer texels per
        /// drawn pixel: the content is stretched across the padded quad, so the icon <b>grew</b>), a UV rect
        /// widened without growing the quad makes it too large (more texels per drawn pixel: the padded rect
        /// is squeezed into the nominal quad, so the icon <b>shrank</b>).
        /// </summary>
        [TestCase(0.75f)]
        [TestCase(3f)]
        public void TexelsPerDrawnPixel_IsTheSameForBorderAndContent(float iconSize)
        {
            foreach (SpriteEntry entry in new[] { PaddedMarker, PaddedStar, PaddedDot, Marker, Dot })
            {
                SymbolQuad quad = IconQuadLayout.Layout(entry, SheetSize, iconSize, TextAnchor.Center, float2.zero);
                float uvTexels = (quad.UvBottomRight.x - quad.UvTopLeft.x) * SheetSize.x;
                float quadWidth = quad.BottomRight.x - quad.TopLeft.x;

                Assert.AreEqual(entry.PixelRatio / iconSize, uvTexels / quadWidth, Eps,
                    $"{entry.Width}x{entry.Height}@{entry.PixelRatio} pad {entry.Padding}, size {iconSize}: " +
                    $"a drawn pixel must cover PixelRatio/iconSize texels — the SAME rate inside the border " +
                    $"as inside the content, or the ink is being scaled by the padding.");
            }
        }

        /// <summary>
        /// A malformed sheet may declare an explicit <c>"pixelRatio": 0</c>, which parses straight through
        /// (<c>SpriteIndex</c> only DEFAULTS the field to 1). Unguarded, an unpadded such entry makes the
        /// skirt <c>0 / 0f</c> — NaN, not the infinity the size maths produces — and NaN bounds compare false
        /// against every collision test rather than swallowing the screen. Mirrors the identical guard
        /// <c>FillPattern.TryResolve</c> already carries for the same malformed field.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        public void SkirtPx_WithAMalformedZeroPixelRatio_IsFinite(int padding)
        {
            var malformed = new SpriteEntry
            {
                X = 0, Y = 0, Width = 8, Height = 8, PixelRatio = 0f, Padding = padding,
            };

            float skirt = IconQuadLayout.SkirtPx(malformed, 2f);

            Assert.IsFalse(float.IsNaN(skirt), $"padding {padding}: the skirt must never be NaN");
            Assert.IsFalse(float.IsInfinity(skirt), $"padding {padding}: nor infinite");
            Assert.AreEqual(padding * 2f, skirt, Eps, "a zero pixelRatio falls back to 1, exactly as FillPattern's does");
        }

        private static void AssertQuadEqual(in SymbolQuad expected, in SymbolQuad actual)
        {
            Assert.AreEqual(expected.TopLeft.x, actual.TopLeft.x, Eps);
            Assert.AreEqual(expected.TopLeft.y, actual.TopLeft.y, Eps);
            Assert.AreEqual(expected.BottomRight.x, actual.BottomRight.x, Eps);
            Assert.AreEqual(expected.BottomRight.y, actual.BottomRight.y, Eps);
            Assert.AreEqual(expected.UvTopLeft.x, actual.UvTopLeft.x, Eps);
            Assert.AreEqual(expected.UvTopLeft.y, actual.UvTopLeft.y, Eps);
            Assert.AreEqual(expected.UvBottomRight.x, actual.UvBottomRight.x, Eps);
            Assert.AreEqual(expected.UvBottomRight.y, actual.UvBottomRight.y, Eps);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SdfDistanceFieldTests — the decoded glyph bitmap is a real signed distance field
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The decoded glyph-PBF bitmap is a REAL signed distance field, not a coverage
    /// bitmap masquerading as one, against the same committed fixture
    /// (<c>Assets/Fixtures/glyphs/NotoSansRegular/0-255.pbf.bytes</c>) Slice 1's decode tests use.
    ///
    /// Convention confirmed: the MapLibre/Mapbox glyph-PBF SDF encodes the glyph OUTLINE at
    /// <see cref="IsoLevel"/> ≈ 0.75 × 255 (≈191) — not the generic-SDF 0.5 — leaving more of the byte
    /// range for outward distance (halo) than inward (fill); this is the (public, non-source) "Drawing
    /// Text with Signed Distance Fields in Mapbox GL" convention, cross-checked directly against the
    /// committed fixture's real 'A' bitmap below (deep-outside padding corners decode to flat 0, values
    /// climb smoothly toward the interior — never the reverse — confirming the sign and rough placement
    /// of the cutoff without needing to re-derive it from the pixels, which would be circular).
    ///
    /// Two checks, each run against BOTH the real fixture (must pass) and a synthetic coverage-style
    /// (flat 0 / flat 255, single-pixel jump) array:
    ///  1. Graded-edge structural guard — THE decisive tooth. A real SDF has many distinct mid-range
    ///     byte values forming a multi-pixel transition band; a coverage bitmap has none (it is exactly
    ///     {0, 255}), so it fails <c>distinctMidBandValues &gt; 10</c> outright (0 is never &gt; 10).
    ///  2. Scale-invariance — a real SDF's iso-level crossing, reconstructed from a coarse (every-2nd-
    ///     sample) set, agrees with the full-resolution reconstruction to a fraction of a pixel (positive
    ///     property, checked on real data only). Separately (different, coarser step — a hard edge has
    ///     no gradient for step=2 to expose, see the step-2-vs-step-4 note on the teeth test below), a
    ///     coverage bitmap's reconstructed position visibly SHIFTS as the sampling grid coarsens — the
    ///     scale-dependence a real SDF does not have. This second check demonstrates the failure mode
    ///     qualitatively; it is not a like-for-like re-run of check 2's real-data assertion.
    /// </summary>
    [TestFixture]
    public class SdfDistanceFieldTests
    {
        // 0.75 * 255 = 191.25 -> 191. The MapLibre/Mapbox SDF convention: the outline sits at ~0.75 of
        // the byte range (not the generic-SDF 0.5), asymmetrically favoring outward (halo) distance.
        private const int IsoLevel = 191;

        // A texel counts as "graded" (neither deep-outside nor deep-inside) if it falls strictly within
        // this band — loose on purpose: the point is "not flat 0/255", not pinning an exact width.
        private const int MidBandLo = 32;
        private const int MidBandHi = 223;

        // Same threshold used both to accept the real SDF's coarse/fine agreement and to reject the
        // synthetic coverage bitmap's coarse/fine divergence (see the two ScaleInvariance_* tests).
        private const double ScaleInvarianceTolerancePixels = 0.5;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static SdfGlyph LoadUppercaseA()
            => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0].Glyphs[65u];

        // =========================================================================================
        // 1. Graded-edge structural guard (real fixture): deep-outside padding corner is flat 0; the
        //    bitmap carries many distinct graded mid-range values, not a hard 0/255 split.
        // =========================================================================================
        [Test]
        public void StructuralGuard_GlyphEdgeTexelsAreGradedNotFlat()
        {
            SdfGlyph a = LoadUppercaseA();

            Assert.AreEqual(0, a.Bitmap[0], "a deep-padding corner texel must be flat 0 (far outside the spread radius)");

            int distinctMidBandValues = CountDistinctValuesInBand(a.Bitmap, MidBandLo, MidBandHi);
            Assert.Greater(distinctMidBandValues, 10,
                "a real SDF must carry many graded mid-range values around the glyph edge, not a hard 0/255 step");
        }

        // =========================================================================================
        // 1b. Teeth: the SAME check applied to a synthetic coverage-style (flat 0 / flat 255) array
        //     finds ZERO graded values — proving the guard above actually discriminates.
        // =========================================================================================
        [Test]
        public void StructuralGuard_Teeth_SyntheticCoverageBitmapHasNoGradedBand()
        {
            byte[] coverageBitmap = BuildSyntheticHardEdge(length: 21, jumpAtIndex: 10);

            Assert.AreEqual(0, CountDistinctValuesInBand(coverageBitmap, MidBandLo, MidBandHi),
                "a plain coverage bitmap is exactly {0,255} everywhere -- it must show ZERO graded mid-band values");
        }

        // =========================================================================================
        // 2. Scale-invariance (real fixture): row 3 of 'A' (0-based; the apex, empirically verified
        //    against the committed fixture to cross the iso-level cleanly) thresholded at IsoLevel from
        //    a coarse (every-2nd-sample) reconstruction agrees with the full-resolution reconstruction,
        //    within a fraction of a pixel -- the entire point of an SDF: crisp at any effective size.
        // =========================================================================================
        [Test]
        public void ScaleInvariance_CoarseAndFineSamplingAgreeOnIsoCrossing()
        {
            SdfGlyph a = LoadUppercaseA();
            int cellWidth = a.Width + 2 * GlyphSdf.Buffer;
            const int row = 3;
            var rowBytes = new byte[cellWidth];
            Array.Copy(a.Bitmap, row * cellWidth, rowBytes, 0, cellWidth);

            Assert.IsTrue(TryFindRisingCrossing(rowBytes, step: 1, IsoLevel, out double fineCrossing),
                "full-resolution sampling must find a rising iso crossing on this scanline");
            Assert.IsTrue(TryFindRisingCrossing(rowBytes, step: 2, IsoLevel, out double coarseCrossing),
                "half-resolution (every-2nd-sample) reconstruction must find the same rising crossing");

            Assert.LessOrEqual(Math.Abs(fineCrossing - coarseCrossing), ScaleInvarianceTolerancePixels,
                "the recovered iso-level edge position must be (near-)identical whether reconstructed from " +
                "a coarse or a fine sample set -- that is what lets one SDF texture stay crisp at any size");
        }

        // =========================================================================================
        // 2b. A coverage bitmap's reconstructed iso crossing SHIFTS as the sampling grid coarsens --
        //     the scale-dependence a real SDF does not have. Uses a coarser step (4, vs 2 for the real
        //     scanline above) deliberately: a hard, zero-width transition carries no gradient at all
        //     between its two flat plateaus, so a coarse-enough sample set slides the interpolated
        //     crossing toward whichever bracket it lands in; step=2 on THIS array only drifts ~0.25px
        //     (an alignment coincidence, not evidence the check is toothless -- the graded-value guard
        //     above is the decisive tooth). This is a qualitative demonstration of the failure mode, not
        //     a like-for-like re-run of the real-data assertion (which uses step=2 and passes it).
        // =========================================================================================
        [Test]
        public void ScaleInvariance_Teeth_SyntheticCoverageBitmapDiverges()
        {
            byte[] coverageBitmap = BuildSyntheticHardEdge(length: 24, jumpAtIndex: 12);

            Assert.IsTrue(TryFindRisingCrossing(coverageBitmap, step: 1, IsoLevel, out double fineCrossing));
            Assert.IsTrue(TryFindRisingCrossing(coverageBitmap, step: 4, IsoLevel, out double coarseCrossing));

            Assert.Greater(Math.Abs(fineCrossing - coarseCrossing), ScaleInvarianceTolerancePixels,
                "a coverage bitmap's coarse/fine iso crossings shift with sampling coarseness -- the " +
                "scale-dependence a real SDF (see the check above) does not have");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Flat 0 for [0, jumpAtIndex), flat 255 for [jumpAtIndex, length) -- a plain coverage mask.</summary>
        private static byte[] BuildSyntheticHardEdge(int length, int jumpAtIndex)
        {
            var data = new byte[length];
            for (int i = jumpAtIndex; i < length; i++) data[i] = 255;
            return data;
        }

        private static int CountDistinctValuesInBand(byte[] data, int lo, int hi)
        {
            var seen = new HashSet<byte>();
            foreach (byte v in data)
            {
                if (v >= lo && v <= hi) seen.Add(v);
            }
            return seen.Count;
        }

        /// <summary>
        /// Finds the first index i (in units of the ORIGINAL/native array, i.e. already scaled by
        /// <paramref name="step"/>) such that sample[i] &lt; iso &lt;= sample[i+step], subsampling
        /// <paramref name="row"/> every <paramref name="step"/> native indices, and linearly
        /// interpolates the sub-pixel crossing position within that bracket (native index units).
        /// </summary>
        private static bool TryFindRisingCrossing(byte[] row, int step, int iso, out double nativeCrossing)
        {
            int sampleCount = (row.Length - 1) / step + 1;
            for (int i = 0; i + 1 < sampleCount; i++)
            {
                int nativeA = i * step;
                int nativeB = (i + 1) * step;
                byte a = row[nativeA];
                byte b = row[nativeB];
                if (a < iso && b >= iso)
                {
                    double t = (iso - a) / (double)(b - a);
                    nativeCrossing = nativeA + t * step;
                    return true;
                }
            }
            nativeCrossing = 0;
            return false;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextAnchorOffsetJustifyTests — anchor/offset/justify shift the multi-line block bbox
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T2 (text-anchor shifts the whole multi-line block bbox, H and V), T3
    /// (text-offset / text-radial-offset, ems -&gt; baked px), T4 (text-justify incl. auto).
    /// </summary>
    [TestFixture]
    public class TextAnchorOffsetJustifyTests
    {
        private const float Tolerance = 1e-3f;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = TextDirection.LeftToRight };
        }

        private static TextLayoutOptions MakeOptions(
            TextAnchor anchor = TextAnchor.Center,
            float2 offset = default,
            float radialOffset = 0f,
            TextJustify justify = TextJustify.Auto,
            float maxWidthEm = 10f,
            float lineHeightEm = 1.2f,
            float letterSpacingEm = 0f)
            => new TextLayoutOptions
            {
                Anchor = anchor,
                Offset = offset,
                RadialOffset = radialOffset,
                Justify = justify,
                MaxWidthEm = maxWidthEm,
                LineHeightEm = lineHeightEm,
                LetterSpacingEm = letterSpacingEm,
            };

        private static void AssertConstantDelta(IReadOnlyList<SymbolQuad> baseline, IReadOnlyList<SymbolQuad> shifted, float2 expectedDelta, string label)
        {
            Assert.AreEqual(baseline.Count, shifted.Count, $"{label}: quad counts must match");
            for (int i = 0; i < baseline.Count; i++)
            {
                Assert.AreEqual(expectedDelta.x, shifted[i].TopLeft.x - baseline[i].TopLeft.x, Tolerance, $"{label}: quad {i} TopLeft.x");
                Assert.AreEqual(expectedDelta.y, shifted[i].TopLeft.y - baseline[i].TopLeft.y, Tolerance, $"{label}: quad {i} TopLeft.y");
                Assert.AreEqual(expectedDelta.x, shifted[i].BottomRight.x - baseline[i].BottomRight.x, Tolerance, $"{label}: quad {i} BottomRight.x");
                Assert.AreEqual(expectedDelta.y, shifted[i].BottomRight.y - baseline[i].BottomRight.y, Tolerance, $"{label}: quad {i} BottomRight.y");
            }
        }

        private static void AssertAllQuadsEqual(IReadOnlyList<SymbolQuad> a, IReadOnlyList<SymbolQuad> b, string label)
        {
            Assert.AreEqual(a.Count, b.Count, $"{label}: quad counts must match");
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].TopLeft.x, b[i].TopLeft.x, Tolerance, $"{label}: quad {i} TopLeft.x");
                Assert.AreEqual(a[i].TopLeft.y, b[i].TopLeft.y, Tolerance, $"{label}: quad {i} TopLeft.y");
                Assert.AreEqual(a[i].BottomRight.x, b[i].BottomRight.x, Tolerance, $"{label}: quad {i} BottomRight.x");
                Assert.AreEqual(a[i].BottomRight.y, b[i].BottomRight.y, Tolerance, $"{label}: quad {i} BottomRight.y");
            }
        }

        // =========================================================================================
        // T2a — single line: TopLeft/Center/BottomRight differ by exactly the anchor delta. BottomRight
        // uses the block's bbox height (unchanged); Center uses the optical-centre shift --
        // NOT the arithmetic midpoint of Top and BottomRight, which is the point of this stage.
        // =========================================================================================
        [Test]
        public void Anchor_SingleLine_BottomShiftsByBlockHeight_CentreByOpticalCentre()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            float lineWidth = entryA.Advance + entryLowerA.Advance;
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            var topLeftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            var bottomRightQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.TopLeft), topLeftQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.Center), centerQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.BottomRight), bottomRightQuads);

            // Optical-centre shift: GlyphSdf.BaselineBelowReferencePx (26) minus half a
            // cap height (0.5 * (17/24)em * 24 = 8.5) = 17.5 -- a hand-derived literal, not read
            // back from the production constants it exists to check.
            AssertConstantDelta(topLeftQuads, centerQuads, new float2(-0.5f * lineWidth, 17.5f), "Center - TopLeft");
            AssertConstantDelta(topLeftQuads, bottomRightQuads, new float2(-lineWidth, lineHeightPx), "BottomRight - TopLeft");

            TextLayoutOptions Opt(TextAnchor a) => MakeOptions(anchor: a);
        }

        // =========================================================================================
        // T2b — a 2-line run: BottomRight's vertical delta uses lineCount * lineHeight*24 (unchanged);
        // Center's uses the line-span midpoint -- the midpoint between the FIRST line's
        // optical centre and the LAST line's, i.e. one line's span (0.5*lineHeightPx) above the
        // single-line optical-centre shift, NOT half of lineCount*lineHeight. Justify held constant
        // (Center) across variants so the pure anchor delta is isolated (see TextQuadLayout's class
        // doc: the per-line justify term cancels in a delta between two Layout() calls that share
        // options except Anchor).
        // =========================================================================================
        [Test]
        public void Anchor_TwoLineRun_BottomUsesLineCountTimesLineHeight_CentreUsesLineSpanMidpoint()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);

            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)' ', entrySpace.Advance), ((uint)'a', entryLowerA.Advance));

            // maxWidthPx strictly between "A" alone and "A a" combined -- forces exactly one break.
            float maxWidthPx = entryA.Advance + entrySpace.Advance * 0.5f;
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;
            float blockWidth = math.max(entryA.Advance, entryLowerA.Advance);
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;
            float blockHeight = 2 * lineHeightPx;

            // 17.5 is the single-line optical-centre shift derived in
            // Anchor_SingleLine_BottomShiftsByBlockHeight_CentreByOpticalCentre above; a 2-line block's
            // centre sits one line's span (0.5*lineHeightPx) above it.
            float deltaYCenterExpected = 17.5f + 0.5f * lineHeightPx;

            var topLeftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            var bottomRightQuads = new List<SymbolQuad>();
            TextLayoutBounds topLeft = TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.TopLeft), topLeftQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.Center), centerQuads);
            TextQuadLayout.Layout(run, atlas, Opt(TextAnchor.BottomRight), bottomRightQuads);

            Assert.AreEqual(2, topLeft.LineCount, "the chosen max-width must force exactly a 2-line wrap");

            float deltaYCenter = centerQuads[0].TopLeft.y - topLeftQuads[0].TopLeft.y;
            float deltaYBottomRight = bottomRightQuads[0].TopLeft.y - topLeftQuads[0].TopLeft.y;

            Assert.AreEqual(deltaYCenterExpected, deltaYCenter, Tolerance, "Center's vertical delta must be the line-span midpoint, not half of lineCount*lineHeight");
            Assert.AreEqual(blockHeight, deltaYBottomRight, Tolerance, "BottomRight's vertical delta must use lineCount*lineHeight");

            // Teeth: a per-line (not block-bbox) vertical anchor would use just ONE lineHeightPx for
            // BottomRight, and the OLD (rejected) box-midpoint formula would give 0.5*blockHeight for
            // Center -- both different, smaller/larger numbers that must NOT match.
            Assert.AreNotEqual(0.5f * lineHeightPx, deltaYCenter, "a per-line vertical anchor would use a single line's height, not the line-span midpoint");
            Assert.AreNotEqual(0.5f * blockHeight, deltaYCenter, "the old box-midpoint formula (0.5*blockHeight) must not match the optical-centre formula");
            Assert.AreNotEqual(lineHeightPx, deltaYBottomRight, "a per-line vertical anchor would use a single line's height, not the block's");

            AssertConstantDelta(topLeftQuads, centerQuads, new float2(-0.5f * blockWidth, deltaYCenterExpected), "Center - TopLeft (2-line)");
            AssertConstantDelta(topLeftQuads, bottomRightQuads, new float2(-blockWidth, blockHeight), "BottomRight - TopLeft (2-line)");

            TextLayoutOptions Opt(TextAnchor a) => MakeOptions(anchor: a, justify: TextJustify.Center, maxWidthEm: maxWidthEm);
        }

        // =========================================================================================
        // T3 — text-offset / text-radial-offset (ems -> baked px, x24).
        // =========================================================================================
        [Test]
        public void Offset_TranslatesEveryQuad_ByEmsTimes24()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var baselineQuads = new List<SymbolQuad>();
            var offsetXQuads = new List<SymbolQuad>();
            var offsetYQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(offset: float2.zero), baselineQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(offset: new float2(1f, 0f)), offsetXQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(offset: new float2(0f, 0.5f)), offsetYQuads);

            AssertConstantDelta(baselineQuads, offsetXQuads, new float2(24f, 0f), "text-offset [1,0]");
            AssertConstantDelta(baselineQuads, offsetYQuads, new float2(0f, 12f), "text-offset [0,0.5]");
        }

        [Test]
        public void RadialOffset_CornerAnchor_ResolvesToDiagonal_AndOverridesOffset()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance));

            var baselineQuads = new List<SymbolQuad>();
            var radialQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft), baselineQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, radialOffset: 1f), radialQuads);

            // TopLeft is a corner anchor (hAlign=0, vAlign=0): pushes away from the anchored edges,
            // i.e. further +x (away from the left edge) and further -y (away from the top edge),
            // split into a diagonal of magnitude RadialOffset/sqrt2 on each axis.
            float diagPx = 24f / math.SQRT2;
            AssertConstantDelta(baselineQuads, radialQuads, new float2(diagPx, -diagPx), "RadialOffset=1 @ TopLeft (corner)");

            // Radial overrides a nonzero Offset entirely (decision: "Radial overrides Offset if !=0").
            var radialWithIgnoredOffsetQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas,
                MakeOptions(anchor: TextAnchor.TopLeft, offset: new float2(5f, 5f), radialOffset: 1f), radialWithIgnoredOffsetQuads);
            AssertAllQuadsEqual(radialQuads, radialWithIgnoredOffsetQuads, "RadialOffset must override a nonzero Offset");
        }

        [Test]
        public void RadialOffset_PureAxisAnchors_ResolveToSingleAxis()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance));

            var baselineLeftQuads = new List<SymbolQuad>();
            var radialLeftQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left), baselineLeftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, radialOffset: 1f), radialLeftQuads);
            AssertConstantDelta(baselineLeftQuads, radialLeftQuads, new float2(24f, 0f), "RadialOffset=1 @ Left (pure x axis)");

            var baselineTopQuads = new List<SymbolQuad>();
            var radialTopQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top), baselineTopQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, radialOffset: 1f), radialTopQuads);
            AssertConstantDelta(baselineTopQuads, radialTopQuads, new float2(0f, -24f), "RadialOffset=1 @ Top (pure y axis)");
        }

        // =========================================================================================
        // T4 — text-justify (left/center/right) shifts each line's local start relative to the
        // other, and "auto" resolves from the anchor.
        // =========================================================================================
        [Test]
        public void Justify_ShiftsEachLine_RelativeToTheOthers()
        {
            (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) = MakeTwoLineFixture();

            var leftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            var rightQuads = new List<SymbolQuad>();
            TextLayoutBounds left = TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Left, maxWidthEm: maxWidthEm), leftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Center, maxWidthEm: maxWidthEm), centerQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft, justify: TextJustify.Right, maxWidthEm: maxWidthEm), rightQuads);

            Assert.AreEqual(2, left.LineCount);
            Assert.AreEqual(2, leftQuads.Count);
            Assert.AreEqual(0, leftQuads[0].LineIndex);
            Assert.AreEqual(1, leftQuads[1].LineIndex);
            // (quads[0] is line 0's only glyph 'A'; quads[1] is line 1's only glyph 'a' -- the space never gets a quad.)

            float lineWidth0 = entryA.Advance;
            float lineWidth1 = entryLowerA.Advance;
            Assert.AreNotEqual(lineWidth0, lineWidth1, "sanity: the two line widths must differ for this to be a decisive test");

            // Pre-justify local start (penX=0 on each line, before any per-line correction).
            float raw0 = entryA.Left - GlyphSdf.Buffer;
            float raw1 = entryLowerA.Left - GlyphSdf.Buffer;

            float diffLeft = leftQuads[0].TopLeft.x - leftQuads[1].TopLeft.x;
            float diffCenter = centerQuads[0].TopLeft.x - centerQuads[1].TopLeft.x;
            float diffRight = rightQuads[0].TopLeft.x - rightQuads[1].TopLeft.x;

            // Left (factor 0): no per-line correction at all -- each line's own local start (raw0/raw1) is untouched.
            float expectedDiffLeft = raw0 - raw1;
            Assert.AreEqual(expectedDiffLeft, diffLeft, Tolerance, "justify:left leaves each line's own local start untouched");

            // Right (factor 1): corrected_i = raw_i - lineWidth_i -- every line's END lands at local x=0.
            float expectedDiffRight = (raw0 - lineWidth0) - (raw1 - lineWidth1);
            Assert.AreEqual(expectedDiffRight, diffRight, Tolerance);

            // Center (factor .5): corrected_i = raw_i - 0.5*lineWidth_i.
            float expectedDiffCenter = (raw0 - 0.5f * lineWidth0) - (raw1 - 0.5f * lineWidth1);
            Assert.AreEqual(expectedDiffCenter, diffCenter, Tolerance);

            Assert.AreNotEqual(diffLeft, diffRight, "left and right justify must produce different relative line offsets");
            Assert.AreNotEqual(diffLeft, diffCenter, "left and center justify must produce different relative line offsets");
        }

        [Test]
        public void Justify_Auto_ResolvesFromAnchor()
        {
            (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) = MakeTwoLineFixture();

            var autoLeftAnchorQuads = new List<SymbolQuad>();
            var explicitLeftQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, justify: TextJustify.Auto, maxWidthEm: maxWidthEm), autoLeftAnchorQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left, justify: TextJustify.Left, maxWidthEm: maxWidthEm), explicitLeftQuads);
            AssertAllQuadsEqual(autoLeftAnchorQuads, explicitLeftQuads, "auto with a Left-ish anchor must resolve to Left");

            var autoRightAnchorQuads = new List<SymbolQuad>();
            var explicitRightQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right, justify: TextJustify.Auto, maxWidthEm: maxWidthEm), autoRightAnchorQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right, justify: TextJustify.Right, maxWidthEm: maxWidthEm), explicitRightQuads);
            AssertAllQuadsEqual(autoRightAnchorQuads, explicitRightQuads, "auto with a Right-ish anchor must resolve to Right");

            var autoTopAnchorQuads = new List<SymbolQuad>();
            var explicitCenterQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, justify: TextJustify.Auto, maxWidthEm: maxWidthEm), autoTopAnchorQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top, justify: TextJustify.Center, maxWidthEm: maxWidthEm), explicitCenterQuads);
            AssertAllQuadsEqual(autoTopAnchorQuads, explicitCenterQuads, "auto with a non-Left/Right anchor must resolve to Center");

            // Teeth: an impl that ignores the anchor for auto (always Center) fails the Left/Right cases above.
            Assert.AreNotEqual(explicitLeftQuads[0].TopLeft.x, explicitRightQuads[0].TopLeft.x,
                "sanity: Left vs Right justify must actually differ for the auto-resolution assertions above to be decisive");
        }

        private static (GlyphAtlas atlas, ShapedRun run, GlyphAtlasEntry entryA, GlyphAtlasEntry entrySpace, GlyphAtlasEntry entryLowerA, float maxWidthEm) MakeTwoLineFixture()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)' ', entrySpace.Advance), ((uint)'a', entryLowerA.Advance));

            float maxWidthPx = entryA.Advance + entrySpace.Advance * 0.5f; // forces exactly one break
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;
            return (atlas, run, entryA, entrySpace, entryLowerA, maxWidthEm);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextQuadLayoutTests — the decisive per-glyph quad golden
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T1 (THE decisive per-glyph quad golden: buffer + UV + pen-advance + anchor,
    /// element-by-element) and T8 (structural: no text-size parameter; <see cref="SymbolQuad"/> is
    /// blittable).
    /// </summary>
    [TestFixture]
    public class TextQuadLayoutTests
    {
        private const float Tolerance = 1e-3f;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(TextDirection direction, params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = direction };
        }

        // =========================================================================================
        // T1 — THE decisive test: per-glyph quad (buffer + UV + pen-advance + anchor), golden
        // hand-computed from entryA/entryLowerA (INPUT fields fetched from the atlas, not builder
        // output) — non-tautological.
        // =========================================================================================
        [Test]
        public void Layout_SingleLineCenterAnchor_MatchesHandComputedGolden()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);

            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, resultQuads);

            Assert.AreEqual(2, resultQuads.Count, "two visible glyphs, no whitespace -- exactly two quads");
            Assert.AreEqual(1, result.LineCount);

            // ---- Hand-computed golden, per the corrected TOP-anchored layout math ----
            // The glyph 'Top' metric is top-referenced, so the cell's TOP edge is at
            // (baselineY + Top + Buffer) and the cell grows DOWNWARD by CellSize.y; left edge at
            // (penX + Left - Buffer). (Convention correctness is a visual/Bucket-B check; this golden
            // guards the mechanical formula — buffer, CellSize, pen-advance — against a shallow impl.)
            float leftXA = 0f + entryA.Left - GlyphSdf.Buffer;
            float cellTopYA = 0f + entryA.Top + GlyphSdf.Buffer;
            float penXAfterA = entryA.Advance; // letter-spacing = 0 (default options)

            float leftXLowerA = penXAfterA + entryLowerA.Left - GlyphSdf.Buffer;
            float cellTopYLowerA = 0f + entryLowerA.Top + GlyphSdf.Buffer;
            float lineWidth = penXAfterA + entryLowerA.Advance; // == blockWidth (single line)

            // Center anchor: hAlign = .5; justify auto -> Center (factor .5), which cancels for a
            // single line (lineWidth == blockWidth) -- see TextQuadLayout's class doc. The y term is
            // the hand-derived optical-centre shift, NOT read back from the production
            // constants it exists to check: GlyphSdf.BaselineBelowReferencePx (26) minus half a
            // cap height (0.5 * (17/24)em * 24 = 8.5) = 17.5.
            float2 anchorShift = new float2(-0.5f * lineWidth, 17.5f);

            // TopLeft = (leftX, cellTopY); BottomRight = (leftX + CellSize.x, cellTopY - CellSize.y).
            float2 expectedTopLeftA = new float2(leftXA, cellTopYA) + anchorShift;
            float2 expectedBottomRightA = new float2(leftXA + entryA.CellSize.x, cellTopYA - entryA.CellSize.y) + anchorShift;
            float2 expectedTopLeftLowerA = new float2(leftXLowerA, cellTopYLowerA) + anchorShift;
            float2 expectedBottomRightLowerA = new float2(leftXLowerA + entryLowerA.CellSize.x, cellTopYLowerA - entryLowerA.CellSize.y) + anchorShift;

            SymbolQuad quadA = resultQuads[0];
            SymbolQuad quadLowerA = resultQuads[1];

            Assert.AreEqual(expectedTopLeftA.x, quadA.TopLeft.x, Tolerance, "'A' TopLeft.x");
            Assert.AreEqual(expectedTopLeftA.y, quadA.TopLeft.y, Tolerance, "'A' TopLeft.y");
            Assert.AreEqual(expectedBottomRightA.x, quadA.BottomRight.x, Tolerance, "'A' BottomRight.x");
            Assert.AreEqual(expectedBottomRightA.y, quadA.BottomRight.y, Tolerance, "'A' BottomRight.y");

            Assert.AreEqual(expectedTopLeftLowerA.x, quadLowerA.TopLeft.x, Tolerance, "'a' TopLeft.x");
            Assert.AreEqual(expectedTopLeftLowerA.y, quadLowerA.TopLeft.y, Tolerance, "'a' TopLeft.y");
            Assert.AreEqual(expectedBottomRightLowerA.x, quadLowerA.BottomRight.x, Tolerance, "'a' BottomRight.x");
            Assert.AreEqual(expectedBottomRightLowerA.y, quadLowerA.BottomRight.y, Tolerance, "'a' BottomRight.y");

            // UV: read directly off the fetched atlas entries (input, not the layout's own output).
            float2 atlasSize = atlas.Size;
            float2 atlasOriginA = entryA.AtlasOrigin;
            float2 atlasOriginLowerA = entryLowerA.AtlasOrigin;
            float2 expectedUvMinA = atlasOriginA / atlasSize;
            float2 expectedUvMaxA = (atlasOriginA + (float2)entryA.CellSize) / atlasSize;
            float2 expectedUvMinLowerA = atlasOriginLowerA / atlasSize;
            float2 expectedUvMaxLowerA = (atlasOriginLowerA + (float2)entryLowerA.CellSize) / atlasSize;

            Assert.AreEqual(expectedUvMinA.x, quadA.UvTopLeft.x, Tolerance);
            Assert.AreEqual(expectedUvMinA.y, quadA.UvTopLeft.y, Tolerance);
            Assert.AreEqual(expectedUvMaxA.x, quadA.UvBottomRight.x, Tolerance);
            Assert.AreEqual(expectedUvMaxA.y, quadA.UvBottomRight.y, Tolerance);

            Assert.AreEqual(expectedUvMinLowerA.x, quadLowerA.UvTopLeft.x, Tolerance);
            Assert.AreEqual(expectedUvMinLowerA.y, quadLowerA.UvTopLeft.y, Tolerance);
            Assert.AreEqual(expectedUvMaxLowerA.x, quadLowerA.UvBottomRight.x, Tolerance);
            Assert.AreEqual(expectedUvMaxLowerA.y, quadLowerA.UvBottomRight.y, Tolerance);
        }

        // =========================================================================================
        // T1 teeth: a quad sized to bare Width×Height (ignoring the 3px SDF buffer), positioned
        // without the buffer offset, or a pen that does not accumulate glyph-to-glyph, would all
        // yield DIFFERENT numbers than the golden above.
        // =========================================================================================
        [Test]
        public void Layout_Teeth_RequiresBufferedCellSizeAndAccumulatedPen()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, resultQuads);
            SymbolQuad quadA = resultQuads[0];
            SymbolQuad quadLowerA = resultQuads[1];

            // Tooth 1: quad size must be the buffered CellSize, not the bare glyph Width/Height
            // (CellSize - 2*Buffer).
            float actualWidthA = quadA.BottomRight.x - quadA.TopLeft.x;
            float actualHeightA = quadA.TopLeft.y - quadA.BottomRight.y;
            int bareWidthA = entryA.CellSize.x - 2 * GlyphSdf.Buffer;
            int bareHeightA = entryA.CellSize.y - 2 * GlyphSdf.Buffer;
            Assert.AreEqual(entryA.CellSize.x, actualWidthA, Tolerance, "quad width must be the buffered CellSize");
            Assert.AreEqual(entryA.CellSize.y, actualHeightA, Tolerance, "quad height must be the buffered CellSize");
            Assert.AreNotEqual((float)bareWidthA, actualWidthA, "a Width-only (no-buffer) quad would be narrower than the golden");
            Assert.AreNotEqual((float)bareHeightA, actualHeightA, "a Height-only (no-buffer) quad would be shorter than the golden");

            // Tooth 2: UV rect size must use CellSize, not bare Width/Height.
            float2 atlasSize = atlas.Size;
            float2 actualUvSize = quadA.UvBottomRight - quadA.UvTopLeft;
            float2 bareUvSize = new float2(bareWidthA, bareHeightA) / atlasSize;
            Assert.AreNotEqual(bareUvSize.x, actualUvSize.x, "a Width-only UV rect would not match the golden");
            Assert.AreNotEqual(bareUvSize.y, actualUvSize.y, "a Height-only UV rect would not match the golden");

            // Tooth 3: pen accumulates glyph-to-glyph. The anchor shift is a single additive constant
            // shared by both quads on the same line, so it cancels out of this relative delta --
            // isolating pen accumulation from anchor math entirely.
            float actualDeltaX = quadLowerA.TopLeft.x - quadA.TopLeft.x;
            float expectedDeltaXAccumulated = (entryA.Advance + entryLowerA.Left - GlyphSdf.Buffer) - (entryA.Left - GlyphSdf.Buffer);
            float wrongDeltaXNonAccumulated = entryLowerA.Left - entryA.Left; // if 'a' were placed at penX=0 too
            Assert.AreEqual(expectedDeltaXAccumulated, actualDeltaX, Tolerance);
            Assert.AreNotEqual(wrongDeltaXNonAccumulated, actualDeltaX, "a non-accumulating pen would place 'a' at the wrong x");
        }

        // =========================================================================================
        // T8a — structural: the layout API takes no text-size/size parameter.
        // =========================================================================================
        [Test]
        public void Layout_StructuralGuard_HasNoSizeParameter()
        {
            MethodInfo[] layoutMethods = typeof(TextQuadLayout).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Layout")
                .ToArray();

            Assert.AreEqual(1, layoutMethods.Length,
                "expected exactly the one no-alloc Layout overload (the allocating overload was retired with the managed layout-result carrier)");

            foreach (MethodInfo method in layoutMethods)
            {
                foreach (ParameterInfo p in method.GetParameters())
                {
                    Assert.IsFalse(p.Name.ToLowerInvariant().Contains("size"),
                        $"{method}: parameter '{p.Name}' must not be a size/text-size parameter (the layout is size-independent)");
                    Assert.IsFalse(p.ParameterType.Name.ToLowerInvariant().Contains("size"),
                        $"{method}: parameter type '{p.ParameterType.Name}' must not be a size-carrying type");
                }
            }
        }

        // =========================================================================================
        // T8b — structural: SymbolQuad is blittable (no managed field breaks the `unmanaged` constraint).
        // =========================================================================================
        [Test]
        public void SymbolQuad_IsBlittable()
        {
            AssertUnmanaged<SymbolQuad>();
        }

        private static void AssertUnmanaged<T>() where T : unmanaged
        {
        }

        // =========================================================================================
        // Options-construction safety net (see TextLayoutOptions class doc: this project's C# 9
        // LangVersion cannot use a hand-written parameterless struct constructor, C# 10+ only).
        // =========================================================================================
        [Test]
        public void Default_MatchesStyleSpecDefaults()
        {
            TextLayoutOptions options = TextLayoutOptions.Default;

            Assert.AreEqual(TextAnchor.Center, options.Anchor);
            Assert.AreEqual(0f, options.Offset.x);
            Assert.AreEqual(0f, options.Offset.y);
            Assert.AreEqual(0f, options.RadialOffset);
            Assert.AreEqual(TextJustify.Auto, options.Justify);
            Assert.AreEqual(10f, options.MaxWidthEm);
            Assert.AreEqual(1.2f, options.LineHeightEm);
            Assert.AreEqual(0f, options.LetterSpacingEm);
        }

        [Test]
        public void ZeroValuedOptions_FallsBackToSaneDefaults_ForMaxWidthAndLineHeight()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance));

            TextLayoutOptions zeroOptions = default;
            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in zeroOptions, resultQuads);

            Assert.AreEqual(1, resultQuads.Count);
            Assert.AreEqual(1, result.LineCount);
            float2 size = result.Max - result.Min;
            Assert.Greater(size.x, 0f, "a zero-valued MaxWidthEm must fall back, not collapse the layout");
            Assert.Greater(size.y, 0f, "a zero-valued LineHeightEm must fall back, not collapse the layout");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextRtlLayoutTests — RTL single-line correctness
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// RTL single-line correctness. Reuses the "marhaba" (Arabic for "hello")
    /// golden setup from <c>TextShapingTests</c> (same fixtures, same shaper) so this test operates on
    /// a real, independently-verified visual-order <see cref="ShapedRun"/> rather than a synthetic one.
    /// </summary>
    [TestFixture]
    public class TextRtlLayoutTests
    {
        private const float Tolerance = 1e-3f;

        // "marhaba" (Arabic for "hello") as explicit codepoint escapes -- avoids embedding a raw RTL
        // string in this (LTR) source file (mirrors TextShapingTests). meem (U+0645), reh (U+0631),
        // hah (U+062D), beh (U+0628), alef (U+0627), in that logical (typed) order.
        private const string Marhaba = "\u0645\u0631\u062D\u0628\u0627";

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private sealed class FixtureGlyphMetricsProvider : IGlyphMetricsProvider
        {
            private readonly Dictionary<uint, float> _advances;
            public FixtureGlyphMetricsProvider(IReadOnlyDictionary<uint, float> advances) => _advances = new Dictionary<uint, float>(advances);
            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                return _advances.TryGetValue(codepoint, out advance);
            }
        }

        /// <summary>Builds a real <see cref="GlyphAtlas"/> (and a matching advance-only metrics provider for shaping) from BOTH presentation-form fixture ranges.</summary>
        private static (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) BuildPresentationFormAtlas()
        {
            FontStackGlyphs presentationFormsA = GlyphPbfDecoder.Decode(LoadFixture("64256-64511.pbf.bytes")).Stacks[0];
            FontStackGlyphs presentationFormsB = GlyphPbfDecoder.Decode(LoadFixture("65024-65279.pbf.bytes")).Stacks[0];

            var atlas = new GlyphAtlas();
            var advances = new Dictionary<uint, float>();
            foreach (var kv in presentationFormsA.Glyphs) { atlas.Append(kv.Value, 0); advances[kv.Key] = kv.Value.Advance; }
            foreach (var kv in presentationFormsB.Glyphs) { atlas.Append(kv.Value, 0); advances[kv.Key] = kv.Value.Advance; }

            return (atlas, new FixtureGlyphMetricsProvider(advances));
        }

        private static ShapedRun ShapeMarhaba(IGlyphMetricsProvider metrics)
        {
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = Marhaba, FontStack = new FontStack { Names = new[] { "Noto Sans Regular" } }, Metrics = metrics };
            return shaper.Shape(in request);
        }

        // =========================================================================================
        // T7 — RTL single-line correctness: forced single line, correct total advance, first VISUAL
        // glyph at the left edge.
        // =========================================================================================
        [Test]
        public void Layout_Rtl_SingleLine_FirstVisualGlyphAtLeftEdge_AndTotalAdvanceMatches()
        {
            (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) = BuildPresentationFormAtlas();
            ShapedRun run = ShapeMarhaba(metrics);

            Assert.AreEqual(TextDirection.RightToLeft, run.Direction, "pure Arabic text must resolve to RTL (T1)");
            Assert.AreEqual(5, run.Glyphs.Count);
            // Sanity: reuse the shaping golden -- the first VISUAL glyph is ALEF (U+FE8E). If this ever
            // regresses it means shaping changed, not layout -- fail loudly here rather than silently.
            Assert.AreEqual(0xFE8Eu, run.Glyphs[0].AtlasCodepoint, "Golden: first visual glyph is ALEF final");

            // Anchor=Left (hAlign=0) -> no horizontal anchor shift, so quads land at their raw pen positions.
            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Left,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(1, result.LineCount, "RTL is forced single-line (decision 4 / fork 3)");
            Assert.AreEqual(5, resultQuads.Count, "no whitespace in \"marhaba\" -- one quad per glyph");

            // First visual glyph (ALEF) sits at the line's left edge: penX = 0.
            Assert.IsTrue(atlas.TryGetEntry(0, run.Glyphs[0].AtlasCodepoint, out GlyphAtlasEntry entryFirst));
            float expectedFirstMinX = 0f + entryFirst.Left - GlyphSdf.Buffer;
            Assert.AreEqual(expectedFirstMinX, resultQuads[0].TopLeft.x, Tolerance, "the first VISUAL glyph must sit at the line's left edge");

            // Pen accumulates the sum of ALL preceding glyphs' advances (in visual order) -- total
            // advance = sum of atlas entry Advances (letter-spacing = 0).
            float expectedTotalAdvance = 0f;
            foreach (PositionedGlyph g in run.Glyphs)
            {
                Assert.IsTrue(atlas.TryGetEntry(0, g.AtlasCodepoint, out GlyphAtlasEntry e));
                expectedTotalAdvance += e.Advance;
            }

            Assert.IsTrue(atlas.TryGetEntry(0, run.Glyphs[4].AtlasCodepoint, out GlyphAtlasEntry entryLast));
            float expectedLastMinX = (expectedTotalAdvance - entryLast.Advance) + entryLast.Left - GlyphSdf.Buffer;
            Assert.AreEqual(expectedLastMinX, resultQuads[4].TopLeft.x, Tolerance, "pen must accumulate every preceding glyph's advance, in visual order");
        }

        // =========================================================================================
        // Teeth: re-reversing the (already-visual-order) run would put MEEM (the LOGICALLY-first,
        // visually-LAST glyph) at the left edge instead of ALEF -- a different quad width, since the
        // two glyphs' cell sizes differ.
        // =========================================================================================
        [Test]
        public void Layout_Rtl_Teeth_DoesNotReReverseTheAlreadyVisualOrderRun()
        {
            (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) = BuildPresentationFormAtlas();
            ShapedRun run = ShapeMarhaba(metrics);

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Left,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };
            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.IsTrue(atlas.TryGetEntry(0, 0xFE8Eu, out GlyphAtlasEntry entryAlef)); // visual position 0
            Assert.IsTrue(atlas.TryGetEntry(0, 0xFEE3u, out GlyphAtlasEntry entryMeem)); // visual position 4
            Assert.AreNotEqual(entryAlef.CellSize.x, entryMeem.CellSize.x, "sanity: ALEF and MEEM cell widths must differ for this tooth to be decisive");

            float actualFirstWidth = resultQuads[0].BottomRight.x - resultQuads[0].TopLeft.x;
            Assert.AreEqual(entryAlef.CellSize.x, actualFirstWidth, Tolerance, "the first emitted quad must be ALEF's (visual position 0)");
            Assert.AreNotEqual(entryMeem.CellSize.x, actualFirstWidth, "a re-reversing implementation would put MEEM first instead of ALEF");
        }

        // =========================================================================================
        // Mis-anchoring tooth: RTL respects the same anchor math as LTR -- Left vs Center anchor
        // differ by exactly the standard -0.5*lineWidth delta (T2's formula), not some RTL-special path.
        // =========================================================================================
        [Test]
        public void Layout_Rtl_AnchorMathMatchesTheGeneralFormula()
        {
            (GlyphAtlas atlas, FixtureGlyphMetricsProvider metrics) = BuildPresentationFormAtlas();
            ShapedRun run = ShapeMarhaba(metrics);

            var leftOptions = new TextLayoutOptions { Anchor = TextAnchor.Left, Offset = float2.zero, RadialOffset = 0f, Justify = TextJustify.Auto, MaxWidthEm = 10f, LineHeightEm = 1.2f, LetterSpacingEm = 0f };
            var centerOptions = new TextLayoutOptions { Anchor = TextAnchor.Center, Offset = float2.zero, RadialOffset = 0f, Justify = TextJustify.Auto, MaxWidthEm = 10f, LineHeightEm = 1.2f, LetterSpacingEm = 0f };

            var leftQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in leftOptions, leftQuads);
            TextQuadLayout.Layout(run, atlas, in centerOptions, centerQuads);

            float lineWidth = 0f;
            foreach (PositionedGlyph g in run.Glyphs)
            {
                Assert.IsTrue(atlas.TryGetEntry(0, g.AtlasCodepoint, out GlyphAtlasEntry e));
                lineWidth += e.Advance;
            }

            for (int i = 0; i < leftQuads.Count; i++)
            {
                Assert.AreEqual(-0.5f * lineWidth, centerQuads[i].TopLeft.x - leftQuads[i].TopLeft.x, Tolerance, $"quad {i}: Center-Left anchor delta");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextShapingTests — the decisive Model-A/Option-Y shaping test
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE decisive Model-A/Option-Y shaping test. Shapes Arabic
    /// <c>"&#x0645;&#x0631;&#x062D;&#x0628;&#x0627;"</c> ("marhaba" / hello — meem, reh, hah, beh, alef)
    /// with <see cref="CodepointTextShaper"/> and asserts the exact ordered
    /// <c>(AtlasCodepoint, Cluster)</c> sequence against a golden pinned by an INDEPENDENT hand
    /// derivation from the public Unicode joining tables — NOT by calling
    /// <see cref="ArabicJoining"/>/<see cref="CodepointTextShaper"/> and copying their output (that
    /// would be tautological; see the "anti-tautology" note in the class body below).
    ///
    /// ── Golden derivation (letter-by-letter, from ArabicShaping.txt Joining_Type values) ──────────
    /// Logical (reading) order: index 0 = meem (first-typed), index 4 = alef (last-typed).
    ///   i=0 MEEM  (U+0645, Dual_Joining):   no previous char -> cannot receive a join.
    ///                                       next = REH (Right_Joining, CAN receive a join) -> sends one.
    ///                                       => INITIAL form -> U+FEE3.
    ///   i=1 REH   (U+0631, Right_Joining):  prev = MEEM (Dual_Joining, CAN send) -> receives one.
    ///                                       Right_Joining letters can NEVER send a join forward
    ///                                       (that is the defining property of the 6 "non-connecting"
    ///                                       Arabic letters: alef/dal/dhal/reh/zain/waw), regardless
    ///                                       of what follows.
    ///                                       => FINAL form -> U+FEAE. (The word visually breaks here —
    ///                                          reh never connects to the following hah.)
    ///   i=2 HAH   (U+062D, Dual_Joining):   prev = REH (Right_Joining -> CANNOT send) -> no join in.
    ///                                       next = BEH (Dual_Joining, CAN receive) -> sends one.
    ///                                       => INITIAL form -> U+FEA3. (Starts a new connected cluster.)
    ///   i=3 BEH   (U+0628, Dual_Joining):   prev = HAH (Dual_Joining, CAN send) -> receives one.
    ///                                       next = ALEF (Right_Joining, CAN receive) -> sends one.
    ///                                       => MEDIAL form -> U+FE92.
    ///   i=4 ALEF  (U+0627, Right_Joining):  prev = BEH (Dual_Joining, CAN send) -> receives one.
    ///                                       no next char -> cannot send (moot: alef never sends anyway).
    ///                                       => FINAL form -> U+FE8E.
    /// So the LOGICAL-order shaped sequence is: [FEE3(c0), FEAE(c1), FEA3(c2), FE92(c3), FE8E(c4)].
    /// Single-run RTL (decision 8) reverses this to VISUAL order:
    ///                                        [FE8E(c4), FE92(c3), FEA3(c2), FEAE(c1), FEE3(c0)].
    /// This matches the standard "Arabic Presentation Forms-B" block layout (each dual-joining letter's
    /// 4 forms are consecutive codepoints in isolated/final/initial/medial order; right-joining-only
    /// letters have only 2). Independently cross-checked: every one of these 5 codepoints was confirmed
    /// present (with a real, positive PBF <c>advance</c>) by decoding the actual committed
    /// <c>65024-65279.pbf.bytes</c> fixture before this test was written (not derived FROM the test).
    ///
    /// ── Anti-tautology ──────────────────────────────────────────────────────────────────────────────
    /// The golden array below is a literal, hand-written from the derivation above — it is never
    /// computed by calling any production shaping code. A codepoint-passthrough (no joining/bidi) run
    /// is asserted to diverge from it (teeth, below). A separate assert (the de-risk tooth) confirms
    /// every golden codepoint actually exists in the decoded presentation-form fixtures — a DIFFERENT
    /// failure mode than shape correctness (a wrong mapping could coincidentally land on an existing
    /// but WRONG codepoint, e.g. FEE1 "meem isolated" also exists in the fixture; only the hand
    /// derivation above guards against that).
    /// </summary>
    [TestFixture]
    public class TextShapingTests
    {
        // "marhaba" (Arabic for "hello") as explicit codepoint escapes -- avoids embedding a raw RTL
        // string in this (LTR) source file. meem (U+0645), reh (U+0631), hah (U+062D), beh (U+0628),
        // alef (U+0627), in that logical (typed) order.
        private const string Marhaba = "\u0645\u0631\u062D\u0628\u0627";

        // lam (U+0644) + alef (U+0627) -- exercises the mandatory lam-alef ligature (U+FEFB/FEFC)
        // separately from T1.
        private const string LamAlef = "\u0644\u0627";

        // The T1 golden: (AtlasCodepoint, Cluster) in VISUAL (post-bidi) order. See the class doc above
        // for the letter-by-letter derivation. Hand-written literal -- never computed from production code.
        private static readonly (uint codepoint, int cluster)[] MarhabaGoldenVisualOrder =
        {
            (0xFE8Eu, 4), // ALEF final
            (0xFE92u, 3), // BEH medial
            (0xFEA3u, 2), // HAH initial
            (0xFEAEu, 1), // REH final
            (0xFEE3u, 0), // MEEM initial
        };

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        /// <summary>
        /// T1's <see cref="IGlyphMetricsProvider"/>, backed by the UNION of both decoded presentation-
        /// form fixture ranges (64256-64511 + 65024-65279) — so advances come from real committed data,
        /// and a joining bug that lands on a codepoint absent from both ranges fails loudly (advance 0,
        /// caught by the "positive advance" assert) rather than silently.
        /// </summary>
        private sealed class FixtureGlyphMetricsProvider : IGlyphMetricsProvider
        {
            private readonly Dictionary<uint, float> _advances;

            public FixtureGlyphMetricsProvider(IReadOnlyDictionary<uint, float> advances)
            {
                _advances = new Dictionary<uint, float>(advances);
            }

            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0; // one synthetic face — this fixture never mixes fonts
                return _advances.TryGetValue(codepoint, out advance);
            }
        }

        private static (FixtureGlyphMetricsProvider metrics, HashSet<uint> allPresentationFormCodepoints) LoadPresentationFormFixtures()
        {
            FontStackGlyphs presentationFormsA = GlyphPbfDecoder.Decode(LoadFixture("64256-64511.pbf.bytes")).Stacks[0];
            FontStackGlyphs presentationFormsB = GlyphPbfDecoder.Decode(LoadFixture("65024-65279.pbf.bytes")).Stacks[0];

            var advances = new Dictionary<uint, float>();
            var codepoints = new HashSet<uint>();
            foreach (var kv in presentationFormsA.Glyphs) { advances[kv.Key] = kv.Value.Advance; codepoints.Add(kv.Key); }
            foreach (var kv in presentationFormsB.Glyphs) { advances[kv.Key] = kv.Value.Advance; codepoints.Add(kv.Key); }

            return (new FixtureGlyphMetricsProvider(advances), codepoints);
        }

        private static ShapedRun ShapeMarhaba(IGlyphMetricsProvider metrics)
        {
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest
            {
                Text = Marhaba,
                FontStack = new FontStack { Names = new[] { "Noto Sans Regular" } },
                Metrics = metrics,
            };
            return shaper.Shape(in request);
        }

        // =========================================================================================
        // T1 — THE decisive test: exact golden (AtlasCodepoint, Cluster) sequence in visual order.
        // =========================================================================================
        [Test]
        public void Shape_Marhaba_ProducesGoldenVisualOrderRun()
        {
            var (metrics, _) = LoadPresentationFormFixtures();
            ShapedRun run = ShapeMarhaba(metrics);

            Assert.AreEqual(TextDirection.RightToLeft, run.Direction, "pure Arabic text must resolve to RTL");
            Assert.AreEqual(MarhabaGoldenVisualOrder.Length, run.Glyphs.Count);

            for (int i = 0; i < MarhabaGoldenVisualOrder.Length; i++)
            {
                PositionedGlyph glyph = run.Glyphs[i];
                (uint codepoint, int cluster) expected = MarhabaGoldenVisualOrder[i];
                Assert.AreEqual(expected.codepoint, glyph.AtlasCodepoint,
                    $"visual position {i}: expected atlas codepoint U+{expected.codepoint:X4}, got U+{glyph.AtlasCodepoint:X4}");
                Assert.AreEqual(expected.cluster, glyph.Cluster, $"visual position {i}: cluster mismatch");
                Assert.Greater(glyph.XAdvance, 0f, $"visual position {i}: advance must be present and positive (real fixture data)");
            }
        }

        // =========================================================================================
        // Teeth: a codepoint-passthrough shortcut (no joining/bidi — nominal forms, logical order)
        // yields a DIFFERENT codepoint sequence AND order than the golden.
        // =========================================================================================
        [Test]
        public void Shape_Teeth_NominalLogicalOrderPassthroughDivergesFromGolden()
        {
            // The naive "codepoint == glyph id" shortcut MapLibre GL JS itself uses for non-complex
            // scripts: base codepoints, unjoined, in logical (typed) order -- no joining, no bidi.
            (uint codepoint, int cluster)[] passthrough =
            {
                (0x0645u, 0), // meem, nominal/base form
                (0x0631u, 1), // reh
                (0x062Du, 2), // hah
                (0x0628u, 3), // beh
                (0x0627u, 4), // alef
            };

            Assert.AreEqual(MarhabaGoldenVisualOrder.Length, passthrough.Length,
                "sanity: same glyph count -- divergence must be in codepoints/order, not count");

            bool anyDivergence = false;
            for (int i = 0; i < MarhabaGoldenVisualOrder.Length; i++)
            {
                if (passthrough[i].codepoint != MarhabaGoldenVisualOrder[i].codepoint
                    || passthrough[i].cluster != MarhabaGoldenVisualOrder[i].cluster)
                {
                    anyDivergence = true;
                    break;
                }
            }
            Assert.IsTrue(anyDivergence,
                "a codepoint-passthrough (no joining, no bidi) sequence must diverge from the golden " +
                "element-by-element -- if it didn't, the golden would be tautologically trivial");

            // The actual shaper must NOT equal the naive passthrough (this is what makes T1 decisive).
            var (metrics, _) = LoadPresentationFormFixtures();
            ShapedRun run = ShapeMarhaba(metrics);
            bool actualMatchesPassthrough = true;
            for (int i = 0; i < passthrough.Length; i++)
            {
                if (run.Glyphs[i].AtlasCodepoint != passthrough[i].codepoint || run.Glyphs[i].Cluster != passthrough[i].cluster)
                {
                    actualMatchesPassthrough = false;
                    break;
                }
            }
            Assert.IsFalse(actualMatchesPassthrough, "the real shaper output must NOT equal the naive passthrough");
        }

        // =========================================================================================
        // De-risk tooth: every shaped presentation-form AtlasCodepoint must actually exist in
        // the decoded committed presentation-form fixtures (64256-64511 UNION 65024-65279). Catches a
        // wrong joining mapping landing on a non-existent atlas cell -- a DIFFERENT failure mode than
        // T1's shape-correctness assert (see the class-doc anti-tautology note).
        // =========================================================================================
        [Test]
        public void Shape_Marhaba_AllAtlasCodepointsExistInPresentationFormFixtures()
        {
            var (metrics, presentCodepoints) = LoadPresentationFormFixtures();
            ShapedRun run = ShapeMarhaba(metrics);

            Assert.Greater(presentCodepoints.Count, 0, "fixture precondition: presentation-form fixtures must decode glyphs");
            foreach (PositionedGlyph glyph in run.Glyphs)
            {
                Assert.IsTrue(presentCodepoints.Contains(glyph.AtlasCodepoint),
                    $"shaped atlas codepoint U+{glyph.AtlasCodepoint:X4} does not exist in the decoded " +
                    "presentation-form fixtures (64256-64511 ∪ 65024-65279) -- the joining mapping is wrong");
            }
        }

        // =========================================================================================
        // Supplementary (non-T1): mandatory lam-alef ligature. "لا" (LAM+ALEF, no preceding letter)
        // must collapse to the single isolated ligature glyph U+FEFB, cluster 0 (the LAM's offset).
        // Independently confirmed present in the committed 65024-65279 fixture (see the class-doc
        // note) -- NOT independently golden-pinned like T1 (no external tool cross-check), so this is
        // a lower-confidence supplementary tooth, not a second decisive test.
        // =========================================================================================
        [Test]
        public void Shape_LamAlef_CollapsesToSingleIsolatedLigatureGlyph()
        {
            var (metrics, presentCodepoints) = LoadPresentationFormFixtures();
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = LamAlef, FontStack = null, Metrics = metrics };

            ShapedRun run = shaper.Shape(in request);

            Assert.AreEqual(1, run.Glyphs.Count, "LAM+ALEF must collapse into exactly one glyph");
            PositionedGlyph glyph = run.Glyphs[0];
            Assert.AreEqual(0xFEFBu, glyph.AtlasCodepoint, "isolated lam-alef ligature (nothing precedes it)");
            Assert.AreEqual(0, glyph.Cluster, "ligature reports the LAM's (first) source char offset");
            Assert.IsTrue(presentCodepoints.Contains(glyph.AtlasCodepoint), "ligature codepoint must exist in the fixtures");
        }

        // =========================================================================================
        // Decision 8: mixed strong-direction (RTL + LTR) input is a documented throw, not silently
        // wrong output -- full UAX #9 bidi is a deferred follow-up.
        // =========================================================================================
        [Test]
        public void Shape_MixedDirectionText_Throws()
        {
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest
            {
                Text = "abc" + Marhaba, // Latin + Arabic in the same run
                FontStack = null,
                Metrics = new FixtureGlyphMetricsProvider(new Dictionary<uint, float>()),
            };

            Assert.Throws<NotSupportedException>(() => shaper.Shape(in request));
        }

        // =========================================================================================
        // Sanity: pure Latin text takes the LTR passthrough path -- logical order == visual order,
        // codepoints unchanged (no Arabic joining applied to non-Arabic text).
        // =========================================================================================
        [Test]
        public void Shape_LatinText_PassesThroughInLogicalOrder()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            var advances = new Dictionary<uint, float>();
            foreach (var kv in latin.Glyphs) advances[kv.Key] = kv.Value.Advance;
            var metrics = new FixtureGlyphMetricsProvider(advances);

            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = "Ab", FontStack = null, Metrics = metrics };
            ShapedRun run = shaper.Shape(in request);

            Assert.AreEqual(TextDirection.LeftToRight, run.Direction);
            Assert.AreEqual(2, run.Glyphs.Count);
            Assert.AreEqual((uint)'A', run.Glyphs[0].AtlasCodepoint);
            Assert.AreEqual(0, run.Glyphs[0].Cluster);
            Assert.AreEqual((uint)'b', run.Glyphs[1].AtlasCodepoint);
            Assert.AreEqual(1, run.Glyphs[1].Cluster);
            Assert.Greater(run.Glyphs[0].XAdvance, 0f);
            Assert.Greater(run.Glyphs[1].XAdvance, 0f);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextVerticalCentringTests — a centred text block is centred on its ink, not a line box
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// docs/road-shields-design.md — a centred text block is centred on its INK,
    /// not on a line box. Teeth V1-V7 match the design's table of the same numbers, plus V0 (a
    /// precondition on this file's own ink-band helper) and V9 (the fixture-vs-constant guard). V8 is
    /// not here — it is the "offset/justify/RTL tests still pass verbatim" guard, in those files. Keep
    /// the numbering in sync with the doc.
    /// </summary>
    [TestFixture]
    public class TextVerticalCentringTests
    {
        private const float Tolerance = 1e-3f;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(TextDirection direction, params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = direction };
        }

        private static TextLayoutOptions MakeOptions(TextAnchor anchor = TextAnchor.Center, float lineHeightEm = 1.2f, float maxWidthEm = 10f)
            => new TextLayoutOptions
            {
                Anchor = anchor,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = maxWidthEm,
                LineHeightEm = lineHeightEm,
                LetterSpacingEm = 0f,
            };

        private static void AssertAllQuadsEqual(IReadOnlyList<SymbolQuad> a, IReadOnlyList<SymbolQuad> b, string label)
        {
            Assert.AreEqual(a.Count, b.Count, $"{label}: quad counts must match");
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].TopLeft.x, b[i].TopLeft.x, Tolerance, $"{label}: quad {i} TopLeft.x");
                Assert.AreEqual(a[i].TopLeft.y, b[i].TopLeft.y, Tolerance, $"{label}: quad {i} TopLeft.y");
                Assert.AreEqual(a[i].BottomRight.x, b[i].BottomRight.x, Tolerance, $"{label}: quad {i} BottomRight.x");
                Assert.AreEqual(a[i].BottomRight.y, b[i].BottomRight.y, Tolerance, $"{label}: quad {i} BottomRight.y");
            }
        }

        // The INK band of a laid-out block: the quads' cell band inset by GlyphSdf.Buffer on both edges
        // (PlaceGlyph pads symmetrically, so the cell centre IS the ink centre — asserted by V0 below).
        private static (float min, float max) InkBandY(IReadOnlyList<SymbolQuad> quads)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (SymbolQuad q in quads)
            {
                min = math.min(min, q.BottomRight.y + GlyphSdf.Buffer);
                max = math.max(max, q.TopLeft.y - GlyphSdf.Buffer);
            }
            return (min, max);
        }

        // =========================================================================================
        // V0 — precondition: InkBandY really measures ink (cell inset by Buffer), not the padded cell.
        // =========================================================================================
        [Test]
        public void InkBand_IsCellBandInsetByBuffer_Precondition()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            // Top anchor => globalY = 0, so quad y is baselineY-relative with no anchor shift --
            // isolates the ink-band computation from the anchor math this stage changes.
            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top), resultQuads);
            (float min, float max) = InkBandY(resultQuads);

            int bareHeight = entryFive.CellSize.y - 2 * GlyphSdf.Buffer;
            Assert.AreEqual(entryFive.Top, max, Tolerance, "ink top must equal the atlas entry's own Top metric");
            Assert.AreEqual(entryFive.Top - bareHeight, min, Tolerance, "ink bottom must equal Top - bare glyph height");
        }

        // =========================================================================================
        // V1 — the shield defect itself: a centred digit's ink must be centred on the anchor. RED
        // today at -3.1. The ±0.5 window is the design's acceptance band, wider than the actual
        // residual: with the cap height at 17/24 em the '5' lands at EXACTLY 0 (its ink spans
        // [-8.5, +8.5] about the anchor), because its 17 px bare height is the very cap the constant
        // measures. The band is there for glyphs whose ink is not exactly cap-high, not for slack here.
        // =========================================================================================
        [Test]
        public void CentreAnchor_SingleLineDigit_InkIsCentredOnTheAnchor()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, resultQuads);
            (float min, float max) = InkBandY(resultQuads);
            float centre = 0.5f * (min + max);

            Assert.LessOrEqual(math.abs(centre), 0.5f, $"centred ink must sit within 0.5 baked px of the anchor, was {centre}");
        }

        // =========================================================================================
        // V2 — the shield claim itself: text and icon centring must agree.
        // =========================================================================================
        [Test]
        public void CentreAnchor_TextInkCentre_CoincidesWithCentredIconInkCentre()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var textResultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in TextLayoutOptions.Default, textResultQuads);
            (float textMin, float textMax) = InkBandY(textResultQuads);
            float textCentre = 0.5f * (textMin + textMax);

            // A synthetic even-sized sprite: IconQuadLayout centres box == ink exactly (design doc).
            var spriteEntry = new SpriteEntry { X = 0, Y = 0, Width = 20, Height = 20, PixelRatio = 1f, Sdf = false };
            SymbolQuad iconQuad = IconQuadLayout.Layout(in spriteEntry, new int2(64, 64), 1f, TextAnchor.Center, float2.zero);
            float iconCentre = 0.5f * (iconQuad.TopLeft.y + iconQuad.BottomRight.y);

            Assert.AreEqual(iconCentre, textCentre, 0.5f,
                $"a centred digit's text ink centre must coincide with a centred icon's ink centre (icon={iconCentre}, text={textCentre})");

            // …and the padded-repack border must not disturb that: SpriteSheet hands every drawable sprite a
            // one-texel transparent border, IconQuadLayout draws it, and the growth must be SYMMETRIC — an
            // asymmetric skirt would shift a shield's icon off the text it is centred behind.
            var paddedEntry = new SpriteEntry { X = 1, Y = 1, Width = 20, Height = 20, PixelRatio = 1f, Padding = 1 };
            SymbolQuad paddedQuad = IconQuadLayout.Layout(in paddedEntry, new int2(64, 64), 1f, TextAnchor.Center, float2.zero);
            float paddedCentre = 0.5f * (paddedQuad.TopLeft.y + paddedQuad.BottomRight.y);
            float paddedHeight = paddedQuad.TopLeft.y - paddedQuad.BottomRight.y;

            Assert.AreEqual(iconCentre, paddedCentre, 1e-5f,
                "the transparent border must grow the quad symmetrically — the content's centre must not move");
            Assert.AreEqual(20f + 2f, paddedHeight, 1e-5f,
                "the drawn quad must be the content plus one texel of border on EACH side");
        }

        // =========================================================================================
        // V3 — line-height independence: a single line has nothing to stack, so a centred single-line
        // block must not move when text-line-height changes. RED today (9.6 px) and RED against any
        // "subtract a constant from the old formula" impl.
        // =========================================================================================
        [Test]
        public void CentreAnchor_SingleLine_IsIndependentOfLineHeight()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var tightQuads = new List<SymbolQuad>();
            var looseQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(lineHeightEm: 1.2f), tightQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(lineHeightEm: 2.0f), looseQuads);

            AssertAllQuadsEqual(tightQuads, looseQuads, "a single-line Center block must not move when line-height changes");
        }

        // =========================================================================================
        // V4 — multi-line: a 2-line centred block's line-0 and line-1 ink centres are symmetric about
        // the anchor, and their separation is exactly one lineHeightPx (line spacing is untouched).
        // =========================================================================================
        [Test]
        public void CentreAnchor_TwoLines_AreSymmetricAboutTheAnchor()
        {
            // The SAME glyph on both lines: symmetry is a property of the block's formula, not of
            // each line's own ink shape -- putting a cap ('A') on one line and an x-height glyph ('a')
            // on the other would compare two DIFFERENT ink profiles and mask the property under test
            // (their real ink centres differ by ~2px even under a perfectly symmetric formula).
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight,
                ((uint)'5', entryFive.Advance), ((uint)' ', entrySpace.Advance), ((uint)'5', entryFive.Advance));

            // maxWidthPx strictly between "5" alone and "5 5" combined -- forces exactly one break.
            float maxWidthPx = entryFive.Advance + entrySpace.Advance * 0.5f;
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;
            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, MakeOptions(maxWidthEm: maxWidthEm), resultQuads);
            Assert.AreEqual(2, result.LineCount, "the chosen max-width must force exactly a 2-line wrap");
            Assert.AreEqual(2, resultQuads.Count, "one glyph per line -- '5' on line 0, '5' on line 1");
            Assert.AreEqual(0, resultQuads[0].LineIndex);
            Assert.AreEqual(1, resultQuads[1].LineIndex);

            (float min0, float max0) = InkBandY(new[] { resultQuads[0] });
            (float min1, float max1) = InkBandY(new[] { resultQuads[1] });
            float centre0 = 0.5f * (min0 + max0);
            float centre1 = 0.5f * (min1 + max1);
            float mean = 0.5f * (centre0 + centre1);

            Assert.LessOrEqual(math.abs(mean), 0.5f, $"the two lines' ink centres must be symmetric about the anchor, mean was {mean}");
            Assert.AreEqual(lineHeightPx, centre0 - centre1, Tolerance, "the two lines' ink centres must be separated by exactly one lineHeightPx");
        }

        // =========================================================================================
        // V5 — guard: Top/Bottom (and the four corners) keep their block-edge behaviour, hand-computed
        // from atlas entries, no production constants read back.
        // =========================================================================================
        [Test]
        public void TopAndBottomAnchors_KeepTheirBlockEdges_Golden()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'A', entryA.Advance));

            float lineHeightPx = 1.2f * TextQuadLayout.OneEm;

            var topQuads = new List<SymbolQuad>();
            var bottomQuads = new List<SymbolQuad>();
            var topLeftQuads = new List<SymbolQuad>();
            var topRightQuads = new List<SymbolQuad>();
            var bottomLeftQuads = new List<SymbolQuad>();
            var bottomRightQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Top), topQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Bottom), bottomQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopLeft), topLeftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.TopRight), topRightQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.BottomLeft), bottomLeftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.BottomRight), bottomRightQuads);

            // Top anchor => globalY = 0, so the cell's top edge sits at entry.Top + Buffer.
            float expectedTopCellTopY = entryA.Top + GlyphSdf.Buffer;
            Assert.AreEqual(expectedTopCellTopY, topQuads[0].TopLeft.y, Tolerance, "Top anchor: block top edge at y=0");

            // Bottom anchor => globalY = lineCount*lineHeightPx = lineHeightPx (lineCount==1), added to the
            // pre-shift cell edge (entry.Top + Buffer - CellSize.y).
            float expectedBottomCellBottomY = lineHeightPx + (entryA.Top + GlyphSdf.Buffer - entryA.CellSize.y);
            Assert.AreEqual(expectedBottomCellBottomY, bottomQuads[0].BottomRight.y, Tolerance, "Bottom anchor: block bottom edge at y = lineHeightPx");

            Assert.AreEqual(topQuads[0].TopLeft.y, topLeftQuads[0].TopLeft.y, Tolerance, "TopLeft shares Top's y");
            Assert.AreEqual(topQuads[0].TopLeft.y, topRightQuads[0].TopLeft.y, Tolerance, "TopRight shares Top's y");
            Assert.AreEqual(bottomQuads[0].TopLeft.y, bottomLeftQuads[0].TopLeft.y, Tolerance, "BottomLeft shares Bottom's y");
            Assert.AreEqual(bottomQuads[0].TopLeft.y, bottomRightQuads[0].TopLeft.y, Tolerance, "BottomRight shares Bottom's y");
        }

        // =========================================================================================
        // V6 — guard: the centred block's position must not depend on which glyphs are actually
        // present (kills the rejected ink-bounds-of-the-run metric).
        // =========================================================================================
        [Test]
        public void CentreAnchor_BlockPosition_DoesNotDependOnWhichGlyphsArePresent()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            GlyphAtlasEntry entryZero = atlas.Append(latin.Glyphs[(uint)'0'], 0);
            GlyphAtlasEntry entryG = atlas.Append(latin.Glyphs[(uint)'g'], 0);
            GlyphAtlasEntry entryX = atlas.Append(latin.Glyphs[(uint)'x'], 0);

            ShapedRun runFive = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));
            ShapedRun runFiveZero = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance), ((uint)'0', entryZero.Advance));
            ShapedRun runFiveG = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance), ((uint)'g', entryG.Advance));
            ShapedRun runFiveX = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance), ((uint)'x', entryX.Advance));

            var fiveQuads = new List<SymbolQuad>();
            var fiveZeroQuads = new List<SymbolQuad>();
            var fiveGQuads = new List<SymbolQuad>();
            var fiveXQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(runFive, atlas, in TextLayoutOptions.Default, fiveQuads);
            TextQuadLayout.Layout(runFiveZero, atlas, in TextLayoutOptions.Default, fiveZeroQuads);
            TextQuadLayout.Layout(runFiveG, atlas, in TextLayoutOptions.Default, fiveGQuads);
            TextQuadLayout.Layout(runFiveX, atlas, in TextLayoutOptions.Default, fiveXQuads);
            float yFive = fiveQuads[0].TopLeft.y;
            float yFiveZero = fiveZeroQuads[0].TopLeft.y;
            float yFiveG = fiveGQuads[0].TopLeft.y;
            float yFiveX = fiveXQuads[0].TopLeft.y;

            Assert.AreEqual(yFive, yFiveZero, Tolerance, "'5' must be at the same y in \"5\" and \"50\"");
            Assert.AreEqual(yFive, yFiveG, Tolerance, "'5' must be at the same y in \"5\" and \"5g\" (descender)");
            Assert.AreEqual(yFive, yFiveX, Tolerance, "'5' must be at the same y in \"5\" and \"5x\" (x-height)");
        }

        // =========================================================================================
        // V7 — Left and Right (the other vertical-centre-carrying anchors) must shift vertically
        // exactly like Center; only x differs.
        // =========================================================================================
        [Test]
        public void LeftAndRightAnchors_ShiftVerticallyExactlyLikeCentre()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);
            ShapedRun run = MakeRun(TextDirection.LeftToRight, ((uint)'5', entryFive.Advance));

            var leftQuads = new List<SymbolQuad>();
            var rightQuads = new List<SymbolQuad>();
            var centerQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Left), leftQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Right), rightQuads);
            TextQuadLayout.Layout(run, atlas, MakeOptions(anchor: TextAnchor.Center), centerQuads);

            Assert.AreEqual(centerQuads[0].TopLeft.y, leftQuads[0].TopLeft.y, Tolerance, "Left must shift vertically exactly like Center");
            Assert.AreEqual(centerQuads[0].TopLeft.y, rightQuads[0].TopLeft.y, Tolerance, "Right must shift vertically exactly like Center");
            Assert.AreNotEqual(leftQuads[0].TopLeft.x, centerQuads[0].TopLeft.x, "sanity: x must actually differ between Left and Center");

            (float min, float max) = InkBandY(leftQuads);
            float centre = 0.5f * (min + max);
            Assert.LessOrEqual(math.abs(centre), 0.5f, $"Left-anchored ink must also be centred on the anchor, was {centre}");
        }

        // =========================================================================================
        // V9 — the constant is pinned to the FIXTURE, not just to itself. GlyphSdf.BaselineBelowReferencePx
        // is an assumption about how the glyph PBF was baked (the PBF carries no font-level metrics), so
        // nothing in the layout math can detect it going wrong: every other tooth here measures positions
        // that are all derived from the same constant, and would stay green if the fixture were regenerated
        // from a font baked against a different ascent. The other shipped ranges modally measure 27,
        // so this is a live hazard, and its symptom is silent — every centred symbol would sit one baked px
        // low with a fully green suite. This re-derives the constant from a baseline-resting reference
        // glyph's OWN metrics: for such a glyph the ink bottom IS the baseline, so its distance below the
        // line's reference origin is (bare height - Top). A digit is used deliberately — a descender ('g')
        // or an above-baseline mark does not rest on the baseline and measures 30/32 or 19/23 instead.
        // =========================================================================================
        [Test]
        public void BaselineBelowReferencePx_IsReproducedByTheFixturesOwnGlyphMetrics()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5'], 0);

            int bareHeight = entryFive.CellSize.y - 2 * GlyphSdf.Buffer;
            float baselineBelowReference = bareHeight - entryFive.Top;

            Assert.AreEqual(GlyphSdf.BaselineBelowReferencePx, baselineBelowReference, Tolerance,
                $"the committed fixture's baseline-resting '5' must reproduce GlyphSdf.BaselineBelowReferencePx " +
                $"({GlyphSdf.BaselineBelowReferencePx}); measured {baselineBelowReference} from bare height " +
                $"{bareHeight} and Top {entryFive.Top}. A fixture baked against a different ascent breaks the " +
                $"constant's premise and drops every centred label by the difference.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextWrapTests — greedy word-wrap golden line assignment
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T5 (greedy word-wrap, golden line assignment) and T6 (whitespace advances the
    /// pen but emits no quad).
    /// </summary>
    [TestFixture]
    public class TextWrapTests
    {
        private const float Tolerance = 1e-3f;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static FontStackGlyphs DecodeLatin() => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

        private static ShapedRun MakeRun(params (uint codepoint, float advance)[] glyphs)
        {
            var list = new List<PositionedGlyph>(glyphs.Length);
            for (int i = 0; i < glyphs.Length; i++)
            {
                list.Add(new PositionedGlyph { AtlasCodepoint = glyphs[i].codepoint, XAdvance = glyphs[i].advance, Cluster = i });
            }
            return new ShapedRun { Glyphs = list, Direction = TextDirection.LeftToRight };
        }

        // =========================================================================================
        // T5 — greedy word-wrap breaks at the correct WORD boundary (never mid-word), golden line
        // assignment. Three 2-glyph words ("Aa" x3) separated by single spaces: the chosen max-width
        // is derived generically (2.5*wordWidth + 1.5*spaceWidth) so it always lands strictly between
        // "word1 space word2" (fits) and adding "space word3" (overflows) -- independent of the exact
        // fixture metrics.
        // =========================================================================================
        [Test]
        public void Layout_GreedyWrap_BreaksAtCorrectWordBoundary()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);

            ShapedRun run = MakeRun(
                ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance),   // word1: glyphs 0,1
                ((uint)' ', entrySpace.Advance),                                // ws:    glyph  2
                ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance),   // word2: glyphs 3,4
                ((uint)' ', entrySpace.Advance),                                // ws:    glyph  5
                ((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance)    // word3: glyphs 6,7
            );

            float wordWidth = entryA.Advance + entryLowerA.Advance;
            float spaceWidth = entrySpace.Advance;
            float maxWidthPx = 2.5f * wordWidth + 1.5f * spaceWidth;
            float maxWidthEm = maxWidthPx / TextQuadLayout.OneEm;

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = maxWidthEm,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(2, result.LineCount, "word1+space+word2 fits; adding space+word3 must overflow to a new line");
            Assert.AreEqual(6, resultQuads.Count, "8 glyphs minus the 2 whitespace glyphs (never quaded) = 6");

            // Golden line map: word1+word2 (4 quads) on line 0, word3 (2 quads) on line 1.
            int[] expectedLineIndices = { 0, 0, 0, 0, 1, 1 };
            for (int i = 0; i < expectedLineIndices.Length; i++)
            {
                Assert.AreEqual(expectedLineIndices[i], resultQuads[i].LineIndex, $"quad {i} LineIndex");
            }

            // Teeth: a no-wrap impl would put everything on line 0.
            Assert.IsTrue(resultQuads[4].LineIndex != resultQuads[0].LineIndex, "a no-wrap impl fails: word3 must be on a different line than word1");

            // Teeth: a break-mid-word impl would split word2's 'A'/'a' across two different LineIndex
            // values (they must both be on line 0, adjacent to word1's glyphs, not word3's line).
            Assert.AreEqual(resultQuads[2].LineIndex, resultQuads[3].LineIndex, "word2's two glyphs must share the same line (no mid-word break)");
            Assert.AreNotEqual(resultQuads[3].LineIndex, resultQuads[4].LineIndex, "word2 and word3 must be on different lines");
        }

        [Test]
        public void Layout_GreedyWrap_NeverBreaksBeforeTheFirstWordOnALine()
        {
            // A single word wider than max-width must still be placed whole on line 0 (plan (e): "first
            // word always on line 0" -- generalizes to "never break before a line's first word").
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);
            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)'a', entryLowerA.Advance));

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 0.01f, // absurdly narrow -- narrower than even a single glyph
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextLayoutBounds result = TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(1, result.LineCount, "a single word (no interior whitespace) can never be split -- one line regardless of max-width");
            Assert.AreEqual(2, resultQuads.Count);
            Assert.AreEqual(0, resultQuads[0].LineIndex);
            Assert.AreEqual(0, resultQuads[1].LineIndex);
        }

        // =========================================================================================
        // T6 — whitespace advances the pen but emits no quad; the following glyph is positioned as
        // if the space consumed its full advance.
        // =========================================================================================
        [Test]
        public void Layout_Whitespace_AdvancesPenButEmitsNoQuad()
        {
            FontStackGlyphs latin = DecodeLatin();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A'], 0);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' '], 0);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a'], 0);

            ShapedRun run = MakeRun(((uint)'A', entryA.Advance), ((uint)' ', entrySpace.Advance), ((uint)'a', entryLowerA.Advance));

            // Anchor=TopLeft + Justify=Left (single line, so justify is moot anyway) -> quads land at
            // their raw (unshifted) coordinates, so the golden below needs no anchor-shift term.
            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.TopLeft,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Left,
                MaxWidthEm = 10f,
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };

            var resultQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, in options, resultQuads);

            Assert.AreEqual(2, resultQuads.Count, "3 glyphs minus the 1 whitespace glyph (never quaded) = 2 (N-1)");

            float penAfterSpace = entryA.Advance + entrySpace.Advance; // pen consumes the space's FULL advance
            float expectedLowerAMinX = penAfterSpace + entryLowerA.Left - GlyphSdf.Buffer;

            Assert.AreEqual(expectedLowerAMinX, resultQuads[1].TopLeft.x, Tolerance,
                "the glyph after the space must be positioned as if the space consumed its full advance");

            // Teeth: an impl that drops the space's advance would place 'a' as if right after 'A' alone.
            float wrongMinXIfSpaceDropped = entryA.Advance + entryLowerA.Left - GlyphSdf.Buffer;
            Assert.AreNotEqual(wrongMinXIfSpaceDropped, resultQuads[1].TopLeft.x, "dropping the space's advance would mis-position the following glyph");
        }
    }
}
