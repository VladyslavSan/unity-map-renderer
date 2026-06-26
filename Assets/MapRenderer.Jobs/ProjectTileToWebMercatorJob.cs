using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst job: converts tile-space double2 coordinates to origin-relative float3 world positions
    /// using the Web Mercator projection (<see cref="WebMercator.Forward"/>).
    ///
    /// Formula (docs/coordinates-and-projections.md §4 + §2):
    ///   u = (tileX + px / extent) / 2^z
    ///   v = (tileY + py / extent) / 2^z
    ///   lon_rad = u * 2π − π
    ///   lat_rad = atan(sinh(π * (1 − 2v)))
    ///   forward = WebMercator.Forward(GeoCoordinate3D(lon_deg, lat_deg, 0))
    ///   output  = float3(forward.x − originX, 0, forward.z − originY)   (east=+X, north=+Z, height=+Y)
    ///
    /// The double subtract-then-cast pattern keeps single-tile precision within float32 range (RTC).
    ///
    /// Note: EditMode tests run jobs via managed fallback (not Burst-compiled) — they validate
    /// numerics but do NOT prove Burst compilation. Burst-compile correctness is confirmed by
    /// running ./Tools/run-tests.sh (batch mode) and checking Logs/test-run.log.
    ///
    /// The tile→lon/lat conversion is kept inline (atan(sinh(…)) form — NOT the Mercator literal);
    /// only the Mercator-forward step delegates to <see cref="WebMercator.Forward"/> (T2 single source).
    /// The rad→deg→rad round-trip introduces sub-nanometre drift vs the pre-refactor inline; this is
    /// far below any tolerance and below float32 ULP at world scale.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct ProjectTileToWebMercatorJob : IJobParallelFor
    {
        // Tile address
        [ReadOnly] public int    TileZ;
        [ReadOnly] public int    TileX;
        [ReadOnly] public int    TileY;
        [ReadOnly] public double Extent;

        // Origin in Web Mercator meters (subtract before cast to float)
        [ReadOnly] public double OriginMercX;
        [ReadOnly] public double OriginMercY;

        [ReadOnly]  public NativeArray<double2> TileCoords;     // (px, py) in tile space
        [WriteOnly] public NativeArray<float3>  WorldPositions; // east=+X, height=+Y, north=+Z

        public void Execute(int index)
        {
            double2 tp = TileCoords[index];
            double  px = tp.x;
            double  py = tp.y;

            // Tile → normalised [0,1] map coordinates
            double pow2z = math.pow(2.0, TileZ);
            double u     = (TileX + px / Extent) / pow2z;
            double v     = (TileY + py / Extent) / pow2z;

            // Normalised → lon/lat (radians)
            double lonRad = u * (2.0 * math.PI_DBL) - math.PI_DBL;
            // lat = atan(sinh(π*(1−2v))) — expand sinh via exp to stay in Unity.Mathematics
            double arg     = math.PI_DBL * (1.0 - 2.0 * v);
            double sinhArg = (math.exp(arg) - math.exp(-arg)) * 0.5;
            double latRad  = math.atan(sinhArg);

            // Convert to degrees for WebMercator.Forward (the single source of the Mercator literal).
            // The rad→deg→rad round-trip is sub-nanometre drift at world scale — well within any tolerance.
            double lonDeg = lonRad * (180.0 / math.PI_DBL);
            double latDeg = latRad * (180.0 / math.PI_DBL);

            // Project via the shared math module (T2: WebMercator.Forward is the single source).
            double3 world = WebMercator.Forward(new GeoCoordinate3D
                { Latitude = latDeg, Longitude = lonDeg, Altitude = 0.0 });

            // Subtract origin in double, then cast to float — RTC precision
            double dx = world.x - OriginMercX;
            double dz = world.z - OriginMercY;

            WorldPositions[index] = new float3((float)dx, 0f, (float)dz);
        }
    }
}