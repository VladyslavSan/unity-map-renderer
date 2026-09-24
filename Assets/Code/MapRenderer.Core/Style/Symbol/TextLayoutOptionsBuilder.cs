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
        /// <paramref name="feature"/>. <c>text-offset</c>'s y is negated, because MapLibre is y-down and
        /// <see cref="TextLayoutOptions.Offset"/> is y-up; <c>RadialOffset</c> is a magnitude and passes unflipped.
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
