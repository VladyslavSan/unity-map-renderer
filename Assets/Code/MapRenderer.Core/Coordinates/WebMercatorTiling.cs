using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Geodetic → slippy-tile coordinates, the ONE lon/lat conversion site of the GeoJSON slicing stack and the
    /// exact inverse of <see cref="TileId.ToLonLat"/>, so a round-trip test is independent. Non-local invariant:
    /// <c>v</c> comes from metres via <see cref="WebMercator.FromLonLat"/>, because only
    /// <see cref="WebMercator.Forward"/> may hold the Mercator formula; <c>v</c> grows southward, which
    /// reverses ring winding (<c>GeoJsonParser</c> accounts for it). <c>|lat| &gt; MaxLatitude</c> is clamped,
    /// not rejected, so a pole-covering polygon renders; latitudes outside [−90, 90] are rejected at parse.
    /// </summary>
    public static class WebMercatorTiling
    {
        /// <summary>
        /// Geodetic → unit square <c>[0,1]²</c>, zoom-independent. Latitude is clamped to
        /// <see cref="WebMercator.MaxLatitude"/>.
        /// </summary>
        public static double2 UnitSquareFromLonLat(GeoCoordinate geo)
        {
            double latitude = math.clamp(geo.Latitude, -WebMercator.MaxLatitude, WebMercator.MaxLatitude);

            double2 metres = WebMercator.FromLonLat(
                new GeoCoordinate3D { Latitude = latitude, Longitude = geo.Longitude });

            double planeWidth = 2.0 * WebMercator.WorldExtent;
            return new double2(0.5 + metres.x / planeWidth,
                               0.5 - metres.y / planeWidth);
        }

        /// <summary>
        /// Unit square → tile-local coordinates in <paramref name="tile"/> at <paramref name="extent"/>.
        /// Origin top-left, Y down; <c>[0, extent]</c> is the tile proper, values outside it are the buffer.
        /// </summary>
        public static double2 TileLocal(double2 unitSquare, TileId tile, double extent)
        {
            double n = math.pow(2.0, tile.Z);
            return new double2((unitSquare.x * n - tile.X) * extent,
                               (unitSquare.y * n - tile.Y) * extent);
        }

        /// <summary>The tile's north-west corner in unit-square coordinates (bbox-reject helper).</summary>
        public static double2 UnitSquareTileMin(TileId tile)
        {
            double n = math.pow(2.0, tile.Z);
            return new double2(tile.X / n, tile.Y / n);
        }

        /// <summary>The tile's south-east corner in unit-square coordinates (bbox-reject helper).</summary>
        public static double2 UnitSquareTileMax(TileId tile)
        {
            double n = math.pow(2.0, tile.Z);
            return new double2((tile.X + 1) / n, (tile.Y + 1) / n);
        }
    }
}
