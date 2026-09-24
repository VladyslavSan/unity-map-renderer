using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// One line layer's <see cref="ILayerMeshBuild"/> — owns <see cref="LayerInput"/>'s own columns
    /// (<c>Geometry</c> excepted — BORROWED), <see cref="LineGraphOutput"/> and <see cref="MeshWriteOutput"/>.
    /// Pooled via <see cref="LayerMeshBuildPool{T}"/>.
    /// </summary>
    internal sealed class LineLayerBuild : ILayerMeshBuild
    {
        private LayerInput       _input;
        private NativeArray<Vector4> _featureColors;
        private NativeArray<float>   _featureWidths;
        private int                  _materialIndex;
        private string               _payloadName;

        private LineGraphOutput _measure;
        private MeshWriteOutput _write;
        private bool             _disposed;

        // Pool-only — real construction happens via Reset, called from Rent after LayerMeshBuildPool<T>.Rent().
        public LineLayerBuild() { }

        /// <summary>Rents a pooled instance and resets it to this build's own inputs — see
        /// <see cref="FillLayerBuild.Rent"/>'s own doc for why <see cref="LayerMeshBuildCounters.RecordRented"/> is
        /// unconditional here.</summary>
        internal static LineLayerBuild Rent(
            LayerInput input, NativeArray<Vector4> featureColors, NativeArray<float> featureWidths,
            int materialIndex, string payloadName)
        {
            LineLayerBuild build = LayerMeshBuildPool<LineLayerBuild>.Rent();
            build.Reset(input, featureColors, featureWidths, materialIndex, payloadName);
            LayerMeshBuildCounters.RecordRented();
            return build;
        }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance — every field <see cref="Dispose"/>
        /// reads, so a reused instance never leaks a prior build's state into the next one.</summary>
        private void Reset(
            LayerInput input, NativeArray<Vector4> featureColors, NativeArray<float> featureWidths,
            int materialIndex, string payloadName)
        {
            _input         = input;
            _featureColors = featureColors;
            _featureWidths = featureWidths;
            _materialIndex = materialIndex;
            _payloadName   = payloadName;
            _measure       = default;
            _write         = default;
            _disposed      = false;
        }

        public JobHandle ScheduleMeasure(JobHandle deps)
        {
            _measure = LineMeshGraph.Schedule(_input, deps);
            return _measure.Handle;
        }

        public bool TryScheduleWrite(out JobHandle writeHandle)
        {
            writeHandle = default;
            if (!_measure.IsCreated) return false;

            if (_measure.Error.Value != LineGraphCounts.Ok)
            {
                Debug.LogWarning(
                    $"[TileBuildGraph] layer '{_payloadName}' faulted during measure (error code " +
                    $"{_measure.Error.Value}) — settling as zero-vertex, no mesh registered.");
                return false;
            }

            if (_measure.Vertices.Length == 0 || _measure.Indices.Length == 0)
                return false;

            _write = StyledLineTileBuilder.ScheduleWrite(_measure, _featureColors, _featureWidths);
            if (!_write.IsCreated) return false;

            writeHandle = _write.Handle;
            return true;
        }

        public MeshDataPayload TakePayload()
            => _write.IsCreated ? _write.TakePayload(_payloadName, _materialIndex) : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // _write completes and frees FIRST, because its in-flight job reads _measure's buffers
            // (ILayerMeshBuild.Dispose's order).
            _write.Dispose();
            _measure.Dispose();
            _input.FeatureSelected.Dispose();
            _featureColors.Dispose();
            _featureWidths.Dispose();

            LayerMeshBuildCounters.RecordDisposed();
            LayerMeshBuildPool<LineLayerBuild>.Return(this);
        }
    }
}
