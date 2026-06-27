// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the pure pan/zoom/tilt input math.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ViewInputTests
    {
        // ── Test helpers ─────────────────────────────────────────────────────────────────────────

        private static readonly IProjection Proj = new WebMercatorProjection();

        private static CameraProperties Cam(double lon, double lat, double zoom,
                                            double heading = 0.0, double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, heading, tilt);

        // 1920×1080 viewport used throughout.
        private static readonly double2 Vp = new double2(1920.0, 1080.0);

        // Centre pixel (vp*0.5).
        private static readonly double2 Centre = new double2(960.0, 540.0);

        // Build a CameraProperties from a patch applied to a base camera (using CameraPropertiesUpdate.ApplyTo).
        private static CameraProperties ApplyPatch(CameraProperties cam, CameraPropertiesUpdate u)
            => u.ApplyTo(cam, 1080.0, 60.0);

        // ── ApplyZoom ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ApplyZoom_PositiveScroll_ZoomsIn_AndClamps()
        {
            var v = Cam(0, 0, 5.0);

            // At centre cursor, zoom changes correctly and lon/lat stay at look-at.
            var z = ViewInput.ApplyZoom(Proj, v, Centre, Vp, scrollDelta: 2.0, sensitivity: 0.5, minZoom: 0, maxZoom: 22);
            Assert.AreEqual(6.0, z.Zoom.Value, 1e-9, "scroll 2 * sens 0.5 = +1 zoom");
            // At centre, lookAt stays (within float noise).
            Assert.IsNotNull(z.Longitude, "new ApplyZoom always sets Longitude (zoom-to-cursor invariant)");
            Assert.IsNotNull(z.Latitude,  "new ApplyZoom always sets Latitude");
            Assert.AreEqual(v.LookAt.Longitude, z.Longitude.Value, 1e-6, "centre zoom keeps lon");
            Assert.AreEqual(v.LookAt.Latitude,  z.Latitude.Value,  1e-6, "centre zoom keeps lat");

            // Clamp to maxZoom.
            var hi = ViewInput.ApplyZoom(Proj, v, Centre, Vp, scrollDelta: 100.0, sensitivity: 1.0, minZoom: 0, maxZoom: 14);
            Assert.AreEqual(14.0, hi.Zoom.Value, 1e-9, "clamps to maxZoom");

            // Clamp to minZoom.
            var lo = ViewInput.ApplyZoom(Proj, v, Centre, Vp, scrollDelta: -100.0, sensitivity: 1.0, minZoom: 2, maxZoom: 22);
            Assert.AreEqual(2.0, lo.Zoom.Value, 1e-9, "clamps to minZoom");

            // Anchored zoom invariant: off-centre cursor P stays pinned after scroll.
            // (Full T1 coverage is in CameraInteractionTests; this is a quick sign-check here.)
            var v2   = Cam(0, 45.0, 4.0);
            double2 P = new double2(1400.0, 800.0);
            GeoCoordinate3D before = Proj.ScreenToGround(P, Vp, v2);
            var patch   = ViewInput.ApplyZoom(Proj, v2, P, Vp, scrollDelta: +1.5, sensitivity: 1.0, minZoom: 0, maxZoom: 22);
            CameraProperties after = ApplyPatch(v2, patch);
            double2 reproject = Proj.GroundToScreen(before, Vp, after);
            double2 diff = reproject - P;
            Assert.LessOrEqual(math.sqrt(diff.x * diff.x + diff.y * diff.y), 0.5, "zoom-to-cursor: off-centre P stays pinned within 0.5px");
        }

        // ── ApplyPan ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ApplyPan_DragRight_MovesCenterWest()
        {
            // Grab the earth point at screen centre; cursor moves RIGHT (+x).
            // The camera center must shift WEST so the grabbed point follows the cursor.
            var v = Cam(0, 0, 4.0);
            GeoCoordinate3D grabbed = Proj.ScreenToGround(Centre, Vp, v);
            double2 cursorNow = Centre + new double2(50.0, 0.0);  // cursor moved right

            var p = ViewInput.ApplyPan(Proj, v, grabbed, cursorNow, Vp);
            Assert.IsNotNull(p.Longitude, "a pan sets Lon");
            Assert.IsNotNull(p.Latitude,  "a pan sets Lat");
            Assert.IsNull(p.Zoom, "a pan leaves Zoom null");
            // Cursor moved right → grabbed point must appear further right → center moves WEST.
            Assert.Less(p.Longitude.Value, v.LookAt.Longitude, "drag-right shifts center west (content follows cursor)");
            Assert.AreEqual(0.0, p.Latitude.Value, 1e-6, "no vertical drag → latitude unchanged");
        }

        [Test]
        public void ApplyPan_DragUp_MovesCenterSouth()
        {
            // Grab the earth point at screen centre; cursor moves UP (+y in +y-up Unity convention).
            // In the +y-up screen convention: dragging cursor UP (y increases) means the grabbed point
            // must appear at a higher pixel position. The camera centre must shift SOUTH so the grabbed
            // point (originally at the screen centre) now sits above centre, under the cursor.
            var v = Cam(0, 0, 4.0);
            GeoCoordinate3D grabbed = Proj.ScreenToGround(Centre, Vp, v);
            double2 cursorNow = Centre + new double2(0.0, 30.0);  // cursor moved UP (+y)

            var p = ViewInput.ApplyPan(Proj, v, grabbed, cursorNow, Vp);
            Assert.Less(p.Latitude.Value, v.LookAt.Latitude,
                "drag-up (cursor y increases in +y-up convention) shifts center SOUTH (grabbed point glues to cursor above centre)");
        }

        [Test]
        public void ApplyPan_HigherZoom_MovesLess()
        {
            // The same cursor displacement moves fewer degrees at a higher zoom (finer ground resolution).
            var v2  = Cam(0, 0, 2.0);
            var v10 = Cam(0, 0, 10.0);
            double2 cursorNow = Centre + new double2(50.0, 0.0);  // cursor moved right

            GeoCoordinate3D grab2  = Proj.ScreenToGround(Centre, Vp, v2);
            GeoCoordinate3D grab10 = Proj.ScreenToGround(Centre, Vp, v10);

            var lowZ  = ViewInput.ApplyPan(Proj, v2,  grab2,  cursorNow, Vp);
            var highZ = ViewInput.ApplyPan(Proj, v10, grab10, cursorNow, Vp);

            double lowDelta  = math.abs(lowZ.Longitude.Value  - v2.LookAt.Longitude);
            double highDelta = math.abs(highZ.Longitude.Value - v10.LookAt.Longitude);
            Assert.Greater(lowDelta, highDelta, "a pixel drag moves more degrees at low zoom than high zoom");
        }

        [Test]
        public void ApplyPan_ClampsLatitudeToMercatorLimit()
        {
            // Start near the latitude limit; cursor moves DOWN (y decreases in +y-up convention) by a
            // huge amount, which drives the look-at toward +MaxMercatorLat. ClampValidLatitude must cap it.
            var v = Cam(0, 84.0, 2.0);
            GeoCoordinate3D grabbed = Proj.ScreenToGround(Centre, Vp, v);
            // Cursor moved DOWN (−y): the grabbed point needs to appear below centre → center moves north.
            double2 cursorNow = Centre - new double2(0.0, 100000.0);

            var p = ViewInput.ApplyPan(Proj, v, grabbed, cursorNow, Vp);
            Assert.LessOrEqual(p.Latitude.Value, CameraProperties.MaxMercatorLat + 1e-9,
                "latitude must clamp to the Mercator limit");
        }

        // ── ApplyTilt ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ApplyTilt_AccumulatesPitchAndBearing_WithClamps()
        {
            var v = Cam(0, 0, 5.0);
            var t = ViewInput.ApplyTilt(v, dxPixels: 10.0, dyPixels: 20.0,
                bearingSensitivity: 1.0, pitchSensitivity: 1.0, maxPitch: 60.0);
            Assert.IsNotNull(t.Heading, "a tilt drag sets Heading");
            Assert.IsNotNull(t.Tilt,    "a tilt drag sets Tilt");
            Assert.IsNull(t.Longitude,  "a tilt drag leaves Lon null");
            Assert.IsNull(t.Zoom, "a tilt drag leaves Zoom null");
            Assert.AreEqual(10.0, t.Heading.Value, 1e-9, "horizontal drag → bearing");
            Assert.AreEqual(20.0, t.Tilt.Value,    1e-9, "vertical drag → pitch");

            var clamped = ViewInput.ApplyTilt(v, 0.0, 1000.0, 1.0, 1.0, 60.0);
            Assert.AreEqual(60.0, clamped.Tilt.Value, 1e-9, "pitch clamps to maxPitch");

            var neg = ViewInput.ApplyTilt(v, 0.0, -1000.0, 1.0, 1.0, 60.0);
            Assert.AreEqual(0.0, neg.Tilt.Value, 1e-9, "pitch clamps at 0");
        }

        [Test]
        public void ApplyTilt_BearingWrapsTo0_360()
        {
            var v = Cam(0, 0, 5.0, heading: 350.0, tilt: 0.0);
            var t = ViewInput.ApplyTilt(v, dxPixels: 20.0, dyPixels: 0.0,
                bearingSensitivity: 1.0, pitchSensitivity: 1.0, maxPitch: 60.0);
            Assert.AreEqual(10.0, t.Heading.Value, 1e-9, "350 + 20 wraps to 10");
        }
    }
}
