using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Projection;
namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// Schedules the fill measure graph over one <see cref="FillMeshPipeline.LayerInput"/>, and returns an
    /// UNCOMPLETED <see cref="FillGraphOutput"/>. Nothing in this file calls <c>Complete()</c>.
    ///
    /// <para><b>The <c>.AsArray()</c> rule — obeyed everywhere in this file.</b> No <c>.AsArray()</c> at
    /// schedule time on any list a graph node resizes: a view captured before the resize has the stale
    /// length. Every node here either holds the <see cref="NativeList{T}"/> itself and resolves it inside
    /// <c>Execute</c>, or takes <c>AsDeferredJobArray()</c>, which resolves to the list's EXECUTE-time
    /// state.</para>
    ///
    /// <para><b>Input lifetime is a contract.</b> The borrowed <c>input.Geometry</c> and
    /// <c>input.RingVisitOrder</c> stay live <c>[ReadOnly]</c> job inputs until <c>Handle.Complete()</c>.
    /// The caller must not dispose either before it completes the returned
    /// <see cref="FillGraphOutput"/>.</para>
    ///
    /// <para><b>The capacity error flags are never-fired backstops here.</b> A trip leaves every later node
    /// with nothing to do, not with garbage-length flat lists: each node bounds its own loop — or its
    /// deferred count — by a column its sizing job resizes, never by a borrowed count.</para>
    /// </summary>
    public static class FillMeshGraph
    {
        /// <summary>Vertices per batch for this graph's tile→geo node (<see cref="TileToGeoJob"/>).
        /// <see cref="TileToGeoJob.GeoAt"/> runs several transcendental functions per vertex, so 1024
        /// vertices carries enough work to cover a batch hand-off and still splits a corpus tile several
        /// ways. A starting value from that reasoning, not a measured optimum.</summary>
        internal const int VertexBatch = 1024;

        /// <summary>Polygons per batch for <see cref="EarcutBatchJob"/>. Earcut is superlinear in a
        /// polygon's vertex count and corpus polygons vary by orders of magnitude, so batch 1 lets the job
        /// system's work-stealing balance the load instead of pinning a long polygon and its neighbours to
        /// one worker.</summary>
        internal const int EarcutPolygonBatch = 1;

        /// <summary>Schedules the fill measure graph for one layer. Returns <see cref="default"/>
        /// (<c>IsCreated == false</c>) when there is nothing to draw — the borrowed-input guard: an uncreated
        /// or empty <see cref="FillMeshPipeline.LayerInput.RingVisitOrder"/> means nothing to triangulate.
        /// Otherwise returns a <see cref="FillGraphOutput"/> whose <c>Handle</c> is UNCOMPLETED; the caller
        /// polls or completes it before reading any field.
        /// </summary>
        /// <param name="input">By value, not <c>in</c> — <see cref="FillMeshPipeline.LayerInput"/> is a
        /// mutable struct, and <c>in</c> on one forces a defensive copy per member read.</param>
        /// <param name="deps">Upstream dependency this whole layer's chain must wait for.</param>
        public static FillGraphOutput Schedule(
            FillMeshPipeline.LayerInput input, JobHandle deps = default)
        {
            if (!input.Geometry.IsCreated || !input.RingVisitOrder.IsCreated || input.RingVisitOrder.Length == 0)
                return default;

            // Validated HERE, before any node schedules: a throw from deeper in this method strands the
            // already-scheduled jobs that hold input.Geometry, with no terminal handle to Complete() them.
            if (input.Projection == null)
                throw new NotSupportedException(
                    "FillMeshGraph.Schedule received a null Projection — every caller resolves the " +
                    "null-means-Mercator default before reaching a graph (MapCamera's constructor for the " +
                    "live path, and the managed seam's own StyledFillTileBuilder's " +
                    "own ?? DefaultProjection), not here. A caller reaching this directly must pass a real " +
                    "IProjection.");

            TileGeometryBuffers source = input.Geometry;
            NativeArray<int>    visit  = input.RingVisitOrder;
            TileId tile   = source.Tile;
            double extent = source.Extent;

            // ── Main-thread pre-pass: borrowed inputs only, never a job output. ────────────────
            int maxRingLen = 0;
            int totalVerts = 0;
            for (int k = 0; k < visit.Length; k++)
            {
                int ri  = visit[k];
                int len = source.RingOffsets[ri + 1] - source.RingOffsets[ri];
                maxRingLen  = math.max(maxRingLen, len);
                totalVerts += len;
            }

            int maxPolygons = math.max(1, visit.Length); // upper bound: clipping only ever DROPS rings
            int maxHoles    = maxPolygons;

            // ── Select or clip. ─────────────────────────────────────────────────────────────────────
            var outVerts   = NewBuffer<double2>(math.max(1, totalVerts));
            var outOffsets = NewBuffer<int>(visit.Length + 1);
            var outFeatIdx = NewBuffer<int>(math.max(1, visit.Length));

            JobHandle derived;
            JobHandle clipDisposeHandle = default;

            // Kept as a local: the boundary-band node needs to know whether a window exists at all, and the
            // window value cannot say so — an unset double2 is (0,0), the tile's origin corner.
            bool clipEnabled = input.Clip.TryWindow(source.Extent, out double2 clipMin, out double2 clipMax);
            if (clipEnabled)
            {
                int bufferCap = math.max(1, maxRingLen * RingClipJob.BufferLengthMultiplier);
                var bufferA = new NativeArray<double2>(bufferCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var bufferB = new NativeArray<double2>(bufferCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                derived = new RingClipJob
                {
                    Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                    RingVisitOrder = visit, ClipMin = clipMin, ClipMax = clipMax,
                    BufferA = bufferA, BufferB = bufferB,
                    OutVertices = outVerts, OutRingOffsets = outOffsets, OutRingFeatureIdx = outFeatIdx,
                }.Schedule(deps);

                clipDisposeHandle = JobHandle.CombineDependencies(bufferA.Dispose(derived), bufferB.Dispose(derived));
            }
            else
            {
                derived = new RingSelectJob
                {
                    Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                    RingVisitOrder = visit,
                    OutVertices = outVerts, OutRingOffsets = outOffsets, OutRingFeatureIdx = outFeatIdx,
                }.Schedule(deps);
            }

            // ── Ring assembly. ────────────────────────────────────────────────────────────────────────
            var polys = PolygonDescriptors.Allocate(maxPolygons, maxHoles);

            JobHandle assembled = new RingAssemblyJob
            {
                Vertices = outVerts.AsDeferredJobArray(), RingOffsets = outOffsets.AsDeferredJobArray(),
                RingFeatureIdx = outFeatIdx.AsDeferredJobArray(),
                RingCountFromOffsetsLength = true,
                FeatureGeometryType = source.FeatureGeometryType, // borrowed, [ReadOnly]
                OutPolyOuterRingIdx = polys.PolyOuterRingIdx, OutPolyHoleListStart = polys.PolyHoleListStart, OutPolyHoleCount = polys.PolyHoleCount,
                OutHoleRingIdxs = polys.HoleRingIdxs, OutPolygonCount = polys.PolyCountArr, OutHoleCount = polys.HoleCountArr,
            }.Schedule(derived);

            // ── Sizing — also folds in the ring-count statistic (RingOffsets.Length - 1), which is already
            // a borrowed input here. Depends on assembly directly; no join needed.
            var counts  = FillGraphOutput.AllocateCounts();
            var error   = FillGraphOutput.AllocateError();
            var buffers = TriangulationBuffers.Allocate();

            JobHandle sized = new SizingJob
            {
                PolyOuterRingIdx = polys.PolyOuterRingIdx, PolyHoleListStart = polys.PolyHoleListStart, PolyHoleCount = polys.PolyHoleCount,
                HoleRingIdxs = polys.HoleRingIdxs, PolyCountArr = polys.PolyCountArr, HoleCountArr = polys.HoleCountArr,
                RingOffsets = outOffsets.AsDeferredJobArray(),
                MaxPolygons = maxPolygons, MaxHoles = maxHoles,
                Buffers = buffers,
                Counts = counts, Error = error,
            }.Schedule(assembled);

            // ── Gather (the hole sort). ───────────────────────────────────────────────────────────────
            var comparer = new FillMeshPipeline.HoleRingComparer(outVerts.AsDeferredJobArray(), outOffsets.AsDeferredJobArray());
            JobHandle gathered = new FillGatherJob<FillMeshPipeline.HoleRingComparer>
            {
                Vertices = outVerts.AsDeferredJobArray(), RingOffsets = outOffsets.AsDeferredJobArray(),
                RingFeatureIdx = outFeatIdx.AsDeferredJobArray(),
                PolyOuterRingIdx = polys.PolyOuterRingIdx, PolyHoleListStart = polys.PolyHoleListStart, PolyHoleCount = polys.PolyHoleCount,
                HoleRingIdxs = polys.HoleRingIdxs,
                Comparer = comparer,
                Buffers = buffers,
            }.Schedule(sized);

            // ── Earcut batch, parallel over polygons. The count source is buffers.PerPolyOuterCount, which
            // SizingJob resizes to exactly polyCount — and leaves at 0 on a capacity early-return. ────────
            JobHandle triangulated = new EarcutBatchJob
            {
                PolyHoleCount = polys.PolyHoleCount,
                Buffers = buffers,
            }.Schedule(buffers.PerPolyOuterCount, EarcutPolygonBatch, gathered);

            // ── The arm split: picks the AGGREGATE targets, on exactly WriteGeometry's predicate. Always
            // subdividing would pay GlobeFillSubdivideJob's per-vertex cost on flat layers that never split.
            bool curved = !double.IsInfinity(input.Projection.MaxRefineAngleRad);

            // ── Aggregate (exact sizing). Flat arm: these ARE the graph's final output columns. Curved arm:
            // throwaway buffers — GlobeFillScatterJob overwrites the same locals with its own columns. ─────
            NativeList<double2> tileVertices;
            NativeList<double3> worldPositions;
            NativeList<double3> vertexUp;
            NativeList<double3> vertexEast;
            NativeList<float3>  vertexBand;
            NativeList<int>     vertexFeatureIdx;
            NativeList<int>     triangleIndices;
            if (curved)
            {
                tileVertices     = NewBuffer<double2>(1);
                worldPositions   = NewBuffer<double3>(1);
                vertexUp         = NewBuffer<double3>(1);
                vertexEast       = NewBuffer<double3>(1);
                vertexBand       = NewBuffer<float3>(1);
                vertexFeatureIdx = NewBuffer<int>(1);
                triangleIndices  = NewBuffer<int>(1);
            }
            else
            {
                tileVertices     = FillGraphOutput.AllocateOutputList<double2>();
                worldPositions   = FillGraphOutput.AllocateOutputList<double3>();
                vertexUp         = FillGraphOutput.AllocateOutputList<double3>();
                vertexEast       = FillGraphOutput.AllocateOutputList<double3>();
                vertexBand       = FillGraphOutput.AllocateOutputList<float3>();
                vertexFeatureIdx = FillGraphOutput.AllocateOutputList<int>();
                triangleIndices  = FillGraphOutput.AllocateOutputList<int>();
            }
            var geo = NewBuffer<GeoCoordinate>(1);

            JobHandle aggregated = new AggregateJob
            {
                Buffers = buffers,
                TileVertices = tileVertices, WorldPositions = worldPositions, VertexUp = vertexUp, VertexEast = vertexEast,
                VertexBand = vertexBand,
                VertexFeatureIdx = vertexFeatureIdx, TriangleIndices = triangleIndices, Geo = geo,
                Counts = counts, Error = error,
            }.Schedule(triangulated);

            // ── The boundary band, BOTH ARMS. It appends outward-band quads to the aggregate's own columns,
            // so it runs after the node that owns the interior vertex count, and after the clip/select and
            // ring assembly it reads. On the curved arm it runs HERE, upstream of subdivision: a band quad is
            // degenerate in tile space, so its long edges share endpoints with the interior boundary edge
            // they abut and take the identical mark. After subdivision the band's inner ring would stay on
            // the flat chord while the interior bulges onto the sphere — a visible gap at low zoom.
            JobHandle banded = aggregated;
            if (!input.SuppressBoundaryBand)
            {
                banded = new FillBandJob
                {
                    RingVertices = outVerts.AsDeferredJobArray(), RingOffsets = outOffsets.AsDeferredJobArray(),
                    RingFeatureIdx = outFeatIdx.AsDeferredJobArray(),
                    PolyOuterRingIdx = polys.PolyOuterRingIdx, PolyHoleListStart = polys.PolyHoleListStart,
                    PolyHoleCount = polys.PolyHoleCount, HoleRingIdxs = polys.HoleRingIdxs,
                    PolyCountArr = polys.PolyCountArr,
                    ClipEnabled = clipEnabled, ClipMin = clipMin, ClipMax = clipMax,
                    TileVertices = tileVertices, VertexBand = vertexBand, VertexFeatureIdx = vertexFeatureIdx,
                    TriangleIndices = triangleIndices, VertexEast = vertexEast,
                    WorldPositions = worldPositions, VertexUp = vertexUp, Geo = geo,
                    Counts = counts,
                }.Schedule(aggregated);
            }

            // Every derived list, and the earcut / polygon-descriptor groups, are dead after aggregate. Each
            // group disposes its own containers through its own DisposeAfter. The three ring columns and the
            // polygon descriptors outlive the aggregate by one node on both arms: FillBandJob reads both.
            // The triangulation buffers are NOT re-pointed — the band node reads none of them.
            JobHandle disposeListsAfterAggregate = ScheduleDispose(outVerts, banded);
            disposeListsAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, ScheduleDispose(outOffsets, banded));
            disposeListsAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, ScheduleDispose(outFeatIdx, banded));
            disposeListsAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, buffers.DisposeAfter(aggregated));

            JobHandle disposeAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, polys.DisposeAfter(banded));

            // ── Flat arm: tile → geodetic → project, straight into the final output columns. Curved arm:
            // neither runs — GlobeFillSubdivideJob projects internally and reads no WorldPositions/VertexUp.
            JobHandle terminalGeometry;
            JobHandle geometryDisposeHandle;
            if (!curved)
            {
                JobHandle geodetic = new TileToGeoJob
                {
                    Tile = tile, Extent = extent,
                    TileCoords = tileVertices.AsDeferredJobArray(), OutGeo = geo.AsDeferredJobArray(),
                }.Schedule(tileVertices, VertexBatch, banded);

                JobHandle projected = ProjectionDispatch.Schedule(
                    input.Projection, input.OriginRender, geo, worldPositions, vertexUp, geodetic);

                geometryDisposeHandle = ScheduleDispose(geo, projected);
                terminalGeometry = projected;
            }
            else
            {
                // geo is written by AggregateJob and the band node, but never READ on this arm — dispose
                // after the band node, the last job that touched it.
                JobHandle disposeGeo = ScheduleDispose(geo, banded);
                // worldPositions/vertexUp/vertexEast are dead here — the scattered lists below become this
                // arm's world/up/east columns. vertexBand is NOT among them: it is a subdivision INPUT.
                JobHandle deadAggregateColumnsDispose = JobHandle.CombineDependencies(
                    ScheduleDispose(worldPositions, banded), ScheduleDispose(vertexUp, banded), ScheduleDispose(vertexEast, banded));

                var subdividedVertices = NewBuffer<GlobeFillVertex>(1);
                var subdividedIndices  = NewBuffer<int>(1);

                JobHandle subdivideHandle = GlobeFillSubdivideDispatch.Schedule(
                    input.Projection, tileVertices, triangleIndices, vertexFeatureIdx, vertexBand,
                    tile, extent, input.OriginRender,
                    GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                    GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices,
                    GlobeFillSubdivideDispatch.DefaultMaxTotalVertices,
                    subdividedVertices, subdividedIndices,
                    banded);

                JobHandle subdivisionSourceDispose = JobHandle.CombineDependencies(
                    JobHandle.CombineDependencies(
                        ScheduleDispose(tileVertices, subdivideHandle), ScheduleDispose(triangleIndices, subdivideHandle)),
                    JobHandle.CombineDependencies(
                        ScheduleDispose(vertexFeatureIdx, subdivideHandle), ScheduleDispose(vertexBand, subdivideHandle)));

                var scatteredWorldPositions   = FillGraphOutput.AllocateOutputList<double3>();
                var scatteredVertexUp         = FillGraphOutput.AllocateOutputList<double3>();
                var scatteredVertexEast       = FillGraphOutput.AllocateOutputList<double3>();
                var scatteredVertexBand       = FillGraphOutput.AllocateOutputList<float3>();
                var scatteredTileVertices     = FillGraphOutput.AllocateOutputList<double2>();
                var scatteredVertexFeatureIdx = FillGraphOutput.AllocateOutputList<int>();
                var scatteredTriangleIndices  = FillGraphOutput.AllocateOutputList<int>();

                JobHandle scattered = new GlobeFillScatterJob
                {
                    Vertices = subdividedVertices, Indices = subdividedIndices,
                    OutWorldPositions = scatteredWorldPositions, OutVertexUp = scatteredVertexUp, OutVertexEast = scatteredVertexEast,
                    OutVertexBand = scatteredVertexBand,
                    OutTileVertices = scatteredTileVertices, OutVertexFeatureIdx = scatteredVertexFeatureIdx,
                    OutTriangleIndices = scatteredTriangleIndices,
                    Counts = counts,
                }.Schedule(subdivideHandle);

                JobHandle disposeSubdivided = JobHandle.CombineDependencies(
                    ScheduleDispose(subdividedVertices, scattered), ScheduleDispose(subdividedIndices, scattered));

                // The pre-subdivision buffers are superseded — reassign so the returned
                // FillGraphOutput reads the same six names regardless of arm.
                worldPositions = scatteredWorldPositions; vertexUp = scatteredVertexUp; vertexEast = scatteredVertexEast;
                vertexBand = scatteredVertexBand;
                tileVertices = scatteredTileVertices; vertexFeatureIdx = scatteredVertexFeatureIdx; triangleIndices = scatteredTriangleIndices;

                terminalGeometry = scattered;
                geometryDisposeHandle = JobHandle.CombineDependencies(
                    JobHandle.CombineDependencies(disposeGeo, deadAggregateColumnsDispose, subdivisionSourceDispose), disposeSubdivided);
            }

            JobHandle buffersDisposeHandle = JobHandle.CombineDependencies(clipDisposeHandle, disposeAfterAggregate, geometryDisposeHandle);
            JobHandle terminal = JobHandle.CombineDependencies(terminalGeometry, buffersDisposeHandle);

            return new FillGraphOutput
            {
                TileVertices = tileVertices, WorldPositions = worldPositions, VertexUp = vertexUp, VertexEast = vertexEast,
                VertexBand = vertexBand,
                VertexFeatureIdx = vertexFeatureIdx, TriangleIndices = triangleIndices,
                Counts = counts, Error = error,
                Handle = terminal,
                IsCreated = true,
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Allocates one buffer this graph owns and disposes internally — <see cref="Allocator.Persistent"/>,
        /// counted via <see cref="FillGraphOutput.DebugBuffersAllocated"/> so an allocation with no matching
        /// <see cref="ScheduleDispose{T}"/> node is detectable as a pairing mismatch.</summary>
        private static NativeList<T> NewBuffer<T>(int capacity) where T : unmanaged
        {
            FillGraphOutput.RecordBuffersAllocated();
            return new NativeList<T>(capacity, Allocator.Persistent);
        }

        /// <summary>Schedules a <c>Dispose(handle)</c> node for a buffer, counted via
        /// <see cref="FillGraphOutput.DebugBufferDisposeNodes"/>.</summary>
        private static JobHandle ScheduleDispose<T>(NativeList<T> list, JobHandle h) where T : unmanaged
        {
            FillGraphOutput.RecordBufferDisposeNode();
            return list.Dispose(h);
        }
    }
}
