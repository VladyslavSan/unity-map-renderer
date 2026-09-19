// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S105 Slice 3b — the FIXED-size glyph atlas (a big pre-allocated atlas whose <c>Size</c> never
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
        // Stage M: a glyph that no longer fits the CURRENT page now opens a NEW page instead of being
        // dropped — this is the multi-page capacity behaviour M-T2 pins end-to-end (layout/vertex/render);
        // this test is the atlas-level slice of it.
        // =========================================================================================
        [Test]
        public void FixedAtlas_PageOverflow_OpensNewPage_InsteadOfDropping()
        {
            // 16 wide (1 cell per shelf) x 32 tall: a 16-tall cell fits at y=0 and y=16; the third would
            // start at y=32 on page 0 — Stage M opens page 1 for it instead of dropping it.
            var atlas = new GlyphAtlas(width: 16, fixedHeight: 32);

            GlyphAtlasEntry first = atlas.Append(Glyph(65, w: 10, h: 10), 0); // cell 16x16 → page 0, y=0
            GlyphAtlasEntry second = atlas.Append(Glyph(66, w: 10, h: 10), 0); // cell 16x16 → page 0, y=16
            Assert.AreEqual(0, atlas.OverflowCount, "two 16px cells fit a 32px-tall page");
            Assert.AreEqual(1, atlas.PageCount, "no new page needed yet");
            Assert.AreEqual(0, first.Page);
            Assert.AreEqual(0, second.Page);

            GlyphAtlasEntry third = atlas.Append(Glyph(67, w: 10, h: 10), 0); // would start at y=32 on page 0
            Assert.AreEqual(0, atlas.OverflowCount,
                "Stage M: no longer a drop — the glyph lands on a fresh page instead");
            Assert.AreEqual(2, atlas.PageCount, "the third glyph forced a second page open");
            Assert.AreEqual(1, third.Page, "the third glyph packed onto page 1");
            Assert.AreEqual(new int2(0, 0), third.AtlasOrigin, "page 1 is a fresh packer — starts at its own origin");
            Assert.IsTrue(atlas.TryGetEntry(0, 67, out GlyphAtlasEntry roundTrip), "the page-1 glyph round-trips through TryGetEntry");
            Assert.AreEqual(1, roundTrip.Page);
            Assert.AreEqual(new int2(16, 32), atlas.Size, "Size (per-page) is unchanged by paging");
        }

        // =========================================================================================
        // Genuine overflow (Stage M redefinition): a cell that doesn't fit even a BRAND-NEW empty page
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
        // Stage M robustness (Codex finding #2): a cell WIDER than the fixed page must be surfaced as
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
        // Stage M robustness (Codex finding #3): page growth is capped at GlyphAtlas.MaxPages. Once the cap
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
}
