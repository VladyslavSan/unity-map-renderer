// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the IProjection + ViewInput invariants.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
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

        // ── T0 — Absolute axis + rotation-sign pin ───────────────────────────────────────────────

        [Test]
        public void ScreenToGround_Heading0_AbsoluteAxisPin()
        {
            // Non-round-trip forward check: pins absolute screen convention, axis mapping, and scale.
            // Camera at lon=0, lat=45, zoom=4. Viewport 1920×1080; centre=(960,540).
            // Cursor at P=(1160,740): offsetPx=(+200,+200) → east=+200, north=+200.
            // Expected ground: WebMercator.ToLonLat(fromLonLat(0,45).x+200*mpp, fromLonLat(0,45).y+200*mpp).
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
            // Pins the rotation SIGN that round-trip T1/T2 cannot catch.
            // At heading=90°: screen-right (sx>0) must map to ground SOUTH (lat<45, lon≈0);
            //                  screen-up (sy>0) must map to ground EAST (lon>0, lat≈45).
            // A wrong ±Heading sign in the rotation formula makes screen-right → NORTH → FAILS.
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

        // ── T1 — Zoom-to-cursor invariant ────────────────────────────────────────────────────────

        [Test]
        public void ApplyZoom_ZoomToCursor_PinsGroundUnderCursor()
        {
            // THE decisive test. Camera at lon=0, lat=45, zoom=4.
            // Cursor at off-centre P=(1400,800). After a Δzoom=+1.5, the earth point that was under P
            // must still be under P in the new camera (within 0.5px).
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

        // ── T2 — Anchored-pan invariant ──────────────────────────────────────────────────────────

        [Test]
        public void ApplyPan_AnchoredGrabbedGround_StaysUnderCursor()
        {
            // Camera at lon=0, lat=45, zoom=4. Grab at P_start=(1400,800).
            // Cursor moves to P_now=(1100,650). After the anchored pan, the grabbed point must appear
            // at P_now within 0.5px.
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
            // S92: the logical interaction seam (Controller S92 D3) fixes retina Mercator pan/zoom for FREE —
            // NO WebMercatorProjection edit (the GroundResolution/MetersPerPixel freeze holds). The render
            // frames the LOGICAL viewport (MapCamera D1), so GroundToScreen(·, vpLogical) IS the render; the
            // seam divides BOTH cursor and viewport by DPR before the projection call, so a grabbed point
            // re-renders under the cursor. Skipping the ÷DPR (physical viewport) drifts it — same failure the
            // globe D3 tooth pins, here for the planar case.
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
