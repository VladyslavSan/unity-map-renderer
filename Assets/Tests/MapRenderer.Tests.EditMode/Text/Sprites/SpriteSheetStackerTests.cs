// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Unity.Text.Sprites;

namespace MapRenderer.Tests.Text.Sprites
{
    /// <summary>
    /// <see cref="SpriteSheetStacker.Stack"/>: the Y-offset arithmetic and the id-prefix rule, plus the
    /// malformed-entry guard that keeps a rect from ever reading a neighbouring sheet's rows.
    /// </summary>
    [TestFixture]
    public class SpriteSheetStackerTests
    {
        private const int BytesPerTexel = 4;

        private static byte[] SolidBuffer(int2 size, byte r, byte g, byte b, byte a)
        {
            var buffer = new byte[size.x * size.y * BytesPerTexel];
            for (int i = 0; i < buffer.Length; i += BytesPerTexel)
            {
                buffer[i] = r; buffer[i + 1] = g; buffer[i + 2] = b; buffer[i + 3] = a;
            }
            return buffer;
        }

        private static SpriteIndex OneEntryIndex(string name, SpriteEntry entry)
            => SpriteIndex.FromEntries(new Dictionary<string, SpriteEntry> { [name] = entry });

        [Test]
        public void TwoSheets_NonDefaultIdPrefixed_YOffsetByThePriorSheetsHeight()
        {
            var sheetDefault = new SpriteSheetStacker.Sheet
            {
                Id = "default",
                Pixels = SolidBuffer(new int2(4, 4), 255, 0, 0, 255),
                Size = new int2(4, 4),
                Index = OneEntryIndex("x", new SpriteEntry { X = 0, Y = 0, Width = 4, Height = 4, PixelRatio = 1 }),
            };
            var sheetA = new SpriteSheetStacker.Sheet
            {
                Id = "a",
                Pixels = SolidBuffer(new int2(4, 6), 0, 255, 0, 255),
                Size = new int2(4, 6),
                Index = OneEntryIndex("x", new SpriteEntry { X = 0, Y = 0, Width = 4, Height = 6, PixelRatio = 1 }),
            };

            (byte[] pixels, int2 size, SpriteIndex index) = SpriteSheetStacker.Stack(new[] { sheetDefault, sheetA });

            Assert.AreEqual(new int2(4, 10), size);
            Assert.IsTrue(index.TryGetSprite("x", out SpriteEntry defaultEntry));
            Assert.AreEqual(0, defaultEntry.Y, "the default sheet stays at the top, unshifted");
            Assert.IsTrue(index.TryGetSprite("a:x", out SpriteEntry aEntry));
            Assert.AreEqual(4, aEntry.Y, "sheet 'a' must start at sheet 'default's height, not overlap it");
        }

        [Test]
        public void MalformedEntry_TallerThanItsOwnSheet_IsDropped_NeverReadsTheNeighbourSheet()
        {
            var sheetDefault = new SpriteSheetStacker.Sheet
            {
                Id = "default",
                Pixels = SolidBuffer(new int2(4, 4), 255, 0, 0, 255),
                Size = new int2(4, 4),
                // Malformed: height 10 reaches past this sheet's own 4px height. Once stacked, that rect
                // still lies inside sheet "b"'s rows — it must never resolve there.
                Index = OneEntryIndex("bad", new SpriteEntry { X = 0, Y = 0, Width = 4, Height = 10, PixelRatio = 1 }),
            };
            var sheetB = new SpriteSheetStacker.Sheet
            {
                Id = "b",
                Pixels = SolidBuffer(new int2(4, 4), 0, 255, 0, 255),
                Size = new int2(4, 4),
                Index = OneEntryIndex("good", new SpriteEntry { X = 0, Y = 0, Width = 4, Height = 4, PixelRatio = 1 }),
            };

            (byte[] pixels, int2 size, SpriteIndex index) = SpriteSheetStacker.Stack(new[] { sheetDefault, sheetB });

            Assert.IsFalse(index.TryGetSprite("bad", out _), "an entry taller than its own sheet must be dropped");
            Assert.IsTrue(index.TryGetSprite("b:good", out _), "the other sheet's well-formed entry must survive");
        }
    }
}
