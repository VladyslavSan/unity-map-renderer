// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
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
    /// S18 Slice 2 — atlas packing + CPU blit, against the same committed glyph-PBF fixture Slice 1's
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
        [Test]
        public void Append_BitmapGlyph_PacksCellBlitsBytesAndRoundTripsLookup()
        {
            SdfGlyph a = LoadLatinStack().Glyphs[65u]; // 'A'
            var atlas = new GlyphAtlas();

            GlyphAtlasEntry entry = atlas.Append(a);

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

            Assert.IsTrue(atlas.TryGetEntry(65u, out GlyphAtlasEntry roundTrip), "'A' must round-trip through TryGetEntry");
            Assert.AreEqual(entry.Codepoint, roundTrip.Codepoint);
            Assert.AreEqual(entry.AtlasOrigin, roundTrip.AtlasOrigin);
            Assert.AreEqual(entry.CellSize, roundTrip.CellSize);

            Assert.IsFalse(atlas.TryGetEntry(0xFFFFu, out _), "an unappended codepoint must not be found");
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

            GlyphAtlasEntry entry = atlas.Append(space);

            Assert.AreEqual(32u, entry.Codepoint);
            Assert.AreEqual(new int2(space.Width + 6, space.Height + 6), entry.CellSize);
            Assert.IsTrue(atlas.TryGetEntry(32u, out _), "a no-bitmap glyph still gets an entry");

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
                entries.Add((atlas.Append(kv.Value), kv.Value));
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
}
