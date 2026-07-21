// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// CPU-side SDF glyph atlas: owns a <see cref="GlyphAtlasPacker"/> (per page) plus a single-channel
    /// (R8), row-major pixel buffer per page and a codepoint-keyed <see cref="GlyphAtlasEntry"/> lookup.
    /// The Unity-side <c>Texture2DArray</c> upload is a later, engine-side pass — this type stays
    /// engine-free (Core).
    ///
    /// <see cref="Pixels"/> is always exactly <c>Size.x * Size.y</c> bytes — page 0's buffer (kept for
    /// back-compat with single-page callers; see <see cref="PagePixels"/> for the rest). Growth is
    /// height-only (see <see cref="GlyphAtlasPacker"/>'s growth policy) in classic grow mode (never
    /// multi-page — only a FIXED atlas pages), so growing page 0's buffer is a row-preserving resize: the
    /// atlas width — and therefore every already-blitted row's byte offsets — never changes, so no reflow
    /// of previously appended glyphs is ever needed.
    ///
    /// <para><b>Stage M — multi-page (fixed mode only).</b> A classic grow-mode atlas (<c>fixedHeight ==
    /// 0</c>) never pages — it just keeps growing taller, exactly as before (byte-identical). A FIXED atlas
    /// (<c>fixedHeight &gt; 0</c>, S105) used to DROP a glyph that didn't fit (counted in
    /// <see cref="OverflowCount"/>). Now, when the CURRENT page's packer can't fit a cell, a NEW fixed page
    /// (own pixel buffer + fresh packer) opens and becomes current — older pages are never revisited (this
    /// mirrors the packer's own append-only shelf policy: once a page's packer fails a cell, it never
    /// "un-fails" for a same-or-larger cell later). <see cref="OverflowCount"/> now means genuine overflow:
    /// a cell that doesn't fit even a brand-new empty page (wider than a full fixed-height column of
    /// shelves — GlyphAtlasPacker.TryPack itself throws for a cell wider than the atlas; this is the
    /// height-exceeds-a-fresh-page case). Every page shares the SAME fixed <c>(width, fixedHeight)</c>
    /// <see cref="Size"/>, so a layout site's <c>uv = AtlasOrigin / Size</c> math is unchanged — only the
    /// <see cref="GlyphAtlasEntry.Page"/> the UV samples from differs.</para>
    /// </summary>
    public sealed class GlyphAtlas : IGlyphAtlasView
    {
        /// <summary>
        /// Hard ceiling on the number of fixed-mode pages (Texture2DArray layers) before a glyph that
        /// would need a NEW page is surfaced as <see cref="OverflowCount"/> overflow instead of allocating
        /// (Stage M robustness — no unbounded page growth → OOM / exceeding the platform's Texture2DArray
        /// layer limit).
        ///
        /// <para><b>16, justified.</b> At the production 4096² R8 atlas (16 MB/page — <c>SymbolLabelSubsystem
        /// .AtlasDimension</c>), one page holds ~17k typical ~30px SDF glyph cells, so 16 pages ≈ 270k glyph
        /// cells — comfortably more than the largest realistic multi-script set (a full Noto CJK ≈ 65k glyphs
        /// plus every other Unicode script's common set is well under ~100k, ~6 pages), leaving ≥2.5×
        /// headroom. It is also safe on EVERY platform: the most conservative Texture2DArray layer limit
        /// (GLES3.0's guaranteed 256) is 16× this, and worst-case memory is bounded at 16 × 16 MB = 256 MB —
        /// a ceiling a real font set never reaches. Hitting it means the atlas is genuinely too small; the
        /// subsystem logs it via <see cref="OverflowCount"/> (no silent cap).</para>
        /// </summary>
        public const int MaxPages = 16;

        private readonly List<GlyphAtlasPacker> _packers = new List<GlyphAtlasPacker>();
        private readonly List<byte[]> _pixelPages = new List<byte[]>();
        private readonly Dictionary<uint, GlyphAtlasEntry> _entries = new Dictionary<uint, GlyphAtlasEntry>();

        private int _committedHeight; // grow-mode only (page 0's row-preserving resize watermark)
        private readonly int _fixedHeight;
        private int _currentPage; // fixed mode only — the page new Appends try first; never revisited once full

        public GlyphAtlas(int width = GlyphAtlasPacker.DefaultWidth) : this(width, 0) { }

        /// <param name="width">Atlas width in pixels.</param>
        /// <param name="fixedHeight">0 = classic height-grows atlas (single page, never pages); a positive
        /// value = a FIXED-capacity <c>width x fixedHeight</c> PAGE whose <see cref="Size"/> never changes
        /// (its pixel buffer is pre-allocated in full) — S105 uses a big fixed atlas so incremental
        /// per-tile layout never invalidates earlier tiles' UVs (glyph-atlas-uv-growth-staleness lesson).
        /// Stage M: a glyph that no longer fits the current page opens a NEW page (own buffer + packer)
        /// instead of dropping — see the class doc's multi-page section. <see cref="OverflowCount"/> now
        /// only counts a cell that doesn't fit even a fresh empty page.</param>
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
            _entries[glyph.Codepoint] = entry;
            return entry;
        }

        /// <summary>
        /// Fixed-mode packing: try the CURRENT page first; if its packer can't fit the cell, open a NEW
        /// page (own buffer + fresh packer) and try there. Older pages are never revisited — once a page's
        /// packer fails a cell it never un-fails for a same-or-larger one later (the packer's own
        /// append-only shelf policy), so searching them would only waste cycles.
        ///
        /// <para>Returns <c>false</c> (genuine overflow — the caller counts it, no throw) in three cases:
        /// the cell is too WIDE or too TALL for even a fresh empty page, or a new page would exceed
        /// <see cref="MaxPages"/>. The width/height PREFLIGHT is load-bearing: <see cref="GlyphAtlasPacker.TryPack"/>
        /// itself THROWS for a cell wider than the page width, so probing a packer with an over-wide cell
        /// would abort the whole build instead of surfacing as overflow — the preflight returns false before
        /// ever touching a packer.</para>
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

            // Current page full — a NEW page is needed. Cap page growth (Stage M robustness): at MaxPages,
            // surface the glyph as overflow instead of allocating unboundedly (OOM / Texture2DArray layer
            // limit). The preflight above guarantees the cell fits a fresh page, so the TryPack below always
            // succeeds once we commit to opening one.
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
