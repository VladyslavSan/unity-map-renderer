using Unity.Collections;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The MVT <see cref="INativeFeatureMatcher"/>: a rebound <see cref="NativeFilterProgram"/> plus the
    /// <see cref="NativeFilterEvaluator"/> scratch it runs through, both <c>Allocator.Persistent</c> —
    /// minted per selection by <see cref="NativeFilterSelection.TryBind"/> and disposed by the caller.
    ///
    /// <para>A per-layer-per-build native allocation of the evaluator's two length-1 result arrays.
    /// Acceptable for a correctness-first stage; batching the VM into a single job over the layer's
    /// offset column (deferred) removes it.</para>
    /// </summary>
    internal sealed class MvtNativeFeatureMatcher : INativeFeatureMatcher
    {
        private readonly NativeFilterProgram _program;
        private readonly NativeArray<int> _binding;
        private readonly MvtLayer _layer;
        private readonly NativeFilterEvaluator _eval;

        /// <param name="program">The compiled program this matcher was bound against.</param>
        /// <param name="binding">The rebound <c>slot→id</c> array — owned by this matcher from here on.</param>
        /// <param name="layer">The layer this matcher evaluates features of.</param>
        internal MvtNativeFeatureMatcher(NativeFilterProgram program, NativeArray<int> binding, MvtLayer layer)
        {
            _program = program;
            _binding = binding;
            _layer = layer;
            _eval = new NativeFilterEvaluator(Allocator.Persistent);
        }

        /// <summary>True iff feature <paramref name="ordinal"/> matches and the VM reported no error — a
        /// VM error excludes, mirroring <c>CompiledFilter</c>'s caught-exception → exclude.</summary>
        public bool Matches(int ordinal)
        {
            _eval.Evaluate(_program, _binding, _layer, ordinal, out bool matched, out NativeFilterError error);
            return matched && error == NativeFilterError.None;
        }

        /// <summary>Disposes the rebound binding, then the evaluator scratch — both <c>Allocator.Persistent</c>.</summary>
        public void Dispose()
        {
            _binding.Dispose();
            _eval.Dispose();
        }
    }
}
