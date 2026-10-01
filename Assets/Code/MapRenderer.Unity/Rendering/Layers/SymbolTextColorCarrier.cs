using MapRenderer.Core.Expressions;
using MapRenderer.Unity.Style;

namespace MapRenderer.Unity.Rendering.Layers
{
    /// <summary>
    /// Which symbol colours ride a per-layer uniform and which bake into the vertex COLOR stream.
    /// <c>text-color</c> rides only at <c>Constant</c>; <c>text-halo-color</c> rides at every kind that does
    /// not depend on the feature, and its alpha always stays on the stream.
    /// <see cref="MeshSignature"/> reads both predicates, so widening one frees a key the in-place path
    /// cannot re-bake.
    /// </summary>
    internal static class SymbolTextColorCarrier
    {
        /// <summary>True iff the <c>text-color</c> <paramref name="color"/> rides its per-layer uniform rather
        /// than the vertex COLOR stream.</summary>
        internal static bool RidesUniform(StyleProperty<Color> color)
            => color != null && RidesUniform(color.Kind);

        /// <summary>The kind-only form of the predicate — the gate's freeness check has no
        /// <see cref="StyleProperty{T}"/> to evaluate, only a parsed expression's kind.</summary>
        internal static bool RidesUniform(ExpressionKind kind) => kind == ExpressionKind.Constant;

        /// <summary>True iff the <c>text-halo-color</c> <paramref name="color"/> rides <c>_HaloColor</c>.</summary>
        internal static bool HaloRidesUniform(StyleProperty<Color> color)
            => color != null && HaloRidesUniform(color.Kind);

        /// <summary>The kind-only form of <see cref="HaloRidesUniform(StyleProperty{Color})"/>.</summary>
        internal static bool HaloRidesUniform(ExpressionKind kind) => !ExpressionKinds.DependsOnFeature(kind);
    }
}
