using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The MVT <see cref="INativeFeatureMatcher"/>: a rebound <see cref="NativeFilterProgram"/> plus the
    /// per-feature result/error/geometry-kind columns <see cref="MatchAll"/> runs the batched
    /// <see cref="NativeFilterEvaluationJob"/> over, all <c>Allocator.Persistent</c> — minted per selection
    /// by <see cref="NativeFilterSelection.TryBind"/> and disposed by the caller.
    ///
    /// <para>A per-layer-per-build native allocation count of <b>three</b>, each sized to the layer's own
    /// feature count (<c>O(featureCount)</c>, not the pre-batching <c>O(1)</c> pair) — batching the VM into
    /// one job removes the per-feature <i>dispatch</i> (a struct copy, a job launch, five safety-handle
    /// checks, once per feature); it does not remove the per-selection allocation, which merely grew from
    /// two length-1 arrays to three feature-length ones.</para>
    /// </summary>
    internal sealed class MvtNativeFeatureMatcher : INativeFeatureMatcher
    {
        private readonly NativeFilterProgram _program;
        private readonly NativeArray<int> _binding;
        private readonly MvtLayer _layer;
        private NativeArray<byte> _results;
        private NativeArray<byte> _errors;
        private NativeArray<int> _kinds;

        /// <param name="program">The compiled program this matcher was bound against.</param>
        /// <param name="binding">The rebound <c>slot→id</c> array — owned by this matcher from here on.</param>
        /// <param name="layer">The layer this matcher evaluates features of.</param>
        internal MvtNativeFeatureMatcher(NativeFilterProgram program, NativeArray<int> binding, MvtLayer layer)
        {
            _program = program;
            _binding = binding;
            _layer = layer;

            int featureCount = layer.Features.Count;
            _results = new NativeArray<byte>(featureCount, Allocator.Persistent);
            _errors = new NativeArray<byte>(featureCount, Allocator.Persistent);
            // Built unconditionally, once per selection, from the same source MvtFeature.GeometryType
            // already reads (MvtDecoder fills both from the same decoded header) — never borrowed from
            // MvtLayer.Geometry, which may be uncreated for a layer with no adopted geometry. See
            // NativeFilterEvaluationJob's Kinds doc.
            _kinds = new NativeArray<int>(featureCount, Allocator.Persistent);
            for (int i = 0; i < featureCount; i++)
                _kinds[i] = (int)layer.Features[i].GeometryType;
        }

        /// <summary>Runs the batched VM job over <c>[0, featureCount)</c> — this matcher's own feature count,
        /// the same <c>layer.Features.Count</c> the ctor sized <c>_results</c>/<c>_errors</c>/<c>_kinds</c>
        /// from, so there is one source for the bound, not two — and returns the result column. A
        /// feature-less layer returns the (already length-0) column without touching any job field — the
        /// hazard is a batch call placed before an empty selection's loop, not the loop itself.</summary>
        public NativeArray<byte> MatchAll()
        {
            int featureCount = _results.Length;
            if (featureCount == 0) return _results;

            INativeFilterColumns columns = _layer.DenseKeyResolver;
            var job = new NativeFilterEvaluationJob
            {
                Program = _program.Operations,
                Binding = _binding,
                TagWords = columns.TagWords,
                Values = columns.Values,
                TagOffsets = columns.TagOffsets,
                TagLengths = columns.TagLengths,
                Kinds = _kinds,
                FeatureCount = featureCount,
                ResultMatched = _results,
                ResultError = _errors,
            };
            job.RunByRef();
            return _results;
        }

        /// <summary>The per-feature <see cref="NativeFilterError"/> codes from the last <see cref="MatchAll"/>
        /// call, as raw bytes — test-only introspection (production reads only the match bits <see cref="MatchAll"/>
        /// returns).</summary>
        internal NativeArray<byte> Errors => _errors;

        /// <summary>Disposes the rebound binding, then the three per-selection columns — all
        /// <c>Allocator.Persistent</c>.</summary>
        public void Dispose()
        {
            _binding.Dispose();
            _results.Dispose();
            _errors.Dispose();
            _kinds.Dispose();
        }
    }
}
