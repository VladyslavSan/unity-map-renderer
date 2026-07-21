// Unity-only (needs UnityEngine.Texture2DArray) — NOT added to core-tests.csproj. Complements the
// engine-free core-tests T3 (SdfDistanceFieldTests, raw-bitmap iso-crossing/graded-band checks) by
// exercising the REAL Texture2DArray upload path (GlyphAtlasTexture), which core-tests cannot touch.

using System;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S18 Unity-side batch — T3 texel-from-texture: uploads a decoded glyph's atlas region via
    /// <see cref="GlyphAtlasTexture"/> and reads a texel on the glyph's edge back from the uploaded
    /// <see cref="Texture2DArray"/>'s CPU-side buffer, confirming it matches the source
    /// <see cref="GlyphAtlas.Pixels"/> byte exactly AND is graded (mid-range), not flat 0/255 — the
    /// same "real SDF, not a coverage bitmap" guard <c>SdfDistanceFieldTests</c> applies to the raw
    /// decoded bitmap, now applied end-to-end through the GPU-texture upload.
    /// </summary>
    [TestFixture]
    public class GlyphAtlasTextureTests
    {
        // Same mid-band thresholds as SdfDistanceFieldTests: "graded", not pinning an exact width.
        private const int MidBandLo = 32;
        private const int MidBandHi = 223;

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

        [Test]
        public void Upload_DecodedGlyph_TexelOnEdgeMatchesCpuPixelsAndIsGraded()
        {
            SdfGlyph a = LoadUppercaseA();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entry = atlas.Append(a);

            var atlasTexture = new GlyphAtlasTexture();
            try
            {
                atlasTexture.Upload(atlas);
                Texture2DArray texture = atlasTexture.Texture;

                Assert.IsNotNull(texture, "Upload must create a Texture2DArray once the atlas has packed a glyph");
                Assert.AreEqual(atlas.Size.x, texture.width);
                Assert.AreEqual(atlas.Size.y, texture.height);
                Assert.AreEqual(1, texture.depth, "M-T1: single-page behaviour is byte-identical — one array layer");
                Assert.AreEqual(0, entry.Page, "M-T1: every glyph is Page 0 on the single-page path");

                int2 local = FindGradedTexelLocal(a.Bitmap, entry.CellSize);
                byte expected = a.Bitmap[local.y * entry.CellSize.x + local.x];

                // GetPixelData<byte> reads the texture's CPU-side buffer directly (1 byte/texel for
                // R8, no float round-trip, no channel ambiguity if the format ever falls back to
                // Alpha8 — unlike GetPixel().r, which would silently read 0 from an Alpha8 texture).
                var raw = texture.GetPixelData<byte>(0, entry.Page);
                int px = entry.AtlasOrigin.x + local.x;
                int py = entry.AtlasOrigin.y + local.y;
                byte actual = raw[py * texture.width + px];

                Assert.AreEqual(expected, actual,
                    $"texel at cell-local ({local.x},{local.y}) must round-trip byte-exact through the uploaded texture");
                Assert.Greater(actual, MidBandLo, "the edge texel must be graded (mid-range), not flat 0/255 -- an SDF, not a coverage bitmap");
                Assert.Less(actual, MidBandHi, "the edge texel must be graded (mid-range), not flat 0/255 -- an SDF, not a coverage bitmap");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        [Test]
        public void Upload_EmptyAtlas_IsANoOp()
        {
            var atlas = new GlyphAtlas(); // nothing appended -> Size.y == 0
            var atlasTexture = new GlyphAtlasTexture();

            Assert.DoesNotThrow(() => atlasTexture.Upload(atlas));
            Assert.IsNull(atlasTexture.Texture, "an atlas with nothing packed yet must not create a Texture2DArray");
        }

        [Test]
        public void Upload_AfterGrowth_RecreatesTextureAtNewSize()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            var atlasTexture = new GlyphAtlasTexture();

            try
            {
                atlas.Append(stack.Glyphs[65u]); // 'A'
                atlasTexture.Upload(atlas);
                int2 firstSize = new int2(atlasTexture.Texture.width, atlasTexture.Texture.height);

                // Appending enough more glyphs to force the packer/atlas to grow taller.
                foreach (var kv in stack.Glyphs) atlas.Append(kv.Value);
                atlasTexture.Upload(atlas);

                Assert.AreEqual(atlas.Size.x, atlasTexture.Texture.width);
                Assert.AreEqual(atlas.Size.y, atlasTexture.Texture.height);
                Assert.GreaterOrEqual(atlasTexture.Texture.height, firstSize.y, "the re-uploaded texture must cover the grown atlas");
                Assert.AreEqual(1, atlasTexture.Texture.depth, "grow mode never pages — always one array layer");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // =========================================================================================
        // M-T2 (texture half): a fixed atlas forced to page (a small page + enough glyphs to overflow it)
        // uploads a Texture2DArray with ONE LAYER PER PAGE, and page 1's uploaded bytes match the SOURCE
        // glyph's bitmap at its page-1 origin — proving the second array layer actually carries the
        // overflowed glyph's pixels, not empty/garbage data.
        // =========================================================================================
        [Test]
        public void Upload_FixedAtlasForcedToPage_UploadsOneArrayLayerPerPage()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

            // A page just wide/tall enough for ONE glyph's cell — the second appended glyph overflows to page 1.
            SdfGlyph a = stack.Glyphs[65u]; // 'A'
            SdfGlyph b = stack.Glyphs[66u]; // 'B'
            int2 cellA = a.CellSize, cellB = b.CellSize;
            int pageWidth = math.max(cellA.x, cellB.x);
            int pageHeight = math.max(cellA.y, cellB.y);
            var atlas = new GlyphAtlas(width: pageWidth, fixedHeight: pageHeight);

            GlyphAtlasEntry entryA = atlas.Append(a);
            GlyphAtlasEntry entryB = atlas.Append(b);
            Assert.AreEqual(0, entryA.Page, "fixture precondition: 'A' fits page 0");
            Assert.AreEqual(1, entryB.Page, "fixture precondition: 'B' overflows onto page 1");
            Assert.AreEqual(2, atlas.PageCount);

            var atlasTexture = new GlyphAtlasTexture();
            try
            {
                atlasTexture.Upload(atlas);
                Texture2DArray texture = atlasTexture.Texture;

                Assert.IsNotNull(texture);
                Assert.AreEqual(2, texture.depth, "one Texture2DArray layer per GlyphAtlas page");
                Assert.AreEqual(atlas.Size.x, texture.width);
                Assert.AreEqual(atlas.Size.y, texture.height);

                var layer1 = texture.GetPixelData<byte>(0, 1);
                for (int row = 0; row < cellB.y; row++)
                {
                    for (int col = 0; col < cellB.x; col++)
                    {
                        byte expected = b.Bitmap[row * cellB.x + col];
                        byte actual = layer1[(entryB.AtlasOrigin.y + row) * texture.width + entryB.AtlasOrigin.x + col];
                        Assert.AreEqual(expected, actual, $"layer-1 pixel mismatch at cell-local ({col},{row})");
                    }
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        /// <summary>Finds the first cell-local (x,y) whose CPU bitmap byte falls in the graded mid-band.</summary>
        private static int2 FindGradedTexelLocal(byte[] bitmap, int2 cellSize)
        {
            for (int y = 0; y < cellSize.y; y++)
            {
                for (int x = 0; x < cellSize.x; x++)
                {
                    byte v = bitmap[y * cellSize.x + x];
                    if (v > MidBandLo && v < MidBandHi) return new int2(x, y);
                }
            }
            throw new InvalidOperationException("fixture precondition: 'A' must contain at least one graded mid-band texel");
        }
    }
}
