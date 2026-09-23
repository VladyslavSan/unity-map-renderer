using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Projection;
namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// Schedules one fill-extrusion layer's roof + wall graph (job-scheduling-design.md): the roof measure
    /// via <see cref="FillMeshGraph.Schedule"/>, composed UNCHANGED, plus
    /// the wall chain — <see cref="RingSelectJob"/> or <see cref="RingClipJob"/> (raw, pre-earcut ring
    /// vertices off the borrowed geometry, on the same <c>input.Clip</c> branch the roof takes) →
    /// <see cref="ProjectionColumnSizingJob"/> → <see cref="TileToGeoJob"/> →
    /// <see cref="ProjectionDispatch"/> → <see cref="StyledFillExtrusionTileBuilder.WallQuadJob"/>, one quad
    /// per boundary edge (both exterior AND hole rings). Both arms are scheduled on the SAME
    /// <paramref name="deps"/> a caller passes to <see cref="Schedule"/> — not the roof's own handle: roof
    /// and walls both read <c>input.Geometry</c>'s columns <c>[ReadOnly]</c> only, so they are independent
    /// and may run concurrently.
    ///
    /// <para><b>Walls honour the tile-buffer clip.</b> The wall chain reads the same <c>input.Clip</c>
    /// window the roof does, and a wall is emitted for EVERY edge of the clipped ring — the cut edges
    /// introduced by the window included, because what is extruded is the clipped polygon. The reasoning,
    /// and the one condition that reopens the alternative (translucent fill-extrusion), are in
    /// job-scheduling-design.md.</para>
    ///
    /// <para><b>Byte-identical is NOT inherited at the managed-vs-Burst projection boundary:</b> linear
    /// quantities are bit-exact, quantities downstream of a transcendental diverge by a magnitude that
    /// depends on the call site (never a flat ULP figure carried between them) — job-scheduling-design.md
    /// carries the measurement, including this call site's own wall-tail figures. Scheduling
    /// instead of running does not itself move a bit: every node here is either an <c>IJob</c> (identical
    /// <c>Execute()</c> under <c>.Run()</c> or <c>.Schedule()</c>) or an element-wise-independent
    /// <c>IJobParallelForDefer</c> batched by <see cref="VertexBatch"/> — the batch size chooses which
    /// worker evaluates a given index, never the expression.</para>
    ///
    /// <para>Sits in <c>MapRenderer.Unity</c>, not beside its siblings in <c>MapRenderer.Jobs</c> — the one
    /// mesh graph that does not, by decision:
    /// <see cref="StyledFillExtrusionTileBuilder.WallQuadJob"/> writes
    /// <see cref="StyledFillExtrusionTileBuilder.PositionNormal"/>/<see cref="StyledFillExtrusionTileBuilder.ExtrudeAndBake"/>
    /// — the vertex-stream layout at the <c>Mesh.MeshData</c> boundary, which the same discriminator that
    /// would move this graph puts in Unity.</para>
    ///
    /// <b>No <c>Complete()</c> anywhere in this file</b> (mirrors <see cref="FillMeshGraph"/>/<see cref="LineMeshGraph"/>).
    /// </summary>
    public static class FillExtrusionMeshGraph
    {
        /// <summary>Vertices per batch for this graph's wall-chain <see cref="TileToGeoJob"/> node
        /// (job-scheduling-design.md) — same reasoning as <see cref="FillMeshGraph.VertexBatch"/>:
        /// ~10-50 µs of work per 1024 vertices, comfortably above a batch hand-off's own cost. A starting
        /// value chosen by this reasoning, not a measured optimum — see the design doc's dated measurement
        /// before moving it.</summary>
        internal const int VertexBatch = 1024;

        /// <summary>Schedules the roof + wall graph for one fill-extrusion layer. Returns <see cref="default"/>
        /// (<c>IsCreated == false</c>) when there is nothing to draw — the same borrowed-input guard as
        /// <see cref="FillMeshGraph.Schedule"/>: an uncreated geometry buffer or an empty
        /// <see cref="FillMeshPipeline.LayerInput.RingVisitOrder"/> means nothing to build. Otherwise returns
        /// a <see cref="FillExtrusionGraphOutput"/> whose <c>Handle</c> is UNCOMPLETED; the caller polls or
        /// completes it before reading any field.
        /// </summary>
        /// <param name="input">By value, not <c>in</c> — mutable struct, same conventions gate as the
        /// siblings this mirrors.</param>
        /// <param name="featureColors">Per-feature linear colour, indexed by ring feature — the wall chain's
        /// own borrowed copy of the same column the roof write later reads.</param>
        /// <param name="featureBake">Per-feature data-driven base/height bake, indexed the same way.</param>
        /// <param name="deps">Upstream dependency both the roof and the wall chain must wait for.</param>
        internal static FillExtrusionGraphOutput Schedule(
            FillMeshPipeline.LayerInput input,
            NativeArray<Vector4> featureColors, NativeArray<Vector2> featureBake,
            JobHandle deps = default)
        {
            if (!input.Geometry.IsCreated || !input.RingVisitOrder.IsCreated || input.RingVisitOrder.Length == 0)
                return default;

            // Validated HERE, before any node schedules — same reason FillMeshGraph.Schedule's own guard
            // gives (its own doc): a throw after nodes are already in flight (roof or wall) would strand jobs
            // holding input.Geometry live [ReadOnly] with no terminal handle left to Complete() them — the
            // bystander-fault signature, not the real defect. FillMeshGraph.Schedule below repeats this same
            // check on its own input copy; that is harmless duplication, not a second source of truth.
            if (input.Projection == null)
                throw new NotSupportedException(
                    "FillExtrusionMeshGraph.Schedule received a null Projection — every caller resolves the " +
                    "null-means-Mercator default before reaching a graph, not here. A caller reaching this " +
                    "directly must pass a real IProjection.");

            TileGeometryBuffers source = input.Geometry;
            NativeArray<int>    visit  = input.RingVisitOrder;

            // ── Roof: the flat fill's own earcut+project chain, composed unchanged. ─────────────────────
            // The roof rides the extrusion mesh's own vertex layout, which carries no band attribute, and an
            // extruded building keeps a hard silhouette by design.
            input.SuppressBoundaryBand = true;
            FillGraphOutput roof = FillMeshGraph.Schedule(input, deps);

            // ── Walls: raw (pre-earcut) ring vertices off the borrowed source — NOT earcut output
            // (WallColumns' own doc) — via the SAME select-or-clip branch the roof takes on input.Clip.
            // Then tile→geo→project, then WallQuadJob emits quads. ───────────────────────────────────────────

            // Main-thread pre-pass over BORROWED inputs only (visit/source.RingOffsets — never a job output,
            // same legitimacy as FillMeshGraph.Schedule's own pre-pass). totalVerts is a CAPACITY HINT, not a
            // length: on the clip arm the post-Execute Length of flatTile is not known here at all
            // (Sutherland-Hodgman may add vertices to a ring and may drop a ring outright). The exact length
            // is established at execute time by ProjectionColumnSizingJob, which resizes geo/world/up to
            // flatTile's final Length before the deferred nodes that write them run. Adding those three
            // columns to RingSelectJob/RingClipJob themselves was rejected — their other call sites
            // (FillMeshGraph.cs, FillGraphBurstProbeTests.cs) would then have to carry three dead lists a job
            // field cannot leave unassigned.
            int maxRingLen = 0;
            int totalVerts = 0;
            for (int k = 0; k < visit.Length; k++)
            {
                int ri  = visit[k];
                int len = source.RingOffsets[ri + 1] - source.RingOffsets[ri];
                maxRingLen  = math.max(maxRingLen, len);
                totalVerts += len;
            }

            var flatTile    = NewBuffer<double2>(math.max(1, totalVerts));
            var flatOffsets = NewBuffer<int>(visit.Length + 1);
            var flatFeatIdx = NewBuffer<int>(math.max(1, visit.Length));

            JobHandle gathered;
            JobHandle clipDisposeHandle = default;

            if (input.Clip.TryWindow(source.Extent, out double2 clipMin, out double2 clipMax))
            {
                // Raw NativeArrays, not NewBuffer: deliberate parity with the roof's own ping-pong buffers
                // (FillMeshGraph.Schedule). The DebugBuffersAllocated/DebugBufferDisposeNodes pair counts the
                // NativeLists this graph owns; these are not part of that pairing.
                int bufferCap = math.max(1, maxRingLen * RingClipJob.BufferLengthMultiplier);
                var bufferA = new NativeArray<double2>(bufferCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var bufferB = new NativeArray<double2>(bufferCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                gathered = new RingClipJob
                {
                    Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                    RingVisitOrder = visit, ClipMin = clipMin, ClipMax = clipMax,
                    BufferA = bufferA, BufferB = bufferB,
                    OutVertices = flatTile, OutRingOffsets = flatOffsets, OutRingFeatureIdx = flatFeatIdx,
                }.Schedule(deps);

                clipDisposeHandle = JobHandle.CombineDependencies(bufferA.Dispose(gathered), bufferB.Dispose(gathered));
            }
            else
            {
                gathered = new RingSelectJob
                {
                    Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                    RingVisitOrder = visit,
                    OutVertices = flatTile, OutRingOffsets = flatOffsets, OutRingFeatureIdx = flatFeatIdx,
                }.Schedule(deps);
            }

            var geo   = NewBuffer<GeoCoordinate>(math.max(1, totalVerts));
            var world = NewBuffer<double3>(math.max(1, totalVerts));
            var up    = NewBuffer<double3>(math.max(1, totalVerts));

            // On the chain, not parallel to it, and on BOTH arms: an arm-dependent sizing path is exactly
            // what would let one arm drift. On the select arm flatTile.Length == totalVerts by RingSelectJob's
            // verbatim-copy contract, so this node, not the main thread, computes that number.
            JobHandle sized = new ProjectionColumnSizingJob
            {
                SourceTileCoords = flatTile, OutGeo = geo, OutWorld = world, OutUp = up,
            }.Schedule(gathered);

            JobHandle geodetic = new TileToGeoJob
            {
                Tile = source.Tile, Extent = source.Extent,
                TileCoords = flatTile.AsDeferredJobArray(), OutGeo = geo.AsDeferredJobArray(),
            }.Schedule(flatTile, VertexBatch, sized);

            // flatTile's last reader is TileToGeoJob (as TileCoords) — WallQuadJob never reads it, only the
            // columns TileToGeoJob/ProjectionDispatch derive from it.
            JobHandle flatTileDisposed = ScheduleDispose(flatTile, geodetic);

            JobHandle projected = ProjectionDispatch.Schedule(
                input.Projection, input.OriginRender, geo, world, up, geodetic);

            StyledFillExtrusionTileBuilder.WallColumns walls = StyledFillExtrusionTileBuilder.WallColumns.Allocate();
            bool globeArm = !double.IsInfinity(input.Projection.MaxRefineAngleRad);

            JobHandle walled = new StyledFillExtrusionTileBuilder.WallQuadJob
            {
                RingOffsets = flatOffsets, RingFeatureIdx = flatFeatIdx,
                Geo = geo, World = world, Up = up,
                FeatureColors = featureColors, FeatureBake = featureBake, Globe = globeArm,
                OutPositionNormal = walls.PositionNormal, OutExtrude = walls.Extrude,
                OutTangent = walls.Tangent, OutColor = walls.Color, OutIndices = walls.Indices,
            }.Schedule(projected);

            // flatOffsets/flatFeatIdx/geo/world/up's last reader is WallQuadJob — geo included, it reads
            // Geo[idx].Latitude for the sec φ factor.
            JobHandle flatOffsetsDisposed = ScheduleDispose(flatOffsets, walled);
            JobHandle flatFeatIdxDisposed = ScheduleDispose(flatFeatIdx, walled);
            JobHandle geoDisposed         = ScheduleDispose(geo, walled);
            JobHandle worldDisposed       = ScheduleDispose(world, walled);
            JobHandle upDisposed          = ScheduleDispose(up, walled);

            // clipDisposeHandle folds in the clip arm's ping-pong buffers (default, and harmless, on the
            // select arm) — without it their dispose nodes are unreachable from the returned handle and the
            // Persistent arrays leak once per extrusion layer per tile.
            JobHandle scratchDisposed = JobHandle.CombineDependencies(
                JobHandle.CombineDependencies(flatTileDisposed, flatOffsetsDisposed, flatFeatIdxDisposed),
                JobHandle.CombineDependencies(geoDisposed, worldDisposed, upDisposed),
                clipDisposeHandle);

            JobHandle terminal = JobHandle.CombineDependencies(roof.Handle, walled, scratchDisposed);

            return new FillExtrusionGraphOutput
            {
                Roof = roof, Walls = walls, Handle = terminal, IsCreated = true,
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Allocates one buffer this graph owns and disposes internally — <see cref="Allocator.Persistent"/>,
        /// counted via <see cref="FillExtrusionGraphOutput.DebugBuffersAllocated"/> so an allocation with no
        /// matching <see cref="ScheduleDispose{T}"/> node is detectable as a pairing mismatch.</summary>
        private static NativeList<T> NewBuffer<T>(int capacity) where T : unmanaged
        {
            FillExtrusionGraphOutput.RecordBuffersAllocated();
            return new NativeList<T>(capacity, Allocator.Persistent);
        }

        /// <summary>Schedules a <c>Dispose(handle)</c> node for a buffer, counted via
        /// <see cref="FillExtrusionGraphOutput.DebugBufferDisposeNodes"/>.</summary>
        private static JobHandle ScheduleDispose<T>(NativeList<T> list, JobHandle h) where T : unmanaged
        {
            FillExtrusionGraphOutput.RecordBufferDisposeNode();
            return list.Dispose(h);
        }
    }
}
