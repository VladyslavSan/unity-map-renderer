// Unity EditMode only — needs MapRenderer.Unity's internals via InternalsVisibleTo. UMR-151 moved this
// off SurvivingLayerGate, where all 13 callers were tests and the whole-document design was retired.

using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Style
{
    /// <summary>The retired WHOLE-DOCUMENT restyle gate, kept here because it still names the concept these
    /// teeth assert: every layer of the new document takes the old one's meshes and materials, at the same
    /// index. Composes the two predicates production DOES still call —
    /// <see cref="SurvivingLayerGate.RootMatches"/> and <see cref="SurvivingLayerGate.LayerSurvives"/> — so
    /// the gate's rules stay pinned even though nothing composes them this way any more.</summary>
    internal static class WholeDocumentGate
    {
        /// <summary>True when both documents are present, their roots-minus-layers match, and every layer
        /// survives against the layer at its own index.</summary>
        internal static bool AllLayersSurvive(StyleDocument oldStyle, StyleDocument newStyle)
        {
            if (oldStyle == null || newStyle == null) return false;
            if (!SurvivingLayerGate.RootMatches(oldStyle, newStyle)) return false;
            if (oldStyle.Layers.Count != newStyle.Layers.Count) return false;

            for (int i = 0; i < oldStyle.Layers.Count; i++)
                if (!SurvivingLayerGate.LayerSurvives(oldStyle.Layers[i], newStyle.Layers[i]))
                    return false;

            return true;
        }
    }
}
