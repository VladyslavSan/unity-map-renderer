using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

// Generic Burst jobs must be registered so IL2CPP/AOT compiles each concrete instantiation (the Editor
// JIT-compiles them on first use without this). One line per projection struct that is ever run.
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.Projection.ProjectPointsJob<MapRenderer.Core.Geo.WebMercatorProjection>))]
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.Projection.ProjectPointsJob<MapRenderer.Core.Geo.SphericalProjection>))]

namespace MapRenderer.Jobs.Projection
{
    /// <summary>
    /// The single projection job: projects an array of geodetic SURFACE points (<see cref="GeoCoordinate"/>,
    /// no elevation) to origin-relative render positions + per-vertex surface normals. Does ONLY projection —
    /// the tile→geodetic step is a separate projection-independent job (<see cref="TileToGeoJob"/>), so this job
    /// is reusable for any geodetic input (tile vertices, symbols). Elevated geometry (GeoCoordinate3D) is a
    /// future, more-complex path.
    ///
    /// <para><b>Polymorphism via the struct type, no enum.</b> The concrete projection is the generic type
    /// parameter <typeparamref name="TProj"/> — a STATELESS struct implementing <see cref="IProjection"/>
    /// (e.g. <see cref="WebMercatorProjection"/> / <see cref="SphericalProjection"/>). Burst specialises this
    /// job per <typeparamref name="TProj"/> and devirtualises + inlines <c>Projection.ProjectPoint</c> — no
    /// boxing, no branch. The struct type IS the discriminator; the managed side picks the instantiation (see
    /// <see cref="ProjectionDispatch"/>). This is the SAME <c>ProjectPoint</c> the OOP <see cref="IProjection"/>
    /// forwards to — no separate copy of the projection math.</para>
    ///
    /// <para><b>Origin (RTC):</b> <see cref="OriginWorld"/> is the pre-projected render-space
    /// origin (projected once by the caller — not per vertex). Output stays <c>double3</c> (origin-relative);
    /// the mesh-write casts to <c>float3</c>. The subtract-in-double keeps single-tile precision within float32
    /// range — mandatory on the globe (ECEF ≈ ±6.37 Mm).</para>
    ///
    /// Note: Burst runs this job only if Jobs ▸ Burst ▸ Enable Compilation is on AND the job compiles —
    /// <c>CompileSynchronously = true</c> falls back to managed IL SILENTLY on a compile failure
    /// (<c>FillGraphBurstProbeTests</c>), so a passing numeric test alone never proves Burst compiled it. The
    /// runner is not the discriminator; in this project's practice <c>./Tools/run-tests.sh</c> (batch mode) is
    /// the path verified Burst-compiled, via its log's Burst-error grep, not the test result.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct ProjectPointsJob<TProj> : IJobParallelFor, IJobParallelForDefer
        where TProj : struct, IProjection
    {
        /// <summary>The projection — a stateless struct; Burst inlines its <c>ProjectPoint</c>.</summary>
        [ReadOnly] public TProj Projection;

        /// <summary>Pre-projected render-space origin to subtract (double; RTC). Projected once by the caller.</summary>
        [ReadOnly] public double3 OriginWorld;

        [ReadOnly]  public NativeArray<GeoCoordinate> Points;         // geodetic surface points to project
        [WriteOnly] public NativeArray<double3>       WorldPositions; // origin-relative (east=+X, up=+Y, north=+Z)
        [WriteOnly] public NativeArray<double3>       Normals;        // unit surface up per point

        public void Execute(int index)
        {
            ProjectedPoint pp = Projection.ProjectPoint(Points[index]);
            WorldPositions[index] = pp.World - OriginWorld;
            Normals[index]        = pp.Up;
        }
    }
}
