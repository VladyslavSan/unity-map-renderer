// Unity EditMode — pure managed math (Unity.Mathematics only), no engine dependency, but lives alongside its
// callers rather than in core-tests.csproj (test-only, not exercised by the fast loop).
//
// AnchorLocal for TileKey=0 (tile 0/0/0) is ~2e7 m from a mid-latitude anchor, which is float32-unsafe
// (ULP ~2 m — glyphs jitter or vanish on-screen). A point/icon render test must therefore anchor into a
// REALISTIC tile that contains its anchor, which is what this helper computes.

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
