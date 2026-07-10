using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// The managed side of projection polymorphism: given a (boxed) <see cref="IProjection"/>, picks the
    /// matching <see cref="ProjectPointsJob{TProj}"/> specialisation and runs it. Burst cannot hold a managed
    /// <see cref="IProjection"/> or do virtual dispatch, so the concrete projection STRUCT is chosen here (once
    /// per tile) and handed to the generic job as its type parameter.
    ///
    /// <para>This is the ONE place the concrete projection structs are enumerated — the type-dispatch that
    /// replaces an enum switch (the struct type IS the discriminator). Adding a projection = its struct + one
    /// <c>case</c> here + its <c>RegisterGenericJobType</c> line in <see cref="ProjectPointsJob{TProj}"/>.</para>
    /// </summary>
    public static class ProjectionDispatch
    {
        /// <summary>Project <paramref name="points"/> → origin-relative <paramref name="world"/> + <paramref name="normals"/>
        /// through the concrete projection, run synchronously on the calling thread (the S89 worker-thread path).</summary>
        public static void Run(
            IProjection projection, double3 originWorld,
            NativeArray<GeoCoordinate> points, NativeArray<double3> world, NativeArray<double3> normals, int count)
        {
            switch (projection)
            {
                case SphericalProjection sp:  RunTyped(sp,                       originWorld, points, world, normals, count); break;
                case WebMercatorProjection wm: RunTyped(wm,                      originWorld, points, world, normals, count); break;
                case null:                     RunTyped(new WebMercatorProjection(), originWorld, points, world, normals, count); break; // default planar
                default:
                    throw new NotSupportedException(
                        $"No ProjectPointsJob dispatch for projection type {projection.GetType().Name}. " +
                        "Add a case here + a RegisterGenericJobType line in ProjectPointsJob.");
            }
        }

        private static void RunTyped<TProj>(
            TProj projection, double3 originWorld,
            NativeArray<GeoCoordinate> points, NativeArray<double3> world, NativeArray<double3> normals, int count)
            where TProj : struct, IProjection
            => new ProjectPointsJob<TProj>
            {
                Projection     = projection,
                OriginWorld    = originWorld,
                Points         = points,
                WorldPositions = world,
                Normals        = normals,
            }.Run(count);
    }
}
