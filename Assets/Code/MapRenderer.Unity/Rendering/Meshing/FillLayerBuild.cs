using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// One fill layer's <see cref="ILayerMeshBuild"/> — owns the request columns
    /// <see cref="FillMeshPipeline.LayerInput"/> carries (<c>Input.Geometry</c> excepted — BORROWED, never
    /// disposed here) plus <see cref="FillGraphOutput"/> and <see cref="MeshWriteOutput"/>. Pooled via
    /// <see cref="LayerMeshBuildPool{T}"/> — never allocated per build.
    /// </summary>
    internal sealed class FillLayerBuild : ILayerMeshBuild
    {
        private FillMeshPipeline.LayerInput _input;
        private NativeArray<Vector4>        _featureColors;
        private int                         _materialIndex;
        private string                      _payloadName;

        private FillGraphOutput _measure;
        private MeshWriteOutput _write;
        private bool             _disposed;

        // Pool-only — real construction happens via Reset, called from Rent after LayerMeshBuildPool<T>.Rent().
        public FillLayerBuild() { }

        /// <summary>Rents a pooled instance and resets it to this build's own inputs — the only construction
        /// path (§2.2): a pooled class has no object-initializer bypass, so <see cref="LayerMeshBuildCounters.RecordRented"/>
        /// is unconditional here — the caller (<see cref="Style.FillRenderLayer.BuildGraphRequest"/>) only
        /// reaches this once its own emptiness gate (<c>input.RingVisitOrder.IsCreated</c>) has already
        /// passed.</summary>
        internal static FillLayerBuild Rent(
            FillMeshPipeline.LayerInput input, NativeArray<Vector4> featureColors,
            int materialIndex, string payloadName)
        {
            FillLayerBuild build = LayerMeshBuildPool<FillLayerBuild>.Rent();
            build.Reset(input, featureColors, materialIndex, payloadName);
            LayerMeshBuildCounters.RecordRented();
            return build;
        }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance — every field <see cref="Dispose"/>
        /// reads, so a reused instance never leaks a prior build's state into the next one.</summary>
        private void Reset(
            FillMeshPipeline.LayerInput input, NativeArray<Vector4> featureColors,
            int materialIndex, string payloadName)
        {
            _input         = input;
            _featureColors = featureColors;
            _materialIndex = materialIndex;
            _payloadName   = payloadName;
            _measure       = default;
            _write         = default;
            _disposed      = false;
        }

        public JobHandle ScheduleMeasure(JobHandle deps)
        {
            _measure = FillMeshGraph.Schedule(_input, deps);
            return _measure.Handle;
        }

        public bool TryScheduleWrite(out JobHandle writeHandle)
        {
            writeHandle = default;
            if (!_measure.IsCreated) return false;

            if (_measure.Error.Value != FillGraphCounts.Ok)
            {
                Debug.LogWarning(
                    $"[TileBuildGraph] layer '{_payloadName}' faulted during measure (error code " +
                    $"{_measure.Error.Value}) — settling as zero-vertex, no mesh registered.");
                return false;
            }

            // StyledFillTileBuilder.ScheduleWrite is NOT self-guarding — it allocates and returns
            // IsCreated == true unconditionally, so this empty check must stay here (unlike
            // FillExtrusionLayerBuild's own arm, self-guarding by design — DIV-A5).
            if (_measure.TileVertices.Length == 0 || _measure.TriangleIndices.Length == 0)
                return false;

            TileGeometryBuffers geometry = _input.Geometry;
            _write = StyledFillTileBuilder.ScheduleWrite(_measure, _featureColors, geometry.Tile, geometry.Extent);
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

            // _write completes and frees FIRST: its job reads _measure's buffers (ScheduleWrite is called
            // with _measure as an input), so disposing _measure first would free those buffers while the
            // write job could still be in flight — ILayerMeshBuild.Dispose's own documented order.
            _write.Dispose();
            _measure.Dispose();
            _input.RingVisitOrder.Dispose();
            _featureColors.Dispose();

            LayerMeshBuildCounters.RecordDisposed();
            LayerMeshBuildPool<FillLayerBuild>.Return(this);
        }
    }
}
