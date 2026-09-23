using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Geodetic → slippy-tile coordinates: the ONE conversion site for the GeoJSON slicing stack. Nothing else
    /// in that stack may touch lon/lat. The four paragraphs below are non-local invariants.
    /// <para><b>Independent oracle.</b> The exact inverse of <see cref="TileId.ToLonLat"/>
    /// (<c>u = (X + px/extent)/2^z</c>, <c>lon = u·360 − 180</c>, <c>lat = atan(sinh(π·(1 − 2v)))</c>), so a
    /// round-trip test through <c>ToLonLat</c> is an independent check, not a restatement.</para>
    /// <para><b>Single-source rule.</b> <c>v</c> comes from metres, not the closed form
    /// <c>v = (1 − asinh(tan φ)/π)/2</c>: that is the Mercator forward literal, which only
    /// <see cref="WebMercator.Forward"/> may contain. The detour through
    /// <see cref="WebMercator.FromLonLat"/> costs one division.</para>
    /// <para><b>Orientation.</b> <c>(0,0)</c> is the world's north-west corner and <c>(1,1)</c> the south-east:
    /// <c>v</c> grows southward, so the map reverses orientation. A ring's shoelace sign flips on projection,
    /// and <c>GeoJsonParser</c>'s winding normalisation accounts for it.</para>
    /// <para><b>Latitude clamp.</b> <c>|lat| &gt; MaxLatitude</c> has no Mercator image (<c>v → ±∞</c>).
    /// Clamping, not rejection, is the only way a pole-covering polygon renders; a test pins it. Latitudes
    /// outside <c>[−90, 90]</c> are malformed input, rejected at parse.</para>
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
