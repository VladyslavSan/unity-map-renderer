using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;

namespace MapRenderer.Unity.Rendering.Layers
{
    /// <summary>The restyle-in-place gate's two predicates: <see cref="RootMatches"/> over everything outside <c>layers</c>, and
    /// <see cref="LayerSurvives"/> over ONE pair, which <c>RenderLayerSet.TryRestyleInPlace</c> applies per id-matched pair. Each compares
    /// <see cref="MeshSignature"/>, so a pair survives exactly when both documents bake the same mesh.</summary>
    internal static class SurvivingLayerGate
    {
        /// <summary>True iff the two documents have the same root signature. A null document fails closed.</summary>
        internal static bool RootMatches(StyleDocument oldStyle, StyleDocument newStyle)
            => oldStyle != null && newStyle != null
            && JsonCanonical.Write(MeshSignature.Root(oldStyle)) == JsonCanonical.Write(MeshSignature.Root(newStyle));

        /// <summary>True iff the two layers have the same signature.</summary>
        internal static bool LayerSurvives(StyleLayer oldLayer, StyleLayer newLayer)
            => JsonCanonical.Write(MeshSignature.Layer(oldLayer)) == JsonCanonical.Write(MeshSignature.Layer(newLayer));
    }
}
