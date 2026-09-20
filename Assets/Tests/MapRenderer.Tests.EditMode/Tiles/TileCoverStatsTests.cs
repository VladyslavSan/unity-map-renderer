// Engine-free (NUnit + Core only) → runs in BOTH the Unity EditMode runner and the fast core-tests project.
// S85: TileCoverStats.Compute teeth (cover-dims + zoom-span derivation) + the FrustumTileSelector
// aspect/Flat dims tooth (planar, no MapView — the wide-viewport case direct ViewContext construction
// exercises without a live camera rig).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;

namespace MapRenderer.Tests.Tiles
{
    public class TileCoverStatsTests
    {
        private static HashSet<int> NewX() => new HashSet<int>();
        private static HashSet<int> NewY() => new HashSet<int>();

        private static TileId T(int z, int x, int y) => new TileId { Z = z, X = x, Y = y };

        // ── TileCoverStats.Compute ───────────────────────────────────────────────────────────────

        [Test]
        public void Antimeridian_WrappedXValues_ColumnsCountsDistinctX_NotMaxMinusMinPlusOne()
        {
            // THE decisive cover-stats test (globe-relevant): a single-zoom cover wrapping the antimeridian —
            // X values {0, 1, n-2, n-1} at z=5 (n=32). A max(X)-min(X)+1 implementation returns 31 (huge);
            // the correct answer is 4 distinct columns.
            const int z = 5, n = 1 << z;
            var cover = new List<TileId>
            {
                T(z, 0,     10), T(z, 1,     10),
                T(z, n - 2, 10), T(z, n - 1, 10),
            };

            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());

            Assert.AreEqual(4, columns, "antimeridian wrap: 4 distinct X values, not n-2");
            Assert.AreEqual(1, rows, "all four tiles share Y=10");
            Assert.AreEqual(z, minZ);
            Assert.AreEqual(z, maxZ);
        }

        [Test]
        public void MixedZoomCover_SpanCorrect_AndDimsCountOnlyTheFinestLevel()
        {
            const int nearZ = 6, farZ = 4;
            var cover = new List<TileId>
            {
                // A 2x3 near-field block at the finest level (nearZ).
                T(nearZ, 10, 20), T(nearZ, 11, 20), T(nearZ, 12, 20),
                T(nearZ, 10, 21), T(nearZ, 11, 21), T(nearZ, 12, 21),
                // A single coarse far tile at a coarser level — must NOT inflate the near-field grid.
                T(farZ, 0, 0),
            };

            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());

            Assert.AreEqual(farZ, minZ);
            Assert.AreEqual(nearZ, maxZ);
            Assert.AreNotEqual(minZ, maxZ, "mixed-zoom cover: IsMixedZoom would be true");
            Assert.AreEqual(3, columns, "columns count only the nearZ tiles (3 distinct X)");
            Assert.AreEqual(2, rows, "rows count only the nearZ tiles (2 distinct Y)");
        }

        [Test]
        public void ContiguousSingleZoomBlock_ColumnsAndRowsExact()
        {
            const int z = 8;
            var cover = new List<TileId>();
            for (int x = 100; x < 104; x++)      // 4 columns
                for (int y = 50; y < 53; y++)    // 3 rows
                    cover.Add(T(z, x, y));

            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());

            Assert.AreEqual(4, columns);
            Assert.AreEqual(3, rows);
            Assert.AreEqual(z, minZ);
            Assert.AreEqual(z, maxZ);
            Assert.AreEqual(cover.Count, columns * rows, "a contiguous single-zoom block is a filled rectangle");
        }

        [Test]
        public void EmptyCover_ReturnsAllZero()
        {
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(new List<TileId>(), NewX(), NewY());
            Assert.AreEqual(0, columns);
            Assert.AreEqual(0, rows);
            Assert.AreEqual(0, minZ);
            Assert.AreEqual(0, maxZ);
        }

        // ── FrustumTileSelector aspect/Flat dims tooth (planar, direct ViewContext — no MapView) ────

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        [Test]
        public void WideViewport_Planar_Flat_ColumnsExceedRows_AndCoverIsAFilledRectangle()
        {
            // A wide (3:1) viewport over a planar Web-Mercator projection, single-zoom (Flat) LOD, overhead
            // (tilt 0): the frustum footprint is a rectangle wider than it is tall, so CoverColumns must exceed
            // CoverRows, and (planar-Flat only — see docs) the cover is exactly cols*rows, no gaps.
            var view = new ViewContext
            {
                Camera     = Cam(0, 0, 6.0),
                ViewportPx = new double2(1536.0, 512.0), // 3:1
                Projection = new WebMercatorProjection(),
            };
            var selector = new FrustumTileSelector(minZoom: 0, maxZoom: 22, onScreenTilePx: 512,
                                                    lod: new FlatLodStrategy());
            var cover = new List<TileId>();
            selector.SelectVisibleTiles(in view, cover);

            Assert.IsNotEmpty(cover);
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(cover, NewX(), NewY());
            Assert.AreEqual(minZ, maxZ, "Flat LOD ⇒ single-zoom cover");
            Assert.Greater(columns, rows, "a 3:1 wide viewport must select a wider-than-tall grid");
            Assert.AreEqual(cover.Count, columns * rows,
                "planar + Flat + overhead ⇒ the cover is a filled rectangle with no gaps");
        }
    }
}
