using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// The RTC render-space origin for a tile: its SW corner (px=0, py=extent) as geodetic, projected through the
    /// chosen projection, so the origin and the tile's vertices share one projection. Mercator:
    /// MercatorBounds().min; globe: the corner's ECEF. Non-local invariant: this is the single source of a tile's
    /// bake origin; the fill bake (<c>LayerInput.OriginRender</c>) and the tile transform (via
    /// <c>TileManager</c>) both call it, so the two RTC levels cancel exactly.
    /// </summary>
    public static class TileRenderOrigin
    {
        /// <summary>Project the tile's SW corner through <paramref name="projection"/> (null ⇒ planar Mercator).</summary>
        public static double3 Project(TileId tile, IProjection projection)
        {
            // Matches TileId.ToLonLat (math.sinh, same u/v), so the Mercator origin equals MercatorBounds().min
            // bit-for-bit, which the tile transform (FloatingOrigin) also uses. Limitation: it differs by
            // sub-nm from the vertices' exp-form sinh in TileToGeoJob, which is invisible.
            double pow2z = math.pow(2.0, tile.Z);
            double u     = tile.X / pow2z;         // west edge (px = 0)
            double v     = (tile.Y + 1.0) / pow2z; // south edge (py = extent)

            var geo = new GeoCoordinate
            {
                Latitude  = math.atan(math.sinh(math.PI_DBL * (1.0 - 2.0 * v))) * 180.0 / math.PI_DBL,
                Longitude = u * 360.0 - 180.0,
            };
            // null ⇒ planar default (no boxing — direct struct call).
            return projection != null
                ? projection.ProjectPoint(geo).World
                : new WebMercatorProjection().ProjectPoint(geo).World;
        }
    }
}
