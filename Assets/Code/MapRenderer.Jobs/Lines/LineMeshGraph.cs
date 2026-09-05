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
    /// Schedules the line measure graph over one <see cref="LineLayerInput"/>, returning an UNCOMPLETED
    /// <see cref="LineGraphOutput"/> (job-scheduling-design.md §8 stage 5). Chain: gather the layer's
    /// selected LineString rings (<see cref="LineRingGatherJob"/>) → project the ORIGINAL centerline to
    /// per-point surface up (<c>TileToGeoJob</c> → <c>ProjectionDispatch</c>, the subdivision metric) →
    /// curvature-subdivide in tile space (<see cref="LineSubdivideJob"/>) → project the SUBDIVIDED
    /// centerline to origin-relative render space (<c>TileToGeoJob</c> → <c>ProjectionDispatch</c> again) →
    /// build the 3D ribbon: size (<see cref="LineRibbonSizingJob"/>) → per-ring parallel
    /// (<see cref="LineRibbonBatchJob"/>) → aggregate, winding swap + rebase
    /// (<see cref="LineRibbonAggregateJob"/>), job-scheduling-design.md §8 stage 6. Mirrors the managed line
    /// builder's original "Subdivide → Project → Triangulate" ordering, scheduled instead of run (that
    /// managed ring loop retired with job-scheduling-design.md §8 stage 5 Group B — this graph is the sole
    /// mesher now).
    ///
    /// <para>The <c>.AsArray()</c>/deferred-list rule <see cref="FillMeshGraph"/>'s doc states applies here
    /// too: every node either holds a <see cref="NativeList{T}"/> field and resolves it inside
    /// <c>Execute</c>, or takes <c>AsDeferredJobArray()</c> resolved at execute time — never a schedule-time
    /// <c>.AsArray()</c> snapshot of a list a later node resizes.</para>
    ///
    /// <b>No <c>Complete()</c> anywhere in this file.</b>
    /// </summary>
    public static class LineMeshGraph
    {
        /// <summary>Vertices per batch for this graph's TWO <see cref="TileToGeoJob"/> nodes
        /// (job-scheduling-design.md §8 stage 6) — same reasoning as <see cref="FillMeshGraph.VertexBatch"/>:
        /// ~10-50 µs of work per 1024 vertices, comfortably above a batch hand-off's own cost. A starting
        /// value chosen by this reasoning, not a measured optimum — see the design doc's dated measurement
        /// before moving it.</summary>
        internal const int VertexBatch = 1024;

        /// <summary>Rings per batch for <see cref="LineRibbonBatchJob"/> (job-scheduling-design.md §8 stage
        /// 6). <c>1</c>: same reasoning as <see cref="FillMeshGraph.EarcutPolygonBatch"/> — a ring's ribbon
        /// cost varies with its join/cap decisions, not linearly with point count, so per-ring work is
        /// uneven; batch 1 lets the job system's work-stealing act as the load balancer.</summary>
        internal const int RibbonRingBatch = 1;

        /// <summary>Per-layer ribbon vertex ceiling (D2, job-scheduling-design.md §10). Threaded to
        /// <see cref="LineRibbonBatchJob"/> through <see cref="LineLayerInput.MaxOutputVertices"/> — never
        /// read directly inside a job, so a test can drive its own ceiling with a synthetic ring without
        /// this constant changing what it observes.
        ///
        /// <para><b>Selection rule</b> (not a verified property of the number below — nobody can check that
        /// from source): at least 8× the largest per-layer ribbon vertex count the corpus produces, measured
        /// over the same boundary fixtures §5(a) uses. The measured number this stage's commit body
        /// records is what backs <c>400_000</c>; raising this constant later does not need to touch this
        /// doc, only the commit that raises it.</para></summary>
        public const int DefaultMaxOutputVertices = 400_000;

        /// <summary>Schedules the line measure graph for one layer. Returns <see cref="default"/>
        /// (<c>IsCreated == false</c>) when there is nothing to draw — the borrowed-input guard: an
        /// uncreated geometry buffer or an uncreated per-feature selection column means nothing to gather.
        /// Otherwise returns a <see cref="LineGraphOutput"/> whose <c>Handle</c> is UNCOMPLETED; the caller
        /// polls or completes it before reading any field.
        /// </summary>
        /// <param name="input">By value, not <c>in</c> — <see cref="LineLayerInput"/> is a mutable struct,
        /// and the conventions gate is <c>in</c> ⟺ <c>readonly struct</c>.</param>
        /// <param name="deps">Upstream dependency this whole layer's chain must wait for.</param>
        /// <remarks><c>public</c> (not <c>internal</c> per the plan's literal wording): <c>MapRenderer.Jobs</c>
        /// grants <c>InternalsVisibleTo</c> only to the test assemblies (<c>InternalsVisibleTo.cs</c>), not to
        /// <c>MapRenderer.Unity</c> — the same reason <see cref="FillMeshGraph.Schedule"/> and
        /// <see cref="FillGraphOutput"/> are public. Production callers
        /// (<c>StyledLineTileBuilder</c>/<c>LineRenderLayer</c>/<c>TileBuildGraph</c>) live in that assembly
        /// and must reach both this method and <see cref="LineGraphOutput"/> across the assembly
        /// boundary.</remarks>
        public static LineGraphOutput Schedule(LineLayerInput input, JobHandle deps = default)
        {
            if (!input.Geometry.IsCreated || !input.FeatureSelected.IsCreated)
                return default;

            // Validated HERE, before any node schedules — see FillMeshGraph.Schedule's own doc for why a
            // throw after nodes are already in flight would strand jobs the caller's cleanup cannot complete.
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

        /// <summary>The GENERIC entry point — takes the concrete projection struct directly, so a caller can
        /// drive a projection Burst never registered generically for (tooth (g),
        /// <c>RightHandedSphereProjectionWindingTests</c>, called from <c>TestTileMeshBuilder.BuildLineFromLayer{TProj}</c>
        /// via the test assemblies' <c>InternalsVisibleTo</c> grant) without going through <see cref="Schedule"/>'s
        /// closed switch. Calls <see cref="ProjectionDispatch.ScheduleTyped{TProj}"/> directly, not
        /// <see cref="ProjectionDispatch.Schedule"/> — the same reason that method's own switch stays closed
        /// (A1.8's own doc).</summary>
        internal static LineGraphOutput ScheduleTyped<TProj>(LineLayerInput input, TProj projection, JobHandle deps)
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

            JobHandle gathered = new LineRingGatherJob
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

            // Computes a WORLD column nothing reads — the subdivision metric is .Up only, mirroring
            // StyledLineTileBuilder.cs:320. Deliberate: do not "optimise" this into a bug.
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

            JobHandle subdivided = new LineSubdivideJob
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

            // ── Build the 3D ribbon per ring — sizing (serial) → ribbon (IJobParallelForDefer over rings) →
            // aggregate (serial), job-scheduling-design.md §8 stage 6, E.2. The earcut's sizing→parallel→
            // aggregate shape verbatim: a ring's true vertex/index count is not analytically predictable
            // (it falls out of LineRibbonJob's join/cap decisions), exactly the earcut's own reason. ────────
            var vertices         = LineGraphOutput.AllocateOutputList<LineRibbonVertex>();
            var vertexFeatureIdx = LineGraphOutput.AllocateOutputList<int>();
            var indices          = LineGraphOutput.AllocateOutputList<int>();
            var error            = LineGraphOutput.AllocateError();

            LineRibbonBuffers ribbonBuffers = LineRibbonBuffers.Allocate();

            // Sizing and aggregate SHARE `error` (not a separate reference): LineGraphOutput.AllocateError()
            // hands out a zero-initialised NativeReference<int> (Ok == 0), and neither node ever resets it —
            // each only conditionally SETS it on its own failure. Sizing runs strictly before aggregate, so
            // this is NOT "whichever fires first (if either)" — a naive reading would let aggregate's own
            // check overwrite a real sizing error (last-writer-wins). It cannot: a sizing failure returns
            // before its Resize calls, leaving PerRingVertexCount/PerRingIndexCount at length 0, and
            // LineRibbonAggregateJob bounds its own loop by PerRingVertexCount.Length — so aggregate's error
            // condition structurally cannot evaluate true in the same run sizing's already did. The two
            // conditions are mutually exclusive by construction, not merely by scheduling order — mirrors
            // FillGraphOutput's Error posture across FillSizingJob/FillAggregateJob exactly (same bound fix).
            JobHandle sized = new LineRibbonSizingJob
            {
                RingSubOffsets = ringSubOffsets, RoundSegments = input.RoundSegments,
                Buffers = ribbonBuffers, Error = error,
            }.Schedule(subdivided);

            JobHandle ribboned = new LineRibbonBatchJob
            {
                SubWorld = subWorld, SubUp = subUp, RingSubOffsets = ringSubOffsets,
                Join = input.Join, Cap = input.Cap, MiterLimit = input.MiterLimit,
                RoundSegments = input.RoundSegments, RoundLimit = input.RoundLimit,
                Buffers = ribbonBuffers,
            }.Schedule(ribbonBuffers.PerRingVertexCount, RibbonRingBatch, JobHandle.CombineDependencies(subProjected, sized));

            JobHandle aggregated = new LineRibbonAggregateJob
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
