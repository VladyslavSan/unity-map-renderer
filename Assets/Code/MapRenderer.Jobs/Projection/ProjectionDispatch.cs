using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Projection
{
    /// <summary>
    /// The managed side of projection polymorphism: given a (boxed) <see cref="IProjection"/>, schedules the
    /// matching <see cref="ProjectPointsJob{TProj}"/> specialisation, because Burst cannot do virtual dispatch.
    /// This is the one place the concrete projection structs are enumerated: a new projection needs its struct,
    /// one <c>case</c> here, and its <c>RegisterGenericJobType</c> line in the generic job.
    /// </summary>
    public static class ProjectionDispatch
    {
        /// <summary>Scheduled entry point — the fill(-extrusion) graph's tile→geo/project node.
        /// <paramref name="points"/>/<paramref name="world"/>/<paramref name="normals"/> are lists whose
        /// deferred views the scheduled <see cref="ProjectPointsJob{TProj}"/> reads/writes — resolved inside
        /// the job at execute time, so their lengths need only be correct by then, not at this call.</summary>
        public static JobHandle Schedule(
            IProjection projection, double3 originWorld,
            NativeList<GeoCoordinate> points, NativeList<double3> world, NativeList<double3> normals,
            JobHandle deps)
        {
            switch (projection)
            {
                case SphericalProjection sp:   return ScheduleTyped(sp, originWorld, points, world, normals, deps);
                case WebMercatorProjection wm: return ScheduleTyped(wm, originWorld, points, world, normals, deps);
                case null:
                    throw new NotSupportedException(
                        "ProjectionDispatch.Schedule received a null projection — the null-means-Mercator " +
                        "default now lives ONE place (TileManager.TickCore), not here. A caller reaching this " +
                        "directly (a fixture, most likely) must pass a real IProjection.");
                default:
                    throw new NotSupportedException(
                        $"No ProjectPointsJob dispatch for projection type {projection.GetType().Name}. " +
                        "Add a case here + a RegisterGenericJobType line in ProjectPointsJob.");
            }
        }

        /// <summary>Vertices per batch for <see cref="ProjectPointsJob{TProj}"/>, shared by all three mesh
        /// graphs because this is the one place projection dispatch happens. One transcendental cluster per
        /// point, so 1024 points carries enough work to cover a batch hand-off. A starting value, not a
        /// measured optimum.</summary>
        internal const int VertexBatch = 1024;

        /// <summary><c>internal</c>, not <c>private</c>, so a test can drive a projection Burst never
        /// registers generically without going through <see cref="Schedule"/>'s closed switch. That switch
        /// stays the production enumeration; this visibility adds no case to it.</summary>
        internal static JobHandle ScheduleTyped<TProj>(
            TProj projection, double3 originWorld,
            NativeList<GeoCoordinate> points, NativeList<double3> world, NativeList<double3> normals,
            JobHandle deps)
            where TProj : struct, IProjection
            => new ProjectPointsJob<TProj>
            {
                Projection     = projection,
                OriginWorld    = originWorld,
                Points         = points.AsDeferredJobArray(),
                WorldPositions = world.AsDeferredJobArray(),
                Normals        = normals.AsDeferredJobArray(),
            }.Schedule(points, VertexBatch, deps);
    }
}
