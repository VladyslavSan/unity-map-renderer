using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// One selected feature, carried <b>beside</b> its position in the source-layer's own feature list.
    /// Non-obvious why: the shared geometry buffer's <c>RingFeatureIdx</c> indexes <see cref="ITileLayer.Features"/>,
    /// and an ordinal member on <see cref="IFeature"/> would put a geometry-join concern on the neutral
    /// evaluation surface. Use a <b>single</b> list of pairs, never two parallel lists that can desync.
    /// </summary>
    public readonly struct SelectedTileFeature
    {
        /// <summary>The selected feature itself.</summary>
        public IFeature Feature { get; init; }

        /// <summary>Its index into the source <see cref="ITileLayer.Features"/> list — the ordinal every
        /// shared-buffer side array is keyed on.</summary>
        public int Ordinal { get; init; }
    }

    /// <summary>
    /// The feature-selection seam: given a <see cref="StyleLayer"/> and a decoded <see cref="IDecodedTile"/>,
    /// returns the features of the matching source-layer (none if absent) that pass the layer's filter.
    /// The filter compiles once per filter node (<see cref="FilterFor"/>). At each
    /// <see cref="ITileLayer"/> entry point, a VM-compilable filter over an <see cref="INativeFilterSource"/>
    /// layer runs on the Burst filter VM instead (<see cref="NativeProgramFor"/>), with the same result.
    /// </summary>
    public static class FeatureSelector
    {
        /// <summary>
        /// Selects features from <paramref name="tile"/> that belong to the source-layer declared by
        /// <paramref name="layer"/> and pass <paramref name="layer"/>'s filter at <paramref name="zoom"/>.
        ///
        /// Returns an empty list when the source-layer is absent/unresolvable. Never throws for missing
        /// layers; may throw <see cref="ExpressionParseException"/> if the layer's filter is malformed.
        /// </summary>
        public static IReadOnlyList<IFeature> SelectFeatures(
            StyleLayer layer, IDecodedTile tile, double zoom = 0.0)
        {
            // 1. Resolve the tile layer through the one resolution point.
            var tileLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            if (tileLayer == null)
                return System.Array.Empty<IFeature>();

            // 2. Probe the native-filter seam first. Only when it declines does the managed filter compile and
            // bind; the native branch never rents a key-binding buffer or compiles CompiledFilter.
            INativeFeatureMatcher native = BindNativeFilter(tileLayer, layer);
            CompiledFilter filter = null;
            int[] binding = null;
            if (native == null)
            {
                filter = FilterFor(layer);
                var keyResolver = (tileLayer as IIndexedFeatureSource)?.KeyResolver;
                binding = BindKeys(filter, keyResolver);
            }
            try
            {
                // 3. Evaluate per feature, indexed so the native branch has an ordinal to address. The feature
                // passes straight to filter.Matches with no adapter allocation.
                IReadOnlyList<IFeature> features = tileLayer.Features;
                NativeArray<byte> results = native != null ? native.MatchAll() : default;
                var result = new List<IFeature>();
                for (int i = 0; i < features.Count; i++)
                {
                    IFeature feature = features[i];
                    bool matched = native != null ? results[i] != 0 : filter.Matches(feature, zoom, binding);
                    if (matched)
                        result.Add(feature);
                }
                return result;
            }
            finally
            {
                ReturnKeys(binding);
                native?.Dispose();
            }
        }

        /// <summary>
        /// Asks an <see cref="INativeFilterSource"/> <paramref name="tileLayer"/> to bind <paramref name="layer"/>'s
        /// memoized <see cref="NativeFilterProgram"/>. Returns <c>null</c> (use the managed
        /// <see cref="CompiledFilter"/> path) when the layer lacks the capability, the filter is outside the VM
        /// subset, or the layer refuses the bind (<see cref="INativeFilterSource.TryBindNativeFilter"/>).
        /// The caller owns disposing a non-null result.
        /// </summary>
        private static INativeFeatureMatcher BindNativeFilter(ITileLayer tileLayer, StyleLayer layer)
        {
            if (tileLayer is not INativeFilterSource nfs) return null;
            NativeFilterProgram program = NativeProgramFor(layer?.Filter);
            return program != null ? nfs.TryBindNativeFilter(program) : null;
        }

        /// <summary>
        /// The ordinal-returning form — appends every feature of <paramref name="tileLayer"/> that
        /// passes <paramref name="layer"/>'s filter at <paramref name="zoom"/> into <paramref name="into"/>,
        /// each paired with its position in <see cref="ITileLayer.Features"/>. It takes the resolved layer,
        /// because the caller needs it as the shared geometry buffer's memo key and a second resolve could differ.
        /// <paramref name="into"/> is cleared first; a null/absent <paramref name="tileLayer"/> leaves it empty.
        /// </summary>
        public static void SelectFeatures(
            StyleLayer layer, ITileLayer tileLayer, double zoom, List<SelectedTileFeature> into)
        {
            var keyResolver = (tileLayer as IIndexedFeatureSource)?.KeyResolver;
            INativeFeatureMatcher native = BindNativeFilter(tileLayer, layer);
            SelectFeaturesInto(layer, tileLayer?.Features, zoom, into, keyResolver, native);
        }

        /// <summary>The <see cref="List{T}"/> selection over an ALREADY-FETCHED feature list (the caller read
        /// <see cref="ITileLayer.Features"/> once). Clears <paramref name="into"/> first; a null
        /// <paramref name="features"/> leaves it empty. With no <see cref="ITileLayer"/> to probe, this overload
        /// always evaluates on the string key-lookup path (no binding).</summary>
        public static void SelectFeatures(
            StyleLayer layer, IReadOnlyList<IFeature> features, double zoom, List<SelectedTileFeature> into)
            => SelectFeaturesInto(layer, features, zoom, into, keyResolver: null, native: null);

        /// <summary>The <see cref="List{T}"/> selection over an ALREADY-FETCHED feature list, WITH native-filter
        /// binding. <b><paramref name="features"/> MUST be <paramref name="tileLayer"/>'s own
        /// <see cref="ITileLayer.Features"/></b>: a bound native matcher addresses features by their ordinal in
        /// the layer, so a mismatched list silently selects the wrong feature at that ordinal.</summary>
        public static void SelectFeatures(
            StyleLayer layer, ITileLayer tileLayer, IReadOnlyList<IFeature> features, double zoom,
            List<SelectedTileFeature> into)
        {
            var keyResolver = (tileLayer as IIndexedFeatureSource)?.KeyResolver;
            INativeFeatureMatcher native = BindNativeFilter(tileLayer, layer);
            SelectFeaturesInto(layer, features, zoom, into, keyResolver, native);
        }

        private static void SelectFeaturesInto(
            StyleLayer layer, IReadOnlyList<IFeature> features, double zoom, List<SelectedTileFeature> into,
            IFeatureKeyResolver keyResolver, INativeFeatureMatcher native)
        {
            into.Clear();
            if (features == null) { native?.Dispose(); return; }

            CompiledFilter filter = null;
            int[] binding = null;
            if (native == null)
            {
                filter = FilterFor(layer);
                binding = BindKeys(filter, keyResolver);
            }
            try
            {
                NativeArray<byte> results = native != null ? native.MatchAll() : default;
                for (int i = 0; i < features.Count; i++)
                {
                    IFeature feature = features[i];
                    bool matched = native != null ? results[i] != 0 : filter.Matches(feature, zoom, binding);
                    if (matched)
                        into.Add(new SelectedTileFeature { Feature = feature, Ordinal = i });
                }
            }
            finally
            {
                ReturnKeys(binding);
                native?.Dispose();
            }
        }

        /// <summary>
        /// The scratch-buffer form of <see cref="SelectFeatures(StyleLayer, ITileLayer, double, List{SelectedTileFeature})"/>:
        /// writes matches into <paramref name="into"/> from index 0 and returns the count, so the mesh-build
        /// worker pass allocates nothing. The caller passes a grow-only array of at least
        /// <c>tileLayer.Features.Count</c> (e.g. <c>TileBuildBuffers.SelectionBuffer</c>). It does <b>not</b>
        /// clear <paramref name="into"/>; the caller reads only the returned <c>[0, count)</c> prefix.
        /// </summary>
        public static int SelectFeatures(
            StyleLayer layer, ITileLayer tileLayer, double zoom, SelectedTileFeature[] into)
        {
            var keyResolver = (tileLayer as IIndexedFeatureSource)?.KeyResolver;
            INativeFeatureMatcher native = BindNativeFilter(tileLayer, layer);
            return SelectFeaturesInto(layer, tileLayer?.Features, zoom, into, keyResolver, native);
        }

        /// <summary>
        /// The scratch-buffer selection over an ALREADY-FETCHED feature list, so a worker pass that sizes its
        /// buffer from <c>features.Count</c> reads <c>ITileLayer.Features</c> once per layer
        /// (<c>RunWorkerPass_ObtainsGeometryWithoutReReadingTheFeatureList</c>). Writes <c>[0, count)</c> without
        /// clearing <paramref name="into"/> and returns the count. With no <see cref="ITileLayer"/> to probe, it
        /// always evaluates on the string key-lookup path (no binding).
        /// </summary>
        public static int SelectFeatures(
            StyleLayer layer, IReadOnlyList<IFeature> features, double zoom, SelectedTileFeature[] into)
            => SelectFeaturesInto(layer, features, zoom, into, keyResolver: null, native: null);

        /// <summary>The scratch-buffer selection over an ALREADY-FETCHED feature list, WITH native-filter
        /// binding. <b><paramref name="features"/> MUST be <paramref name="tileLayer"/>'s own
        /// <see cref="ITileLayer.Features"/></b>: a bound native matcher addresses features by their ordinal in
        /// the layer, so a mismatched list silently selects the wrong feature at that ordinal.</summary>
        public static int SelectFeatures(
            StyleLayer layer, ITileLayer tileLayer, IReadOnlyList<IFeature> features, double zoom,
            SelectedTileFeature[] into)
        {
            var keyResolver = (tileLayer as IIndexedFeatureSource)?.KeyResolver;
            INativeFeatureMatcher native = BindNativeFilter(tileLayer, layer);
            return SelectFeaturesInto(layer, features, zoom, into, keyResolver, native);
        }

        private static int SelectFeaturesInto(
            StyleLayer layer, IReadOnlyList<IFeature> features, double zoom, SelectedTileFeature[] into,
            IFeatureKeyResolver keyResolver, INativeFeatureMatcher native)
        {
            if (features == null) { native?.Dispose(); return 0; }

            CompiledFilter filter = null;
            int[] binding = null;
            if (native == null)
            {
                filter = FilterFor(layer);
                binding = BindKeys(filter, keyResolver);
            }
            try
            {
                NativeArray<byte> results = native != null ? native.MatchAll() : default;
                int count = 0;
                for (int i = 0; i < features.Count; i++)
                {
                    IFeature feature = features[i];
                    bool matched = native != null ? results[i] != 0 : filter.Matches(feature, zoom, binding);
                    if (matched)
                        into[count++] = new SelectedTileFeature { Feature = feature, Ordinal = i };
                }
                return count;
            }
            finally
            {
                ReturnKeys(binding);
                native?.Dispose();
            }
        }

        // ---- string→id key hoist: the per-layer bind step -----------------------------------------------

        /// <summary>
        /// Resolves <paramref name="filter"/>'s <see cref="CompiledFilter.KeyLayout"/> once per call into a rented
        /// <c>binding[slot] = keyIndex</c> array (<c>-1</c> for a missing name) that <c>Matches</c> reads.
        /// Returns <c>null</c> (every constant-key node takes the string path) when the filter has no such node
        /// or the source has no <see cref="IIndexedFeatureSource"/> capability. The caller MUST pair a non-null
        /// result with <see cref="ReturnKeys"/> in a <c>finally</c>.
        /// </summary>
        private static int[] BindKeys(CompiledFilter filter, IFeatureKeyResolver keyResolver)
        {
            IReadOnlyList<string> layout = filter.KeyLayout;
            if (layout.Count == 0 || keyResolver == null)
                return null;

            int[] binding = KeyBindingBuffers.Rent(layout.Count);
            for (int slot = 0; slot < layout.Count; slot++)
                binding[slot] = keyResolver.TryResolveKey(layout[slot], out int keyIndex) ? keyIndex : -1;
            return binding;
        }

        /// <summary>Returns a <see cref="BindKeys"/> result to its thread-local pool; a <c>null</c> binding
        /// (the no-capability/no-layout case) is a no-op.</summary>
        private static void ReturnKeys(int[] binding)
        {
            if (binding != null) KeyBindingBuffers.Return(binding);
        }

        // ---- compiled-filter memo ---------------------------------------------------------------------

        /// <summary>
        /// The <see cref="CompiledFilter"/> for <paramref name="layer"/>, compiled on first use and then reused.
        /// Non-obvious why: the memo keys on the filter JSON node, because <see cref="StyleLayer.Filter"/> is
        /// mutable; it is a thread-safe, weak-keyed <see cref="ConditionalWeakTable{TKey,TValue}"/> because
        /// tiles build off the main thread and entries must die with the style. A racing compile is harmless
        /// (<see cref="CompiledFilter.Compile"/> is pure). A malformed filter throws and caches nothing.
        /// </summary>
        internal static CompiledFilter FilterFor(StyleLayer layer)
        {
            JsonValue filter = layer?.Filter;
            if (filter == null)
                return CompiledFilter.Compile(null);

            return CompiledFilters.GetValue(filter, CompileCallback);
        }

        private static readonly ConditionalWeakTable<JsonValue, CompiledFilter> CompiledFilters =
            new ConditionalWeakTable<JsonValue, CompiledFilter>();

        // Hoisted so the lookup allocates no delegate per call.
        private static readonly ConditionalWeakTable<JsonValue, CompiledFilter>.CreateValueCallback
            CompileCallback = f => CompiledFilter.Compile(f);

        // ---- native-program memo -----------------------------------------------------------------------

        /// <summary>
        /// The <see cref="NativeFilterProgram"/> for <paramref name="filter"/> — compiled on first use and
        /// reused thereafter, mirroring <see cref="FilterFor"/>'s memo exactly (same keyed-on-the-node
        /// rationale: <see cref="StyleLayer.Filter"/> is mutable, so keying on the layer could serve a stale
        /// compile). Returns <c>null</c> both for "no filter" and for "compiled, but outside the VM's
        /// accepted subset" — either way the caller falls back to the managed path.
        /// </summary>
        internal static NativeFilterProgram NativeProgramFor(JsonValue filter)
        {
            if (filter == null) return null; // no filter ⇒ managed match-all path
            return NativeProgramMemo_.GetValue(filter, CompileNativeCallback).Program;
        }

        /// <summary>The explicit box the memo above stores <see cref="NativeProgramFor"/>'s cached verdict
        /// in. <see cref="ConditionalWeakTable{TKey,TValue}"/> cannot store a <c>null</c> value, and the
        /// REFUSAL verdict must be cached too — otherwise every call for an unsupported filter recompiles
        /// and refuses it again. A box makes "refused" an explicit, unambiguous cached state rather than
        /// relying on CWT null-value semantics.</summary>
        private sealed class NativeProgramMemo
        {
            internal NativeFilterProgram Program;
        }

        private static readonly ConditionalWeakTable<JsonValue, NativeProgramMemo> NativeProgramMemo_ =
            new ConditionalWeakTable<JsonValue, NativeProgramMemo>();

        private static readonly ConditionalWeakTable<JsonValue, NativeProgramMemo>.CreateValueCallback
            CompileNativeCallback = f =>
                new NativeProgramMemo { Program = NativeFilterCompiler.TryCompile(f, out var p) ? p : null };
    }
}
