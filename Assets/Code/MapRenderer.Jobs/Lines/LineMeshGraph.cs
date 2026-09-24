using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;

using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Projection;
namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// Schedules the line measure graph over one <see cref="LayerInput"/>, and returns an UNCOMPLETED
    /// <see cref="LineGraphOutput"/>. Chain: gather rings → project the original centerline to surface up →
    /// subdivide in tile space → project the subdivided centerline → ribbon size → per-ring ribbon →
    /// aggregate. Non-local invariant: no node takes a schedule-time <c>.AsArray()</c> of a list a later node
    /// resizes, as in <see cref="FillMeshGraph"/>, and nothing here calls <c>Complete()</c>.
    /// </summary>
    public static class LineMeshGraph
    {
        /// <summary>Vertices per batch for this graph's TWO <see cref="TileToGeoJob"/> nodes — same
        /// reasoning as <see cref="FillMeshGraph.VertexBatch"/>: enough work per batch to cover the
        /// hand-off. A starting value, not a measured optimum.</summary>
        internal const int VertexBatch = 1024;

        /// <summary>Rings per batch for <see cref="RibbonBatchJob"/>. Same reasoning as
        /// <see cref="FillMeshGraph.EarcutPolygonBatch"/>: a ring's ribbon cost varies with its join and cap
        /// decisions, not linearly with point count, so batch 1 lets work-stealing balance the
        /// load.</summary>
        internal const int RibbonRingBatch = 1;

        /// <summary>Per-layer ribbon vertex ceiling. Threaded to <see cref="RibbonBatchJob"/> through
        /// <see cref="LayerInput.MaxOutputVertices"/>, never read directly inside a job, so a test can drive
        /// its own ceiling with a synthetic ring.
        ///
        /// <para><b>Selection rule</b>, which the number alone cannot show: at least 8x the largest
        /// per-layer ribbon vertex count the corpus produces.</para></summary>
        public const int DefaultMaxOutputVertices = 400_000;

        /// <summary>Schedules the line measure graph for one layer. Returns <see cref="default"/>
        /// (<c>IsCreated == false</c>) when there is nothing to draw — the borrowed-input guard: an
        /// uncreated geometry buffer or an uncreated per-feature selection column means nothing to gather.
        /// Otherwise returns a <see cref="LineGraphOutput"/> whose <c>Handle</c> is UNCOMPLETED; the caller
        /// polls or completes it before reading any field.
        /// </summary>
        /// <param name="input">By value, not <c>in</c> — <see cref="LayerInput"/> is a mutable struct, and
        /// <c>in</c> on one forces a defensive copy per member read.</param>
        /// <param name="deps">Upstream dependency this whole layer's chain must wait for.</param>
        /// <remarks><c>public</c>, not <c>internal</c>: <c>MapRenderer.Jobs</c> grants
        /// <c>InternalsVisibleTo</c> only to the test assemblies, and the production callers live in
        /// <c>MapRenderer.Unity</c>.</remarks>
        public static LineGraphOutput Schedule(LayerInput input, JobHandle deps = default)
        {
            if (!input.Geometry.IsCreated || !input.FeatureSelected.IsCreated)
                return default;

            // Validated HERE, before any node schedules: a throw after nodes are in flight strands jobs
            // the caller's cleanup cannot complete.
            switch (input.Projection)
            {
                case SphericalProjection sp:    return ScheduleTyped(input, sp, deps);
                case WebMercatorProjection wm:  return ScheduleTyped(input, wm, deps);
                case null:
                    throw new NotSupportedException(
                        "LineMeshGraph.Schedule received a null Projection — every caller resolves the " +
                        "null-means-Mercator default before reaching a graph, not here. A caller reaching " +
                        "this directly must pass a real IProjection.");
                default:
                    throw new NotSupportedException(
                        $"No LineMeshGraph dispatch for projection type {input.Projection.GetType().Name}. " +
                        "Add a case here, or call ScheduleTyped<TProj> directly for a projection Burst never " +
                        "registers generically (RightHandedSphereProjectionWindingTests' own shape).");
            }
        }

        /// <summary>The GENERIC entry point: takes the concrete projection struct directly, so a caller
        /// can drive a projection Burst never registered generically for, without going through
        /// <see cref="Schedule"/>'s closed switch. It calls
        /// <see cref="ProjectionDispatch.ScheduleTyped{TProj}"/> directly for the same reason.</summary>
        internal static LineGraphOutput ScheduleTyped<TProj>(LayerInput input, TProj projection, JobHandle deps)
            where TProj : struct, IProjection
        {
            TileGeometryBuffers source = input.Geometry;

            // ── Gather this layer's selected LineString rings. ─────────────────────────────────────
            var srcTile        = NewBuffer<double2>(512);
            var ringSrcOffsets = NewBuffer<int>(64);
            var ringFeature    = NewBuffer<int>(64);
            var srcGeo         = NewBuffer<GeoCoordinate>(512);
            var srcWorld       = NewBuffer<double3>(512);
            var srcUp          = NewBuffer<double3>(512);

            JobHandle gathered = new RingGatherJob
            {
                Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                FeatureGeometryType = source.FeatureGeometryType, RingCount = source.RingCount,
                FeatureSelected = input.FeatureSelected,
                OutSrcTile = srcTile, OutRingSrcOffsets = ringSrcOffsets, OutRingFeature = ringFeature,
                OutSrcGeo = srcGeo, OutSrcWorld = srcWorld, OutSrcUp = srcUp,
            }.Schedule(deps);

            // ── Project the ORIGINAL centerline to per-point surface up — the subdivision metric only. ──
            JobHandle srcGeodetic = new TileToGeoJob
            {
                Tile = source.Tile, Extent = source.Extent,
                TileCoords = srcTile.AsDeferredJobArray(), OutGeo = srcGeo.AsDeferredJobArray(),
            }.Schedule(srcTile, VertexBatch, gathered);

            // Computes a WORLD column nothing reads: the subdivision metric is .Up only. Do not
            // "optimise" this into a bug.
            JobHandle srcProjected = ProjectionDispatch.ScheduleTyped(
                projection, input.OriginRender, srcGeo, srcWorld, srcUp, srcGeodetic);

            JobHandle srcGeoDisposed   = ScheduleDispose(srcGeo, srcProjected);
            JobHandle srcWorldDisposed = ScheduleDispose(srcWorld, srcProjected); // dead column

            // ── Curvature-subdivide in tile space. ──────────────────────────────────────────────────
            var subTile       = NewBuffer<double2>(512);
            var ringSubOffsets = NewBuffer<int>(64);
            var subGeo        = NewBuffer<GeoCoordinate>(512);
            var subWorld      = NewBuffer<double3>(512);
            var subUp         = NewBuffer<double3>(512);

            JobHandle subdivided = new SubdivideJob
            {
                SrcTile = srcTile, RingSrcOffsets = ringSrcOffsets, SrcUp = srcUp,
                MaxRefineAngleRad = projection.MaxRefineAngleRad,
                OutSubTile = subTile, OutRingSubOffsets = ringSubOffsets,
                OutSubGeo = subGeo, OutSubWorld = subWorld, OutSubUp = subUp,
            }.Schedule(srcProjected);

            JobHandle srcTileDisposed        = ScheduleDispose(srcTile, subdivided);
            JobHandle srcUpDisposed          = ScheduleDispose(srcUp, subdivided);
            JobHandle ringSrcOffsetsDisposed = ScheduleDispose(ringSrcOffsets, subdivided);

            // ── Project the SUBDIVIDED centerline to origin-relative render space — the ribbon's real
            // position/up columns. ───────────────────────────────────────────────────────────────────
            JobHandle subGeodetic = new TileToGeoJob
            {
                Tile = source.Tile, Extent = source.Extent,
                TileCoords = subTile.AsDeferredJobArray(), OutGeo = subGeo.AsDeferredJobArray(),
            }.Schedule(subTile, VertexBatch, subdivided);

            JobHandle subTileDisposed = ScheduleDispose(subTile, subGeodetic);

            JobHandle subProjected = ProjectionDispatch.ScheduleTyped(
                projection, input.OriginRender, subGeo, subWorld, subUp, subGeodetic);

            JobHandle subGeoDisposed = ScheduleDispose(subGeo, subProjected);

            // ── Build the 3D ribbon per ring: sizing → parallel ribbon → aggregate, because a ring's true
            // vertex/index count falls out of RibbonJob's join/cap decisions. ─────────────────────────────
            var vertices         = LineGraphOutput.AllocateOutputList<LineRibbonVertex>();
            var vertexFeatureIdx = LineGraphOutput.AllocateOutputList<int>();
            var indices          = LineGraphOutput.AllocateOutputList<int>();
            var error            = LineGraphOutput.AllocateError();

            RibbonBuffers ribbonBuffers = RibbonBuffers.Allocate();

            // Sizing and aggregate share `error`; each only sets it. A sizing failure leaves PerRingVertexCount
            // at length 0, so aggregate's loop never runs and cannot overwrite that error.
            JobHandle sized = new RibbonSizingJob
            {
                RingSubOffsets = ringSubOffsets, RoundSegments = input.RoundSegments,
                Buffers = ribbonBuffers, Error = error,
            }.Schedule(subdivided);

            JobHandle ribboned = new RibbonBatchJob
            {
                SubWorld = subWorld, SubUp = subUp, RingSubOffsets = ringSubOffsets,
                Join = input.Join, Cap = input.Cap, MiterLimit = input.MiterLimit,
                RoundSegments = input.RoundSegments, RoundLimit = input.RoundLimit,
                Buffers = ribbonBuffers,
            }.Schedule(ribbonBuffers.PerRingVertexCount, RibbonRingBatch, JobHandle.CombineDependencies(subProjected, sized));

            JobHandle aggregated = new RibbonAggregateJob
            {
                RingFeature = ringFeature,
                Buffers = ribbonBuffers, MaxOutputVertices = input.MaxOutputVertices,
                OutVertices = vertices, OutVertexFeatureIdx = vertexFeatureIdx, OutIndices = indices, Error = error,
            }.Schedule(ribboned);

            JobHandle ribbonBuffersDisposed = ribbonBuffers.DisposeAfter(aggregated);

            JobHandle subWorldDisposed       = ScheduleDispose(subWorld, aggregated);
            JobHandle subUpDisposed          = ScheduleDispose(subUp, aggregated);
            JobHandle ringSubOffsetsDisposed = ScheduleDispose(ringSubOffsets, aggregated);
            JobHandle ringFeatureDisposed    = ScheduleDispose(ringFeature, aggregated);

            JobHandle scratchDisposed = JobHandle.CombineDependencies(
                JobHandle.CombineDependencies(srcGeoDisposed, srcWorldDisposed, srcTileDisposed),
                JobHandle.CombineDependencies(srcUpDisposed, ringSrcOffsetsDisposed, subTileDisposed),
                JobHandle.CombineDependencies(subGeoDisposed, subWorldDisposed, subUpDisposed));
            scratchDisposed = JobHandle.CombineDependencies(
                scratchDisposed, ringSubOffsetsDisposed, ringFeatureDisposed);
            scratchDisposed = JobHandle.CombineDependencies(
                scratchDisposed, ribbonBuffersDisposed);

            JobHandle terminal = JobHandle.CombineDependencies(aggregated, scratchDisposed);

            return new LineGraphOutput
            {
                Vertices = vertices, VertexFeatureIdx = vertexFeatureIdx, Indices = indices, Error = error,
                Handle = terminal, IsCreated = true,
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Allocates one buffer this graph owns and disposes internally — <see cref="Allocator.Persistent"/>,
        /// counted via <see cref="LineGraphOutput.DebugBuffersAllocated"/> so an allocation with no matching
        /// <see cref="ScheduleDispose{T}"/> node is detectable as a pairing mismatch.</summary>
        private static NativeList<T> NewBuffer<T>(int capacity) where T : unmanaged
        {
            LineGraphOutput.RecordBuffersAllocated();
            return new NativeList<T>(capacity, Allocator.Persistent);
        }

        /// <summary>Schedules a <c>Dispose(handle)</c> node for a buffer, counted via
        /// <see cref="LineGraphOutput.DebugBufferDisposeNodes"/>.</summary>
        private static JobHandle ScheduleDispose<T>(NativeList<T> list, JobHandle h) where T : unmanaged
        {
            LineGraphOutput.RecordBufferDisposeNode();
            return list.Dispose(h);
        }
    }
}
