using Unity.Collections;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// <see cref="MvtLayer.TryBindNativeFilter"/>'s implementation, kept off <see cref="MvtLayer"/> itself
    /// so the layer stays a thin forwarder — it already forwards its <see cref="MvtLayer.DenseKeyResolver"/>
    /// for <c>IIndexedFeatureSource.KeyResolver</c> the same way.
    /// </summary>
    internal static class NativeFilterSelection
    {
        /// <summary>
        /// Rebinds <paramref name="program"/> against <paramref name="layer"/>'s key/value tables and, on
        /// success, wraps the binding in a disposable <see cref="INativeFeatureMatcher"/>. Returns
        /// <c>null</c> — "fall back to the managed path" — when <paramref name="layer"/> carries no
        /// resolver yet (a hand-built layer outside the decode, never true of a decoded production layer;
        /// see <see cref="MvtLayer.DenseKeyResolver"/>) or the rebind is refused (a literal string resolves
        /// to two or more ids in this layer — see <c>NativeFilterRebind.Rebind</c>).
        /// </summary>
        internal static INativeFeatureMatcher TryBind(MvtLayer layer, NativeFilterProgram program)
        {
            if (layer.DenseKeyResolver == null) return null;
            if (!program.Rebind(layer.DenseKeyResolver, Allocator.Persistent, out NativeArray<int> binding))
                return null;
            return new MvtNativeFeatureMatcher(program, binding, layer);
        }
    }
}
