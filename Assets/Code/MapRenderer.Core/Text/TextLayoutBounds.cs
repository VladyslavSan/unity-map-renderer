// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The no-alloc caller-buffer <see cref="TextQuadLayout.Layout(ShapedRun, IGlyphAtlasView, in TextLayoutOptions, System.Collections.Generic.List{SymbolQuad})"/>
    /// overload's return value: the block bbox + line count, with the quads themselves landing in the
    /// caller-owned list rather than a class allocation.
    /// </summary>
    public readonly struct TextLayoutBounds
    {
        public float2 Min { get; init; }
        public float2 Max { get; init; }
        public int LineCount { get; init; }
    }
}
