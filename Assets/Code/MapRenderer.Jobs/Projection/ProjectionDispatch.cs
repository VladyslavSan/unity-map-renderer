using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Projection
{
    /// <summary>
    /// The managed side of projection polymorphism: given a (boxed) <see cref="IProjection"/>, picks the
    /// matching <see cref="ProjectPointsJob{TProj}"/> specialisation and schedules it. Burst cannot hold a
    /// managed <see cref="IProjection"/> or do virtual dispatch, so the concrete projection STRUCT is chosen
    /// here (once per tile) and handed to the generic job as its type parameter.
    ///
    /// <para>This is the ONE place the concrete projection structs are enumerated — the type-dispatch that
    /// replaces an enum switch (the struct type IS the discriminator). Adding a projection = its struct + one
    /// <c>case</c> here + its <c>RegisterGenericJobType</c> line in <see cref="ProjectPointsJob{TProj}"/>.</para>
    ///
    /// <para>There is no synchronous entry point. Every caller schedules, including the extrusion wall
    /// chain, so no call site needs <c>.Run()</c>.</para>
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
