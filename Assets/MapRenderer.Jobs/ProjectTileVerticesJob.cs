using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst job: converts tile-space double2 coordinates to origin-relative float3 world positions.
    ///
    /// Formula (reimplemented from docs/coordinates-and-projections.md §4 + §2 using Unity.Mathematics):
    ///   u = (tileX + px / extent) / 2^z
    ///   v = (tileY + py / extent) / 2^z
    ///   lon_rad = u * 2π − π
    ///   lat_rad = atan(sinh(π * (1 − 2v)))
    ///   mercX = R * lon_rad
    ///   mercY = R * log(tan(π/4 + lat_rad/2))
    ///   output = float3(mercX − originX, 0, mercY − originY)   (east=+X, north=+Z, height=+Y)
    ///
    /// The double subtract-then-cast pattern keeps single-tile precision within float32 range.
    /// Note: EditMode tests run jobs via managed fallback (not Burst-compiled) — they validate
    /// numerics but do NOT prove Burst compilation. Burst-compile test is deferred.
    ///
    /// Does NOT reference MapRenderer.Core to keep Core's System.Math off the Burst code path.
    /// </summary>
    [BurstCompile]
    public struct ProjectTileVerticesJob : IJobParallelFor
    {
        // Tile address
        [ReadOnly] public int TileZ;
        [ReadOnly] public int TileX;
        [ReadOnly] public int TileY;
        [ReadOnly] public double Extent;

        // Origin in Web Mercator meters (subtract before cast to float)
        [ReadOnly] public double OriginMercX;
        [ReadOnly] public double OriginMercY;

        [ReadOnly] public NativeArray<double2> TileCoords;   // (px, py) in tile space
        [WriteOnly] public NativeArray<float3> WorldPositions; // east=+X, height=+Y, north=+Z

        // Earth radius (Web Mercator / EPSG:3857)
        private const double R = 6378137.0;

        public void Execute(int index)
        {
            double2 tp = TileCoords[index];
            double px = tp.x;
            double py = tp.y;

            // Tile → normalised [0,1] map coordinates
            double pow2z = math.pow(2.0, TileZ);
            double u = (TileX + px / Extent) / pow2z;
            double v = (TileY + py / Extent) / pow2z;

            // Normalised → lon/lat (radians)
            double lonRad = u * (2.0 * math.PI_DBL) - math.PI_DBL;
            // lat = atan(sinh(π*(1−2v))) — expand sinh via exp to stay in Unity.Mathematics
            double arg = math.PI_DBL * (1.0 - 2.0 * v);
            double sinhArg = (math.exp(arg) - math.exp(-arg)) * 0.5;
            double latRad = math.atan(sinhArg);

            // lon/lat → Web Mercator meters (spherical Mercator, R = semi-major axis)
            double mercX = R * lonRad;
            // y = R * ln(tan(π/4 + lat/2))
            double halfLat = latRad * 0.5;
            double tanArg = math.tan(math.PI_DBL * 0.25 + halfLat);
            double mercY = R * math.log(tanArg);

            // Subtract origin in double, then cast to float — RTC precision
            double dx = mercX - OriginMercX;
            double dz = mercY - OriginMercY;

            WorldPositions[index] = new float3((float)dx, 0f, (float)dz);
        }
    }
}
