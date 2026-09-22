// Engine-free: no UnityEngine dependency.

using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The bridge from a parsed symbol <see cref="LayoutProperties"/> to the size-independent
    /// <see cref="TextLayoutOptions"/> that <c>TextQuadLayout</c> consumes: it threads text-anchor /
    /// -offset / -justify / -max-width / -line-height / -letter-spacing / -radial-offset. Its own
    /// engine-free step so the string→enum + em + y-flip reconcile is headless-testable in isolation.
    /// </summary>
    public static class TextLayoutOptionsBuilder
    {
        /// <summary>
        /// Assemble the per-feature layout options. The zoom-capable ems (<see cref="LayoutProperties.TextMaxWidth"/>,
        /// <see cref="LayoutProperties.TextLineHeight"/>, <see cref="LayoutProperties.TextLetterSpacing"/>,
        /// <see cref="LayoutProperties.TextRadialOffset"/>) are evaluated at <paramref name="zoom"/> for
        /// <paramref name="feature"/>; anchor/justify are the already-parsed enums.
        ///
        /// <para><b>Offset y-flip:</b> MapLibre <c>text-offset</c> is y-DOWN (positive y = down), while
        /// <see cref="TextLayoutOptions.Offset"/> / <c>TextQuadLayout</c> are y-UP (line 0's baseline at y=0,
        /// lower lines at negative y). So the y component is negated here, consistent with the sign in
        /// <c>TextQuadLayout.ComputeRadialOffset</c> (a Top anchor pushes toward −y). <c>RadialOffset</c> is a
        /// magnitude (direction is derived from the anchor downstream), so it is passed through unflipped.</para>
        /// </summary>
        public static TextLayoutOptions Build(LayoutProperties layout, double zoom, IFeature feature)
        {
            if (layout == null) return TextLayoutOptions.Default;

            return new TextLayoutOptions
            {
                Anchor = layout.TextAnchor,
                Justify = layout.TextJustify,
                Offset = new float2(layout.TextOffset.x, -layout.TextOffset.y),
                RadialOffset = layout.TextRadialOffset.Evaluate(zoom, feature),
                MaxWidthEm = layout.TextMaxWidth.Evaluate(zoom, feature),
                LineHeightEm = layout.TextLineHeight.Evaluate(zoom, feature),
                LetterSpacingEm = layout.TextLetterSpacing.Evaluate(zoom, feature),
            };
        }
    }
}
