// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the pure pan/zoom/tilt input math.

using NUnit.Framework;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ViewInputTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom,
                                            double heading = 0.0, double tilt = 0.0)
            => new CameraProperties(new LookAtPoint(lon, lat, 0), zoom, heading, tilt);

        [Test]
        public void ApplyZoom_PositiveScroll_ZoomsIn_AndClamps()
        {
            var v = Cam(0, 0, 5.0);
            var z = ViewInput.ApplyZoom(v, scrollDelta: 2.0, sensitivity: 0.5, minZoom: 0, maxZoom: 22);
            Assert.AreEqual(6.0, z.Zoom.Value, 1e-9, "scroll 2 * sens 0.5 = +1 zoom");
            Assert.IsNull(z.Lon, "ApplyZoom patch must leave Lon null");
            Assert.IsNull(z.Lat, "ApplyZoom patch must leave Lat null");

            var hi = ViewInput.ApplyZoom(v, scrollDelta: 100.0, sensitivity: 1.0, minZoom: 0, maxZoom: 14);
            Assert.AreEqual(14.0, hi.Zoom.Value, 1e-9, "clamps to maxZoom");

            var lo = ViewInput.ApplyZoom(v, scrollDelta: -100.0, sensitivity: 1.0, minZoom: 2, maxZoom: 22);
            Assert.AreEqual(2.0, lo.Zoom.Value, 1e-9, "clamps to minZoom");
        }

        [Test]
        public void ApplyPan_DragRight_MovesCenterWest()
        {
            var v = Cam(0, 0, 4.0);
            var p = ViewInput.ApplyPan(v, dxPixels: 50.0, dyPixels: 0.0);
            Assert.IsNotNull(p.Lon, "a pan sets Lon");
            Assert.IsNotNull(p.Lat, "a pan sets Lat");
            Assert.IsNull(p.Zoom, "a pan leaves Zoom null");
            Assert.Less(p.Lon.Value, v.LookAt.Lon, "drag-right shifts center west (content follows cursor)");
            Assert.AreEqual(0.0, p.Lat.Value, 1e-9, "no vertical drag → latitude unchanged");
        }

        [Test]
        public void ApplyPan_DragUp_MovesCenterNorth()
        {
            var v = Cam(0, 0, 4.0);
            var p = ViewInput.ApplyPan(v, dxPixels: 0.0, dyPixels: 30.0);
            Assert.Greater(p.Lat.Value, v.LookAt.Lat, "drag-up (screen dy>0) shifts center north");
        }

        [Test]
        public void ApplyPan_HigherZoom_MovesLess()
        {
            // The same pixel drag moves the center fewer degrees at a higher zoom (finer ground res).
            var lowZ  = ViewInput.ApplyPan(Cam(0, 0, 2.0), 50.0, 0.0);
            var highZ = ViewInput.ApplyPan(Cam(0, 0, 10.0), 50.0, 0.0);
            double lowDelta  = System.Math.Abs(lowZ.Lon.Value);
            double highDelta = System.Math.Abs(highZ.Lon.Value);
            Assert.Greater(lowDelta, highDelta, "a pixel drag moves more degrees at low zoom than high zoom");
        }

        [Test]
        public void ApplyPan_ClampsLatitudeToMercatorLimit()
        {
            var v = Cam(0, 84.0, 2.0);
            var p = ViewInput.ApplyPan(v, 0.0, 100000.0);   // huge upward drag
            Assert.LessOrEqual(p.Lat.Value, CameraProperties.MaxMercatorLat + 1e-9,
                "latitude must clamp to the Mercator limit");
        }

        [Test]
        public void ApplyTilt_AccumulatesPitchAndBearing_WithClamps()
        {
            var v = Cam(0, 0, 5.0);
            var t = ViewInput.ApplyTilt(v, dxPixels: 10.0, dyPixels: 20.0,
                bearingSensitivity: 1.0, pitchSensitivity: 1.0, maxPitch: 60.0);
            Assert.IsNotNull(t.Heading, "a tilt drag sets Heading");
            Assert.IsNotNull(t.Tilt,    "a tilt drag sets Tilt");
            Assert.IsNull(t.Lon,  "a tilt drag leaves Lon null");
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
