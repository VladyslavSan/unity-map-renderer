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
    /// glyphs (glyph-atlas-uv-growth-staleness). Overflow degrades gracefully (no throw, counted).
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

            for (uint c = 65; c < 90; c++) atlas.Append(Glyph(c));

            Assert.AreEqual(before, atlas.Size,
                "Size MUST NOT change as glyphs append — the whole point of the fixed atlas (UV stability)");
            Assert.AreEqual(0, atlas.OverflowCount, "25 small glyphs fit a 256x256 atlas with no overflow");
        }

        [Test]
        public void FixedAtlas_Overflow_SkipsGracefully_AndCounts()
        {
            // 16 wide (1 cell per shelf) x 32 tall: a 16-tall cell fits at y=0 and y=16; the third would
            // start at y=32 and overflow. So exactly two glyphs fit, the rest are dropped (not thrown).
            var atlas = new GlyphAtlas(width: 16, fixedHeight: 32);

            atlas.Append(Glyph(65, w: 10, h: 10)); // cell 16x16 → y=0
            atlas.Append(Glyph(66, w: 10, h: 10)); // cell 16x16 → y=16
            Assert.AreEqual(0, atlas.OverflowCount, "two 16px cells fit a 32px-tall atlas");
            Assert.IsTrue(atlas.TryGetEntry(65, out _));
            Assert.IsTrue(atlas.TryGetEntry(66, out _));

            atlas.Append(Glyph(67, w: 10, h: 10)); // would start at y=32 → overflow
            Assert.AreEqual(1, atlas.OverflowCount, "the third glyph overflows and is counted");
            Assert.IsFalse(atlas.TryGetEntry(67, out _), "an overflowed glyph gets NO entry (layout omits it)");
            Assert.AreEqual(new int2(16, 32), atlas.Size, "Size is unchanged by overflow");
        }

        [Test]
        public void FixedAtlas_EarlyGlyphOrigin_UnaffectedByLaterAppends()
        {
            var atlas = new GlyphAtlas(width: 256, fixedHeight: 512);

            GlyphAtlasEntry first = atlas.Append(Glyph(65));
            int2 originAtFirst = first.AtlasOrigin;
            int2 sizeAtFirst = atlas.Size;

            for (uint c = 66; c < 120; c++) atlas.Append(Glyph(c));

            Assert.IsTrue(atlas.TryGetEntry(65, out GlyphAtlasEntry stillFirst));
            Assert.AreEqual(originAtFirst, stillFirst.AtlasOrigin, "an early glyph's atlas origin never moves");
            Assert.AreEqual(sizeAtFirst, atlas.Size,
                "and the UV denominator (Size) is unchanged → its baked UVs stay valid");
        }
    }
}
