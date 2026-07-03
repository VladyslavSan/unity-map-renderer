using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Converts tile-space <c>double2</c> coordinates to geodetic SURFACE points (<see cref="GeoCoordinate"/>,
    /// lat/lon degrees, no elevation). Projection-INDEPENDENT — pure tile math (docs §4), the same for every
    /// projection. It feeds the projection job (<c>ProjectPointsJob&lt;TProj&gt;</c>), which does ONLY the projection.
    ///
    /// <para>Splitting tile→geo out of the projection keeps the projection job reusable for any geodetic input
    /// (symbols, markers). A future terrain step would elevate these surface points into
    /// <see cref="GeoCoordinate3D"/> (a more-complex path than the surface case) before projection.</para>
    ///
    /// Formula (docs/coordinates-and-projections.md §4):
    ///   u = (tileX + px / extent) / 2^z ;  v = (tileY + py / extent) / 2^z
    ///   lon = u·360 − 180 ;  lat = atan(sinh(π·(1 − 2v)))·180/π
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct TileToGeoJob : IJobParallelFor
    {
        [ReadOnly] public int    TileZ;
        [ReadOnly] public int    TileX;
        [ReadOnly] public int    TileY;
        [ReadOnly] public double Extent;

        [ReadOnly]  public NativeArray<double2>       TileCoords; // (px, py) in tile space
        [WriteOnly] public NativeArray<GeoCoordinate> OutGeo;     // lat/lon degrees (surface, no elevation)

        public void Execute(int index)
        {
            double2 tp = TileCoords[index];

            double pow2z = math.pow(2.0, TileZ);
            double u     = (TileX + tp.x / Extent) / pow2z;
            double v     = (TileY + tp.y / Extent) / pow2z;

            double lonRad  = u * (2.0 * math.PI_DBL) - math.PI_DBL;
            double arg     = math.PI_DBL * (1.0 - 2.0 * v);
            double sinhArg = (math.exp(arg) - math.exp(-arg)) * 0.5; // sinh via exp — stays in Unity.Mathematics
            double latRad  = math.atan(sinhArg);

            OutGeo[index] = new GeoCoordinate
            {
                Latitude  = latRad * (180.0 / math.PI_DBL),
                Longitude = lonRad * (180.0 / math.PI_DBL),
            };
        }
    }
}
