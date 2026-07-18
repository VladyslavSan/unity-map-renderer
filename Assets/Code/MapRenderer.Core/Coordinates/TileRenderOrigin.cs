using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// The RTC render-space origin for a tile: its SW corner (tile-space west+south, i.e. px=0, py=extent) as
    /// geodetic, projected through the chosen projection. Mercator: ≈ MercatorBounds().min (sub-nanometre drift
    /// from the lon/lat round-trip). Globe: the corner's ECEF. Projection-derived so the origin and the tile's
    /// vertices share one projection — correct for the globe, not just the plane.
    ///
    /// <para>This is the SINGLE source of a tile's bake origin: the fill mesh bake (the fill pipeline's
    /// <c>LayerInput.OriginRender</c>) and the tile transform (via <c>TileManager</c>) both call it, so the two
    /// RTC levels cancel exactly. It is engine-free tile+projection math used by every geometry kind (fills,
    /// lines, symbols) and the camera — NOT fill-specific — so it lives here in Core, never on the fill
    /// pipeline. (Producers declare, boundaries convert — see docs/conventions.md.)</para>
    /// </summary>
    public static class TileRenderOrigin
    {
        /// <summary>Project the tile's SW corner through <paramref name="projection"/> (null ⇒ planar Mercator).</summary>
        public static double3 Project(TileId tile, IProjection projection)
        {
            // Matches TileId.ToLonLat exactly (math.sinh, same u/v), so the Mercator origin equals
            // MercatorBounds().min bit-for-bit — the tile-transform (FloatingOrigin) uses that, and the mesh
            // must share it. (Sub-nm vs the vertices' exp-form sinh in TileToGeoJob — invisible.)
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
