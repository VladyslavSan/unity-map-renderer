using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Engine-free camera pose math: the zoom→altitude framing, the clip planes derived from it, and
    /// the orbit pose around the look-at point. Lives in Core so headless tests can exercise the full
    /// pose computation.
    /// </summary>
    public static class CameraPoseMath
    {
        // ── Constants — reference EarthConstants and WebMercator (single sources of truth) ──────────
        // EarthCircumferenceMetres is a literal in EarthConstants, NOT derived as 2π·A.
        private const double EarthCircumferenceMetres = EarthConstants.EquatorialCircumferenceMetres;
        private const double TilePixelSize            = WebMercator.TilePixelSize;

        // ── Altitude ↔ Zoom ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Web-Mercator ground resolution: metres per pixel at a given fractional zoom level.
        ///
        /// <para>This is the single source of truth for the px→m conversion used everywhere (altitude
        /// calculation, line-width scaling, zoom-stability tests).</para>
        /// </summary>
        /// <param name="zoom">Fractional zoom level.</param>
        /// <returns>Ground resolution in metres per pixel.</returns>
        public static double MetersPerPixel(double zoom)
            => EarthCircumferenceMetres / (TilePixelSize * math.pow(2.0, zoom));

        /// <summary>
        /// Computes camera altitude in render-space metres from a fractional zoom level.
        /// </summary>
        /// <param name="zoom">Fractional zoom level.</param>
        /// <param name="viewportHeightPx">Deterministic reference viewport height.</param>
        /// <param name="verticalFovDeg">Vertical field-of-view in degrees.</param>
        /// <returns>Camera altitude in metres.</returns>
        public static double AltitudeForZoom(double zoom, double viewportHeightPx, double verticalFovDeg)
        {
            double metersPerPixel = MetersPerPixel(zoom);
            double halfFovRad     = Angle.FromDegrees(verticalFovDeg * 0.5).Radians;
            return (viewportHeightPx * metersPerPixel) / (2.0 * math.tan(halfFovRad));
        }

        // ── Fit-to-viewport minimum zoom ──────────────────────────────────────────────────────────

        /// <summary>
        /// The most-zoomed-out fractional zoom at which the whole Web-Mercator world still frames inside the
        /// viewport with a margin: <c>zoom_fit = log2(min(vpW, vpH) / tilePixelSize)</c> and the floor is
        /// <c>zoom_fit − margin</c>, so the world is never larger than the viewport and never cropped.
        /// Takes the DPI-normalized viewport, the same basis the camera and tile selector use. Cyclic (globe)
        /// worlds use this; the finite sheet uses <see cref="MinZoomToFill(double,double,double,double)"/>.
        /// </summary>
        /// <param name="viewportWidthLogical">Viewport width in logical (DPR-normalized) pixels.</param>
        /// <param name="viewportHeightLogical">Viewport height in logical (DPR-normalized) pixels.</param>
        /// <param name="tilePixelSize">Tile edge in px (the zoom→scale convention; <see cref="WebMercator.TilePixelSize"/>).</param>
        /// <param name="margin">Breathing-room margin in zoom levels (≈0.25–0.5).</param>
        /// <returns>The minimum-zoom floor (fractional).</returns>
        public static double MinZoomToFit(double viewportWidthLogical, double viewportHeightLogical,
                                          double tilePixelSize, double margin)
        {
            double minSide = math.min(viewportWidthLogical, viewportHeightLogical);
            double zoomFit = math.log2(minSide / tilePixelSize);
            return zoomFit - margin;
        }

        /// <summary>
        /// <see cref="MinZoomToFit(double,double,double,double)"/> over the standard tile size
        /// (<see cref="WebMercator.TilePixelSize"/>). The convenience the <c>Controller</c> calls so the tile
        /// constant — projection math — stays in Core: no <c>WebMercator.*</c> in the Unity input adapter.
        /// </summary>
        public static double MinZoomToFit(double viewportWidthLogical, double viewportHeightLogical, double margin)
            => MinZoomToFit(viewportWidthLogical, viewportHeightLogical, TilePixelSize, margin);

        /// <summary>
        /// The most-zoomed-out fractional zoom at which the finite Web-Mercator world square exactly FILLS the
        /// viewport, with no off-world margin on either axis — the floor for the finite planar sheet. Same as
        /// <see cref="MinZoomToFit(double,double,double,double)"/> but fits the LARGER viewport side
        /// (<c>math.max</c>): on a landscape viewport the width fills and the shorter height crops instead of
        /// showing an empty margin. Default <paramref name="margin"/> is 0; a positive value reopens that gap.
        /// </summary>
        /// <param name="viewportWidthLogical">Viewport width in logical (DPR-normalized) pixels.</param>
        /// <param name="viewportHeightLogical">Viewport height in logical (DPR-normalized) pixels.</param>
        /// <param name="tilePixelSize">Tile edge in px (the zoom→scale convention; <see cref="WebMercator.TilePixelSize"/>).</param>
        /// <param name="margin">Breathing-room margin in zoom levels. Default 0 (exact fill).</param>
        /// <returns>The minimum-zoom floor (fractional) at which the world fills the viewport.</returns>
        public static double MinZoomToFill(double viewportWidthLogical, double viewportHeightLogical,
                                           double tilePixelSize, double margin = 0.0)
        {
            double maxSide = math.max(viewportWidthLogical, viewportHeightLogical);
            double zoomFit = math.log2(maxSide / tilePixelSize);
            return zoomFit - margin;
        }

        /// <summary>
        /// <see cref="MinZoomToFill(double,double,double,double)"/> over the standard tile size
        /// (<see cref="WebMercator.TilePixelSize"/>) — mirrors the <see cref="MinZoomToFit(double,double,double)"/>
        /// convenience overload so callers stay free of <c>WebMercator.*</c>.
        /// </summary>
        public static double MinZoomToFill(double viewportWidthLogical, double viewportHeightLogical, double margin)
            => MinZoomToFill(viewportWidthLogical, viewportHeightLogical, TilePixelSize, margin);

        /// <summary>
        /// The projection-keyed min-zoom floor selector: branches once on
        /// <see cref="IProjection.IsFinitePlanarWorld"/> so neither <c>Controller</c> nor <c>TouchController</c>
        /// duplicates the finite/cyclic decision. Finite (Mercator) → <see cref="MinZoomToFill(double,double,double)"/>
        /// (fills the viewport, no margin — a positive margin would reopen the off-world gap). Cyclic (globe) →
        /// <see cref="MinZoomToFit(double,double,double)"/> (fits with breathing-room margin).
        /// </summary>
        /// <param name="projection">The active projection — determines finite-fill vs cyclic-fit.</param>
        /// <param name="viewportWidthLogical">Viewport width in logical (DPR-normalized) pixels.</param>
        /// <param name="viewportHeightLogical">Viewport height in logical (DPR-normalized) pixels.</param>
        /// <param name="margin">Breathing-room margin in zoom levels — applies to the cyclic branch only.</param>
        /// <returns>The minimum-zoom floor (fractional).</returns>
        public static double MinZoomFloor(IProjection projection, double viewportWidthLogical,
                                          double viewportHeightLogical, double margin)
            => projection.IsFinitePlanarWorld
                ? MinZoomToFill(viewportWidthLogical, viewportHeightLogical, 0.0)
                : MinZoomToFit(viewportWidthLogical, viewportHeightLogical, margin);

        /// <summary>Near clip plane from altitude: near = altitude · 0.01, min 0.1.</summary>
        public static double NearClip(double altitude) => math.max(0.1, altitude * 0.01);

        /// <summary>Far clip plane from altitude: far = altitude · 4. The plain overhead/globe form;
        /// the planar view uses the geometry-aware overload below.</summary>
        public static double FarClip(double altitude) => altitude * 4.0;

        /// <summary>
        /// Geometry-aware far clip for the PLANAR view: slant distance to the farthest visible ground point (the
        /// viewport-corner ray, from height <c>altitude·cos(tilt)</c>), capped at <c>altitude·capMultiplier</c>
        /// so it never blows up near the horizon (LOD covers the distant band instead).
        /// <c>MapCamera.SyncToCamera</c> (planar) and <c>FrustumTileSelector</c> share this call, so the selected
        /// frustum matches what renders. The globe uses <see cref="FarClip(double)"/> to reach the sphere's limb.
        /// </summary>
        public static double FarClip(double altitude, Angle tilt, double fovDegVertical, double aspect,
                                     double capMultiplier = 4.0)
        {
            double halfV    = Angle.FromDegrees(fovDegVertical * 0.5).Radians;
            double tanV     = math.tan(halfV);
            double tanH     = tanV * aspect;
            double halfDiag = math.atan(math.sqrt(tanV * tanV + tanH * tanH)); // corner half-angle

            double h      = altitude * tilt.Cos;                 // camera height above the ground plane
            double phi    = tilt.Radians + halfDiag;             // top-corner ray angle from vertical
            double phiMax = 89.0 * math.PI_DBL / 180.0;          // cap just short of horizontal (else → ∞)
            if (phi > phiMax) phi = phiMax;

            double geom = h / math.cos(phi) * 1.1;               // slant distance to that ground point (+10%)
            double cap  = altitude * capMultiplier;
            return geom < cap ? geom : cap;
        }

        // ── Pose computation ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the camera pose relative to the floating origin (look-at at scene 0,0,0) from a
        /// <see cref="CameraProperties"/>. Coordinates are right-handed, Y=up, X=east, Z=north; heading is CW
        /// from north; the camera orbits the look-at.
        ///
        /// <para>At heading 0 the camera sits south of and above the look-at and looks north and down, so tilt
        /// swings the view toward the horizon in the bearing direction. The closed-form orbit geometry is a
        /// non-local invariant. With <c>sinT=tilt.Sin</c>, <c>cosT=tilt.Cos</c>, <c>sinH/cosH</c> from heading:
        /// <code>
        ///   pos = (−alt·sinT·sinH,  alt·cosT,  −alt·sinT·cosH)   // camera, opposite the bearing dir
        ///   fwd = −pos/alt = (sinT·sinH, −cosT, sinT·cosH)       // toward look-at; |pos| ≡ alt
        ///   up  = (sinH·cosT,  sinT,  cosH·cosT)                 // world-up preserved (up.y = sinT ≥ 0)
        /// </code>
        /// <c>up</c> is unit-length and ⟂ <c>fwd</c> for all tilt∈[0,90] (sin²H+cos²H=1 cancels the
        /// cross-terms), so no Gram-Schmidt fallback is needed. <c>up.y=sinT≥0</c> stops the image flipping
        /// vertically as tilt approaches 90.</para>
        /// </summary>
        /// <param name="altitude">Camera orbit radius in metres (from <see cref="AltitudeForZoom"/>).</param>
        /// <param name="heading">Camera bearing (constraint already enforced upstream).</param>
        /// <param name="tilt">Camera tilt angle (constraint already enforced upstream).</param>
        /// <param name="pos">Output: camera position offset from look-at (render-space).</param>
        /// <param name="fwd">Output: unit forward vector (toward look-at).</param>
        /// <param name="up">Output: camera up vector (world-up preserved; non-degenerate at tilt=0).</param>
        public static void ComputeRelativePose(double altitude,
            Angle                             heading, Angle       tilt,
            out double3                       pos,     out double3 fwd, out double3 up)
        {
            double sinH = heading.Sin;
            double cosH = heading.Cos;
            double sinT = tilt.Sin;
            double cosT = tilt.Cos;

            // Camera position: orbit offset on the side OPPOSITE the bearing direction (so the camera
            // looks toward the bearing). At tilt=0 this is (0, altitude, 0) — directly overhead.
            double px = -altitude * sinT * sinH;
            double py =  altitude * cosT;
            double pz = -altitude * sinT * cosH;
            pos = new double3(px, py, pz);

            // Forward = toward the look-at = -pos normalized. |pos| ≡ altitude analytically, but divide
            // by the computed length to stay robust to rounding.
            double len           = math.sqrt(px * px + py * py + pz * pz);
            if (len < 1e-10) len = 1e-10;
            fwd = new double3(-px / len, -py / len, -pz / len);

            // Camera up: world-up preserved through the tilt, rotated by heading. Closed form (always
            // unit-length and ⟂ fwd) — see method doc. up.y = sinT ≥ 0 keeps the sky up at every tilt.
            up = new double3(sinH * cosT, sinT, cosH * cosT);
        }

        // ── Heading interpolation ───────────────────────────────────────────────────────────────

    }
}