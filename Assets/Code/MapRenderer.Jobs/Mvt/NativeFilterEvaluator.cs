using System;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The native filter VM's per-feature managed entry point: reads a feature's tag slice, runs
    /// <see cref="NativeFilterEvalJob"/> synchronously, and reports the result — the seam the parity
    /// oracle and the zero-allocation tooth exercise. Reuses its length-1 result buffers across calls
    /// (construct once per scope, <see cref="Dispose"/> when done), so a warm per-feature loop adds no
    /// per-call native allocation.
    ///
    /// <para>Declared beside <see cref="NativeFilterEvalJob"/> in the decoder folder rather than with the
    /// rest of the VM in <c>MapRenderer.Jobs.Expressions</c>, because <see cref="Evaluate"/> names
    /// <see cref="MvtLayer"/> and <see cref="MvtFeature"/> — see that type's doc for why.</para>
    /// </summary>
    internal sealed class NativeFilterEvaluator : IDisposable
    {
        private NativeArray<byte> _resultMatched;
        private NativeArray<byte> _resultError;

        internal NativeFilterEvaluator(Allocator allocator)
        {
            _resultMatched = new NativeArray<byte>(1, allocator);
            _resultError = new NativeArray<byte>(1, allocator);
        }

        /// <summary>
        /// Evaluates <paramref name="program"/> — already rebound into <paramref name="binding"/> (see
        /// <see cref="NativeFilterRebind.Rebind"/>) — against <paramref name="layer"/>'s feature at
        /// <paramref name="featureIndex"/>.
        /// </summary>
        internal void Evaluate(
            NativeFilterProgram program, NativeArray<int> binding, MvtLayer layer, int featureIndex,
            out bool matched, out NativeFilterError error)
        {
            // All native inputs come through the native-column CAPABILITY, never a concrete store type:
            // the slice by ordinal, plus the shared tag-word/value tables. GeometryKind is a feature
            // attribute (not a store concern). A source that isn't native-column-capable never reaches
            // here — the compiler/selection gate keeps it on the managed path.
            INativeFilterColumns columns = layer.DenseKeyResolver;
            columns.TryGetFeatureSlice(featureIndex, out int offset, out int count);

            var job = new NativeFilterEvalJob
            {
                Program = program.Ops,
                Binding = binding,
                TagWords = columns.TagWords,
                Values = columns.Values,
                TagOffset = offset,
                TagCount = count,
                GeometryKind = (int)layer.Features[featureIndex].GeometryType,
                ResultMatched = _resultMatched,
                ResultError = _resultError,
            };
            job.Run();

            matched = _resultMatched[0] != 0;
            error = (NativeFilterError)_resultError[0];
        }

        public void Dispose()
        {
            if (_resultMatched.IsCreated) _resultMatched.Dispose();
            if (_resultError.IsCreated) _resultError.Dispose();
        }
    }
}
