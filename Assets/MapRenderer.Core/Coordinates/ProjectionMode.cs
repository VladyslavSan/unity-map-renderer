namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Selects the projection used to transform geodetic coordinates to render-space world coordinates.
    ///
    /// <para>Projection mode is chosen ONCE per session at startup and is never toggled at runtime
    /// (DOD design — no runtime polymorphism, no Mercator↔Globe morph). The live path in S61 is
    /// always <see cref="WebMercator"/>; the selector exists so the tile-build pipeline can be
    /// extended to <see cref="Ecef"/> in a future globe stage without changing the architecture.</para>
    ///
    /// <para>Per-projection concrete jobs call shared static math (<see cref="WebMercator"/> /
    /// <see cref="Ecef"/>). An interface cannot run inside Burst; this enum is the seam.</para>
    /// </summary>
    public enum ProjectionMode
    {
        /// <summary>
        /// EPSG:3857 spherical Web Mercator. Flat-plane projection. Live path in S61.
        /// Render axes: east=+X, altitude=+Y, north=+Z.
        /// </summary>
        WebMercator = 0,

        /// <summary>
        /// WGS-84 ellipsoidal ECEF. 3D globe projection. Math module shipped in S61;
        /// full globe rendering (job, tile cover, camera orbit) deferred to a later stage.
        /// </summary>
        Ecef = 1,
    }
}
