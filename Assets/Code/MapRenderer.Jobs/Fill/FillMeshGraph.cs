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
    /// Schedules the fill measure graph over one <see cref="FillMeshPipeline.LayerInput"/>, returning an
    /// UNCOMPLETED <see cref="FillGraphOutput"/> (job-scheduling-design.md §3.2, §8 stage 1). job-scheduling-
    /// design.md §8 stage 4 Group B: this is now the ONLY mesher — the synchronous
    /// <c>FillMeshPipeline.Schedule</c> it was measured against is retired; its regression pins survive as
    /// frozen per-stream goldens (<c>FillMeshGraphParityTests</c>, <c>FillMeshGraphGlobeParityTests</c>,
    /// <c>StyledFillExtrusionGraphWriteTests</c>, <c>TileBuildGraphTests</c>).
    ///
    /// <para><b>The <c>.AsArray()</c> rule — obeyed everywhere in this file.</b> No <c>.AsArray()</c> at
    /// schedule time on any list a graph node resizes: a view captured before the resize has the stale
    /// length. Every node here either holds the <see cref="NativeList{T}"/> itself and resolves it inside
    /// <c>Execute</c>, or takes <c>AsDeferredJobArray()</c> — resolved to the list's EXECUTE-time state, not
    /// its state when the view was taken (proven for this exact construction by
    /// <c>RingAssemblyDeferredCountTests</c>). This is the single easiest way to ship a silently
    /// wrong-length graph.</para>
    ///
    /// <para><b>What runs on the main thread at schedule time — legitimately</b>, because it reads only
    /// BORROWED inputs, never a job output: the empty-input guard; the <c>maxRingLen</c>/<c>totalVerts</c>
    /// pre-pass sizing the clip's ping-pong buffers (not absorbed into <c>RingClipJob</c>, which
    /// would edit an existing call site and <c>RingClipJobTests</c>); <c>maxPolygons = maxHoles =
    /// max(1, RingVisitOrder.Length)</c> (a valid upper bound because the clip stage only ever DROPS
    /// rings); and passing <c>input.Geometry.FeatureGeometryType</c> to <c>RingAssemblyJob</c> BORROWED and
    /// <c>[ReadOnly]</c> rather than building a <see cref="TileGeometryBuffers"/> the graph would then own
    /// (<c>AdoptDerivedLists</c> computes its ring count at CALL time, which is wrong before the
    /// select/clip job has run).</para>
    ///
    /// <para><b>Input lifetime is a contract, and it is new.</b> Unlike the retired <c>FillMeshPipeline.Schedule</c>,
    /// which did not retain its input past the call, <see cref="Schedule"/> DOES: the borrowed
    /// <c>input.Geometry</c> and <c>input.RingVisitOrder</c> are live <c>[ReadOnly]</c> job inputs until
    /// <c>Handle.Complete()</c> (design §6's ownership rule arriving one stage early). The caller
    /// must not dispose either before completing the returned <see cref="FillGraphOutput"/>.</para>
    ///
    /// <para><b>The capacity error flags are never-fired backstops here</b>, same posture as
    /// <c>FillMeshPipeline.EnsureCapacity</c>: That bound makes <c>SizingJob</c>'s
    /// <c>MaxPolygons</c>/<c>MaxHoles</c> capacity check unreachable from THIS caller (only a test handing
    /// <c>SizingJob</c> an artificially small capacity standalone can trip it). Downstream nodes do not
    /// branch on the flag — the write graph (stage 2) is what reads it and settles a faulted layer as
    /// zero-vertex, mirroring the design doc's stated shape. A hypothetical trip through THIS file today
    /// reads NOTHING in every later node, not garbage-length flat lists: every node that holds a sizing-owned
    /// buffer struct (<see cref="TriangulationBuffers"/>/<c>RibbonBuffers</c>) bounds its own loop —
    /// or its deferred count — by a column its sizing job resizes, never a borrowed count that job's early
    /// return does not touch. Nodes past the aggregate bound by columns the AGGREGATE sizes, and inherit
    /// emptiness through it (job-scheduling-design.md §7 rule 2).
    /// <c>FillSizingJobTests.SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo</c> is the
    /// observing tooth for <see cref="FillGatherJob{TComparer}"/> and <see cref="AggregateJob"/> — it
    /// drives both over the same buffers and goes red if either re-introduces a borrowed count as its loop
    /// bound. <see cref="EarcutBatchJob"/> has no loop of its own to bound (it is deferred over
    /// <c>buffers.PerPolyOuterCount</c>); <c>FillMeshGraphStructureTests</c> pins that deferred-count source
    /// structurally instead.</para>
    ///
    /// <b>No <c>Complete()</c> anywhere in this file.</b>
    /// </summary>
    public static class FillMeshGraph
    {
        /// <summary>Vertices per batch for this graph's tile→geo node (<see cref="TileToGeoJob"/>,
        /// job-scheduling-design.md §8 stage 6). <c>1024</c>: <see cref="TileToGeoJob.GeoAt"/> is ~2
        /// <c>exp</c>, 1 <c>atan</c>, 1 <c>pow</c> per vertex — tens of ns each, so 1024 vertices is roughly
        /// 10-50 µs of work, comfortably above a batch hand-off's own cost while still splitting a corpus
        /// tile's few-thousand vertices several ways. A starting value chosen by this reasoning, not a
        /// measured optimum — see the design doc's dated measurement before moving it.</summary>
        internal const int VertexBatch = 1024;

        /// <summary>Polygons per batch for <see cref="EarcutBatchJob"/> (job-scheduling-design.md §8 stage
        /// 6). <c>1</c>: earcut is superlinear in a polygon's vertex count and corpus polygons vary by orders
        /// of magnitude, so per-polygon work is wildly uneven — batch 1 lets the job system's work-stealing
        /// act as the load balancer, rather than pinning a long polygon and its neighbours to one
        /// worker.</summary>
        internal const int EarcutPolygonBatch = 1;

        /// <summary>Schedules the fill measure graph for one layer. Returns <see cref="default"/>
        /// (<c>IsCreated == false</c>) when there is nothing to draw — the borrowed-input guard: an uncreated
        /// or empty <see cref="FillMeshPipeline.LayerInput.RingVisitOrder"/> means nothing to triangulate.
        /// Otherwise returns a <see cref="FillGraphOutput"/> whose <c>Handle</c> is UNCOMPLETED; the caller
        /// polls or completes it before reading any field.
        /// </summary>
        /// <param name="input">By value, not <c>in</c> — <see cref="FillMeshPipeline.LayerInput"/> is a
        /// mutable struct, and the conventions gate is <c>in</c> ⟺ <c>readonly struct</c>; <c>in</c>
        /// on a non-readonly struct forces a defensive copy per member read.</param>
        /// <param name="deps">Upstream dependency this whole layer's chain must wait for.</param>
        public static FillGraphOutput Schedule(
            FillMeshPipeline.LayerInput input, JobHandle deps = default)
        {
            if (!input.Geometry.IsCreated || !input.RingVisitOrder.IsCreated || input.RingVisitOrder.Length == 0)
                return default;

            // Validated HERE, before any node schedules — not left to ProjectionDispatch's own throw. A
            // throw from deeper in this method (after RingSelect/RingClip/RingAssembly/sizing/gather/earcut/
            // aggregate have already scheduled, all holding input.Geometry as a live [ReadOnly] input) would
            // propagate with no terminal handle ever constructed — nothing left to Complete() those jobs, so
            // the caller's own cleanup throws trying to dispose geometry the safety system still considers
            // in flight (the bystander-fault signature, not the real defect). Fail fast, before anything is
            // in flight to strand.
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

            if (input.Clip.TryWindow(source.Extent, out double2 clipMin, out double2 clipMax))
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

            // ── Ring assembly (existing, unmodified except the additive bool). ─────────────────────────
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

            // ── Sizing — also folds in the ring-count statistic (RingOffsets.Length - 1): it is already a
            // borrowed input here, so this needs no dedicated node/edge the way the deleted FillRingCountJob
            // did. Depends on assembly directly; no join needed.
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

            // ── Gather (the hole sort — same struct comparer FillMeshPipeline.Schedule uses). ───────────
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

            // ── Earcut batch, parallel over polygons (job-scheduling-design.md §8 stage 6, C.3/C.4):
            // EarcutJob's fields untouched — GetSubArray + Execute() inside. Count source is
            // buffers.PerPolyOuterCount — a WRITTEN list, not merely borrowed, but this job only READS it
            // (SizingJob resizes it to exactly polyCount); a written list as the count source invites a
            // question a read-only one does not, so it is called out here. A capacity early-return in
            // SizingJob leaves PerPolyOuterCount at length 0, so this runs zero batches — strictly better
            // than reading garbage-length flat lists downstream (FillMeshGraph.cs's own doc, above). ────────
            JobHandle triangulated = new EarcutBatchJob
            {
                PolyHoleCount = polys.PolyHoleCount,
                Buffers = buffers,
            }.Schedule(buffers.PerPolyOuterCount, EarcutPolygonBatch, gathered);

            // ── The arm split (job-scheduling-design.md §3.7): picks the AGGREGATE targets. Exactly
            // WriteGeometry's predicate. No null test — the guard at the top of this method already threw,
            // so Projection is non-null here. "Always subdivide" would be wrong: on a flat projection every
            // edge mark is false, so a flat layer would only ever take GlobeFillSubdivideJob's markCount==0
            // pass-through path — paying its per-vertex Project()/tangent-basis + vertex-key map overhead for
            // no split at all, instead of streaming earcut's already-minimal merged vertex array straight
            // through (vertex sharing: sharing narrows, but does not remove, this cost —
            // it does not restore the flat arm's O(1) vertex reuse, which needs no hash lookup at all).
            bool curved = !double.IsInfinity(input.Projection.MaxRefineAngleRad);

            // ── Aggregate (exact sizing). Flat arm: these five ARE the graph's final output columns. Curved
            // arm: throwaway buffers — GlobeFillScatterJob overwrites the same-named locals below with the
            // post-subdivision columns, which is the graph's actual one-column-set output on that arm. ──────
            NativeList<double2> tileVertices;
            NativeList<double3> worldPositions;
            NativeList<double3> vertexUp;
            NativeList<double3> vertexEast;
            NativeList<int>     vertexFeatureIdx;
            NativeList<int>     triangleIndices;
            if (curved)
            {
                tileVertices     = NewBuffer<double2>(1);
                worldPositions   = NewBuffer<double3>(1);
                vertexUp         = NewBuffer<double3>(1);
                vertexEast       = NewBuffer<double3>(1);
                vertexFeatureIdx = NewBuffer<int>(1);
                triangleIndices  = NewBuffer<int>(1);
            }
            else
            {
                tileVertices     = FillGraphOutput.AllocateOutputList<double2>();
                worldPositions   = FillGraphOutput.AllocateOutputList<double3>();
                vertexUp         = FillGraphOutput.AllocateOutputList<double3>();
                vertexEast       = FillGraphOutput.AllocateOutputList<double3>();
                vertexFeatureIdx = FillGraphOutput.AllocateOutputList<int>();
                triangleIndices  = FillGraphOutput.AllocateOutputList<int>();
            }
            var geo = NewBuffer<GeoCoordinate>(1);

            JobHandle aggregated = new AggregateJob
            {
                Buffers = buffers,
                TileVertices = tileVertices, WorldPositions = worldPositions, VertexUp = vertexUp, VertexEast = vertexEast,
                VertexFeatureIdx = vertexFeatureIdx, TriangleIndices = triangleIndices, Geo = geo,
                Counts = counts, Error = error,
            }.Schedule(triangulated);

            // Every derived list, and the earcut buffers / polygon-descriptor GROUPS, are dead after
            // aggregate — they either fed it directly or fed a node it already transitively depends on. Each
            // group disposes its own containers via its own DisposeAfter — no hand-counted array, no forgotten
            // increment (the confound a hand-counted array invites — see TriangulationBuffers's own doc).
            JobHandle disposeListsAfterAggregate = ScheduleDispose(outVerts, aggregated);
            disposeListsAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, ScheduleDispose(outOffsets, aggregated));
            disposeListsAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, ScheduleDispose(outFeatIdx, aggregated));
            disposeListsAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, buffers.DisposeAfter(aggregated));

            JobHandle disposeAfterAggregate = JobHandle.CombineDependencies(disposeListsAfterAggregate, polys.DisposeAfter(aggregated));

            // ── Flat arm: tile → geodetic → project, straight into the final output columns. Curved arm:
            // NEITHER node is scheduled — GlobeFillSubdivideJob projects internally and never reads
            // WorldPositions/VertexUp (job-scheduling-design.md §3.7's dead-curved-arm-projection finding), so
            // computing them first would be pure waste. ─────────────────────────────────────────────────────
            JobHandle terminalGeometry;
            JobHandle geometryDisposeHandle;
            if (!curved)
            {
                JobHandle geodetic = new TileToGeoJob
                {
                    Tile = tile, Extent = extent,
                    TileCoords = tileVertices.AsDeferredJobArray(), OutGeo = geo.AsDeferredJobArray(),
                }.Schedule(tileVertices, VertexBatch, aggregated);

                JobHandle projected = ProjectionDispatch.Schedule(
                    input.Projection, input.OriginRender, geo, worldPositions, vertexUp, geodetic);

                geometryDisposeHandle = ScheduleDispose(geo, projected);
                terminalGeometry = projected;
            }
            else
            {
                // The geodetic/project nodes are genuinely dead here: GlobeFillSubdivideJob takes only
                // TileVerts/TriangleIndices/VertexFeatureIdx and projects internally — it has no
                // WorldPositions/VertexUp input to read, established by reading the job's field list.
                //
                // geo was pre-sized by AggregateJob (a field every caller of that job fills) but never
                // used on this arm — dispose after the job that last touched it, same rule as any buffer.
                JobHandle disposeGeo = ScheduleDispose(geo, aggregated);
                // worldPositions/vertexUp/vertexEast were likewise pre-sized but are dead buffers here — the
                // scattered lists below become the graph's actual world/up/east columns on this arm.
                JobHandle deadAggregateColumnsDispose = JobHandle.CombineDependencies(
                    ScheduleDispose(worldPositions, aggregated), ScheduleDispose(vertexUp, aggregated), ScheduleDispose(vertexEast, aggregated));

                var subdividedVertices = NewBuffer<GlobeFillVertex>(1);
                var subdividedIndices  = NewBuffer<int>(1);

                JobHandle subdivideHandle = GlobeFillSubdivideDispatch.Schedule(
                    input.Projection, tileVertices, triangleIndices, vertexFeatureIdx,
                    tile, extent, input.OriginRender,
                    GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                    GlobeFillSubdivideDispatch.DefaultMaxOutputVertices,
                    subdividedVertices, subdividedIndices,
                    aggregated);

                JobHandle subdivisionSourceDispose = JobHandle.CombineDependencies(
                    ScheduleDispose(tileVertices, subdivideHandle),
                    ScheduleDispose(triangleIndices, subdivideHandle), ScheduleDispose(vertexFeatureIdx, subdivideHandle));

                var scatteredWorldPositions   = FillGraphOutput.AllocateOutputList<double3>();
                var scatteredVertexUp         = FillGraphOutput.AllocateOutputList<double3>();
                var scatteredVertexEast       = FillGraphOutput.AllocateOutputList<double3>();
                var scatteredTileVertices     = FillGraphOutput.AllocateOutputList<double2>();
                var scatteredVertexFeatureIdx = FillGraphOutput.AllocateOutputList<int>();
                var scatteredTriangleIndices  = FillGraphOutput.AllocateOutputList<int>();

                JobHandle scattered = new GlobeFillScatterJob
                {
                    Vertices = subdividedVertices, Indices = subdividedIndices,
                    OutWorldPositions = scatteredWorldPositions, OutVertexUp = scatteredVertexUp, OutVertexEast = scatteredVertexEast,
                    OutTileVertices = scatteredTileVertices, OutVertexFeatureIdx = scatteredVertexFeatureIdx,
                    OutTriangleIndices = scatteredTriangleIndices,
                }.Schedule(subdivideHandle);

                JobHandle disposeSubdivided = JobHandle.CombineDependencies(
                    ScheduleDispose(subdividedVertices, scattered), ScheduleDispose(subdividedIndices, scattered));

                // The pre-subdivision buffers are superseded — reassign so the returned
                // FillGraphOutput reads the same six names regardless of arm.
                worldPositions = scatteredWorldPositions; vertexUp = scatteredVertexUp; vertexEast = scatteredVertexEast;
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
