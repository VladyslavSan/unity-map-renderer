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
    /// Schedules one fill-extrusion layer's roof (<see cref="FillMeshGraph.Schedule"/>, unchanged) and wall
    /// chain, one quad per edge of every clipped ring, cut edges included. Roof and walls read
    /// <c>input.Geometry</c> <c>[ReadOnly]</c> only, so both schedule on the caller's deps and may run
    /// concurrently. It lives in Unity because the wall job writes the Mesh vertex layout; it calls no
    /// <c>Complete()</c>. See docs/job-scheduling-design.md § "Invariants that constrain what is built next".
    /// </summary>
    public static class FillExtrusionMeshGraph
    {
        /// <summary>Vertices per batch for this graph's wall-chain <see cref="TileToGeoJob"/> node
        /// (docs/job-scheduling-design.md), chosen by the same reasoning as
        /// <see cref="FillMeshGraph.VertexBatch"/>. It is a starting value, not a measured optimum.</summary>
        internal const int VertexBatch = 1024;

        /// <summary>Schedules the roof + wall graph for one fill-extrusion layer. Returns <see cref="default"/>
        /// (<c>IsCreated == false</c>) for an uncreated geometry buffer or an empty
        /// <see cref="FillMeshPipeline.LayerInput.RingVisitOrder"/>. Otherwise returns a
        /// <see cref="FillExtrusionGraphOutput"/> whose <c>Handle</c> is UNCOMPLETED; the caller polls or
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

            // Validated before any node schedules: a throw after roof or wall nodes are in flight would strand
            // jobs holding input.Geometry [ReadOnly] with no terminal handle left to Complete() them.
            if (input.Projection == null)
                throw new NotSupportedException(
                    "FillExtrusionMeshGraph.Schedule received a null Projection — every caller resolves the " +
                    "null-means-Mercator default before reaching a graph, not here. A caller reaching this " +
                    "directly must pass a real IProjection.");

            TileGeometryBuffers source = input.Geometry;
            NativeArray<int>    visit  = input.RingVisitOrder;

            // ── Roof: the flat fill's earcut+project chain, unchanged. The extrusion vertex layout carries no
            // band attribute, and an extruded building keeps a hard silhouette. ──────────────────────────
            input.SuppressBoundaryBand = true;
            FillGraphOutput roof = FillMeshGraph.Schedule(input, deps);

            // ── Walls: raw (pre-earcut) ring vertices via the SAME select-or-clip branch the roof takes on
            // input.Clip, then tile→geo→project, then WallQuadJob emits quads. ───────────────────────────────

            // Main-thread pre-pass over BORROWED inputs only, never a job output. totalVerts is a CAPACITY HINT:
            // clipping may add or drop vertices, so ProjectionColumnSizingJob sets the real lengths at execute.
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
                // Raw NativeArrays, not NewBuffer, as the roof's ping-pong buffers are: the
                // DebugBuffersAllocated/DebugBufferDisposeNodes pair counts only this graph's NativeLists.
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

            // On the chain and on BOTH arms, so the two arms cannot drift: even where the select arm's length
            // equals totalVerts, this node, not the main thread, computes it.
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

            // clipDisposeHandle (default on the select arm) makes the clip buffers' dispose nodes reachable
            // from the returned handle; without it they leak once per extrusion layer per tile.
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
