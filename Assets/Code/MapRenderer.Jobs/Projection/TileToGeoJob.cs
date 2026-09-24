using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Projection
{
    /// <summary>
    /// Converts tile-space <c>double2</c> coordinates to geodetic SURFACE points (<see cref="GeoCoordinate"/>,
    /// lat/lon degrees, no elevation). Projection-INDEPENDENT — pure tile math, the same for every projection.
    /// It feeds <c>ProjectPointsJob&lt;TProj&gt;</c>, which then stays reusable for any geodetic input.
    /// Formula: u = (tileX + px / extent) / 2^z ;  v = (tileY + py / extent) / 2^z
    ///   lon = u·360 − 180 ;  lat = atan(sinh(π·(1 − 2v)))·180/π
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct TileToGeoJob : IJobParallelFor, IJobParallelForDefer
    {
        [ReadOnly] public TileId Tile;   // slippy-map address (z/x/y); blittable readonly struct, Burst-safe
        [ReadOnly] public double Extent;

        [ReadOnly]  public NativeArray<double2>       TileCoords; // (px, py) in tile space
        [WriteOnly] public NativeArray<GeoCoordinate> OutGeo;     // lat/lon degrees (surface, no elevation)

        public void Execute(int index) => OutGeo[index] = GeoAt(Tile, Extent, TileCoords[index]);

        /// <summary>
        /// This job's formula, as a standalone Burst-compatible static function — shared with
        /// <see cref="Execute"/> above so there is exactly one copy of the tile→geo math. It serves a caller
        /// that needs a geodetic point at an individual tile-local vertex, in a loop that walks vertices one
        /// at a time rather than a <see cref="NativeArray{T}"/> batch worth scheduling a job over.
        /// </summary>
        /// <param name="tile">The tile whose local space <paramref name="tileVertex"/> is in.</param>
        /// <param name="extent">The tile's quantization range (MVT extent).</param>
        /// <param name="tileVertex">A tile-local point, <c>[0, extent)</c>, Y-down.</param>
        public static GeoCoordinate GeoAt(TileId tile, double extent, double2 tileVertex)
        {
            double pow2z = math.pow(2.0, tile.Z);
            double u     = (tile.X + tileVertex.x / extent) / pow2z;
            double v     = (tile.Y + tileVertex.y / extent) / pow2z;

            double lonRad  = u * (2.0 * math.PI_DBL) - math.PI_DBL;
            double arg     = math.PI_DBL * (1.0 - 2.0 * v);
            double sinhArg = (math.exp(arg) - math.exp(-arg)) * 0.5; // sinh via exp — stays in Unity.Mathematics
            double latRad  = math.atan(sinhArg);

            return new GeoCoordinate
            {
                Latitude  = latRad * (180.0 / math.PI_DBL),
                Longitude = lonRad * (180.0 / math.PI_DBL),
            };
        }
    }
}
