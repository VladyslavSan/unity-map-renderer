using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The one predicate both text-color carriers read: <c>Constant</c> rides the <c>_TextColor</c> uniform
    /// (<see cref="SymbolRenderLayer.BindTextPaint"/>); every other kind bakes into the vertex COLOR stream
    /// (<c>SymbolFeatureExtractor.EvaluatePaint</c>). Not <c>!DependsOnFeature</c> (stage 1's fill-color guard)
    /// — a Zoom-kind <c>text-color</c> stays on the vertex-bake carrier by the decision recorded in
    /// SSOT §6; the restyle survivor gate reads this same predicate (<see cref="SurvivingLayerGate"/>), so
    /// widening it here would silently free a key the in-place path cannot re-bake.
    /// </summary>
    internal static class SymbolTextColorCarrier
    {
        /// <summary>True iff <paramref name="textColor"/> rides the <c>_TextColor</c> uniform rather than
        /// the vertex COLOR stream.</summary>
        internal static bool RidesUniform(StyleProperty<Color> textColor)
            => textColor != null && RidesUniform(textColor.Kind);

        /// <summary>The kind-only form of the predicate — the gate's freeness check has no
        /// <see cref="StyleProperty{T}"/> to evaluate, only a parsed expression's kind.</summary>
        internal static bool RidesUniform(ExpressionKind kind) => kind == ExpressionKind.Constant;
    }
}
