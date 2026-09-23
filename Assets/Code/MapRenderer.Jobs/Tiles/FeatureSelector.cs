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
    ///
    /// <para><b>Why the ordinal travels beside the feature and not on it.</b> Once every consumer reads one
    /// shared per-source-layer geometry buffer, each consumer's per-feature side arrays have to be joined to
    /// the buffer's <c>RingFeatureIdx</c>, which indexes <see cref="ITileLayer.Features"/> — not the
    /// consumer's own selected list. The obvious alternative, an <c>Ordinal</c> member on the feature, would
    /// put a geometry-join concern on the neutral evaluation surface, <see cref="IFeature"/>, whose shape
    /// a structural test pins by reflection.</para>
    ///
    /// <para>A <b>single</b> list of pairs, never two parallel lists: two positionally-joined columns
    /// desync.</para>
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
    /// returns the subset of features from the matching source-layer that pass the layer's filter.
    ///
    /// Design:
    /// <list type="bullet">
    ///   <item>Source-layer resolution is delegated to <see cref="SourceLayerResolver.ResolveTileLayer"/> —
    ///     a single resolution point, not reimplemented here.</item>
    ///   <item>The layer's <c>filter</c> is compiled <b>once per filter</b> and memoized (see
    ///     <see cref="FilterFor"/>), then evaluated per feature.</item>
    ///   <item>Null/absent source-layer → empty result (mirrors <see cref="SourceLayerResolver"/> null-tolerance).</item>
    ///   <item>At each <see cref="ITileLayer"/> entry point, a VM-compilable filter over a
    ///     <see cref="INativeFilterSource"/>-capable layer is evaluated by the Burst filter VM instead of
    ///     <see cref="CompiledFilter"/> — a representation change only; see <see cref="NativeProgramFor"/>
    ///     and the native/managed dispatch in the private <c>SelectFeaturesInto</c> helpers.</item>
    /// </list>
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

            // 2. Probe the native-filter seam first — see BindNativeFilter. Only when it declines (no
            // capability, no compilable program, or the layer's own rebind refusal) do we compile/bind the
            // managed filter at all: the native branch never rents a key-binding buffer or compiles
            // CompiledFilter.
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
                // 3. Evaluate per feature — indexed so the native branch has an ordinal to address. The
                // feature IS an IFeature (the neutral carrier implements it directly), so it passes straight
                // to filter.Matches — no adapter alloc.
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
        /// Probes <paramref name="tileLayer"/> for the native-filter capability (<see cref="INativeFilterSource"/>)
        /// and, when present, compiles/memoizes <paramref name="layer"/>'s filter to a
        /// <see cref="NativeFilterProgram"/> (<see cref="NativeProgramFor"/>) and asks the layer to bind it.
        /// Returns <c>null</c> — meaning "evaluate on the managed <see cref="CompiledFilter"/> path instead"
        /// — when the layer has no such capability, the filter falls outside the VM's accepted subset, or
        /// the layer itself refuses the bind (see <see cref="INativeFilterSource.TryBindNativeFilter"/>).
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
        /// each paired with its position in <see cref="ITileLayer.Features"/>.
        ///
        /// <para>Takes an already-resolved <see cref="ITileLayer"/> rather than an <see cref="IDecodedTile"/>:
        /// the caller needs the resolved layer anyway (it is the shared geometry buffer's memo key), and
        /// resolving twice would be two chances to resolve differently.</para>
        ///
        /// <para><b><see cref="List{T}"/>, never a <c>NativeArray</c>.</b> Selection is managed evaluation
        /// over managed features; the ordinal → <c>NativeArray&lt;int&gt;</c> conversion belongs to whoever
        /// is filling a blittable buffer, not here.</para>
        ///
        /// <para><paramref name="into"/> is cleared first, so a reused buffer cannot accumulate two layers'
        /// selections. A null/absent <paramref name="tileLayer"/> leaves it empty.</para>
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
        /// <paramref name="features"/> leaves it empty. See the <see cref="ITileLayer"/> overload for the
        /// selection contract.
        ///
        /// <para>No <see cref="ITileLayer"/> is available here to probe for <see cref="IIndexedFeatureSource"/>
        /// capability, so this overload always evaluates on the string key-lookup path (no binding).</para></summary>
        public static void SelectFeatures(
            StyleLayer layer, IReadOnlyList<IFeature> features, double zoom, List<SelectedTileFeature> into)
            => SelectFeaturesInto(layer, features, zoom, into, keyResolver: null, native: null);

        /// <summary>The <see cref="List{T}"/> selection over an ALREADY-FETCHED feature list, WITH native-filter
        /// binding — the caller has both read <see cref="ITileLayer.Features"/> once and still has the layer in
        /// scope to probe for capability. <b><paramref name="features"/> MUST be <paramref name="tileLayer"/>'s
        /// own <see cref="ITileLayer.Features"/></b>: a bound native matcher addresses features by their ordinal
        /// in the layer, so a mismatched list silently selects the wrong feature at that ordinal. See the
        /// <see cref="ITileLayer"/> overload for the selection contract.</summary>
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
        /// appends matches into <paramref name="into"/> starting at index 0 and returns the count written,
        /// instead of growing a <see cref="List{T}"/> by doubling. Rung-1 "allocate nothing" for the mesh-build
        /// worker pass — the caller passes a grow-only array sized to at least <c>tileLayer.Features.Count</c>
        /// (the selection can never exceed the source layer's own feature count), e.g.
        /// <c>TileBuildBuffers.SelectionBuffer</c> in <c>MapRenderer.Unity</c>.
        ///
        /// <para>Unlike the <see cref="List{T}"/> overload, this does <b>not</b> clear <paramref name="into"/>
        /// first — the caller is expected to read only the returned <c>[0, count)</c> prefix, exactly the
        /// grow-only-buffer contract a pooled scratch array already carries.</para>
        /// </summary>
        public static int SelectFeatures(
            StyleLayer layer, ITileLayer tileLayer, double zoom, SelectedTileFeature[] into)
        {
            var keyResolver = (tileLayer as IIndexedFeatureSource)?.KeyResolver;
            INativeFeatureMatcher native = BindNativeFilter(tileLayer, layer);
            return SelectFeaturesInto(layer, tileLayer?.Features, zoom, into, keyResolver, native);
        }

        /// <summary>
        /// The scratch-buffer selection over an ALREADY-FETCHED feature list — the caller reads
        /// <c>ITileLayer.Features</c> ONCE and passes it here, so a worker pass that also sizes its scratch
        /// buffer from <c>features.Count</c> touches the <c>Features</c> accessor exactly once per layer, not
        /// twice (pinned by <c>RunWorkerPass_ObtainsGeometryWithoutReReadingTheFeatureList</c>). Appends
        /// matches into <paramref name="into"/> at <c>[0, count)</c> and returns the count; does not clear
        /// <paramref name="into"/> (grow-only-buffer contract).
        ///
        /// <para>No <see cref="ITileLayer"/> is available here to probe for <see cref="IIndexedFeatureSource"/>
        /// capability, so this overload always evaluates on the string key-lookup path (no binding).</para>
        /// </summary>
        public static int SelectFeatures(
            StyleLayer layer, IReadOnlyList<IFeature> features, double zoom, SelectedTileFeature[] into)
            => SelectFeaturesInto(layer, features, zoom, into, keyResolver: null, native: null);

        /// <summary>The scratch-buffer selection over an ALREADY-FETCHED feature list, WITH native-filter
        /// binding — the caller has both read <see cref="ITileLayer.Features"/> once (for its own buffer
        /// sizing) and still has the layer in scope to probe for capability. <b><paramref name="features"/>
        /// MUST be <paramref name="tileLayer"/>'s own <see cref="ITileLayer.Features"/></b>: a bound native
        /// matcher addresses features by their ordinal in the layer, so a mismatched list silently selects the
        /// wrong feature at that ordinal. See the <see cref="ITileLayer"/> overload for the selection contract
        /// and the grow-only-buffer contract.</summary>
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
        /// Resolves <paramref name="filter"/>'s <see cref="CompiledFilter.KeyLayout"/> against
        /// <paramref name="keyResolver"/> ONCE for this call — not once per feature — returning a rented
        /// <c>binding[slot] = keyIndex</c> array (<c>-1</c> for a name the layer's table lacks, mirroring
        /// <see cref="IFeatureKeyResolver.TryResolveKey"/> returning false) for
        /// <see cref="CompiledFilter.Matches(IFeature, double, int[])"/> to read per feature. Returns
        /// <c>null</c> — meaning "every constant-key get/has node falls to the string path" — when the
        /// filter has no such node, or the feature source advertised no <see cref="IIndexedFeatureSource"/>
        /// capability. The caller MUST pair a non-null result with <see cref="ReturnKeys"/> in a
        /// <c>finally</c>.
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
        /// The <see cref="CompiledFilter"/> for <paramref name="layer"/>, compiled on first use and reused
        /// thereafter — <see cref="CompiledFilter"/>'s own contract ("built once per style layer, evaluated
        /// many times per feature"); compiling per call would violate it.
        ///
        /// <para><b>Keyed on the filter JSON node, not on the <see cref="StyleLayer"/>.</b>
        /// <see cref="StyleLayer.Filter"/> is a public mutable field, so a layer-keyed memo could serve a
        /// compile of a filter the layer no longer has. Keying on the node means reassigning
        /// <c>Filter</c> simply misses the memo and recompiles. (Mutating a <see cref="JsonValue"/> tree
        /// <i>in place</i> would still stale it — nothing does; the style document is parsed once and read.)</para>
        ///
        /// <para><b>Why a <see cref="ConditionalWeakTable{TKey,TValue}"/>:</b> tile processing runs off the
        /// main thread, so the memo must be thread-safe — a plain <c>Dictionary</c> here would be a data
        /// race. It also holds keys <i>weakly</i>, so the entries for a style go away with the style
        /// document rather than pinning every filter ever parsed for the life of the process. A racing pair
        /// of callers may both compile; only one instance is published, and the loser is garbage —
        /// <see cref="CompiledFilter.Compile"/> is pure, so that is harmless.</para>
        ///
        /// <para>A null/absent filter compiles to the shared match-all sentinel, which is free — no memo
        /// entry is made for it. A malformed filter throws out of here exactly as it did before, and
        /// nothing is cached, so the next call throws too.</para>
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
