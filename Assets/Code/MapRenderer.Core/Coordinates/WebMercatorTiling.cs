using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Geodetic → slippy-tile coordinates: the ONE conversion site for the GeoJSON slicing stack. Nothing
    /// else in that stack may touch lon/lat.
    ///
    /// <para><b>Exact inverse of <see cref="TileId.ToLonLat"/></b> (<c>Coordinates/TileId.cs</c>), which
    /// computes <c>u = (X + px/extent)/2^z</c>, <c>lon = u·360 − 180</c>,
    /// <c>lat = atan(sinh(π·(1 − 2v)))</c>. This type is that algebra run backwards — which is what makes a
    /// round-trip test through <c>ToLonLat</c> an INDEPENDENT oracle rather than a restatement.</para>
    ///
    /// <para><b>Why <c>v</c> is derived from metres and not written in closed form.</b> The obvious closed
    /// form <c>v = (1 − asinh(tan φ)/π)/2</c> IS the Mercator forward literal, and
    /// <see cref="WebMercator.Forward"/> is documented as the only method in the codebase allowed to contain
    /// it. Going through <see cref="WebMercator.FromLonLat"/> and dividing by the plane width keeps that
    /// single-source rule intact at the cost of one division.</para>
    ///
    /// <para><b>Unit square.</b> <c>(0,0)</c> is the north-west corner of the world (lon −180,
    /// lat +<see cref="WebMercator.MaxLatitude"/>) and <c>(1,1)</c> the south-east one: <c>u</c> grows
    /// eastward, <c>v</c> grows SOUTHWARD. The map is therefore orientation-REVERSING — a ring's shoelace
    /// sign flips on projection, which <c>GeoJsonParser</c>'s winding normalisation accounts for.</para>
    ///
    /// <para><b>Latitude clamp, not rejection.</b> <c>|lat| &gt; MaxLatitude</c> has no Mercator image
    /// (<c>v → ±∞</c>). Clamping is the only behaviour under which a pole-covering polygon renders at all;
    /// it is a recorded limitation, pinned by a test, not silent drift. Latitudes outside <c>[−90, 90]</c>
    /// are malformed input and are rejected upstream, at parse.</para>
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
