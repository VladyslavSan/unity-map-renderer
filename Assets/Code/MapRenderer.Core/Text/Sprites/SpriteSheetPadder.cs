// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// Plans a <b>padded repack</b> of a sprite sheet: every sprite gets its own cell with a border of
    /// <c>padding</c> texels on each side, so its silhouette becomes a texture ALPHA edge (which bilinear
    /// filtering antialiases) instead of the drawn quad's polygon edge (which, with MSAA off, gets one binary
    /// coverage sample per pixel and therefore wobbles a whole pixel as the quad slides sub-pixel).
    ///
    /// <para>Published sheets are full-bleed and abutting — the shipped style's sheet measures 228 of 264
    /// sprites with ink on the rect edge and 371 abutting pairs — so the border cannot come from the sheet;
    /// it has to be manufactured at decode time. This type does the <b>rect maths only</b>: which content
    /// rect lands where, and what the derived index says. <c>SpriteSheetComposer</c> moves the pixels.</para>
    ///
    /// <para><b>The content rect stays the content rect.</b> A derived <see cref="SpriteEntry"/> keeps its
    /// <c>Width/Height/PixelRatio/Sdf</c> and only relocates <c>X/Y</c> — the border is reported separately as
    /// <see cref="SpriteEntry.Padding"/>. That is what keeps <c>IconQuadLayout</c>'s logical-size maths and
    /// <c>FillPattern</c>'s tiling period untouched by the repack.</para>
    ///
    /// <para><b>Fallback, and what it actually costs.</b> If the cells cannot fit inside
    /// <see cref="MaxSheetDimension"/>, or the sheet has no packable sprite at all, <see cref="Plan"/> returns
    /// the SOURCE sheet unchanged: source size, the source index (every <c>Padding == 0</c>), and one identity
    /// blit. Icons still draw — <c>SpriteSheet</c> logs a warning rather than throwing — but this is NOT
    /// "exactly as they did before this stage". With the half-texel UV inset retired there is deliberately
    /// only ONE sampling path, so a <c>Padding == 0</c> sprite is drawn edge-to-edge; on an unpadded,
    /// abutting, full-bleed sheet a bilinear edge tap then reaches into the sprite packed next door and
    /// <b>neighbour bleed returns</b> — worse than the pre-stage inset, not equal to it. That is the accepted
    /// price of one sampling path (two would be exactly how this bug class comes back); the fallback is a
    /// degraded mode, not a free one.</para>
    /// </summary>
    public static class SpriteSheetPadder
    {
        /// <summary>Hard cap on either repacked dimension — the floor of what any target guarantees.
        /// <c>internal</c>: no cross-assembly production caller, and the one test that needs it lives in an
        /// assembly Core already grants <c>InternalsVisibleTo</c>.</summary>
        internal const int MaxSheetDimension = 8192;

        /// <summary>
        /// Plans the repack of <paramref name="index"/> over a <paramref name="sourceSize"/> sheet with
        /// <paramref name="padding"/> texels of border per side. Never throws on a malformed index: a sprite
        /// with a non-positive extent or a rect reaching outside the source sheet passes through into the
        /// derived index UNCHANGED with <c>Padding == 0</c> (the same forward-compat posture
        /// <see cref="SpriteIndex.Parse"/> takes), so every downstream degenerate guard behaves as before.
        /// </summary>
        public static SpritePadPlan Plan(SpriteIndex index, int2 sourceSize, int padding)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (padding < 0) throw new ArgumentOutOfRangeException(nameof(padding));

            var derived = new Dictionary<string, SpriteEntry>();
            List<RectGroup> groups = GroupBySourceRect(index, sourceSize, derived);

            if (groups.Count == 0)
                return Identity(index, sourceSize);

            var cells = new int2[groups.Count];
            for (int i = 0; i < groups.Count; i++)
                cells[i] = new int2(groups[i].Width + 2 * padding, groups[i].Height + 2 * padding);

            var cellOrigins = new int2[groups.Count];
            if (!ShelfRectPacker.TryPack(cells, sourceSize.x, MaxSheetDimension, cellOrigins, out int2 size))
                return Identity(index, sourceSize);

            var blits = new SpriteBlit[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                RectGroup group = groups[i];
                int2 content = cellOrigins[i] + new int2(padding, padding);

                blits[i] = new SpriteBlit
                {
                    SrcX = group.X, SrcY = group.Y,
                    DstX = content.x, DstY = content.y,
                    Width = group.Width, Height = group.Height,
                };

                foreach (string name in group.Names)
                {
                    derived[name] = new SpriteEntry
                    {
                        X = content.x,
                        Y = content.y,
                        Width = group.Width,
                        Height = group.Height,
                        PixelRatio = group.PixelRatio,
                        Sdf = group.Sdf,
                        Padding = padding,
                    };
                }
            }

            return new SpritePadPlan
            {
                Size = size,
                Index = SpriteIndex.FromEntries(derived),
                Blits = blits,
                Padding = padding,
            };
        }

        /// <summary>
        /// Collects the packable sprites into one group per distinct source rect — real sheets alias one rect
        /// under several names, and packing each name separately would both bloat the sheet and un-alias them.
        /// Degenerate entries are written straight into <paramref name="passThrough"/> instead. The returned
        /// groups are ordered by their lexicographically-smallest name (<c>string.CompareOrdinal</c>), which
        /// is what makes the whole plan independent of dictionary insertion order.
        /// </summary>
        private static List<RectGroup> GroupBySourceRect(
            SpriteIndex index, int2 sourceSize, Dictionary<string, SpriteEntry> passThrough)
        {
            var byRect = new Dictionary<RectKey, RectGroup>();
            var groups = new List<RectGroup>();

            foreach (var kv in index.Entries)
            {
                SpriteEntry entry = kv.Value;
                if (!IsPackable(entry, sourceSize))
                {
                    passThrough[kv.Key] = entry;
                    continue;
                }

                var key = new RectKey(entry);
                if (!byRect.TryGetValue(key, out RectGroup group))
                {
                    group = new RectGroup(entry);
                    byRect[key] = group;
                    groups.Add(group);
                }

                group.Names.Add(kv.Key);
            }

            foreach (RectGroup group in groups)
                group.Names.Sort(string.CompareOrdinal);

            groups.Sort((a, b) => string.CompareOrdinal(a.Names[0], b.Names[0]));
            return groups;
        }

        /// <summary>
        /// Whether <paramref name="entry"/>'s rect is a real, in-bounds block of the source sheet. The
        /// reach tests widen to <c>long</c> deliberately: <c>X + Width</c> in 32-bit signed arithmetic WRAPS
        /// for a malformed index (<c>x: 2147483647, width: 1</c> lands on <c>int.MinValue</c>, which passes
        /// <c>&lt;= sourceSize.x</c>), and such an entry would then be packed, blitted from a negative byte
        /// offset, and throw out of the <c>SpriteSheet</c> constructor — taking the whole texture and
        /// index with it, so EVERY icon disappears. <see cref="SpriteIndex.Parse(string)"/> tolerates malformed JSON
        /// rather than throwing, so this is reachable from a bad sheet.
        /// </summary>
        private static bool IsPackable(in SpriteEntry entry, int2 sourceSize)
            => entry.Width > 0 && entry.Height > 0
               && entry.X >= 0 && entry.Y >= 0
               && (long)entry.X + entry.Width <= sourceSize.x
               && (long)entry.Y + entry.Height <= sourceSize.y;

        /// <summary>The no-repack plan: the source sheet copied verbatim, every entry as parsed.</summary>
        private static SpritePadPlan Identity(SpriteIndex index, int2 sourceSize) => new SpritePadPlan
        {
            Size = sourceSize,
            Index = SpriteIndex.FromEntries(index.Entries),
            Blits = new[]
            {
                new SpriteBlit
                {
                    SrcX = 0, SrcY = 0, DstX = 0, DstY = 0,
                    Width = math.max(0, sourceSize.x), Height = math.max(0, sourceSize.y),
                },
            },
            Padding = 0,
        };

        /// <summary>Identity of a source rect — two names sharing one are the same sprite, drawn once.</summary>
        private readonly struct RectKey : IEquatable<RectKey>
        {
            private readonly int _x, _y, _width, _height;
            private readonly float _pixelRatio;
            private readonly bool _sdf;

            public RectKey(in SpriteEntry entry)
            {
                _x = entry.X; _y = entry.Y; _width = entry.Width; _height = entry.Height;
                _pixelRatio = entry.PixelRatio; _sdf = entry.Sdf;
            }

            public bool Equals(RectKey other)
                => _x == other._x && _y == other._y && _width == other._width && _height == other._height
                   && _pixelRatio.Equals(other._pixelRatio) && _sdf == other._sdf;

            public override bool Equals(object obj) => obj is RectKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(_x, _y, _width, _height, _pixelRatio, _sdf);
        }

        /// <summary>One source rect plus every name that resolves to it.</summary>
        private sealed class RectGroup
        {
            public RectGroup(in SpriteEntry entry)
            {
                X = entry.X; Y = entry.Y; Width = entry.Width; Height = entry.Height;
                PixelRatio = entry.PixelRatio; Sdf = entry.Sdf;
            }

            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }
            public float PixelRatio { get; }
            public bool Sdf { get; }
            public List<string> Names { get; } = new List<string>();
        }
    }
}
