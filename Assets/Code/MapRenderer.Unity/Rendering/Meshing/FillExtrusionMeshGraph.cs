using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Jobs.Fill;
using MapRenderer.Unity.Jobs.Geometry;
using MapRenderer.Unity.Jobs.Projection;
namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// Schedules one fill-extrusion layer's roof (<see cref="FillMeshGraph.Schedule"/>, unchanged) and wall
    /// chain, one quad per edge of every clipped ring except the edges the clip introduced. Roof and walls read
    /// <c>input.Geometry</c> <c>[ReadOnly]</c> only, so both schedule on the caller's deps and may run
    /// concurrently; neither calls <c>Complete()</c>. See docs/job-scheduling-design.md
    /// § "Invariants that constrain what is built next".
    /// </summary>
    public static class FillExtrusionMeshGraph
    {
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

            // ── Walls: raw (pre-earcut) ring vertices via VisitedRingCopy — the SAME select-or-clip branch
            // the roof takes on input.Clip, then tile→geo→project, then WallQuadJob emits quads. ────────────

            // totalVerts is a CAPACITY HINT: clipping may add or drop vertices, so ProjectionColumnSizingJob
            // sets the real lengths at execute.
            VisitedRingCopy.Measure(source, visit, out int maxRingLen, out int totalVerts);

            var flatTile    = NewBuffer<double2>(math.max(1, totalVerts));
            var flatOffsets = NewBuffer<int>(visit.Length + 1);
            var flatFeatIdx = NewBuffer<int>(math.max(1, visit.Length));
            var flatEdgeCut = NewBuffer<byte>(math.max(1, totalVerts)); // 1 = an edge the clip introduced

            bool clipEnabled = input.Clip.TryWindow(source.Extent, out double2 clipMin, out double2 clipMax);
            JobHandle gathered = VisitedRingCopy.Schedule(
                source, visit, maxRingLen, clipEnabled, clipMin, clipMax, flatTile, flatOffsets, flatFeatIdx, flatEdgeCut, deps);

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
            }.Schedule(flatTile, FillMeshGraph.VertexBatch, sized);

            // flatTile's last reader is TileToGeoJob (as TileCoords) — WallQuadJob never reads it, only the
            // columns TileToGeoJob/ProjectionDispatch derive from it.
            JobHandle flatTileDisposed = ScheduleDispose(flatTile, geodetic);

            JobHandle projected = ProjectionDispatch.Schedule(
                input.Projection, input.OriginRender, geo, world, up, geodetic);

            StyledFillExtrusionTileBuilder.WallColumns walls = StyledFillExtrusionTileBuilder.WallColumns.Allocate();
            bool globeArm = !double.IsInfinity(input.Projection.MaxRefineAngleRad);

            JobHandle walled = new StyledFillExtrusionTileBuilder.WallQuadJob
            {
                RingOffsets = flatOffsets, RingFeatureIdx = flatFeatIdx, EdgeCut = flatEdgeCut,
                Geo = geo, World = world, Up = up,
                FeatureColors = featureColors, FeatureBake = featureBake, Globe = globeArm,
                OutPositionNormal = walls.PositionNormal, OutExtrude = walls.Extrude,
                OutTangent = walls.Tangent, OutColor = walls.Color, OutIndices = walls.Indices,
            }.Schedule(projected);

            // flatOffsets/flatFeatIdx/geo/world/up's last reader is WallQuadJob — geo included, it reads
            // Geo[idx].Latitude for the sec φ factor.
            JobHandle flatOffsetsDisposed = ScheduleDispose(flatOffsets, walled);
            JobHandle flatFeatIdxDisposed = ScheduleDispose(flatFeatIdx, walled);
            JobHandle flatEdgeCutDisposed = ScheduleDispose(flatEdgeCut, walled);
            JobHandle geoDisposed         = ScheduleDispose(geo, walled);
            JobHandle worldDisposed       = ScheduleDispose(world, walled);
            JobHandle upDisposed          = ScheduleDispose(up, walled);

            JobHandle scratchDisposed = JobHandle.CombineDependencies(
                JobHandle.CombineDependencies(flatTileDisposed, flatOffsetsDisposed, flatFeatIdxDisposed),
                JobHandle.CombineDependencies(
                    JobHandle.CombineDependencies(geoDisposed, worldDisposed, upDisposed), flatEdgeCutDisposed));

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
