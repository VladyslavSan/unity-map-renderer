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
    /// <para>job-scheduling-design.md §8 stage 4 Group B retired the synchronous entry point
    /// (<c>Run</c>/<c>RunTyped</c>) with its only caller at the time, <c>FillMeshPipeline.Schedule</c>. The
    /// wall-job stage (§8 stage 5's invariant block) reinstated it for <c>WriteWalls</c>' off-main
    /// <c>IWorkScheduler</c> worker body, where §4 rule 1 made <see cref="Schedule"/> illegal there. The
    /// wall-job-GRAPH stage (§8 stage 5, this file's current state) retired it a second time: the wall chain
    /// moved from a synchronous <c>.Run()</c> loop inside <c>WriteWalls</c> to <see cref="Schedule"/>/
    /// <see cref="ScheduleTyped{TProj}"/>, scheduled by <c>FillExtrusionMeshGraph</c> alongside the roof —
    /// there is no longer an off-main call site needing <c>.Run()</c>. Correct in place, not appended: this
    /// paragraph names what is true NOW, the two lines above are the record of what was true when
    /// written.</para>
    /// </summary>
    public static class ProjectionDispatch
    {
        /// <summary>Scheduled entry point — the fill(-extrusion) graph's tile→geo/project node
        /// (job-scheduling-design.md §3.2 node 9).
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

        /// <summary>Vertices per batch for <see cref="ProjectPointsJob{TProj}"/> (job-scheduling-design.md §8
        /// stage 6) — shared by all three mesh graphs (fill, line, extrusion), since this is the one place
        /// projection dispatch happens. <c>1024</c>: one transcendental cluster per point, tens of ns each,
        /// so 1024 points is roughly 10-50 µs of work — comfortably above a batch hand-off's own cost. A
        /// starting value chosen by this reasoning, not a measured optimum — see the design doc's dated
        /// measurement before moving it.</summary>
        internal const int VertexBatch = 1024;

        /// <summary><c>internal</c> (was <c>private</c>): job-scheduling-design.md §8 stage 5 —
        /// <c>RightHandedSphereProjectionWindingTests</c> drives a right-handed curved projection Burst
        /// never registers generically, through <c>LineMeshGraph.ScheduleTyped</c>, which needs this entry
        /// point directly rather than going through <see cref="Schedule"/>'s closed switch (tooth (g)). The
        /// switch in <see cref="Schedule"/> stays the production enumeration — this widened visibility does
        /// not add a case to it.</summary>
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
