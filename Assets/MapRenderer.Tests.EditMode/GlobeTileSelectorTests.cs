// Engine-free (NUnit + Core only) → runs in BOTH the Unity EditMode runner and the fast core-tests project.
// Locks the first-cut globe visible-tile cover (S91-C Slice 3): near-hemisphere cap around the look-at,
// bounded loop, antimeridian wrap, pole → all columns.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    public class GlobeTileSelectorTests
    {
        private const double RefH = 512.0;

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static ViewContext View(in CameraProperties cam)
            => new ViewContext { Camera = cam, ViewportPx = new double2(RefH, RefH), Projection = new SphericalProjection() };

        // onScreenTilePx 512 == TilePixelSize (S93) ⇒ selection-zoom offset 0 ⇒ emitted z == IntegerZoom
        // (the PRODUCTION default now, and predictable in tests). Was 256 under the old 256 convention.
        private static GlobeTileSelector Sel(int pad = 1)
            => new GlobeTileSelector(padTiles: pad, minZoom: 0, maxZoom: 22, onScreenTilePx: 512);

        [Test]
        public void Z0_EmitsExactlyTheSingleWorldTile()
        {
            var buf = new List<TileId>();
            Sel(pad: 0).SelectVisibleTiles(View(Cam(0, 0, 0.0)), buf);
            Assert.AreEqual(1, buf.Count, "z=0 is one tile covering the whole world");
            Assert.AreEqual(new TileId { Z = 0, X = 0, Y = 0 }, buf[0]);
        }

        [Test]
        public void EmitsTilesAtSelectionZoom_ContainingTheLookAt()
        {
            var buf = new List<TileId>();
            Sel().SelectVisibleTiles(View(Cam(0, 0, 4.0)), buf);

            Assert.IsNotEmpty(buf);
            int z = buf[0].Z;
            foreach (var t in buf) Assert.AreEqual(z, t.Z, "all tiles at one selection zoom");

            // Look-at (lon=0,lat=0) sits at tile (n/2, n/2) at zoom z — the cover must contain it.
            int n = 1 << z;
            var center = new TileId { Z = z, X = n / 2, Y = n / 2 };
            CollectionAssert.Contains(buf, center, "cover must include the look-at's own tile");
        }

        [Test]
        public void ZoomedIn_CoverIsBounded_NotWholeWorld()
        {
            var buf = new List<TileId>();
            Sel().SelectVisibleTiles(View(Cam(10, 40, 8.0)), buf); // n=256 ⇒ n²=65536

            Assert.IsNotEmpty(buf);
            int n = 1 << buf[0].Z;
            Assert.Less(buf.Count, n * n / 4,
                "a zoomed-in globe view must cover a small cap, not the whole hemisphere");
        }

        [Test]
        public void NearPole_CoversAllColumns()
        {
            var buf = new List<TileId>();
            // S93 relabel: the 512 convention numbers every zoom −1, so the old z6 view is now z5 (the cap size
            // is a physical quantity — GroundResolution_512(5) == GroundResolution_256(6) — so z5 now reaches
            // the pole exactly as z6 did before). n derives from the emitted Z, so the assert self-adjusts.
            Sel().SelectVisibleTiles(View(Cam(0, 84.0, 5.0)), buf); // cap reaches the (unmapped) pole

            Assert.IsNotEmpty(buf);
            int n = 1 << buf[0].Z;
            var columns = new HashSet<int>();
            foreach (var t in buf) columns.Add(t.X);
            Assert.AreEqual(n, columns.Count, "near a pole the cap spans every longitude column");
        }

        [Test]
        public void Antimeridian_WrapsColumns_NoDuplicates()
        {
            var buf = new List<TileId>();
            Sel().SelectVisibleTiles(View(Cam(180.0, 0.0, 5.0)), buf); // look-at on the antimeridian

            Assert.IsNotEmpty(buf);
            int n = 1 << buf[0].Z;
            var seen = new HashSet<TileId>();
            foreach (var t in buf)
            {
                Assert.GreaterOrEqual(t.X, 0, "wrapped X in range");
                Assert.Less(t.X, n, "wrapped X in range");
                Assert.IsTrue(seen.Add(t), $"no duplicate tile {t}");
            }
        }

        [Test]
        public void LowZoom_CoversTheNearHemisphere()
        {
            var buf = new List<TileId>();
            Sel(pad: 0).SelectVisibleTiles(View(Cam(0, 0, 1.0)), buf); // z=1, n=2 → 4 tiles, all visible
            Assert.AreEqual(4, buf.Count, "at z=1 the whole world (4 tiles) is within the saturated cap");
        }
    }
}
