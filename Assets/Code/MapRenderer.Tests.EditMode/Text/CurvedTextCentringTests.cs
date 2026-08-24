// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// W4 — a curved (along-line) text cell is centred VERTICALLY on the path, by the same optical
    /// (cap-band) metric a centred point symbol uses (<c>docs/road-shields-design.md</c> §11 D12). Before
    /// W4 the curved producer left every cell baseline-relative, so a road symbol rendered a fixed offset
    /// above/below the road it was drawn along — at every tilt, tilt 0 included.
    ///
    /// <para><b>Oracle hygiene.</b> No tooth here takes <c>TextQuadLayout.OpticalCentreBelowReferencePx</c>
    /// as its expected value — that is the code under test, and an oracle derived from it is vacuous.
    /// T1 measures the ink band the committed fixture's own glyph metrics predict, T2 measures a
    /// per-glyph-vs-per-symbol distinction that needs no magnitude at all, and T3 uses the POINT path
    /// (fixed by a different stage, pinned by <c>TextVerticalCentringTests</c>, untouched by W4) as an
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
        // W4-T1 — the headline invariant, in cell coordinates: a baseline-resting CAP glyph's ink band
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
            GlyphAtlasEntry entryFive = atlas.Append(latin.Glyphs[(uint)'5']);

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
        // W4-T2 — the shift is ONE CONSTANT PER LABEL, not per glyph. A run's internal typography must
        // survive intact: 'A', 'g' and '5' keep their relative offsets, descenders still descend.
        //
        // What this tooth does and does NOT observe, stated honestly: the residual is constant under BOTH
        // the pre-W4 code (residual == Buffer) and the correct fix (residual == Buffer + the shift), so
        // T2 cannot see MAGNITUDE — W4-T1 does that. What T2 alone catches is the REJECTED per-glyph
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
            for (int i = 0; i < codepoints.Length; i++) entries[i] = atlas.Append(latin.Glyphs[codepoints[i]]);

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
        // W4-T3 — cross-check against the POINT path, which already centres correctly and is untouched by
        // this stage (fixed by §11 D12, pinned by TextVerticalCentringTests). Same glyph, same atlas, two
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
            atlas.Append(latin.Glyphs[(uint)'5']);
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
}
