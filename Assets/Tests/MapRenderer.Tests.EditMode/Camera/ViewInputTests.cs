// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the pure pan/zoom/tilt input math.


using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.App.View;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Cameras;

namespace MapRenderer.Tests.Cameras
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
            => u.ApplyTo(cam);

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
            // (CameraInteractionTests.ApplyZoom_ZoomToCursor_PinsGroundUnderCursor covers it fully.)
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

        // ── Mercator finite-sheet camera (pan/zoom look-at clamp) ──────────────────────────────────

        [Test]
        public void ApplyPan_NearEastEdge_ClampsInsteadOfWrapping()
        {
            // PAN CLAMP: Mercator is a finite sheet. A pan past the world edge must CLAMP EXACTLY to the
            // boundary, not WRAP the longitude to the opposite edge.
            const double zoom = 5.0; // world square meaningfully larger than the viewport — real clamp room
            double mpp = WebMercator.GroundResolution(zoom);
            double halfSpanX = Vp.x * 0.5 * mpp;
            double hiX = WebMercator.WorldExtent - halfSpanX; // east edge of the clamp range on this axis

            // Look-at inside the clamp range, one half-span short of the east edge (room to drag further east).
            double2 startMerc = new double2(hiX - halfSpanX, 0.0);
            double2 startLl   = WebMercator.ToLonLat(startMerc.x, startMerc.y);
            var v = Cam(startLl.x, startLl.y, zoom);

            GeoCoordinate3D grabbed = Proj.ScreenToGround(Centre, Vp, v);
            // Huge LEFT drag (cursor moves far -x): per ApplyPan_DragRight_MovesCenterWest, dragging left
            // pushes the center EAST — well past the world edge, forcing the clamp to bind.
            double2 cursorNow = Centre - new double2(10000.0, 0.0);

            var p = ViewInput.ApplyPan(Proj, v, grabbed, cursorNow, Vp);

            double2 resultMerc = WebMercator.FromLonLat(new GeoCoordinate3D
            {
                Longitude = p.Longitude.Value, Latitude = p.Latitude.Value
            });
            Assert.AreEqual(hiX, resultMerc.x, halfSpanX * 1e-6,
                "an overshoot past the east edge clamps EXACTLY to the boundary, not a wrapped value on the far side");
        }

        [Test]
        public void ApplyPan_AtFillFloor_LocksBindingAxisToWorldCentre()
        {
            // MIN-ZOOM LOCK: at the fill floor the world square matches the viewport's binding side, so the
            // clamp range on that axis collapses to world-centre and no pan moves it.
            double floorZoom = CameraPoseMath.MinZoomToFill(Vp.x, Vp.y, 0.0); // Vp is 1920x1080 — X is binding
            var v = Cam(0.0, 0.0, floorZoom);

            GeoCoordinate3D grabbed = Proj.ScreenToGround(Centre, Vp, v);
            double2 cursorNow = Centre + new double2(500.0, 0.0); // large horizontal drag

            var p = ViewInput.ApplyPan(Proj, v, grabbed, cursorNow, Vp);
            Assert.AreEqual(0.0, p.Longitude.Value, 1e-6,
                "at the fill floor the binding-axis clamp range is [0,0] — longitude stays locked to world-centre");
        }

        [Test]
        public void ClampLookAtToWorld_Globe_IsIdentity()
        {
            // GLOBE IDENTITY: the globe has no edges, so ClampLookAtToWorld returns the look-at UNCHANGED.
            // The point is far from the antimeridian, where WrapLon-vs-atan2 bit differences are harmless.
            var globe = new SphericalProjection();
            var cam = new CameraProperties(
                new GeoCoordinate3D { Longitude = 42.0, Latitude = 17.0, Altitude = 0.0 }, 5.0, 0, 0);
            double2 vp = new double2(1920.0, 1080.0);

            GeoCoordinate3D result = globe.ClampLookAtToWorld(vp, in cam);
            Assert.AreEqual(cam.LookAt.Longitude, result.Longitude, 1e-12, "globe clamp is the identity (longitude)");
            Assert.AreEqual(cam.LookAt.Latitude,  result.Latitude,  1e-12, "globe clamp is the identity (latitude)");
        }

        // ── Apply dispatch — split helpers ───────────────────────────────────────────────────────
        // Tests below drive the public seam ViewInput.Apply(intent, view), not the private helpers.

        // Helper: build a ViewContext with the test projection + viewport.
        private static ViewContext MakeView(CameraProperties cam)
            => new ViewContext { Camera = cam, ViewportPx = Vp, Projection = Proj };

        // ── TiltBy via Apply — clamps to pitch bounds (B-SPLIT); field-null coverage lives in
        // Apply_EachGestureKind_TouchesOnlyOwnFields below ────────────────────────────────────────

        private static IEnumerable<TestCaseData> TiltByClampCases()
        {
            // Migration of ApplyTilt_AccumulatesPitchAndBearing_WithClamps (clamp high/low).  B-SPLIT.
            yield return new TestCaseData(0.0, 1000.0, 60.0).SetName("Apply_TiltBy_AddsDeltaWithinPitchBounds(ClampsHigh)");
            yield return new TestCaseData(0.0, -1000.0, 0.0).SetName("Apply_TiltBy_AddsDeltaWithinPitchBounds(ClampsLow)");
            // In-range: proves the delta is ADDED, not doubled or otherwise mis-scaled — the two clamp rows
            // above saturate at 60/0 regardless of a delta-scaling mutant, so they cannot catch one alone.
            yield return new TestCaseData(30.0, 5.0, 35.0).SetName("Apply_TiltBy_AddsDeltaWithinPitchBounds(InRange)");
        }

        [Test]
        [TestCaseSource(nameof(TiltByClampCases))]
        public void Apply_TiltBy_AddsDeltaWithinPitchBounds(double startTilt, double tiltDelta, double expectedTilt)
        {
            var view = MakeView(Cam(0, 0, 5.0, heading: 0.0, tilt: startTilt));
            var p    = ViewInput.Apply(GestureIntent.TiltBy(tiltDelta, 60.0), view);

            Assert.AreEqual(expectedTilt, p.Tilt.Value, 1e-9,
                $"TiltBy({tiltDelta:+0;-0}, maxPitch=60) from start={startTilt} → Tilt=={expectedTilt}");
            Assert.IsNull(p.Heading, "TiltBy leaves Heading null  (B-SPLIT)");
        }

        // ── HeadingBy via Apply — wraps across the 0/360 boundary (B-SPLIT); field-null coverage
        // lives in Apply_EachGestureKind_TouchesOnlyOwnFields below ─────────────────────────────────

        private static IEnumerable<TestCaseData> HeadingByWrapCases()
        {
            // Migration of ApplyTilt_BearingWrapsTo0_360.  B-SPLIT.
            yield return new TestCaseData(350.0, 20.0, 10.0).SetName("Apply_HeadingBy_WrapsAcrossBoundary(Forward)");
            yield return new TestCaseData(5.0, -15.0, 350.0).SetName("Apply_HeadingBy_WrapsAcrossBoundary(Backward)");
        }

        [Test]
        [TestCaseSource(nameof(HeadingByWrapCases))]
        public void Apply_HeadingBy_WrapsAcrossBoundary(double startHeading, double headingDelta, double expectedHeading)
        {
            var view = MakeView(Cam(0, 0, 5.0, heading: startHeading, tilt: 0.0));
            var p    = ViewInput.Apply(GestureIntent.HeadingBy(headingDelta), view);

            Assert.AreEqual(expectedHeading, p.Heading.Value, 1e-9,
                $"{startHeading} + {headingDelta} wraps to {expectedHeading}  (B-SPLIT)");
            Assert.IsNull(p.Tilt, "HeadingBy leaves Tilt null  (B-SPLIT)");
        }

        // ── Every gesture kind touches only its own patch fields (B-SPLIT complement) ────────────

        /// <summary>Sequential, one block per gesture kind: TiltBy/HeadingBy/PanToAnchor/ZoomAtAnchor each
        /// set only the field(s) that name them and leave every other patch field null.</summary>
        [Test]
        public void Apply_EachGestureKind_TouchesOnlyOwnFields()
        {
            var v    = Cam(0, 0, 5.0);
            var view = MakeView(v);

            var tiltPatch = ViewInput.Apply(GestureIntent.TiltBy(20.0, 60.0), view);
            Assert.IsNotNull(tiltPatch.Tilt,      "TiltBy sets Tilt");
            Assert.IsNull   (tiltPatch.Heading,   "TiltBy leaves Heading null  (B-SPLIT)");
            Assert.IsNull   (tiltPatch.Longitude, "TiltBy leaves Longitude null");
            Assert.IsNull   (tiltPatch.Zoom,      "TiltBy leaves Zoom null");

            var headingPatch = ViewInput.Apply(GestureIntent.HeadingBy(10.0), MakeView(Cam(0, 0, 5.0, heading: 0.0)));
            Assert.IsNotNull(headingPatch.Heading,   "HeadingBy sets Heading");
            Assert.IsNull   (headingPatch.Tilt,      "HeadingBy leaves Tilt null  (B-SPLIT)");
            Assert.IsNull   (headingPatch.Longitude, "HeadingBy leaves Longitude null");
            Assert.IsNull   (headingPatch.Zoom,      "HeadingBy leaves Zoom null");

            var panCam     = Cam(0, 0, 4.0);
            var panView    = MakeView(panCam);
            var grabbed    = Proj.ScreenToGround(Centre, Vp, panCam);
            var panPatch   = ViewInput.Apply(GestureIntent.Pan(grabbed, Centre + new double2(50.0, 0.0)), panView);
            Assert.IsNotNull(panPatch.Longitude, "PanToAnchor sets Longitude");
            Assert.IsNotNull(panPatch.Latitude,  "PanToAnchor sets Latitude");
            Assert.IsNull   (panPatch.Zoom,      "PanToAnchor leaves Zoom null    (B-SPLIT)");
            Assert.IsNull   (panPatch.Heading,   "PanToAnchor leaves Heading null (B-SPLIT)");
            Assert.IsNull   (panPatch.Tilt,      "PanToAnchor leaves Tilt null    (B-SPLIT)");

            var zoomPatch = ViewInput.Apply(GestureIntent.ZoomAt(Centre, 1.0, 0.0, 22.0), view);
            Assert.IsNotNull(zoomPatch.Zoom,    "ZoomAtAnchor sets Zoom");
            Assert.IsNull   (zoomPatch.Heading, "ZoomAtAnchor leaves Heading null (B-SPLIT)");
            Assert.IsNull   (zoomPatch.Tilt,    "ZoomAtAnchor leaves Tilt null    (B-SPLIT)");
        }

        // ── B-ZOOMPIN — anchored zoom through the seam ────────────────────────────────────────────

        [Test]
        public void Apply_ZoomAtAnchor_OffCentre_PinsGround()
        {
            // The anchored-zoom invariant, exercised via ViewInput.Apply.  B-ZOOMPIN.
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

    }

// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the IProjection + ViewInput invariants.



    [TestFixture]
    public class CameraInteractionTests
    {
        // ── Test helpers ─────────────────────────────────────────────────────────────────────────

        private static readonly IProjection Proj = new WebMercatorProjection();

        // 1920×1080 viewport; centre = (960, 540).
        private static readonly double2 Vp     = new double2(1920.0, 1080.0);
        private static readonly double2 Centre = new double2(960.0, 540.0);

        private static CameraProperties Cam(double lon, double lat, double zoom,
                                            double heading = 0.0, double tilt = 0.0)
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, heading, tilt);

        // Build CameraProperties from patch applied to a base cam.
        private static CameraProperties ApplyPatch(CameraProperties base_, CameraPropertiesUpdate u)
        {
            return new CameraProperties(
                new GeoCoordinate3D
                {
                    Longitude = u.Longitude ?? base_.LookAt.Longitude,
                    Latitude  = u.Latitude  ?? base_.LookAt.Latitude,
                    Altitude  = 0.0
                },
                u.Zoom ?? base_.Zoom,
                base_.Heading.Degrees,
                base_.Tilt.Degrees);
        }

        // ── Absolute axis + rotation-sign pin ───────────────────────────────────────────────

        [Test]
        public void ScreenToGround_Heading0_AbsoluteAxisPin()
        {
            // Forward check of screen convention, axis mapping and scale: P=(1160,740) is (+200,+200) px from
            // centre, so ground is 200*mpp east and north of (0,45).
            var cam = Cam(0.0, 45.0, 4.0, heading: 0.0);
            double2 P   = new double2(1160.0, 740.0);

            GeoCoordinate3D g = Proj.ScreenToGround(P, Vp, cam);

            double mpp   = WebMercator.GroundResolution(4.0);
            double2 cMerc = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = 0.0, Latitude = 45.0 });
            double2 exp   = WebMercator.ToLonLat(cMerc.x + 200.0 * mpp, cMerc.y + 200.0 * mpp);

            Assert.AreEqual(exp.x, g.Longitude, 1e-6, "longitude must match hand-computed value");
            Assert.AreEqual(exp.y, g.Latitude,  1e-6, "latitude must match hand-computed value");
            Assert.Greater(g.Longitude, 0.0,   "offset right of centre → east (lon > 0)");
            Assert.Greater(g.Latitude,  45.0,  "offset above centre  → north (lat > 45)");
        }

        [Test]
        public void ScreenToGround_Heading90_RotationSignPin()
        {
            // Pins the rotation SIGN a round-trip cannot catch: at heading=90° screen-right maps to ground
            // SOUTH and screen-up to ground EAST.
            var cam = Cam(0.0, 45.0, 4.0, heading: 90.0);

            // Screen-right: P=(1160,540), offset=(+200,0).
            double2 pRight = new double2(1160.0, 540.0);
            GeoCoordinate3D gRight = Proj.ScreenToGround(pRight, Vp, cam);
            Assert.Less(gRight.Latitude, 45.0,
                "heading=90°, screen-right → ground SOUTH (lat < 45)");
            Assert.AreEqual(0.0, gRight.Longitude, 1e-3,
                "heading=90°, screen-right (no sy component) → lon ≈ 0");

            // Screen-up: P=(960,740), offset=(0,+200).
            double2 pUp = new double2(960.0, 740.0);
            GeoCoordinate3D gUp = Proj.ScreenToGround(pUp, Vp, cam);
            Assert.Greater(gUp.Longitude, 0.0,
                "heading=90°, screen-up → ground EAST (lon > 0)");
        }

        // ── Zoom-to-cursor invariant ────────────────────────────────────────────────────────

        [Test]
        public void ApplyZoom_ZoomToCursor_PinsGroundUnderCursor()
        {
            // After a Δzoom=+1.5 about off-centre P, the earth point that was under P must still be under P
            // (within 0.5px).
            var cam     = Cam(0.0, 45.0, 4.0);
            double2 P   = new double2(1400.0, 800.0);

            GeoCoordinate3D before = Proj.ScreenToGround(P, Vp, cam);
            CameraPropertiesUpdate patch = ViewInput.ApplyZoom(
                Proj, cam, P, Vp, scrollDelta: +1.5, sensitivity: 1.0, minZoom: 0, maxZoom: 22);
            CameraProperties camAfter = ApplyPatch(cam, patch);

            double2 reproject = Proj.GroundToScreen(before, Vp, camAfter);
            double2 diff = reproject - P;
            Assert.LessOrEqual(math.sqrt(diff.x * diff.x + diff.y * diff.y), 0.5,
                "zoom-to-cursor invariant: earth point under P must remain under P within 0.5px");
        }

        [Test]
        public void ApplyZoom_CentreZoom_NoLookAtChange()
        {
            // When cursor == screen centre, zoom-to-cursor is a pure zoom — look-at unchanged.
            var cam = Cam(0.0, 45.0, 4.0);
            CameraPropertiesUpdate patch = ViewInput.ApplyZoom(
                Proj, cam, Centre, Vp, scrollDelta: +2.0, sensitivity: 1.0, minZoom: 0, maxZoom: 22);
            Assert.AreEqual(6.0, patch.Zoom.Value, 1e-9, "zoom increases by 2");
            Assert.AreEqual(cam.LookAt.Longitude, patch.Longitude.Value, 1e-6, "centre zoom: lon unchanged");
            Assert.AreEqual(cam.LookAt.Latitude,  patch.Latitude.Value,  1e-6, "centre zoom: lat unchanged");
        }

        // ── Anchored-pan invariant ──────────────────────────────────────────────────────────

        [Test]
        public void ApplyPan_AnchoredGrabbedGround_StaysUnderCursor()
        {
            // After an anchored pan from pStart to pNow, the grabbed point must appear at pNow within 0.5px.
            var cam = Cam(0.0, 45.0, 4.0);
            double2 pStart = new double2(1400.0, 800.0);
            double2 pNow   = new double2(1100.0, 650.0);

            GeoCoordinate3D grabbed = Proj.ScreenToGround(pStart, Vp, cam);
            CameraPropertiesUpdate patch = ViewInput.ApplyPan(Proj, cam, grabbed, pNow, Vp);
            CameraProperties camAfter = ApplyPatch(cam, patch);

            double2 reproject = Proj.GroundToScreen(grabbed, Vp, camAfter);
            double2 diff = reproject - pNow;
            Assert.LessOrEqual(math.sqrt(diff.x * diff.x + diff.y * diff.y), 0.5,
                "anchored-pan invariant: grabbed ground must appear at the new cursor position within 0.5px");
        }

        [Test]
        public void ApplyPan_SamePosition_NoChange()
        {
            // When cursor == grabbed position, ApplyPan is a no-op (look-at unchanged).
            var cam = Cam(0.0, 45.0, 4.0);
            double2 P       = new double2(1200.0, 700.0);
            GeoCoordinate3D grabbed = Proj.ScreenToGround(P, Vp, cam);

            CameraPropertiesUpdate patch = ViewInput.ApplyPan(Proj, cam, grabbed, P, Vp);
            CameraProperties camAfter = ApplyPatch(cam, patch);

            Assert.AreEqual(cam.LookAt.Longitude, camAfter.LookAt.Longitude, 1e-6, "no-op: lon unchanged");
            Assert.AreEqual(cam.LookAt.Latitude,  camAfter.LookAt.Latitude,  1e-6, "no-op: lat unchanged");
        }

        // ── Round-trip consistency ────────────────────────────────────────────────────────────────

        [Test]
        public void GroundToScreen_IsInverseOfScreenToGround()
        {
            // For an arbitrary off-centre pixel, GroundToScreen(ScreenToGround(P)) should recover P.
            var cam = Cam(13.4, 52.5, 12.0, heading: 30.0);
            double2 P = new double2(1100.0, 700.0);

            GeoCoordinate3D g = Proj.ScreenToGround(P, Vp, cam);
            double2 back      = Proj.GroundToScreen(g, Vp, cam);

            Assert.AreEqual(P.x, back.x, 1e-6, "round-trip x");
            Assert.AreEqual(P.y, back.y, 1e-6, "round-trip y");
        }

        [Test]
        public void MercatorPin_UnderDpr2_HoldsWithLogicalSeam_DriftsWithPhysical()
        {
            // The render frames the LOGICAL viewport, so the seam divides BOTH cursor and viewport by DPR and a
            // grabbed point re-renders under the cursor. Skipping the ÷DPR drifts it.
            const double dpr = 2.0;
            double2 vpPhysical = new double2(1920, 1080);
            double2 vpLogical  = vpPhysical * (1.0 / dpr); // 960×540
            var     c          = Cam(12, 20, 5.0);
            double2 cursorPhys = new double2(1200, 700);   // off-centre physical pixel

            // CORRECT seam: convert cursor + viewport to logical, then round-trip through the logical render.
            double2         cursorLog = cursorPhys * (1.0 / dpr);
            GeoCoordinate3D grabbed   = Proj.ScreenToGround(cursorLog, vpLogical, c);
            double2         rendered  = Proj.GroundToScreen(grabbed, vpLogical, c) * dpr; // logical → physical
            Assert.AreEqual(cursorPhys.x, rendered.x, 1e-6, "Mercator pin holds under DPR=2 (x)");
            Assert.AreEqual(cursorPhys.y, rendered.y, 1e-6, "Mercator pin holds under DPR=2 (y)");

            // BUG: feed the PHYSICAL cursor + viewport to the projection while the render stays logical — the
            // ground offset is 2× too large, so the re-rendered point drifts far off the cursor.
            GeoCoordinate3D grabbedBug  = Proj.ScreenToGround(cursorPhys, vpPhysical, c);
            double2         renderedBug = Proj.GroundToScreen(grabbedBug, vpLogical, c) * dpr;
            double          dx          = renderedBug.x - cursorPhys.x;
            double          dy          = renderedBug.y - cursorPhys.y;
            double          drift       = math.sqrt(dx * dx + dy * dy);
            Assert.Greater(drift, 3.0, "the un-normalized (physical) seam drifts the Mercator pin under DPR≠1");
        }

        [Test]
        public void ClampValidLatitude_ClampsToMercatorBounds()
        {
            Assert.AreEqual( WebMercator.MaxLatitude, Proj.ClampValidLatitude( 90.0), 1e-9, "clamp high");
            Assert.AreEqual(-WebMercator.MaxLatitude, Proj.ClampValidLatitude(-90.0), 1e-9, "clamp low");
            Assert.AreEqual(45.0,                     Proj.ClampValidLatitude( 45.0), 1e-9, "in-range pass-through");
        }
    }
}
