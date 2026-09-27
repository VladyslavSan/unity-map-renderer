// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Unity.Text.Sprites
{
    /// <summary>
    /// Stacks N decoded sprite sheets into one source buffer, so the single-sheet repack pipeline
    /// (<see cref="SpriteSheetPadder"/> + <see cref="SpriteSheetComposer"/>) runs unchanged over an
    /// array-form root <c>sprite</c> merge. Sheets stack vertically, each at its own Y offset; every
    /// non-<c>"default"</c> id prefixes its names <c>id:name</c>, <c>"default"</c> stays unprefixed. A
    /// name collision after prefixing keeps the LAST sheet's entry, the same forward-compat posture
    /// <see cref="SpriteIndex.Parse"/> takes for a duplicate key.
    /// </summary>
    public static class SpriteSheetStacker
    {
        private const int BytesPerTexel = 4;

        /// <summary>One decoded sheet: its id, top-left-origin RGBA32 pixels, size, and parsed index.</summary>
        public readonly struct Sheet
        {
            public string Id { get; init; }
            public byte[] Pixels { get; init; }
            public int2 Size { get; init; }
            public SpriteIndex Index { get; init; }
        }

        /// <summary>Stacks every sheet into one top-left-origin RGBA32 buffer and one merged index. Empty
        /// input yields an empty (0x0) buffer and an empty index.</summary>
        public static (byte[] Pixels, int2 Size, SpriteIndex Index) Stack(IReadOnlyList<Sheet> sheets)
        {
            if (sheets == null || sheets.Count == 0)
                return (Array.Empty<byte>(), new int2(0, 0), new SpriteIndex());

            int width = 0, height = 0;
            foreach (Sheet sheet in sheets)
            {
                width = math.max(width, sheet.Size.x);
                height += sheet.Size.y;
            }

            var merged = new byte[width * height * BytesPerTexel];
            var entries = new Dictionary<string, SpriteEntry>();
            int yOffset = 0;

            foreach (Sheet sheet in sheets)
            {
                for (int row = 0; row < sheet.Size.y; row++)
                {
                    int srcStart = row * sheet.Size.x * BytesPerTexel;
                    int dstStart = (yOffset + row) * width * BytesPerTexel;
                    Array.Copy(sheet.Pixels, srcStart, merged, dstStart, sheet.Size.x * BytesPerTexel);
                }

                foreach (var kv in sheet.Index.Entries)
                {
                    SpriteEntry e = kv.Value;
                    // A rect past its OWN sheet's edge still lands inside the stacked buffer (some other
                    // sheet's rows) once offset — drop it instead of letting the merge read a neighbour's
                    // pixels under this name.
                    if (!IsWithinSheet(e, sheet.Size))
                        continue;

                    string name = sheet.Id == "default" ? kv.Key : sheet.Id + ":" + kv.Key;
                    entries[name] = new SpriteEntry
                    {
                        X = e.X, Y = e.Y + yOffset, Width = e.Width, Height = e.Height,
                        PixelRatio = e.PixelRatio, Sdf = e.Sdf, Padding = e.Padding,
                    };
                }

                yOffset += sheet.Size.y;
            }

            return (merged, new int2(width, height), SpriteIndex.FromEntries(entries));
        }

        /// <summary>Whether <paramref name="entry"/>'s rect is a real, in-bounds block of ITS OWN sheet
        /// (<paramref name="sheetSize"/>) — checked before stacking, not against the merged buffer, which a
        /// malformed rect could still fit by reaching into a neighbouring sheet's rows. <c>long</c>-widened
        /// the same way <c>SpriteSheetPadder.IsPackable</c> is, so a wrapped 32-bit sum cannot pass.</summary>
        private static bool IsWithinSheet(in SpriteEntry entry, int2 sheetSize)
            => entry.Width > 0 && entry.Height > 0
               && entry.X >= 0 && entry.Y >= 0
               && (long)entry.X + entry.Width <= sheetSize.x
               && (long)entry.Y + entry.Height <= sheetSize.y;
    }
}
