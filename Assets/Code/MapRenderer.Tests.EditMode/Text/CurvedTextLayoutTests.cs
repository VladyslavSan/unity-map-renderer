// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
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
    /// #5 B1 — CurvedTextLayout maps a shaped single-line run to per-glyph (arc-center, baseline-centered
    /// cell), the build-time half of curved along-line text. Expectations are recomputed from the atlas
    /// entries, then pinned.
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
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun((uint)'A', (uint)'a');

            IReadOnlyList<CurvedGlyph> glyphs = CurvedTextLayout.Layout(run, atlas);
            Assert.AreEqual(2, glyphs.Count, "both glyphs are visible");

            // ArcCenter = cumulative advance to each glyph's own advance-midpoint.
            float centerA = entryA.Advance * 0.5f;
            float centerLowerA = entryA.Advance + entryLowerA.Advance * 0.5f;
            Assert.AreEqual(centerA, glyphs[0].ArcCenter, 1e-4f);
            Assert.AreEqual(centerLowerA, glyphs[1].ArcCenter, 1e-4f);

            // Glyph 0's cell: same TOP-referenced box as TextQuadLayout, shifted -arcCenter in x, y baseline-relative.
            SymbolQuad cell0 = glyphs[0].Cell;
            Assert.AreEqual(entryA.Left - GlyphSdf.Buffer - centerA, cell0.TopLeft.x, 1e-4f, "cell x is centered on the arc-center");
            Assert.AreEqual(entryA.Top + GlyphSdf.Buffer, cell0.TopLeft.y, 1e-4f, "cell y stays baseline-relative (NOT vertically centered)");
            Assert.AreEqual(entryA.Left - GlyphSdf.Buffer + entryA.CellSize.x - centerA, cell0.BottomRight.x, 1e-4f);
            Assert.AreEqual(entryA.Top + GlyphSdf.Buffer - entryA.CellSize.y, cell0.BottomRight.y, 1e-4f);

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
            GlyphAtlasEntry entryA = atlas.Append(latin.Glyphs[(uint)'A']);
            GlyphAtlasEntry entrySpace = atlas.Append(latin.Glyphs[(uint)' ']);
            GlyphAtlasEntry entryLowerA = atlas.Append(latin.Glyphs[(uint)'a']);
            ShapedRun run = MakeRun((uint)'A', (uint)' ', (uint)'a');

            IReadOnlyList<CurvedGlyph> glyphs = CurvedTextLayout.Layout(run, atlas);
            Assert.AreEqual(2, glyphs.Count, "the space emits no CurvedGlyph");

            // The second visible glyph's arc-center includes the (skipped) space's advance.
            float expected = entryA.Advance + entrySpace.Advance + entryLowerA.Advance * 0.5f;
            Assert.AreEqual(expected, glyphs[1].ArcCenter, 1e-4f, "arc still advances through whitespace");
        }
    }
}
