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
    /// One fill-extrusion layer's <see cref="ILayerMeshBuild"/> — owns the request columns plus the roof AND
    /// wall geometry as ONE <see cref="FillExtrusionGraphOutput"/> (job-scheduling-design.md §8 stage 5's own
    /// unpacking argument no longer applies once a build owns both: <see cref="FillExtrusionGraphOutput.Dispose"/>
    /// already IS the roof/walls completion-then-free order, so this type reuses it rather than restating it).
    /// Pooled via <see cref="LayerMeshBuildPool{T}"/>.
    /// </summary>
    internal sealed class FillExtrusionLayerBuild : ILayerMeshBuild
    {
        private FillMeshPipeline.LayerInput _input;
        private NativeArray<Vector4>        _featureColors;
        private NativeArray<Vector2>        _featureBake;
        private int                         _materialIndex;
        private string                      _payloadName;

        private FillExtrusionGraphOutput _ext;
        private MeshWriteOutput          _write;
        private bool                     _disposed;

        // Pool-only — real construction happens via Reset, called from Rent after LayerMeshBuildPool<T>.Rent().
        public FillExtrusionLayerBuild() { }

        /// <summary>Rents a pooled instance and resets it to this build's own inputs — see
        /// <see cref="FillLayerBuild.Rent"/>'s own doc for why <see cref="LayerMeshBuildCounters.RecordRented"/> is
        /// unconditional here.</summary>
        internal static FillExtrusionLayerBuild Rent(
            FillMeshPipeline.LayerInput input, NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            int materialIndex, string payloadName)
        {
            FillExtrusionLayerBuild build = LayerMeshBuildPool<FillExtrusionLayerBuild>.Rent();
            build.Reset(input, featureColors, featureBake, materialIndex, payloadName);
            LayerMeshBuildCounters.RecordRented();
            return build;
        }

        /// <summary>Re-initializes a pooled (or freshly-minted) instance — every field <see cref="Dispose"/>
        /// reads, so a reused instance never leaks a prior build's state into the next one.</summary>
        private void Reset(
            FillMeshPipeline.LayerInput input, NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            int materialIndex, string payloadName)
        {
            _input         = input;
            _featureColors = featureColors;
            _featureBake   = featureBake;
            _materialIndex = materialIndex;
            _payloadName   = payloadName;
            _ext           = default;
            _write         = default;
            _disposed      = false;
        }

        public JobHandle ScheduleMeasure(JobHandle deps)
        {
            _ext = FillExtrusionMeshGraph.Schedule(_input, _featureColors, _featureBake, deps);
            return _ext.Handle;
        }

        public bool TryScheduleWrite(out JobHandle writeHandle)
        {
            writeHandle = default;
            if (!_ext.Roof.IsCreated) return false;

            if (_ext.Roof.Error.Value != FillGraphCounts.Ok)
            {
                Debug.LogWarning(
                    $"[TileBuildGraph] layer '{_payloadName}' faulted during measure (error code " +
                    $"{_ext.Roof.Error.Value}) — settling as zero-vertex, no mesh registered.");
                return false;
            }

            // No empty check here — StyledFillExtrusionTileBuilder.ScheduleWrite is self-guarding by design
            // (DIV-A5): it returns IsCreated == false itself for an empty roof, unlike the fill arm above.
            TileGeometryBuffers geometry = _input.Geometry;
            _write = StyledFillExtrusionTileBuilder.ScheduleWrite(
                _ext.Roof, _featureColors, _featureBake, _ext.Walls, _input.Projection, geometry.Tile, geometry.Extent);
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

            // _write completes and frees FIRST: its job reads _ext's Roof/Walls buffers (ScheduleWrite is
            // called with both as inputs), so disposing _ext first would free those buffers while the write
            // job could still be in flight — ILayerMeshBuild.Dispose's own documented order.
            // FillExtrusionGraphOutput.Dispose() IS the completion-then-free order for _ext's own two
            // terminals (Handle.Complete() → Roof.Dispose() → Walls.Dispose()) — reused, not restated.
            _write.Dispose();
            _ext.Dispose();
            _input.RingVisitOrder.Dispose();
            _featureColors.Dispose();
            _featureBake.Dispose();

            LayerMeshBuildCounters.RecordDisposed();
            LayerMeshBuildPool<FillExtrusionLayerBuild>.Return(this);
        }
    }
}
