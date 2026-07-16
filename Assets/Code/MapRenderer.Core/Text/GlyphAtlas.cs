// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// CPU-side SDF glyph atlas: owns a <see cref="GlyphAtlasPacker"/> plus a single-channel (R8),
    /// row-major pixel buffer and a codepoint-keyed <see cref="GlyphAtlasEntry"/> lookup. The Unity-side
    /// <c>Texture2D</c> upload is a later, engine-side pass — this type stays engine-free (Core).
    ///
    /// <see cref="Pixels"/> is always exactly <c>Size.x * Size.y</c> bytes. Growth is height-only (see
    /// <see cref="GlyphAtlasPacker"/>'s growth policy), so growing the buffer is a row-preserving resize:
    /// the atlas width — and therefore every already-blitted row's byte offsets — never changes, so no
    /// reflow of previously appended glyphs is ever needed.
    /// </summary>
    public sealed class GlyphAtlas : IGlyphAtlasView
    {
        private readonly GlyphAtlasPacker _packer;
        private readonly Dictionary<uint, GlyphAtlasEntry> _entries = new Dictionary<uint, GlyphAtlasEntry>();

        private byte[] _pixels = Array.Empty<byte>();
        private int _committedHeight;
        private readonly int _fixedHeight;

        public GlyphAtlas(int width = GlyphAtlasPacker.DefaultWidth) : this(width, 0) { }

        /// <param name="width">Atlas width in pixels.</param>
        /// <param name="fixedHeight">0 = classic height-grows atlas; a positive value = a FIXED-capacity
        /// <c>width x fixedHeight</c> atlas whose <see cref="Size"/> never changes (its pixel buffer is
        /// pre-allocated in full) — S105 uses a big fixed atlas so incremental per-tile layout never
        /// invalidates earlier tiles' UVs (glyph-atlas-uv-growth-staleness lesson). A glyph that no longer
        /// fits is skipped (no entry) and counted in <see cref="OverflowCount"/> rather than throwing.</param>
        public GlyphAtlas(int width, int fixedHeight)
        {
            _packer = new GlyphAtlasPacker(width, fixedHeight);
            _fixedHeight = fixedHeight;
            if (fixedHeight > 0)
            {
                _pixels = new byte[width * fixedHeight];
                _committedHeight = fixedHeight;
            }
        }

        /// <summary>Current atlas size in pixels. In fixed mode this is constant; otherwise height grows as
        /// glyphs are appended.</summary>
        public int2 Size => _packer.Size;

        /// <summary>Number of appended glyph entries — the subsystem uses this to skip a redundant GPU
        /// re-upload when a tile added no new glyphs.</summary>
        public int Count => _entries.Count;

        /// <summary>Number of glyphs dropped because the FIXED atlas was full (0 in grow mode). Non-zero
        /// means the atlas should be larger — the subsystem logs it (no silent cap).</summary>
        public int OverflowCount { get; private set; }

        /// <summary>Single-channel (R8) row-major pixel buffer, exactly <c>Size.x * Size.y</c> bytes.</summary>
        public byte[] Pixels => _pixels;

        /// <summary>Looks up a previously appended glyph's atlas entry by codepoint.</summary>
        public bool TryGetEntry(uint codepoint, out GlyphAtlasEntry entry) => _entries.TryGetValue(codepoint, out entry);

        /// <summary>
        /// Packs the glyph's cell into the atlas and, if it has a bitmap, blits it row-major at the
        /// packed origin. A glyph with no bitmap (<see cref="SdfGlyph.HasBitmap"/> false, e.g. space)
        /// still packs a cell and gets an entry (metrics/advance for layout) but blits nothing.
        /// </summary>
        public GlyphAtlasEntry Append(in SdfGlyph glyph)
        {
            int2 cellSize = glyph.CellSize;
            int2 origin;
            if (_fixedHeight > 0)
            {
                if (!_packer.TryPack(cellSize, out origin))
                {
                    // Fixed atlas full — skip this glyph (no entry; layout omits it, TryGetEntry stays
                    // false). Never expected with a 2048²+ atlas and real fonts; surfaced via OverflowCount.
                    OverflowCount++;
                    return default;
                }
            }
            else
            {
                origin = _packer.Pack(cellSize);
                GrowToFit(_packer.Size);
            }

            if (glyph.HasBitmap)
            {
                BlitRowMajor(glyph.Bitmap, cellSize, origin);
            }

            var entry = new GlyphAtlasEntry
            {
                Codepoint = glyph.Codepoint,
                AtlasOrigin = origin,
                CellSize = cellSize,
                Left = glyph.Left,
                Top = glyph.Top,
                Advance = glyph.Advance,
            };
            _entries[glyph.Codepoint] = entry;
            return entry;
        }

        /// <summary>
        /// Row-preserving resize: the atlas width never changes after the packer picks it, so growing
        /// only ever appends rows — a straight copy of the existing bytes into a taller buffer, no
        /// reflow of already-blitted cells.
        /// </summary>
        private void GrowToFit(int2 requiredSize)
        {
            if (requiredSize.y <= _committedHeight) return;

            var grown = new byte[requiredSize.x * requiredSize.y];
            if (_pixels.Length > 0)
            {
                Array.Copy(_pixels, grown, _pixels.Length);
            }
            _pixels = grown;
            _committedHeight = requiredSize.y;
        }

        private void BlitRowMajor(byte[] bitmap, int2 cellSize, int2 origin)
        {
            int atlasWidth = _packer.Width;
            int cellWidth = cellSize.x;
            int cellHeight = cellSize.y;

            for (int row = 0; row < cellHeight; row++)
            {
                int srcRowStart = row * cellWidth;
                int dstRowStart = (origin.y + row) * atlasWidth + origin.x;
                for (int col = 0; col < cellWidth; col++)
                {
                    _pixels[dstRowStart + col] = bitmap[srcRowStart + col];
                }
            }
        }
    }
}
