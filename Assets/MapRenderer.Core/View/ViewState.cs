using System;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Immutable camera/view description in geographic terms: where the map is centered, how zoomed in,
    /// and the camera orientation (bearing/pitch). This is the single source of truth that drives tile
    /// selection (<see cref="TileCover"/>) and the floating-origin scene placement
    /// (<see cref="FloatingOrigin"/>).
    ///
    /// <para>
    /// <b>Scope split (S06):</b> <see cref="BearingDeg"/> and <see cref="PitchDeg"/> live on the camera
    /// transform only — they orient the Unity camera but do NOT rotate tile/map-root model transforms,
    /// which stay translation-only so +Y fill normals are preserved (deliberate de-risk vs. the
    /// non-identity-transform shader follow-up). <see cref="CenterLon"/>/<see cref="CenterLat"/>/
    /// <see cref="Zoom"/> drive what is loaded and where the scene origin sits.
    /// </para>
    ///
    /// <para>
    /// Pure Core type: doubles + <see cref="double2"/>, no engine dependency.
    /// </para>
    /// </summary>
    public readonly struct ViewState
    {
        /// <summary>Map center longitude, degrees in [-180, 180].</summary>
        public readonly double CenterLon;
        /// <summary>Map center latitude, degrees. Clamped to the Web-Mercator limit for tile math.</summary>
        public readonly double CenterLat;
        /// <summary>Fractional zoom level (MapLibre semantics: z increases by 1 per halving of ground span).</summary>
        public readonly double Zoom;
        /// <summary>Camera bearing (rotation about the vertical axis), degrees clockwise from north.</summary>
        public readonly double BearingDeg;
        /// <summary>Camera pitch (tilt away from straight-down), degrees in [0, ~85].</summary>
        public readonly double PitchDeg;

        /// <summary>Web-Mercator latitude limit (±85.05112878°) — beyond this the projection is undefined.</summary>
        public const double MaxMercatorLat = 85.05112878;

        public ViewState(double centerLon, double centerLat, double zoom,
                         double bearingDeg = 0.0, double pitchDeg = 0.0)
        {
            CenterLon  = centerLon;
            CenterLat  = centerLat;
            Zoom       = zoom;
            BearingDeg = bearingDeg;
            PitchDeg   = pitchDeg;
        }

        /// <summary>
        /// The integer tile zoom for this view: <c>floor(Zoom)</c>, never negative. Tile selection uses
        /// the integer zoom; fractional zoom drives smooth scaling/interpolation, not which tiles load.
        /// </summary>
        public int IntegerZoom => Math.Max(0, (int)Math.Floor(Zoom));

        /// <summary>
        /// The center, clamped to the Mercator latitude limit, projected to Web-Mercator meters.
        /// </summary>
        public double2 CenterMercator()
        {
            double lat = Math.Max(-MaxMercatorLat, Math.Min(MaxMercatorLat, CenterLat));
            return WebMercator.FromLonLat(CenterLon, lat);
        }

        /// <summary>Returns a copy with a new center (lon/lat), keeping zoom/bearing/pitch.</summary>
        public ViewState WithCenter(double lon, double lat)
            => new ViewState(lon, lat, Zoom, BearingDeg, PitchDeg);

        /// <summary>Returns a copy with a new zoom, keeping center/bearing/pitch.</summary>
        public ViewState WithZoom(double zoom)
            => new ViewState(CenterLon, CenterLat, zoom, BearingDeg, PitchDeg);

        /// <summary>Returns a copy with new bearing/pitch, keeping center/zoom.</summary>
        public ViewState WithOrientation(double bearingDeg, double pitchDeg)
            => new ViewState(CenterLon, CenterLat, Zoom, bearingDeg, pitchDeg);

        public override string ToString()
            => $"ViewState(lon={CenterLon:F5}, lat={CenterLat:F5}, z={Zoom:F3}, " +
               $"bearing={BearingDeg:F1}, pitch={PitchDeg:F1})";
    }
}
