// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// CPU-side SDF glyph atlas: a <see cref="GlyphAtlasPacker"/> and a row-major R8 pixel buffer per page, plus
    /// a <see cref="GlyphAtlasEntry"/> lookup; <c>GlyphAtlasTexture</c> uploads it as a <c>Texture2DArray</c>.
    /// Non-local invariant: a grow-mode atlas (<c>fixedHeight == 0</c>) is one page that grows in height only,
    /// so resizing <see cref="Pixels"/> keeps every row's offsets. A fixed atlas opens a new page of the same
    /// <see cref="Size"/> when the current one cannot fit a cell, so <c>uv = AtlasOrigin / Size</c> holds on
    /// every page; <see cref="OverflowCount"/> counts a cell that fits no fresh page.
    /// </summary>
    public sealed class GlyphAtlas : IGlyphAtlasView
    {
        /// <summary>
        /// Hard ceiling on fixed-mode pages (Texture2DArray layers): a glyph that needs one more page counts
        /// as <see cref="OverflowCount"/> overflow instead of allocating. Sixteen 4096² R8 pages (16 MB each)
        /// hold more glyph cells than a full multi-script font set, stay far under GLES3.0's guaranteed 256
        /// layers, and cap memory at 256 MB.
        /// </summary>
        public const int MaxPages = 16;

        private readonly List<GlyphAtlasPacker> _packers = new List<GlyphAtlasPacker>();
        private readonly List<byte[]> _pixelPages = new List<byte[]>();
        // Keyed by (font, codepoint): every layer shares one atlas and a style mixes faces, so on a
        // codepoint-only key the first face to decode 'B' would claim it for every face.
        private readonly Dictionary<long, GlyphAtlasEntry> _entries = new Dictionary<long, GlyphAtlasEntry>();

        // Font name -> dense id. Interned here because the atlas is the thing keyed by it; GlyphManager
        // stamps ids onto the shaped glyphs from the same table, so both sides always agree.
        private readonly Dictionary<string, int> _fontIds = new Dictionary<string, int>(StringComparer.Ordinal);

        private int _committedHeight; // grow-mode only (page 0's row-preserving resize watermark)
        private readonly int _fixedHeight;
        private int _currentPage; // fixed mode only — the page new Appends try first; never revisited once full

        public GlyphAtlas(int width = GlyphAtlasPacker.DefaultWidth) : this(width, 0) { }

        /// <param name="width">Atlas width in pixels.</param>
        /// <param name="fixedHeight">0 = one page that grows in height; a positive value = pre-allocated
        /// <c>width x fixedHeight</c> pages, so earlier tiles' UVs stay valid (see the class doc).</param>
        public GlyphAtlas(int width, int fixedHeight)
        {
            _fixedHeight = fixedHeight;
            _packers.Add(new GlyphAtlasPacker(width, fixedHeight));
            if (fixedHeight > 0)
            {
                _pixelPages.Add(new byte[width * fixedHeight]);
                _committedHeight = fixedHeight;
            }
            else
            {
                _pixelPages.Add(Array.Empty<byte>());
            }
        }

        /// <summary>Current atlas PAGE size in pixels — every page shares this size. In fixed mode this is
        /// constant; otherwise (grow mode, always 1 page) height grows as glyphs are appended.</summary>
        public int2 Size => _packers[0].Size;

        /// <summary>Number of appended glyph entries — the subsystem uses this to skip a redundant GPU
        /// re-upload when a tile added no new glyphs.</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Number of glyphs dropped because they don't fit ANY page (0 in grow mode, which never fails to
        /// fit since it always grows). Non-zero in fixed mode means one of: a cell too WIDE or too TALL for
        /// even a fresh empty page, or the atlas has hit <see cref="MaxPages"/> and a new page would be
        /// needed — i.e. the atlas should be larger or has more distinct glyphs than it can hold. The
        /// subsystem logs it (no silent cap).
        /// </summary>
        public int OverflowCount { get; private set; }

        /// <summary>Number of pages this atlas has allocated (Texture2DArray layer count). Always 1 in grow
        /// mode; 1 in fixed mode until the first page overflows.</summary>
        public int PageCount => _pixelPages.Count;

        /// <summary>Single-channel (R8) row-major pixel buffer for PAGE 0, exactly <c>Size.x * Size.y</c>
        /// bytes — back-compat accessor for single-page callers (grow mode always has exactly this one
        /// page). Multi-page callers use <see cref="PagePixels"/>.</summary>
        public byte[] Pixels => _pixelPages[0];

        /// <summary>Single-channel (R8) row-major pixel buffer for <paramref name="page"/>
        /// (<c>[0, PageCount)</c>), exactly <c>Size.x * Size.y</c> bytes.</summary>
        public byte[] PagePixels(int page) => _pixelPages[page];

        /// <summary>The dense id for <paramref name="fontName"/>, assigned on first use and stable for this
        /// atlas's lifetime. A null name interns as the empty name — one shared id, not a per-call new one.
        /// <see cref="MapRenderer.Unity.Text.GlyphManager"/> stamps the SAME id onto every shaped glyph, which
        /// is what makes <see cref="TryGetEntry"/> find the face the style asked for.</summary>
        public int FontId(string fontName)
        {
            string key = fontName ?? string.Empty;
            if (_fontIds.TryGetValue(key, out int id)) return id;
            id = _fontIds.Count;
            _fontIds[key] = id;
            return id;
        }

        /// <summary>Looks up a previously appended glyph's atlas entry by (font, codepoint). The font half is
        /// load-bearing: two faces' same codepoint are two different bitmaps.</summary>
        public bool TryGetEntry(int fontId, uint codepoint, out GlyphAtlasEntry entry)
            => _entries.TryGetValue(EntryKey(fontId, codepoint), out entry);

        /// <summary>Packs a font id and a codepoint into one dictionary key. A codepoint is at most 21 bits
        /// and the id is an <c>int</c>, so the two never overlap in a <c>long</c>.</summary>
        private static long EntryKey(int fontId, uint codepoint) => ((long)fontId << 32) | codepoint;

        /// <summary>
        /// Packs the glyph's cell into the atlas and, if it has a bitmap, blits it row-major at the
        /// packed origin. A glyph with no bitmap (<see cref="SdfGlyph.HasBitmap"/> false, e.g. space)
        /// still packs a cell and gets an entry (metrics/advance for layout) but blits nothing.
        /// </summary>
        public GlyphAtlasEntry Append(in SdfGlyph glyph, int fontId)
        {
            int2 cellSize = glyph.CellSize;
            int2 origin;
            int page;
            if (_fixedHeight > 0)
            {
                if (!TryPackFixed(cellSize, out origin, out page))
                {
                    // No page — including a fresh empty one — fits this cell (a cell taller than the fixed
                    // page height). Skip it (no entry; layout omits it, TryGetEntry stays false).
                    OverflowCount++;
                    return default;
                }
            }
            else
            {
                origin = _packers[0].Pack(cellSize);
                page = 0;
                GrowToFit(_packers[0].Size);
            }

            if (glyph.HasBitmap)
            {
                BlitRowMajor(_pixelPages[page], _packers[page].Width, glyph.Bitmap, cellSize, origin);
            }

            var entry = new GlyphAtlasEntry
            {
                Codepoint = glyph.Codepoint,
                AtlasOrigin = origin,
                CellSize = cellSize,
                Left = glyph.Left,
                Top = glyph.Top,
                Advance = glyph.Advance,
                Page = page,
            };
            _entries[EntryKey(fontId, glyph.Codepoint)] = entry;
            return entry;
        }

        /// <summary>
        /// Fixed-mode packing: try the current page, else open a new page. Older pages are never revisited,
        /// because a packer that failed a cell fails any same-or-larger one. Returns <c>false</c> (overflow,
        /// no throw) when the cell is too wide or tall for a fresh page or a new page would exceed
        /// <see cref="MaxPages"/>. The size preflight is required: <see cref="GlyphAtlasPacker.TryPack"/>
        /// throws for a cell wider than the page.
        /// </summary>
        private bool TryPackFixed(int2 cellSize, out int2 origin, out int page)
        {
            origin = default;
            page = -1;

            // Preflight (no throw): a cell that can't fit even a fresh empty width×fixedHeight page — too
            // WIDE (GlyphAtlasPacker.TryPack would throw on this) or too TALL — is genuine overflow.
            int pageWidth = _packers[0].Width;
            if (cellSize.x > pageWidth || cellSize.y > _fixedHeight)
                return false;

            if (_packers[_currentPage].TryPack(cellSize, out origin))
            {
                page = _currentPage;
                return true;
            }

            // Current page full: at MaxPages, report overflow instead of allocating. The preflight above
            // guarantees the cell fits a fresh page, so the TryPack below always succeeds.
            if (_packers.Count >= MaxPages)
            {
                origin = default;
                return false;
            }

            var freshPacker = new GlyphAtlasPacker(pageWidth, _fixedHeight);
            freshPacker.TryPack(cellSize, out origin);
            _packers.Add(freshPacker);
            _pixelPages.Add(new byte[pageWidth * _fixedHeight]);
            _currentPage = _packers.Count - 1;
            page = _currentPage;
            return true;
        }

        /// <summary>
        /// Row-preserving resize (grow mode, page 0 only): the atlas width never changes after the packer
        /// picks it, so growing only ever appends rows — a straight copy of the existing bytes into a
        /// taller buffer, no reflow of already-blitted cells.
        /// </summary>
        private void GrowToFit(int2 requiredSize)
        {
            if (requiredSize.y <= _committedHeight) return;

            var grown = new byte[requiredSize.x * requiredSize.y];
            byte[] page0 = _pixelPages[0];
            if (page0.Length > 0)
            {
                Array.Copy(page0, grown, page0.Length);
            }
            _pixelPages[0] = grown;
            _committedHeight = requiredSize.y;
        }

        private static void BlitRowMajor(byte[] pixels, int atlasWidth, byte[] bitmap, int2 cellSize, int2 origin)
        {
            int cellWidth = cellSize.x;
            int cellHeight = cellSize.y;

            for (int row = 0; row < cellHeight; row++)
            {
                int srcRowStart = row * cellWidth;
                int dstRowStart = (origin.y + row) * atlasWidth + origin.x;
                for (int col = 0; col < cellWidth; col++)
                {
                    pixels[dstRowStart + col] = bitmap[srcRowStart + col];
                }
            }
        }
    }
}
