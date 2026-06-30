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

        // ── Apply dispatch — split helpers (S73) ─────────────────────────────────────────────────
        //
        // All tests below drive the public seam ViewInput.Apply(intent, view) — not the private
        // helpers — so they pin the seam contract.  A FakeGestureSource (T-SEAM proof) drives the
        // same seam without any Unity/device dependency.
        //
        // Former ApplyTilt_AccumulatesPitchAndBearing_WithClamps and ApplyTilt_BearingWrapsTo0_360
        // are migrated to the split helpers below (D2); their exact assertion values are preserved.

        // Helper: build a ViewContext with the test projection + viewport.
        private static ViewContext MakeView(CameraProperties cam)
            => new ViewContext { Camera = cam, ViewportPx = Vp, Projection = Proj };

        // ── TiltBy via Apply — B-SPLIT ────────────────────────────────────────────────────────────

        [Test]
        public void Apply_TiltBy_SetsTilt_HeadingNull()
        {
            // Migration of ApplyTilt_AccumulatesPitchAndBearing_WithClamps (tilt direction).
            var v    = Cam(0, 0, 5.0);
            var view = MakeView(v);
            var p    = ViewInput.Apply(GestureIntent.TiltBy(20.0, 60.0), view);

            Assert.IsNotNull(p.Tilt,      "TiltBy sets Tilt");
            Assert.IsNull   (p.Heading,   "TiltBy leaves Heading null  (B-SPLIT)");
            Assert.IsNull   (p.Longitude, "TiltBy leaves Longitude null");
            Assert.IsNull   (p.Zoom,      "TiltBy leaves Zoom null");
            Assert.AreEqual (20.0, p.Tilt.Value, 1e-9, "vertical delta → pitch");
        }

        [Test]
        public void Apply_TiltBy_ClampsHigh()
        {
            // Migration of ApplyTilt_AccumulatesPitchAndBearing_WithClamps (clamp high).  B-SPLIT.
            var view = MakeView(Cam(0, 0, 5.0));
            var p    = ViewInput.Apply(GestureIntent.TiltBy(1000.0, 60.0), view);

            Assert.AreEqual(60.0, p.Tilt.Value, 1e-9, "TiltBy(+1000, maxPitch=60) → Tilt==60.0");
            Assert.IsNull  (p.Heading,            "TiltBy leaves Heading null  (B-SPLIT)");
        }

        [Test]
        public void Apply_TiltBy_ClampsLow()
        {
            // Migration of ApplyTilt_AccumulatesPitchAndBearing_WithClamps (clamp low).  B-SPLIT.
            var view = MakeView(Cam(0, 0, 5.0));
            var p    = ViewInput.Apply(GestureIntent.TiltBy(-1000.0, 60.0), view);

            Assert.AreEqual(0.0, p.Tilt.Value, 1e-9, "TiltBy(-1000, maxPitch=60) → Tilt==0.0");
            Assert.IsNull  (p.Heading,            "TiltBy leaves Heading null  (B-SPLIT)");
        }

        // ── HeadingBy via Apply — B-SPLIT ────────────────────────────────────────────────────────

        [Test]
        public void Apply_HeadingBy_SetsHeading_TiltNull()
        {
            // Migration of ApplyTilt_AccumulatesPitchAndBearing_WithClamps (heading direction).
            var view = MakeView(Cam(0, 0, 5.0, heading: 0.0));
            var p    = ViewInput.Apply(GestureIntent.HeadingBy(10.0), view);

            Assert.IsNotNull(p.Heading,   "HeadingBy sets Heading");
            Assert.IsNull   (p.Tilt,      "HeadingBy leaves Tilt null  (B-SPLIT)");
            Assert.IsNull   (p.Longitude, "HeadingBy leaves Longitude null");
            Assert.IsNull   (p.Zoom,      "HeadingBy leaves Zoom null");
            Assert.AreEqual (10.0, p.Heading.Value, 1e-9, "horizontal delta → bearing");
        }

        [Test]
        public void Apply_HeadingBy_WrapsForward()
        {
            // Migration of ApplyTilt_BearingWrapsTo0_360.  B-SPLIT.
            // From heading 350°, +20° → wraps to 10°.
            var view = MakeView(Cam(0, 0, 5.0, heading: 350.0, tilt: 0.0));
            var p    = ViewInput.Apply(GestureIntent.HeadingBy(20.0), view);

            Assert.AreEqual(10.0, p.Heading.Value, 1e-9, "350 + 20 wraps to 10  (B-SPLIT)");
            Assert.IsNull  (p.Tilt,                       "HeadingBy leaves Tilt null  (B-SPLIT)");
        }

        [Test]
        public void Apply_HeadingBy_WrapsBackward()
        {
            // From heading 5°, -15° → wraps to 350°.  B-SPLIT.
            var view = MakeView(Cam(0, 0, 5.0, heading: 5.0, tilt: 0.0));
            var p    = ViewInput.Apply(GestureIntent.HeadingBy(-15.0), view);

            Assert.AreEqual(350.0, p.Heading.Value, 1e-9, "5 - 15 wraps to 350  (B-SPLIT)");
            Assert.IsNull  (p.Tilt,                        "HeadingBy leaves Tilt null  (B-SPLIT)");
        }

        // ── PanToAnchor / ZoomAtAnchor field-null checks (B-SPLIT complement) ──────────────────────

        [Test]
        public void Apply_PanToAnchor_LeavesZoomHeadingTiltNull()
        {
            var v       = Cam(0, 0, 4.0);
            var view    = MakeView(v);
            var grabbed = Proj.ScreenToGround(Centre, Vp, v);
            double2 cursor = Centre + new double2(50.0, 0.0);

            var p = ViewInput.Apply(GestureIntent.Pan(grabbed, cursor), view);

            Assert.IsNotNull(p.Longitude, "PanToAnchor sets Longitude");
            Assert.IsNotNull(p.Latitude,  "PanToAnchor sets Latitude");
            Assert.IsNull   (p.Zoom,      "PanToAnchor leaves Zoom null    (B-SPLIT)");
            Assert.IsNull   (p.Heading,   "PanToAnchor leaves Heading null (B-SPLIT)");
            Assert.IsNull   (p.Tilt,      "PanToAnchor leaves Tilt null    (B-SPLIT)");
        }

        [Test]
        public void Apply_ZoomAtAnchor_LeavesHeadingTiltNull()
        {
            var view = MakeView(Cam(0, 0, 5.0));
            var p    = ViewInput.Apply(GestureIntent.ZoomAt(Centre, 1.0, 0.0, 22.0), view);

            Assert.IsNotNull(p.Zoom,    "ZoomAtAnchor sets Zoom");
            Assert.IsNull   (p.Heading, "ZoomAtAnchor leaves Heading null (B-SPLIT)");
            Assert.IsNull   (p.Tilt,    "ZoomAtAnchor leaves Tilt null    (B-SPLIT)");
        }

        // ── B-ZOOMPIN — anchored zoom through the seam ────────────────────────────────────────────

        [Test]
        public void Apply_ZoomAtAnchor_OffCentre_PinsGround()
        {
            // Port of the anchored-zoom invariant (formerly in ApplyZoom_PositiveScroll_ZoomsIn_AndClamps)
            // now exercised via ViewInput.Apply.  B-ZOOMPIN.
            var v2   = Cam(0, 45.0, 4.0);
            var view = MakeView(v2);
            double2 P = new double2(1400.0, 800.0);

            GeoCoordinate3D before = Proj.ScreenToGround(P, Vp, v2);
            var patch   = ViewInput.Apply(GestureIntent.ZoomAt(P, 1.5, 0.0, 22.0), view);
            CameraProperties after = ApplyPatch(v2, patch);
            double2 reproject = Proj.GroundToScreen(before, Vp, after);
            double2 diff      = reproject - P;
            Assert.LessOrEqual(math.sqrt(diff.x * diff.x + diff.y * diff.y), 0.5,
                "ZoomAtAnchor via Apply: off-centre P stays pinned within 0.5px (B-ZOOMPIN)");
        }

        // ── B-PAN — anchored pan through the seam ────────────────────────────────────────────────

        [Test]
        public void Apply_PanToAnchor_PinsGrabbedGroundUnderCursor()
        {
            // B-PAN headline pin: keeps grabbed ground under cursor within 0.5px through the seam.
            var v       = Cam(10.0, 45.0, 6.0);
            var view    = MakeView(v);
            double2 cursor  = Centre + new double2(80.0, 35.0);   // off-centre cursor
            var grabbed     = Proj.ScreenToGround(Centre, Vp, v); // captured at drag-start (screen centre)

            var patch  = ViewInput.Apply(GestureIntent.Pan(grabbed, cursor), view);
            CameraProperties after = ApplyPatch(v, patch);

            // The grabbed ground should now appear under cursor (within 0.5px).
            double2 reproject = Proj.GroundToScreen(grabbed, Vp, after);
            double2 diff      = reproject - cursor;
            Assert.LessOrEqual(math.sqrt(diff.x * diff.x + diff.y * diff.y), 0.5,
                "PanToAnchor via Apply: grabbed ground stays under cursor within 0.5px (B-PAN)");
        }

        [Test]
        public void Apply_PanToAnchor_DragRight_MovesCenterWest()
        {
            // Port of ApplyPan_DragRight_MovesCenterWest via the seam.  B-PAN.
            var v       = Cam(0, 0, 4.0);
            var view    = MakeView(v);
            var grabbed = Proj.ScreenToGround(Centre, Vp, v);
            double2 cursor = Centre + new double2(50.0, 0.0);

            var p = ViewInput.Apply(GestureIntent.Pan(grabbed, cursor), view);
            Assert.Less(p.Longitude.Value, v.LookAt.Longitude,
                "PanToAnchor via Apply: drag-right shifts center west (B-PAN)");
            Assert.AreEqual(0.0, p.Latitude.Value, 1e-6, "no vertical drag → latitude unchanged");
        }

        [Test]
        public void Apply_PanToAnchor_ClampsLatitudeToMercatorLimit()
        {
            // Port of ApplyPan_ClampsLatitudeToMercatorLimit via the seam.  B-PAN.
            var v       = Cam(0, 84.0, 2.0);
            var view    = MakeView(v);
            var grabbed = Proj.ScreenToGround(Centre, Vp, v);
            double2 cursor = Centre - new double2(0.0, 100000.0);

            var p = ViewInput.Apply(GestureIntent.Pan(grabbed, cursor), view);
            Assert.LessOrEqual(p.Latitude.Value, CameraProperties.MaxMercatorLat + 1e-9,
                "PanToAnchor via Apply: latitude must clamp to the Mercator limit (B-PAN)");
        }

        // ── T-SEAM — fake source drives the seam (genericity proof) ─────────────────────────────

        /// <summary>
        /// T-SEAM proof-of-genericity: a named fake intent source (no Unity/device dependency) drives
        /// <see cref="ViewInput.Apply"/> and produces correct patches. Demonstrates that S74's touch
        /// backend and any headless test driver are interchangeable sources over the identical seam.
        /// </summary>
        private sealed class FakeGestureSource
        {
            private readonly ViewContext _view;
            public FakeGestureSource(ViewContext view) => _view = view;
            public CameraPropertiesUpdate Drive(GestureIntent i) => ViewInput.Apply(i, _view);
        }

        [Test]
        public void FakeSource_DrivesSeam_NoDeviceDependency()
        {
            var cam    = Cam(10.0, 20.0, 8.0, heading: 90.0, tilt: 30.0);
            var source = new FakeGestureSource(MakeView(cam));

            // TiltBy: tilt changes, heading untouched.
            var tp = source.Drive(GestureIntent.TiltBy(5.0, 60.0));
            Assert.AreEqual(35.0, tp.Tilt.Value, 1e-9, "FakeSource TiltBy: 30+5=35");
            Assert.IsNull  (tp.Heading,                  "FakeSource TiltBy: Heading null");

            // HeadingBy: heading changes, tilt untouched.
            var hp = source.Drive(GestureIntent.HeadingBy(-100.0));
            Assert.AreEqual(350.0, hp.Heading.Value, 1e-9, "FakeSource HeadingBy: 90-100 wraps to 350");
            Assert.IsNull  (hp.Tilt,                        "FakeSource HeadingBy: Tilt null");

            // ZoomAtAnchor: zoom/lon/lat set, heading/tilt null.
            var zp = source.Drive(GestureIntent.ZoomAt(Centre, 2.0, 0.0, 22.0));
            Assert.IsNotNull(zp.Zoom,    "FakeSource ZoomAt: Zoom set");
            Assert.IsNull   (zp.Heading, "FakeSource ZoomAt: Heading null");
            Assert.IsNull   (zp.Tilt,    "FakeSource ZoomAt: Tilt null");

            // PanToAnchor: lon/lat set, zoom/heading/tilt null.
            var grabbed = Proj.ScreenToGround(Centre, Vp, cam);
            var pp = source.Drive(GestureIntent.Pan(grabbed, Centre + new double2(10.0, 0.0)));
            Assert.IsNotNull(pp.Longitude, "FakeSource Pan: Longitude set");
            Assert.IsNull   (pp.Zoom,      "FakeSource Pan: Zoom null");
            Assert.IsNull   (pp.Heading,   "FakeSource Pan: Heading null");
            Assert.IsNull   (pp.Tilt,      "FakeSource Pan: Tilt null");
        }
    }
}
