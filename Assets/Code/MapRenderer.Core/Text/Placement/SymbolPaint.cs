// Engine-free. C# LangVersion 9 (Unity): NO parameterless struct ctor override (that needs C# 10) — prefer
// `Default` for construction (mirrors TextLayoutOptions.Default's documented mitigation).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The resolved text fill PAINT properties for one symbol (as opposed to layout, which
    /// <c>TextQuadLayout.Layout</c> already baked into <see cref="SymbolQuad"/>s). Every color is straight
    /// RGBA (0..1); the linear conversion happens later, at bake (<c>SymbolPlacementSystem.LinearColor</c>).
    ///
    /// <para>All five are evaluated PER FEATURE by <c>SymbolFeatureExtractor</c> and feed the billboard
    /// vertex streams — the halo trio on a second copy of the symbol's glyphs, so the shader has no halo term
    /// of its own. <see cref="TextColor"/> and <see cref="HaloColor"/> carry white RGB when their expression
    /// is CONSTANT: that kind rides a per-layer uniform instead (<c>SymbolRenderLayer.BindTextPaint</c>) so a
    /// restyle can ease it. Their ALPHA and <see cref="Opacity"/> always ride the stream.</para>
    /// </summary>
    public readonly struct SymbolPaint
    {
        /// <summary>Fill color (`text-color`), straight RGBA. White RGB when a CONSTANT value rides the
        /// per-layer uniform instead of this stream.</summary>
        public float4 TextColor { get; init; }

        /// <summary>Overall opacity (`text-opacity`), multiplies <see cref="TextColor"/>.a at bake time.</summary>
        public float Opacity { get; init; }

        /// <summary>Halo color (`text-halo-color`), straight RGBA. White RGB when a CONSTANT value rides the
        /// per-layer uniform, on the same terms as <see cref="TextColor"/>.</summary>
        public float4 HaloColor { get; init; }

        /// <summary>Halo width in pixels (`text-halo-width`).</summary>
        public float HaloWidthPx { get; init; }

        /// <summary>Halo blur in pixels (`text-halo-blur`).</summary>
        public float HaloBlurPx { get; init; }

        /// <summary>Style-spec defaults: opaque black text, no halo (`text-halo-width` default 0). Under the
        /// two-carrier contract above these are the STREAM values — correct for a non-Constant expression; a
        /// Constant one rides the per-layer uniform and leaves white RGB here instead.</summary>
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
