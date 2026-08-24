// Unity EditMode — pure managed math (Unity.Mathematics only), no engine dependency, but lives alongside its
// callers rather than in core-tests.csproj (test-only, not exercised by the fast loop).
//
// Epic A / A1 (Risk R1 — world-anchored-symbols-design.md §11 A1 D2): AnchorLocal for TileKey=0 (tile 0/0/0)
// is ~2e7 m from a mid-latitude anchor — float32-unsafe (ULP ~2m — glyphs jitter/vanish on-screen). Every
// point/icon render test that previously used the "no real tile" placeholder TileKey=0L must instead use a
// REALISTIC tile containing its anchor. Shared here (mirrors WorldSymbolInkAnalysis's identical "one helper,
// not duplicated per file" reasoning — and the private TileContaining WorldBillboardRtcAlgebraTests already
// has, promoted to shared since 5+ files now need it) rather than re-deriving the slippy-map formula per file.

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
        /// the realistic <c>TileKey</c> a point/icon test symbol should carry (R1).</summary>
        public static long PackedContaining(in GeoCoordinate geo, int zoom)
            => SymbolTileKey.Pack(Containing(geo, zoom));
    }
}
