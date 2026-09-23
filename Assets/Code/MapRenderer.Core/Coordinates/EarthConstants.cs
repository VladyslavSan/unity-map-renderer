namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Pure-geodetic WGS-84 constants — the single source of truth for numbers that belong to
    /// the Earth's shape, independent of any projection. Holds ONLY universal geodetic numbers;
    /// projection- and tiling-specific constants (WorldExtent, MaxLatitude, TilePixelSize) live on
    /// <see cref="WebMercator"/>, not here.
    /// </summary>
    public static class EarthConstants
    {
        /// <summary>WGS-84 semi-major axis (equatorial radius), metres.</summary>
        public const double A = 6378137.0;

        /// <summary>WGS-84 flattening factor (inverse ≈ 298.257…). See the const initializer for the exact value.</summary>
        public const double F = 1.0 / 298.257223563;

        /// <summary>First eccentricity squared: E2 = F × (2 − F). Derived expression, not a baked literal.</summary>
        public const double E2 = F * (2.0 - F);

        /// <summary>WGS-84 semi-minor axis (polar radius), metres: B = A × (1 − F) ≈ 6356752.314…</summary>
        public const double B = A * (1.0 - F);

        /// <summary>
        /// Earth equatorial circumference in metres (IAU/WGS-84, clean-room constant).
        /// Kept as a LITERAL — do NOT replace with 2·π·A, which differs from this published value
        /// and would shift pinned camera tests.
        /// </summary>
        public const double EquatorialCircumferenceMetres = 40075016.686;
    }
}
