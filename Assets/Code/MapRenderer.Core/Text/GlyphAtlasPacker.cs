// Engine-free: no UnityEngine dependency.

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Deterministic shelf (row) packer for the SDF glyph atlas: cells pack left-to-right into the current
    /// shelf, and a cell that does not fit closes it and opens a new shelf beneath, as tall as its tallest
    /// cell. Non-obvious why: <see cref="Width"/> is fixed and only <see cref="Height"/> grows, because
    /// <see cref="GlyphAtlas.Pixels"/> is row-major with the width as stride, so a height grow is a pure
    /// append that moves no packed cell. A cell wider than the atlas throws.
    /// </summary>
    public sealed class GlyphAtlasPacker
    {
        /// <summary>Default atlas width in pixels — comfortably wider than any real SDF glyph cell.</summary>
        public const int DefaultWidth = 256;

        private readonly int _width;
        private readonly int _fixedHeight; // 0 = unbounded (height grows by shelf); >0 = fixed-capacity atlas
        private int _height;

        private int _shelfY;
        private int _shelfHeight;
        private int _shelfNextX;

        /// <param name="width">Fixed atlas width in pixels.</param>
        /// <param name="fixedHeight">0 (default) = height grows by shelf; a positive value = a fixed height that
        /// reports overflow via <see cref="TryPack"/> instead of growing, so baked UVs stay valid.</param>
        public GlyphAtlasPacker(int width = DefaultWidth, int fixedHeight = 0)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "atlas width must be positive");
            if (fixedHeight < 0) throw new ArgumentOutOfRangeException(nameof(fixedHeight), "fixed height must be non-negative");
            _width = width;
            _fixedHeight = fixedHeight;
        }

        /// <summary>Fixed atlas width in pixels (never changes after construction).</summary>
        public int Width => _width;

        /// <summary>Current used atlas height in pixels — grows monotonically as cells are packed (grow mode),
        /// or the constant fixed capacity (fixed mode).</summary>
        public int Height => _fixedHeight > 0 ? _fixedHeight : _height;

        /// <summary>Current committed atlas size, <c>(Width, Height)</c>. Constant in fixed mode.</summary>
        public int2 Size => new int2(_width, Height);

        /// <summary>
        /// Packs one cell and returns its top-left origin. Deterministic. In fixed mode this throws if the
        /// atlas is full — prefer <see cref="TryPack"/> where overflow must degrade gracefully.
        /// </summary>
        public int2 Pack(int2 cellSize)
        {
            if (!TryPack(cellSize, out int2 origin))
                throw new InvalidOperationException(
                    $"glyph atlas full: cell {cellSize} does not fit the fixed {_width}x{_fixedHeight} atlas");
            return origin;
        }

        /// <summary>
        /// Tries to pack one cell; on success returns <c>true</c> and its top-left origin, committing the
        /// packer state. In fixed mode, returns <c>false</c> WITHOUT mutating state when the cell would
        /// exceed the fixed height (the atlas is full). A cell wider than the fixed atlas width always
        /// throws (a programming error, not a runtime-capacity condition).
        /// </summary>
        public bool TryPack(int2 cellSize, out int2 origin)
        {
            int w = cellSize.x;
            int h = cellSize.y;

            if (w > _width)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize),
                    $"cell width {w} exceeds the fixed atlas width {_width} " +
                    "(see GlyphAtlasPacker's width-fixed/height-grows policy)");
            }

            // Candidate shelf placement (locals — only committed on success).
            int shelfY = _shelfY, shelfNextX = _shelfNextX, shelfHeight = _shelfHeight;
            if (shelfNextX + w > _width)
            {
                shelfY += shelfHeight;
                shelfNextX = 0;
                shelfHeight = 0;
            }

            origin = new int2(shelfNextX, shelfY);
            int newShelfHeight = h > shelfHeight ? h : shelfHeight;
            int usedHeight = shelfY + newShelfHeight;

            if (_fixedHeight > 0 && usedHeight > _fixedHeight)
            {
                origin = default; // atlas full — do not commit
                return false;
            }

            // Commit.
            _shelfY = shelfY;
            _shelfNextX = shelfNextX + w;
            _shelfHeight = newShelfHeight;
            if (usedHeight > _height) _height = usedHeight;
            return true;
        }
    }
}
