// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.
// C# LangVersion 9 (Unity): NO parameterless struct ctor override (that needs C# 10) — prefer
// `Default` for construction (mirrors TextLayoutOptions.Default's documented mitigation).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The resolved `text-*` PAINT properties for one symbol (as opposed to layout, which
    /// <c>TextQuadLayout.Layout</c> already baked into <see cref="SymbolQuad"/>s). Every color is
    /// straight RGBA (0..1, already linear-space).
    ///
    /// <para><see cref="TextColor"/>/<see cref="Opacity"/> feed the billboard vertex color stream, and the
    /// halo trio (<see cref="HaloColor"/>/<see cref="HaloWidthPx"/>/<see cref="HaloBlurPx"/>) feeds the SAME
    /// stream on a second copy of the label's glyphs — the text shader has no halo term of its own. All five
    /// are evaluated PER FEATURE by <c>SymbolFeatureExtractor</c>, so a data-driven <c>text-halo-*</c>
    /// behaves like a data-driven <c>text-color</c>.</para>
    /// </summary>
    public readonly struct SymbolPaint
    {
        /// <summary>Fill color (`text-color`), straight RGBA.</summary>
        public float4 TextColor { get; init; }

        /// <summary>Overall opacity (`text-opacity`), multiplies <see cref="TextColor"/>.a at bake time.</summary>
        public float Opacity { get; init; }

        /// <summary>Halo color (`text-halo-color`), straight RGBA.</summary>
        public float4 HaloColor { get; init; }

        /// <summary>Halo width in pixels (`text-halo-width`).</summary>
        public float HaloWidthPx { get; init; }

        /// <summary>Halo blur in pixels (`text-halo-blur`).</summary>
        public float HaloBlurPx { get; init; }

        /// <summary>Style-spec defaults: opaque black text, no halo (`text-halo-width` default 0).</summary>
        public static readonly SymbolPaint Default = new SymbolPaint
        {
            TextColor = new float4(0f, 0f, 0f, 1f),
            Opacity = 1f,
            HaloColor = new float4(1f, 1f, 1f, 1f),
            HaloWidthPx = 0f,
            HaloBlurPx = 0f,
        };
    }
}
