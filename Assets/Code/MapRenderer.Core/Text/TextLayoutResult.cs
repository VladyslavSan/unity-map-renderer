// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The allocating <see cref="TextQuadLayout.Layout(ShapedRun, IGlyphAtlasView, in TextLayoutOptions)"/>
    /// overload's result: the label-local quad buffer plus the block bounding box (fork 1, S19 stage
    /// doc §6) — S20 needs per-label bounds for collision and it's already computed during anchoring.
    /// </summary>
    public sealed class TextLayoutResult
    {
        public IReadOnlyList<SymbolQuad> Quads { get; init; }

        /// <summary>Block bbox minimum corner, in the same anchor-relative baked-px space as every <see cref="SymbolQuad"/>.</summary>
        public float2 BoundsMin { get; init; }

        /// <summary>Block bbox maximum corner, in the same anchor-relative baked-px space as every <see cref="SymbolQuad"/>.</summary>
        public float2 BoundsMax { get; init; }

        /// <summary>Number of wrapped lines (always &gt;= 1, even for an empty run).</summary>
        public int LineCount { get; init; }
    }

    /// <summary>
    /// The no-alloc caller-buffer <see cref="TextQuadLayout.Layout(ShapedRun, IGlyphAtlasView, in TextLayoutOptions, List{SymbolQuad})"/>
    /// overload's return value: the same block bbox + line count as <see cref="TextLayoutResult"/>,
    /// without the class allocation (the quads themselves land in the caller-owned list).
    /// </summary>
    public readonly struct TextLayoutBounds
    {
        public float2 Min { get; init; }
        public float2 Max { get; init; }
        public int LineCount { get; init; }
    }
}
