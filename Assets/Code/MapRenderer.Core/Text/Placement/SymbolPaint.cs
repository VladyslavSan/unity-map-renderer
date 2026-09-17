// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.
// C# LangVersion 9 (Unity): NO parameterless struct ctor override (that needs C# 10) — prefer
// `Default` for construction (mirrors TextLayoutOptions.Default's documented mitigation).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The resolved `text-*` PAINT properties for one symbol (as opposed to layout, which
    /// <c>TextQuadLayout.Layout</c> already baked into <see cref="SymbolQuad"/>s). Every color is straight
    /// RGBA (0..1); the linear conversion happens later, at bake (<c>SymbolPlacementSystem.LinearColor</c>).
    ///
    /// <para><see cref="TextColor"/> feeds the billboard vertex color stream for every kind EXCEPT a
    /// CONSTANT <c>text-color</c>, which instead rides a per-layer uniform and leaves this white;
    /// <see cref="Opacity"/> always feeds the vertex stream. Halo is bound directly to a per-layer
    /// uniform (<c>SymbolRenderLayer.BindTextPaint</c>), not carried here.</para>
    /// </summary>
    public readonly struct SymbolPaint
    {
        /// <summary>Fill color (`text-color`), straight RGBA. White when a CONSTANT `text-color` rides the
        /// per-layer uniform instead of this stream.</summary>
        public float4 TextColor { get; init; }

        /// <summary>Overall opacity (`text-opacity`), multiplies <see cref="TextColor"/>.a at bake time.</summary>
        public float Opacity { get; init; }

        /// <summary>Style-spec defaults: opaque black text (`text-color`'s spec default). Under the
        /// two-carrier contract above this is the STREAM value — correct for a non-Constant `text-color`;
        /// a Constant one rides the per-layer uniform instead and would leave <see cref="TextColor"/>
        /// white, not this black.</summary>
        public static readonly SymbolPaint Default = new SymbolPaint
        {
            TextColor = new float4(0f, 0f, 0f, 1f),
            Opacity = 1f,
        };
    }
}
