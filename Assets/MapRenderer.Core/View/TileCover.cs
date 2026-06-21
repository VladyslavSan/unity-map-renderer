using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Selects the set of tiles (<see cref="TileId"/>) that cover a <see cref="CameraProperties"/> at its
    /// integer zoom. Allocation-free in steady state: the caller supplies a reused result buffer that
    /// <see cref="Cover"/> clears and refills (no LINQ, no per-call allocation).
    ///
    /// <para><b>S06 scope — a generous rectangle, not a precise frustum.</b> Frustum/tilt-aware tile
    /// culling is deferred. We over-select a square-ish rectangle of tiles around the center, padded for
    /// viewport aspect and a pad factor that absorbs pitch over-select. Over-selecting is safe (a few
    /// extra tiles load) and conservative (we never under-cover the visible area).</para>
    ///
    /// <para><b>Edge handling:</b> x wraps modulo <c>2^z</c> (antimeridian); y clamps to
    /// <c>[0, 2^z − 1]</c> (poles). Duplicate ids are not emitted when the x-span reaches the full world
    /// width.</para>
    /// </summary>
    public static class TileCover
    {
        /// <summary>
        /// Fills <paramref name="reuseBuffer"/> with the tiles covering <paramref name="view"/>.
        /// </summary>
        /// <param name="view">The camera to cover. Its <see cref="CameraProperties.IntegerZoom"/> is clamped
        ///   to <paramref name="minZoom"/>..<paramref name="maxZoom"/>; its center comes from
        ///   <see cref="CameraProperties.CenterMercator"/>.</param>
        /// <param name="viewportAspect">Viewport width / height. &gt; 1 widens the x-span.</param>
        /// <param name="padFactor">Multiplier on the base half-span (≥ 1). Absorbs viewport size and pitch
        ///   over-select. 1 selects roughly the center tile and its immediate neighbours.</param>
        /// <param name="minZoom">Lower clamp for the selection zoom.</param>
        /// <param name="maxZoom">Upper clamp for the selection zoom.</param>
        /// <param name="reuseBuffer">Caller-owned result buffer. Cleared then refilled (no allocation in
        ///   steady state once its capacity is warm).</param>
        public static void Cover(
            CameraProperties view,
            double viewportAspect,
            double padFactor,
            int minZoom,
            int maxZoom,
            List<TileId> reuseBuffer)
        {
            if (reuseBuffer == null) throw new ArgumentNullException(nameof(reuseBuffer));
            reuseBuffer.Clear();

            int z = view.IntegerZoom;
            if (z < minZoom) z = minZoom;
            if (z > maxZoom) z = maxZoom;

            long n = 1L << z;                    // tiles per axis at zoom z
            if (n <= 0) n = 1;

            // Center → fractional tile coordinates (origin top-left, y increases southward). Derived from
            // the Web-Mercator center (CenterMercator already clamps latitude to the projection limit).
            double2 merc = view.CenterMercator();
            double worldExtent = WebMercator.WorldExtent;
            double cxTile = (merc.x + worldExtent) / (2.0 * worldExtent) * n;   // [0,n] west→east
            double cyTile = (worldExtent - merc.y) / (2.0 * worldExtent) * n;   // [0,n] north→south

            // Half-span in tiles. Base span = 1 tile each way; padFactor and aspect widen it.
            double pad = padFactor < 1.0 ? 1.0 : padFactor;
            double halfSpanY = pad;
            double halfSpanX = pad * (viewportAspect > 1.0 ? viewportAspect : 1.0);

            int xMin = (int)Math.Floor(cxTile - halfSpanX);
            int xMax = (int)Math.Floor(cxTile + halfSpanX);
            int yMin = (int)Math.Floor(cyTile - halfSpanY);
            int yMax = (int)Math.Floor(cyTile + halfSpanY);

            // Clamp y to the valid tile range (no wrap at the poles).
            if (yMin < 0) yMin = 0;
            if (yMax > n - 1) yMax = (int)(n - 1);

            // Bound the x-span to one full world width so we never emit duplicate columns.
            int xCount = xMax - xMin + 1;
            if (xCount > n) xCount = (int)n;

            for (int dy = yMin; dy <= yMax; dy++)
            {
                for (int xi = 0; xi < xCount; xi++)
                {
                    int rawX = xMin + xi;
                    int wrappedX = WrapX(rawX, n);
                    reuseBuffer.Add(new TileId(z, wrappedX, dy));
                }
            }
        }

        /// <summary>
        /// Wraps a tile x-index into <c>[0, n)</c> modulo <paramref name="n"/> (antimeridian wrap).
        /// Correct for negative indices.
        /// </summary>
        private static int WrapX(int x, long n)
        {
            long m = x % n;
            if (m < 0) m += n;
            return (int)m;
        }
    }
}
