// Non-obvious why: AnchorLocal for TileKey=0 is ~2e7 m from a mid-latitude anchor, where float32 ULP is ~2 m
// and glyphs jitter or vanish, so a point/icon render test anchors into the real tile this helper computes.

using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class TestTileKeys
    {
        /// <summary>Standard slippy-map lon/lat → tile x/y at <paramref name="zoom"/> (Web Mercator).</summary>
        public static TileId Containing(in GeoCoordinate geo, int zoom)
        {
            double n = math.pow(2.0, zoom);
            double latRad = geo.Latitude * math.PI_DBL / 180.0;
            double x = (geo.Longitude + 180.0) / 360.0 * n;
            double y = (1.0 - math.log(math.tan(latRad) + 1.0 / math.cos(latRad)) / math.PI_DBL) / 2.0 * n;
            return new TileId { X = (int)math.floor(x), Y = (int)math.floor(y), Z = zoom };
        }

        /// <summary>The packed <see cref="SymbolTileKey.Pack"/> of <see cref="Containing"/> —
        /// the realistic <c>TileKey</c> a point/icon test symbol should carry.</summary>
        public static long PackedContaining(in GeoCoordinate geo, int zoom)
            => SymbolTileKey.Pack(Containing(geo, zoom));
    }
}
