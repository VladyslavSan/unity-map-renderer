// Engine-free (NUnit + Core only) → runs in BOTH the Unity EditMode runner and the fast core-tests project.
// Locks the first-cut globe visible-tile cover (S91-C Slice 3): near-hemisphere cap around the look-at,
// bounded loop, antimeridian wrap, pole → all columns.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;

namespace MapRenderer.Tests.Globe
{
    public class GlobeTileSelectorTests
    {
        private const double RefH = 512.0;

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static ViewContext View(in CameraProperties cam)
            => new ViewContext { Camera = cam, ViewportPx = new double2(RefH, RefH), Projection = new SphericalProjection() };

        // The universal FrustumTileSelector on the globe path (SphericalProjection ⇒ horizon occlusion). Flat
        // LOD + ×4 far reproduce the previous single-zoom globe behaviour these tests lock. onScreenTilePx 512 ⇒
        // offset 0 ⇒ emitted z == IntegerZoom.
        private static FrustumTileSelector Sel(int pad = 1)
            => new FrustumTileSelector(minZoom: 0, maxZoom: 22, onScreenTilePx: 512,
                                       lod: new FlatLodStrategy(), farPolicy: new MultiplierFarPlane(4.0));

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
        public void NearPole_CoversAWideLongitudeWedge()
        {
            var buf = new List<TileId>();
            // Looking near a pole, converging meridians make the frustum footprint span MANY longitude columns
            // (a wide wedge) and reach the pole-adjacent tile row. Unlike the old cap, the frustum does NOT cover
            // ALL columns (that was over-cover of the far side) — only the wedge actually in view.
            Sel().SelectVisibleTiles(View(Cam(0, 84.0, 5.0)), buf);

            Assert.IsNotEmpty(buf);
            int n = 1 << buf[0].Z;
            var columns = new HashSet<int>();
            bool reachesPoleRow = false;
            foreach (var t in buf) { columns.Add(t.X); if (t.Y == 0) reachesPoleRow = true; }

            Assert.Greater(columns.Count, n / 4, "near the pole the wedge spans many columns (convergence)");
            Assert.Less(columns.Count, n, "but not ALL columns — the frustum covers only what's in view, not the cap's over-cover");
            Assert.IsTrue(reachesPoleRow, "the cover reaches the pole-adjacent tile row (Y=0)");
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
        public void Tilted_CoverExtendsTowardTheView_NotSymmetricCap()
        {
            // THE globe fix: at 60° tilt looking north, the cover must reach FARTHER north (toward the horizon,
            // lower tile Y) than south (behind the camera). The old cap was tilt-blind → symmetric, so this
            // fails for it. Heading 0 tilt 60 at the equator; north = lower Y.
            var cam = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 6, heading: 0, tilt: 60);
            var buf = new List<TileId>();
            Sel(pad: 0).SelectVisibleTiles(
                new ViewContext { Camera = cam, ViewportPx = new double2(RefH, RefH), Projection = new SphericalProjection() },
                buf);

            Assert.IsNotEmpty(buf);
            int n = 1 << buf[0].Z;
            int yLookAt = n / 2; // lat 0 → the middle tile row
            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (var t in buf) { if (t.Y < minY) minY = t.Y; if (t.Y > maxY) maxY = t.Y; }

            int northReach = yLookAt - minY; // rows toward the view direction
            int southReach = maxY - yLookAt; // rows behind
            Assert.Greater(northReach, southReach,
                $"tilted view must reach farther toward the view (north={northReach}) than behind (south={southReach}) " +
                "— a symmetric cap would not");
        }

        [Test]
        public void LowZoom_CoversTheNearHemisphere()
        {
            var buf = new List<TileId>();
            Sel(pad: 0).SelectVisibleTiles(View(Cam(0, 0, 1.0)), buf); // z=1, n=2 → 4 tiles, all visible
            Assert.AreEqual(4, buf.Count, "at z=1 the whole world (4 tiles) is within the saturated cap");
        }

        [Test]
        public void RaySphereFarPlane_TightAtHighZoom_OpensTowardLimbAtLowZoom()
        {
            var far = new RaySphereFarPlane(SphericalProjection.Radius);
            const double fov = 60.0, aspect = 16.0 / 9.0;
            Angle overhead = Angle.FromDegrees(0.0);

            // High zoom: the viewport corners hit local (near-flat) ground → far ≈ altitude, NOT the old ×4.
            double altHi   = CameraPoseMath.AltitudeForZoom(13, 900.0, fov);
            double ratioHi = far.FarMetres(altHi, overhead, fov, aspect) / altHi;
            Assert.Greater(ratioHi, 1.0, "far must still reach the ground");
            Assert.Less(ratioHi, 1.3, $"far is tight overhead at high zoom (got x{ratioHi:F2}, not x4)");

            // Low zoom: the wide view sees PAST the horizon, so far opens toward the limb — a bigger multiple of
            // altitude than at high zoom (proving it doesn't clip the curved globe).
            double altLo   = CameraPoseMath.AltitudeForZoom(3, 900.0, fov);
            double ratioLo = far.FarMetres(altLo, overhead, fov, aspect) / altLo;
            Assert.Greater(ratioLo, ratioHi,
                $"far opens toward the limb at low zoom (lo x{ratioLo:F2} > hi x{ratioHi:F2})");
        }
    }
}
