// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the pure pan/zoom/tilt input math.

using NUnit.Framework;
using MapRenderer.Core.View;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ViewInputTests
    {
        [Test]
        public void ApplyZoom_PositiveScroll_ZoomsIn_AndClamps()
        {
            var v = new ViewState(0, 0, 5.0);
            var z = ViewInput.ApplyZoom(v, scrollDelta: 2.0, sensitivity: 0.5, minZoom: 0, maxZoom: 22);
            Assert.AreEqual(6.0, z.Zoom, 1e-9, "scroll 2 * sens 0.5 = +1 zoom");

            var hi = ViewInput.ApplyZoom(v, scrollDelta: 100.0, sensitivity: 1.0, minZoom: 0, maxZoom: 14);
            Assert.AreEqual(14.0, hi.Zoom, 1e-9, "clamps to maxZoom");

            var lo = ViewInput.ApplyZoom(v, scrollDelta: -100.0, sensitivity: 1.0, minZoom: 2, maxZoom: 22);
            Assert.AreEqual(2.0, lo.Zoom, 1e-9, "clamps to minZoom");
        }

        [Test]
        public void ApplyPan_DragRight_MovesCenterWest()
        {
            var v = new ViewState(0, 0, 4.0);
            var p = ViewInput.ApplyPan(v, dxPixels: 50.0, dyPixels: 0.0);
            Assert.Less(p.CenterLon, v.CenterLon, "drag-right shifts center west (content follows cursor)");
            Assert.AreEqual(0.0, p.CenterLat, 1e-9, "no vertical drag → latitude unchanged");
        }

        [Test]
        public void ApplyPan_DragUp_MovesCenterNorth()
        {
            var v = new ViewState(0, 0, 4.0);
            var p = ViewInput.ApplyPan(v, dxPixels: 0.0, dyPixels: 30.0);
            Assert.Greater(p.CenterLat, v.CenterLat, "drag-up (screen dy>0) shifts center north");
        }

        [Test]
        public void ApplyPan_HigherZoom_MovesLess()
        {
            // The same pixel drag moves the center fewer degrees at a higher zoom (finer ground res).
            var lowZ  = ViewInput.ApplyPan(new ViewState(0, 0, 2.0), 50.0, 0.0);
            var highZ = ViewInput.ApplyPan(new ViewState(0, 0, 10.0), 50.0, 0.0);
            double lowDelta  = System.Math.Abs(lowZ.CenterLon);
            double highDelta = System.Math.Abs(highZ.CenterLon);
            Assert.Greater(lowDelta, highDelta, "a pixel drag moves more degrees at low zoom than high zoom");
        }

        [Test]
        public void ApplyPan_ClampsLatitudeToMercatorLimit()
        {
            var v = new ViewState(0, 84.0, 2.0);
            var p = ViewInput.ApplyPan(v, 0.0, 100000.0);   // huge upward drag
            Assert.LessOrEqual(p.CenterLat, ViewState.MaxMercatorLat + 1e-9,
                "latitude must clamp to the Mercator limit");
        }

        [Test]
        public void ApplyTilt_AccumulatesPitchAndBearing_WithClamps()
        {
            var v = new ViewState(0, 0, 5.0);
            var t = ViewInput.ApplyTilt(v, dxPixels: 10.0, dyPixels: 20.0,
                bearingSensitivity: 1.0, pitchSensitivity: 1.0, maxPitch: 60.0);
            Assert.AreEqual(10.0, t.BearingDeg, 1e-9, "horizontal drag → bearing");
            Assert.AreEqual(20.0, t.PitchDeg,   1e-9, "vertical drag → pitch");

            var clamped = ViewInput.ApplyTilt(v, 0.0, 1000.0, 1.0, 1.0, 60.0);
            Assert.AreEqual(60.0, clamped.PitchDeg, 1e-9, "pitch clamps to maxPitch");

            var neg = ViewInput.ApplyTilt(v, 0.0, -1000.0, 1.0, 1.0, 60.0);
            Assert.AreEqual(0.0, neg.PitchDeg, 1e-9, "pitch clamps at 0");
        }

        [Test]
        public void ApplyTilt_BearingWrapsTo0_360()
        {
            var v = new ViewState(0, 0, 5.0).WithOrientation(350.0, 0.0);
            var t = ViewInput.ApplyTilt(v, dxPixels: 20.0, dyPixels: 0.0,
                bearingSensitivity: 1.0, pitchSensitivity: 1.0, maxPitch: 60.0);
            Assert.AreEqual(10.0, t.BearingDeg, 1e-9, "350 + 20 wraps to 10");
        }
    }
}
