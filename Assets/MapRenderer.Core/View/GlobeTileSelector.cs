// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection/WebMercator/TileId + CameraPoseMath only.

using System; // ArgumentNullException
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// S91-C Slice 3 — a first-cut globe visible-tile selector (option 1: front-facing cap cover). Emits the
    /// tiles of the spherical CAP the camera sees around the look-at, at the current integer selection zoom.
    ///
    /// <para>Unlike <see cref="ViewportCornerTileSelector"/> it does NOT route through <c>ScreenToGround</c> —
    /// the globe camera-interaction ray-cast is a separate deferred stage
    /// (<see cref="SphericalProjection.ScreenToGround"/> throws). Instead it derives the visible cap ANGULARLY
    /// from the camera zoom + viewport (viewport half-diagonal in ground metres ÷ Earth radius, saturating to a
    /// full near hemisphere at low zoom) and emits the tiles whose geodetic lat/lon bounding box overlaps it.
    /// The bounding box is looped directly (O(visible tiles), not O(n²)).</para>
    ///
    /// <para><b>Scope (first cut, low-to-mid zoom).</b> Over-covers the cap's bounding box — the renderer
    /// back-face-culls the far hemisphere and frustum-drops off-screen tiles, exactly as the planar selector
    /// leans on a corner bbox + pad. High zoom and true pan/zoom INPUT on the globe need the ray-cast cap cover
    /// of the deferred globe-camera stage. The MVT tile grid is the standard Web-Mercator slippy scheme
    /// regardless of render projection, so the poles (|lat| &gt; ~85.05°) are unmapped — the same limit as the
    /// planar path.</para>
    ///
    /// <para>Engine-free and allocation-free in steady state: clears + refills the caller's buffer; preserves
    /// the antimeridian x-wrap and pole y-clamp.</para>
    /// </summary>
    public sealed class GlobeTileSelector : IVisibleTileSelector
    {
        private readonly int _padTiles;
        private readonly int _minZoom;
        private readonly int _maxZoom;
        private readonly int _selectionZoomOffset;

        /// <param name="padTiles">Safety ring, in tiles, added on every side of the cap's tile bbox. Default 1.</param>
        /// <param name="minZoom">Lower clamp for the selection zoom.</param>
        /// <param name="maxZoom">Upper clamp for the selection zoom.</param>
        /// <param name="onScreenTilePx">Logical-pixel on-screen tile size (512 convention) — enters only as a
        ///   selection-zoom offset <c>log2(TilePixelSize / onScreenTilePx)</c>, matching
        ///   <see cref="ViewportCornerTileSelector"/>.</param>
        public GlobeTileSelector(int padTiles = 1, int minZoom = 0, int maxZoom = 22, int onScreenTilePx = 512)
        {
            _padTiles = padTiles < 0 ? 0 : padTiles;
            _minZoom  = minZoom;
            _maxZoom  = maxZoom;
            double tilePx     = WebMercator.TilePixelSize;
            double onScreenPx = onScreenTilePx > 0 ? onScreenTilePx : tilePx;
            _selectionZoomOffset = (int)math.round(math.log2(tilePx / onScreenPx));
        }

        /// <inheritdoc/>
        public void SelectVisibleTiles(in ViewContext view, List<TileId> reuseBuffer)
        {
            if (reuseBuffer == null) throw new ArgumentNullException(nameof(reuseBuffer));
            reuseBuffer.Clear();

            CameraProperties camera = view.Camera;
            double2          vp     = view.ViewportPx;

            int z = camera.IntegerZoom + _selectionZoomOffset;
            if (z < _minZoom) z = _minZoom;
            if (z > _maxZoom) z = _maxZoom;
            long n = 1L << z;
            if (n <= 0) n = 1;

            // Visible cap half-angle on the sphere = viewport half-diagonal in ground metres ÷ Earth radius.
            // Saturates to a full near hemisphere (π/2) at low zoom (mpp huge); shrinks to the on-screen span
            // at high zoom. mpp uses the canonical Mercator ground resolution as a local metres/pixel scale.
            double mpp        = CameraPoseMath.MetersPerPixel(camera.Zoom);
            double halfDiagPx = 0.5 * math.sqrt(vp.x * vp.x + vp.y * vp.y);
            double capRad     = halfDiagPx * mpp / EarthConstants.A;
            if (capRad > math.PI_DBL * 0.5) capRad = math.PI_DBL * 0.5; // never more than a hemisphere
            double capDeg = capRad * 180.0 / math.PI_DBL;

            double lat0 = camera.LookAt.Latitude;
            double lon0 = camera.LookAt.Longitude;

            double latMax = lat0 + capDeg;
            double latMin = lat0 - capDeg;

            // The cap's longitude extent widens toward the poles (≈ capDeg / cos φ). If it reaches a pole or
            // spans the globe, emit every column.
            double cosLat = math.cos(lat0 * math.PI_DBL / 180.0);
            bool allLon = latMax >= 90.0 || latMin <= -90.0 || capDeg >= 90.0
                          || cosLat <= 1e-6 || capDeg / cosLat >= 180.0;
            double lonHalfSpan = allLon ? 180.0 : capDeg / cosLat;

            // Fractional-tile bounds (Web-Mercator slippy; latitude clamped to the tile grid's ±MaxLatitude).
            int yMin = (int)math.floor(LatToTileY(latMax, n)) - _padTiles; // north edge → smaller y
            int yMax = (int)math.floor(LatToTileY(latMin, n)) + _padTiles;
            if (yMin < 0)     yMin = 0;
            if (yMax > n - 1) yMax = (int)(n - 1);

            int xMin, xCount;
            if (allLon)
            {
                xMin   = 0;
                xCount = (int)n;
            }
            else
            {
                int xW = (int)math.floor(LonToTileX(lon0 - lonHalfSpan, n)) - _padTiles;
                int xE = (int)math.floor(LonToTileX(lon0 + lonHalfSpan, n)) + _padTiles;
                xMin   = xW;
                xCount = xE - xW + 1;
                if (xCount > n) xCount = (int)n;
                if (xCount < 0) xCount = 0;
            }

            for (int y = yMin; y <= yMax; y++)
                for (int xi = 0; xi < xCount; xi++)
                    reuseBuffer.Add(new TileId { Z = z, X = WrapX(xMin + xi, n), Y = y });
        }

        /// <summary>Longitude (deg, may fall outside [-180,180]) → fractional tile x in [0,n], west→east.</summary>
        private static double LonToTileX(double lonDeg, long n) => (lonDeg + 180.0) / 360.0 * n;

        /// <summary>Latitude (deg) → fractional tile y in [0,n], north→south (Web-Mercator slippy). Latitude is
        /// clamped to the tile grid's ±MaxLatitude so tan/asinh stay finite (poles unmapped, as in the planar
        /// path). <c>asinh(tan φ)</c> is the exact inverse of <see cref="TileId.ToLonLat"/>'s <c>sinh</c>.</summary>
        private static double LatToTileY(double latDeg, long n)
        {
            double lat    = math.clamp(latDeg, -WebMercator.MaxLatitude, WebMercator.MaxLatitude);
            double latRad = lat * math.PI_DBL / 180.0;
            double t      = math.tan(latRad);
            double asinh  = math.log(t + math.sqrt(t * t + 1.0)); // asinh(tan φ)
            double v      = (1.0 - asinh / math.PI_DBL) * 0.5;
            return v * n;
        }

        /// <summary>Wraps a tile x-index into <c>[0, n)</c> modulo <paramref name="n"/> (antimeridian wrap).</summary>
        private static int WrapX(int x, long n)
        {
            long m = x % n;
            if (m < 0) m += n;
            return (int)m;
        }
    }
}
