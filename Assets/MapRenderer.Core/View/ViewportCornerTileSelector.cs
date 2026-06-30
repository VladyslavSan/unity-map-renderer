// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection + WebMercator/TileId only. No System.Math.

using System; // ArgumentNullException
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// The S71 default <see cref="IVisibleTileSelector"/> (Era-1, single integer zoom): unprojects the four
    /// viewport corners to ground via the per-frame <see cref="ViewContext.Projection"/>, takes their integer
    /// tile bounding box, and emits every tile in it (plus a fixed safety pad). This <b>covers the actual
    /// viewport span</b> — the on-screen ground extent (<c>viewportPx · metresPerPixel(zoom)</c>) — instead of
    /// the old resolution-independent magic rectangle, which under-covered at high resolution / wide aspect
    /// and produced the white edge borders.
    ///
    /// <para><b>Why corner-unprojection is exact, not a heuristic.</b> <see cref="WebMercatorProjection"/>
    /// computes the pixel→ground offset with the same <c>WebMercator.GroundResolution(zoom)</c> that
    /// <c>CameraPoseMath.AltitudeForZoom</c> uses to frame the perspective camera. So at tilt 0 the four
    /// unprojected screen corners bound <i>exactly</i> the rendered ground extent — this is the inverse of the
    /// framing math the renderer already trusts. The consumer must hand us the <b>framing</b> viewport
    /// <c>(refH · liveAspect, refH)</c>, not the raw live window (see <see cref="IVisibleTileSelector"/>).</para>
    ///
    /// <para><b>Tilt:</b> no tilt branch here. <see cref="WebMercatorProjection.ScreenToGround"/> is
    /// overhead-exact today; the day it gains a tilted ray-cast this selector is automatically tilt-correct.
    /// Under steep tilt today the +<see cref="_padTiles"/> safety ring absorbs the small error.</para>
    ///
    /// <para><b>Algorithm config (constructor only, never on the seam):</b> <see cref="_padTiles"/>,
    /// <see cref="_minZoom"/>, <see cref="_maxZoom"/>. The projection arrives per-call via
    /// <see cref="ViewContext.Projection"/>, so a projection switch needs no selector rebuild.</para>
    ///
    /// <para>Engine-free and allocation-free in steady state: clears + refills the caller's buffer; preserves
    /// the antimeridian x-wrap, pole y-clamp, and duplicate-column guard formerly in <c>TileCover</c>.</para>
    /// </summary>
    public sealed class ViewportCornerTileSelector : IVisibleTileSelector
    {
        private readonly int _padTiles;
        private readonly int _minZoom;
        private readonly int _maxZoom;

        /// <param name="padTiles">Fixed safety margin, in tiles, added on every side of the viewport-derived
        ///   bounding box (covers fractional-edge / rounding slop and mild tilt). NOT a coverage multiplier —
        ///   coverage comes from the unprojected span. Default 1; clamped to ≥ 0.</param>
        /// <param name="minZoom">Lower clamp for the (internal) selection zoom.</param>
        /// <param name="maxZoom">Upper clamp for the (internal) selection zoom.</param>
        public ViewportCornerTileSelector(int padTiles = 1, int minZoom = 0, int maxZoom = 22)
        {
            _padTiles = padTiles < 0 ? 0 : padTiles;
            _minZoom  = minZoom;
            _maxZoom  = maxZoom;
        }

        /// <inheritdoc/>
        public void SelectVisibleTiles(in ViewContext view, List<TileId> reuseBuffer)
        {
            if (reuseBuffer == null) throw new ArgumentNullException(nameof(reuseBuffer));
            reuseBuffer.Clear();

            IProjection      proj   = view.Projection;
            CameraProperties camera = view.Camera;
            double2          vp     = view.ViewportPx;

            // Selection zoom: single integer level (impl detail — never surfaced on the seam). Fractional
            // zoom changes the COUNT of covered tiles (via the unprojected span), not which z they live at.
            int z = camera.IntegerZoom;
            if (z < _minZoom) z = _minZoom;
            if (z > _maxZoom) z = _maxZoom;

            long n = 1L << z; // tiles per axis at zoom z
            if (n <= 0) n = 1;

            double worldExtent = WebMercator.WorldExtent;

            // Unproject the four screen corners (origin bottom-left, +x right, +y up) to fractional tile
            // coordinates; the camera bearing is already baked into ScreenToGround's pixel→ground rotation,
            // so the bbox of the four corners contains the (possibly rotated) viewport quad.
            double2 t0 = CornerTile(proj, in camera, vp, new double2(0.0,  0.0),  n, worldExtent);
            double2 t1 = CornerTile(proj, in camera, vp, new double2(vp.x, 0.0),  n, worldExtent);
            double2 t2 = CornerTile(proj, in camera, vp, new double2(0.0,  vp.y), n, worldExtent);
            double2 t3 = CornerTile(proj, in camera, vp, new double2(vp.x, vp.y), n, worldExtent);

            double xMinF = math.min(math.min(t0.x, t1.x), math.min(t2.x, t3.x));
            double xMaxF = math.max(math.max(t0.x, t1.x), math.max(t2.x, t3.x));
            double yMinF = math.min(math.min(t0.y, t1.y), math.min(t2.y, t3.y));
            double yMaxF = math.max(math.max(t0.y, t1.y), math.max(t2.y, t3.y));

            int xMin = (int)math.floor(xMinF) - _padTiles;
            int xMax = (int)math.floor(xMaxF) + _padTiles;
            int yMin = (int)math.floor(yMinF) - _padTiles;
            int yMax = (int)math.floor(yMaxF) + _padTiles;

            // Clamp y to the valid tile range (no wrap at the poles).
            if (yMin < 0) yMin     = 0;
            if (yMax > n - 1) yMax = (int)(n - 1);

            // Bound the x-span to one full world width so we never emit duplicate columns (antimeridian).
            int xCount = xMax - xMin + 1;
            if (xCount > n) xCount = (int)n;
            if (xCount < 0) xCount = 0;

            for (int dy = yMin; dy <= yMax; dy++)
            {
                for (int xi = 0; xi < xCount; xi++)
                {
                    int rawX     = xMin + xi;
                    int wrappedX = WrapX(rawX, n);
                    reuseBuffer.Add(new TileId { Z = z, X = wrappedX, Y = dy });
                }
            }
        }

        /// <summary>
        /// Unprojects one screen corner to its (unwrapped, unclamped) fractional tile coordinate at zoom
        /// level implied by <paramref name="n"/>. Latitude is clamped to the projection's valid range first
        /// so a near-pole corner cannot produce an infinite Mercator y / out-of-range floor.
        /// </summary>
        private static double2 CornerTile(IProjection proj, in CameraProperties camera, double2 vp,
                                          double2 screenPx, long n, double worldExtent)
        {
            GeoCoordinate3D g   = proj.ScreenToGround(screenPx, vp, in camera);
            double          lat = proj.ClampValidLatitude(g.Latitude);
            double2         merc = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = g.Longitude, Latitude = lat });

            // Mercator → fractional tile (origin top-left, y increases southward). Same mapping as the
            // legacy TileCover center→tile block; the single Mercator source stays WebMercator.
            double xt = (merc.x      + worldExtent) / (2.0 * worldExtent) * n; // [0,n] west→east
            double yt = (worldExtent - merc.y)      / (2.0 * worldExtent) * n; // [0,n] north→south
            return new double2(xt, yt);
        }

        /// <summary>
        /// Wraps a tile x-index into <c>[0, n)</c> modulo <paramref name="n"/> (antimeridian wrap).
        /// Correct for negative indices.
        /// </summary>
        private static int WrapX(int x, long n)
        {
            long m       = x % n;
            if (m < 0) m += n;
            return (int)m;
        }
    }
}
