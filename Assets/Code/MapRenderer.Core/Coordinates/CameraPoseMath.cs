using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// S45: Engine-free pose math.
    ///
    /// Absorbed from <c>MapController.AltitudeForZoom</c> and <c>MapController.ApplyCameraTransform</c>
    /// (S42). Now lives in Core so headless tests can exercise the full pose computation.
    ///
    /// <para><b>Altitude formula (D1 — S42 D2):</b>
    ///   Web-Mercator ground resolution: metersPerPixel = EarthConstants.EquatorialCircumferenceMetres / (TilePixelSize × 2^zoom).
    ///   For a perspective camera looking straight down:
    ///   altitude = (viewportHeightPx × metersPerPixel) / (2 × tan(verticalFovDeg/2)).
    ///   Earth equatorial circumference: see EarthConstants.EquatorialCircumferenceMetres.</para>
    ///
    /// <para><b>Pose formula (D6):</b>
    ///   Orbit on a sphere of radius=altitude around the look-at position.
    ///   Rot(heading, Y) × Rot(tilt, X) × (0, altitude, 0) gives the camera offset from the look-at.
    ///   At tilt=0 the inner rotation is identity → offset=(0,altitude,0) → camera directly above.
    ///   The up-vector is derived from the heading direction in the horizontal plane so LookRotation
    ///   is never fed collinear vectors even at tilt=0 (D6 degeneracy fix).</para>
    ///
    /// <para>Clean-room: standard web-map perspective framing + orbit-camera math.</para>
    /// </summary>
    public static class CameraPoseMath
    {
        // ── Constants — reference EarthConstants and WebMercator (single sources of truth, S62) ─────
        // Note: EarthCircumferenceMetres is kept as a literal in EarthConstants (NOT derived as 2π·A).
        private const double EarthCircumferenceMetres = EarthConstants.EquatorialCircumferenceMetres;
        private const double TilePixelSize            = WebMercator.TilePixelSize;

        // ── Altitude ↔ Zoom ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Web-Mercator ground resolution: metres per pixel at a given fractional zoom level.
        ///
        /// <para>Formula (S42 D2 / S43 D2): metersPerPixel = EarthCircumference / (TilePixelSize × 2^zoom).
        /// This is the single source of truth for the px→m conversion used everywhere (altitude
        /// calculation, line-width scaling, zoom-stability tests).</para>
        /// </summary>
        /// <param name="zoom">Fractional zoom level.</param>
        /// <returns>Ground resolution in metres per pixel.</returns>
        public static double MetersPerPixel(double zoom)
            => EarthCircumferenceMetres / (TilePixelSize * math.pow(2.0, zoom));

        /// <summary>
        /// Computes camera altitude in render-space metres from a fractional zoom level (S42 D2).
        ///
        /// <para>Exact same formula as the retired <c>MapController.AltitudeForZoom</c>; pinned by
        /// the ported <c>CameraTransformTests</c>.</para>
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

        // ── Clip planes (derived from altitude — S42 D3) ──────────────────────────────────────────

        // ── Fit-to-viewport minimum zoom (S92 D2) ─────────────────────────────────────────────────

        /// <summary>
        /// The most-zoomed-out fractional zoom at which the whole Web-Mercator world still frames inside the
        /// viewport with a little breathing room (S92 D2) — the device-derived floor for the zoom clamp,
        /// replacing the hardcoded <c>MinZoom = 0</c> that let the map shrink to a useless world-square grape.
        ///
        /// <para>The world square is <c>tilePixelSize · 2^zoom</c> <b>logical</b> px per side, so it exactly
        /// fits the shorter viewport side when <c>zoom_fit = log2(min(vpW, vpH) / tilePixelSize)</c>. The floor
        /// is <c>zoom_fit − margin</c>: at the floor the world is <c>2^margin</c>× smaller than the viewport
        /// (whole map visible, with margin) — never larger (grape), never cropped.</para>
        ///
        /// <para><b>Logical px in.</b> Feed the DPI-normalized viewport (<c>physicalPx / DevicePixelRatio</c>),
        /// the same basis the DPI-normalized camera (D1) and the tile selector frame in — so the floor scales
        /// with the viewport and with DPR. Cyclic (globe) worlds use this fit; the finite Mercator sheet uses
        /// <see cref="MinZoomToFill(double,double,double,double)"/> instead — see <see cref="MinZoomFloor"/>.</para>
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
        /// constant — projection math — stays in Core (the S73 adapter rule: no <c>WebMercator.*</c> in the
        /// Unity input adapter).
        /// </summary>
        public static double MinZoomToFit(double viewportWidthLogical, double viewportHeightLogical, double margin)
            => MinZoomToFit(viewportWidthLogical, viewportHeightLogical, TilePixelSize, margin);

        /// <summary>
        /// The most-zoomed-out fractional zoom at which the finite Web-Mercator world square exactly FILLS the
        /// viewport (no off-world margin on either axis) — the floor for the finite planar sheet. Identical to
        /// <see cref="MinZoomToFit(double,double,double,double)"/> but fits the world square to the LARGER
        /// viewport side (<c>math.max</c>, not <c>math.min</c>): on a landscape viewport the world then fills
        /// the width, and the (shorter) height crops rather than showing an empty margin. Default
        /// <paramref name="margin"/> is 0 — a positive margin would reintroduce the off-world gap this exists
        /// to remove.
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
        /// The projection-keyed min-zoom floor selector (D1): branches once on
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

        /// <summary>Near clip plane from altitude (S42 D3: near = altitude · 0.01, min 0.1).</summary>
        public static double NearClip(double altitude) => math.max(0.1, altitude * 0.01);

        /// <summary>Far clip plane from altitude (S42 D3: far = altitude · 4). The plain overhead/globe form;
        /// the planar view uses the geometry-aware overload below.</summary>
        public static double FarClip(double altitude) => altitude * 4.0;

        /// <summary>
        /// Geometry-aware far clip for the PLANAR (flat-ground) view: the slant distance to the farthest ground
        /// point actually in view — the ray through a viewport <b>corner</b> (widest reach), from the camera at
        /// height <c>altitude·cos(tilt)</c>. Depends on ALL camera parameters — altitude, tilt/pitch, vertical
        /// FOV, and aspect (via the corner half-angle) — so it's tight when overhead (~×1.6, no wasted far
        /// tiles) and grows as the camera pitches toward the horizon.
        ///
        /// <para><b>Bounded at ×4.</b> Near the horizon the corner ray approaches horizontal and the exact far
        /// runs to ∞; two guards keep it sane: the ray angle is capped just short of 90°, and the whole result
        /// is clamped to <c>altitude·4</c>. So the far never exceeds the overhead budget (bounding the tile
        /// count) — the sky band above that distance is accepted rather than paid for with an exploding cover
        /// (a distant horizon at a single zoom is what LOD is for, not a bigger far plane).</para>
        ///
        /// <para><b>Render ↔ selection share this.</b> <c>MapCamera.SyncToCamera</c> (planar branch) and
        /// <c>FrustumTileSelector</c> both call it with the live camera, so the frustum the selector covers is
        /// exactly the one that renders. The globe keeps <see cref="FarClip(double)"/> (it must reach the
        /// sphere's limb, a different geometry).</para>
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
        /// Computes the camera pose relative to the floating origin (the look-at at scene 0,0,0) from a
        /// <see cref="CameraProperties"/>.
        ///
        /// <para><b>Coordinate convention:</b> right-handed, Y=up, X=east, Z=north; heading is CW from
        /// north. The camera orbits the look-at (origin under camera-relative rendering).</para>
        ///
        /// <para><b>Orbit geometry (closed form).</b> The pose is <c>Ry(heading)</c> applied to the
        /// heading-0 pose. At heading 0 the camera sits <b>south of</b> and above the look-at and looks
        /// <b>north</b> and down, so increasing tilt swings the view from straight-down toward the
        /// horizon in the bearing direction (at bearing 0, north recedes to the top of the screen — the
        /// MapLibre convention). With <c>sinT=tilt.Sin</c>, <c>cosT=tilt.Cos</c>, <c>sinH/cosH</c>:
        /// <code>
        ///   pos = (−alt·sinT·sinH,  alt·cosT,  −alt·sinT·cosH)   // camera, opposite the bearing dir
        ///   fwd = −pos/alt = (sinT·sinH, −cosT, sinT·cosH)       // toward look-at; |pos| ≡ alt
        ///   up  = (sinH·cosT,  sinT,  cosH·cosT)                 // world-up preserved (up.y = sinT ≥ 0)
        /// </code>
        /// </para>
        ///
        /// <para>This <c>up</c> is provably unit-length and orthogonal to <c>fwd</c> for <b>all</b>
        /// tilt∈[0,90] (the heading cross-terms cancel via sin²H+cos²H=1), so no Gram-Schmidt /
        /// degeneracy fallback is needed: at tilt=0 it is the non-degenerate heading-derived
        /// north-up <c>(sinH,0,cosH)</c>; at tilt=90 it is world-up <c>(0,1,0)</c>. Keeping
        /// <c>up.y=sinT≥0</c> is what prevents the image from flipping vertically as tilt→90.</para>
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

        // ── Heading interpolation (D4) ──────────────────────────────────────────────────────────

    }
}