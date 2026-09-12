// Engine-free (NUnit + Core only) → runs in BOTH the Unity EditMode runner and the fast core-tests project.
// UMR-125: locks how many tiles the SHIPPED (default) globe selector wiring emits as tilt increases. The
// default mode retains this growth by design — it is the better-looking mode under tilt; the opt-in
// ProjectedArea mode (see ProjectedAreaLodTests.cs) trades this growth against visual quality.
//
// These are CHARACTERISATION tests. The exact counts below are deliberately brittle: any change to the LOD
// stop rule, the near-field detail cap, the far-plane caps or the projection math moves at least one of them.
// A stage that changes tile selection is REQUIRED to edit these numbers and say so. Do not relax them to
// ranges — the brittleness is the whole point, and a range would hide the next regression.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Tile-cover growth under camera tilt, measured on the production selector wiring
    /// (<see cref="ScreenSpaceLodStrategy"/> + <see cref="RaySphereFarPlane"/>), plus a self-check of the
    /// on-screen-size instrument the measurement reads. See the file header for why the counts are exact.
    /// </summary>
    public class TiltCoverGrowthTests
    {
        // The production globe wiring, mirrored from MapView.EnsureSelector + the MapViewConfig defaults.
        private const int    MinZoom        = 0;
        private const int    MaxZoom        = 14;
        private const int    OnScreenTilePx = 512;
        private const double GlobeFarCap    = 8.0;

        // Fixed pose inputs: Berlin, heading 0, 60° vertical FOV, a 1600x900 viewport.
        private const double LookAtLon = 13.405;
        private const double LookAtLat = 52.52;
        private const double Zoom      = 13.0;
        private static readonly double2 Viewport = new double2(1600.0, 900.0);

        /// <summary>The camera pose for a tilt, with every other input held at the fixture constants.</summary>
        private static CameraProperties Cam(double tiltDeg, double zoom = Zoom)
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = LookAtLon, Latitude = LookAtLat, Altitude = 0.0 },
                zoom, 0.0, tiltDeg);

        /// <summary>The globe cover size at a tilt — the same quantity TileManager publishes as VisibleTileCount.</summary>
        private static int GlobeCover(double tiltDeg)
        {
            var selector = new FrustumTileSelector(MinZoom, MaxZoom, OnScreenTilePx,
                new ScreenSpaceLodStrategy(),
                new RaySphereFarPlane(SphericalProjection.Radius, GlobeFarCap));
            var view = new ViewContext
            {
                Camera     = Cam(tiltDeg),
                ViewportPx = Viewport,
                Projection = new SphericalProjection(),
            };
            var buffer = new List<TileId>();
            selector.SelectVisibleTiles(view, buffer);
            return buffer.Count;
        }

        /// <summary>
        /// Tooth A. Exact cover sizes for the shipped globe wiring at zoom 13, 1600x900, tilt 0/30/45/60.
        /// Brittle on purpose — read the file header before you change a number.
        /// </summary>
        [Test]
        public void GlobeTiltSweep_EmitsTheseExactCoverSizes()
        {
            Assert.AreEqual(24,  GlobeCover(0.0),  "globe z13 1600x900 tilt 0");
            Assert.AreEqual(30,  GlobeCover(30.0), "globe z13 1600x900 tilt 30");
            Assert.AreEqual(54,  GlobeCover(45.0), "globe z13 1600x900 tilt 45");
            Assert.AreEqual(112, GlobeCover(60.0), "globe z13 1600x900 tilt 60");
        }

        /// <summary>
        /// Tooth B. The default mode's cover grows under tilt (billboard approximation, no foreshortening) —
        /// deliberately retained behaviour, not a defect: it is what buys the better-looking cover. See
        /// <c>ProjectedAreaLodTests</c> for the opt-in mode that trades this growth for fewer tiles.
        /// </summary>
        [Test]
        public void DistanceMode_CoverGrowthUnderTiltIsRecordedBehaviour()
        {
            int atTilt0  = GlobeCover(0.0);
            int atTilt60 = GlobeCover(60.0);

            Assert.Greater(atTilt60, 2 * atTilt0,
                "ScreenSpaceLodStrategy's cover grows well past 2x under tilt (today 112 vs 24, 4.67x) — a "
              + "recorded tradeoff for the default's better-looking cover, not a defect to fix here.");
        }

        // ── Tooth C — the instrument ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The tile's projected size on screen, in pixels — the square root of its screen-space quad area.
        /// Runs no selector: it takes ONE explicit tile, so a change to the stop rule cannot move it.
        /// Internal so <c>ProjectedAreaLodTests</c> shares this instrument rather than duplicating it.
        /// </summary>
        internal static double ProjectedTilePx(IProjection projection, in CameraProperties cam,
                                               double2 viewportPx, TileId tile)
        {
            var lookAt = new GeoCoordinate
            {
                Latitude  = projection.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, viewportPx.y, cam.VerticalFovDeg);
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 eye, out double3 fwd, out double3 up);

            // Camera basis and the pinhole scale. Both screen axes share it: the horizontal half-angle is the
            // vertical one times the aspect, and the viewport width is its height times the same aspect.
            double3 forward = math.normalize(fwd);
            double3 right   = math.normalize(math.cross(forward, up));
            double3 upward  = math.cross(right, forward);
            double  scale   = viewportPx.y / (2.0 * math.tan(Angle.FromDegrees(cam.VerticalFovDeg * 0.5).Radians));

            double3  origin = projection.Project(lookAt);
            float3x3 basis  = projection.TangentBasisAt(lookAt);

            // The four tile corners as a ring, so the shoelace sum below closes.
            double2 topLeft     = ToScreenPx(0.0, 0.0);
            double2 topRight    = ToScreenPx(1.0, 0.0);
            double2 bottomRight = ToScreenPx(1.0, 1.0);
            double2 bottomLeft  = ToScreenPx(0.0, 1.0);

            double twiceArea = Cross(topLeft, topRight) + Cross(topRight, bottomRight)
                             + Cross(bottomRight, bottomLeft) + Cross(bottomLeft, topLeft);
            return math.sqrt(math.abs(twiceArea) * 0.5);

            double2 ToScreenPx(double u, double v)
            {
                double2 lonLat = tile.ToLonLat(u, v, 1.0);
                double3 world  = projection.Project(
                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                double rx = world.x - origin.x, ry = world.y - origin.y, rz = world.z - origin.z;
                var render = new double3(
                    basis.c0.x * rx + basis.c0.y * ry + basis.c0.z * rz,
                    basis.c1.x * rx + basis.c1.y * ry + basis.c1.z * rz,
                    basis.c2.x * rx + basis.c2.y * ry + basis.c2.z * rz);

                double3 toPoint = render - eye;
                double  depth   = math.dot(toPoint, forward);
                return new double2(scale * math.dot(toPoint, right)  / depth,
                                   scale * math.dot(toPoint, upward) / depth);
            }

            double Cross(double2 a, double2 b) => a.x * b.y - b.x * a.y;
        }

        /// <summary>The tile at a zoom that holds a lon/lat.</summary>
        private static TileId TileAt(double lon, double lat, int zoom)
        {
            double2 unit = WebMercatorTiling.UnitSquareFromLonLat(
                new GeoCoordinate { Latitude = lat, Longitude = lon });
            int n = 1 << zoom;
            return new TileId { Z = zoom, X = (int)(unit.x * n), Y = (int)(unit.y * n) };
        }

        /// <summary>
        /// Tooth C. A flat Mercator tile viewed straight down has no foreshortening, so its projected size is
        /// exactly the on-screen tile size the selector targets. Proves the instrument, not the selector.
        /// </summary>
        [Test]
        public void FlatMercatorAtTiltZero_ProjectsOneTileAtTheOnScreenTileSize()
        {
            var    projection = new WebMercatorProjection();
            TileId lookAtTile = TileAt(LookAtLon, LookAtLat, 13);
            var    neighbour  = new TileId { Z = 13, X = lookAtTile.X + 2, Y = lookAtTile.Y + 1 };

            double own            = ProjectedTilePx(projection, Cam(0.0), Viewport, lookAtTile);
            double awayFromLookAt = ProjectedTilePx(projection, Cam(0.0), Viewport, neighbour);
            double coarserCamera  = ProjectedTilePx(projection, Cam(0.0, zoom: 10.0), Viewport,
                                                    TileAt(LookAtLon, LookAtLat, 10));
            double smallViewport  = ProjectedTilePx(projection, Cam(0.0), new double2(960.0, 540.0), lookAtTile);

            Assert.AreEqual(OnScreenTilePx, own,            1.0, "the look-at's own tile");
            Assert.AreEqual(OnScreenTilePx, awayFromLookAt, 1.0, "a tile away from the look-at");
            Assert.AreEqual(OnScreenTilePx, coarserCamera,  1.0, "camera and tile three zooms coarser");
            Assert.AreEqual(OnScreenTilePx, smallViewport,  1.0, "a 960x540 viewport");
        }

        /// <summary>
        /// Tooth C, second half: an instrument that always reads 512 would be useless. It must move when the
        /// projected size moves — with tilt (foreshortening) and with the tile's own zoom.
        /// </summary>
        [Test]
        public void ProjectedTileSize_MovesWithTiltAndWithTileZoom()
        {
            var    projection = new WebMercatorProjection();
            TileId own        = TileAt(LookAtLon, LookAtLat, 13);

            double tilted  = ProjectedTilePx(projection, Cam(30.0), Viewport, own);
            double coarser = ProjectedTilePx(projection, Cam(0.0), Viewport, TileAt(LookAtLon, LookAtLat, 12));

            Assert.AreEqual(463.710, tilted,  0.01, "tilt 30 foreshortens the look-at's own tile");
            Assert.AreEqual(1024.0,  coarser, 1.0,  "one zoom coarser is twice the side");
        }
    }
}
