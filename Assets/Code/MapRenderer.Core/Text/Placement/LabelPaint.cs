// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members (see docs/conventions.md).
// C# LangVersion 9 (Unity): NO parameterless struct ctor override (that needs C# 10) — prefer
// `Default` for construction (mirrors TextLayoutOptions.Default's documented mitigation).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 Slice 1: the resolved `text-*` PAINT properties for one label (as opposed to layout, which
    /// S19's <see cref="TextLayoutResult"/> already baked into <see cref="SymbolQuad"/>s). Every color is
    /// straight RGBA (0..1, already linear-space at the point <see cref="LabelPlacementSystem"/> bakes it
    /// into a vertex color — mirrors <c>StyledLineTileBuilder</c>'s sRGB→linear-at-build-time convention).
    ///
    /// <para>Slice 1 wires <see cref="TextColor"/>/<see cref="Opacity"/> onto the billboard vertex color
    /// stream; halo (<see cref="HaloColor"/>/<see cref="HaloWidthPx"/>/<see cref="HaloBlurPx"/>) is
    /// declared here (and the shader honours it) but a real per-label halo value is bound as a Slice-3
    /// paint concern once S105 threads <c>text-halo-*</c> through — Slice 1's synthetic demo supplies its
    /// own constant halo via <see cref="Default"/>.</para>
    /// </summary>
    public readonly struct LabelPaint
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
        public static readonly LabelPaint Default = new LabelPaint
        {
            TextColor = new float4(0f, 0f, 0f, 1f),
            Opacity = 1f,
            HaloColor = new float4(1f, 1f, 1f, 1f),
            HaloWidthPx = 0f,
            HaloBlurPx = 0f,
        };
    }
}
