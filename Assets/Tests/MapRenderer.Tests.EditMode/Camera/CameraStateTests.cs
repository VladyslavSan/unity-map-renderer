// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
//
// Tests CameraProperties, CameraPropertiesUpdate (patch semantics), CameraPoseMath (altitude formula +
// inverse + pose), and the shortest-angle heading lerp. There is no animation path: the instant
// patch path is CameraPropertiesUpdate.ApplyTo.


using System;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.App.View;
using MapRenderer.Unity.View.Camera;
using MapRenderer.App.View.Camera;

namespace MapRenderer.Tests.Cameras
{

    [TestFixture]
    public class CameraPropertiesTests
    {
        private const double TestViewportHeight = 1080.0;
        private const double TestFovDeg         = 60.0;

        // ── Patch semantics ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A patch with only Tilt set changes ONLY tilt; all other fields (Zoom, Heading, LookAt) hold.
        /// </summary>
        [Test]
        public void Patch_TiltOnly_ChangesOnlyTilt()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 13.4, Latitude = 52.5, Altitude = 0 }, zoom: 10.0, heading: 45.0, tilt: 0.0);

            var patch = new CameraPropertiesUpdate { Tilt = 25.0 };
            CameraProperties result = patch.ApplyTo(initial);

            Assert.AreEqual(25.0, result.Tilt.Degrees,    1e-9, "Tilt must be updated to 25.");
            Assert.AreEqual(10.0, result.Zoom,           1e-9, "Zoom must be unchanged.");
            Assert.AreEqual(45.0, result.Heading.Degrees, 1e-9, "Heading must be unchanged.");
            Assert.AreEqual(13.4, result.LookAt.Longitude,   1e-9, "Lon must be unchanged.");
            Assert.AreEqual(52.5, result.LookAt.Latitude,   1e-9, "Lat must be unchanged.");
        }

        /// <summary>Empty patch is a no-op.</summary>
        [Test]
        public void Patch_Empty_IsNoOp()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 1.0, Latitude = 2.0, Altitude = 3.0 }, zoom: 7.5, heading: 30.0, tilt: 15.0);

            var patch = new CameraPropertiesUpdate();
            CameraProperties result = patch.ApplyTo(initial);

            Assert.AreEqual(initial.LookAt.Longitude, result.LookAt.Longitude, 1e-9);
            Assert.AreEqual(initial.LookAt.Latitude, result.LookAt.Latitude, 1e-9);
            Assert.AreEqual(initial.Zoom,        result.Zoom,       1e-9);
            Assert.AreEqual(initial.Heading.Degrees, result.Heading.Degrees, 1e-9);
            Assert.AreEqual(initial.Tilt.Degrees,   result.Tilt.Degrees,   1e-9);
        }

        /// <summary>
        /// Instant patch sets the target field immediately (the only apply path now that animation is gone).
        /// </summary>
        [Test]
        public void Patch_Zoom_SetsImmediately()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0, tilt: 0);

            CameraProperties result = new CameraPropertiesUpdate { Zoom = 8.0 }
                .ApplyTo(initial);

            Assert.AreEqual(8.0, result.Zoom, 1e-9, "Zoom must be set immediately.");
        }

        // ── Altitude formula ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// CameraPoseMath.AltitudeForZoom matches the hand-computed value at zoom 10.
        /// Hand: metersPerPixel = 40075016.686 / (512 * 2^10) ≈ 76.437; alt ≈ 71492.1
        /// </summary>
        [Test]
        public void AltitudeForZoom_MatchesHandComputedValue()
        {
            const double zoom   = 10.0;
            const double height = 1080.0;
            const double fov    = 60.0;

            const double earthCirc = 40075016.686;
            const double tileSize  = 512.0; // the 512 convention
            double mpp      = earthCirc / (tileSize * Math.Pow(2.0, zoom));
            double halfFov  = fov * 0.5 * Math.PI / 180.0;
            double expected = (height * mpp) / (2.0 * Math.Tan(halfFov));

            double actual = CameraPoseMath.AltitudeForZoom(zoom, height, fov);

            Assert.AreEqual(expected, actual, expected * 0.001,
                $"AltitudeForZoom(10, 1080, 60) ≈ {expected:F1}. Got {actual:F1}.");
        }

        /// <summary>
        /// Higher zoom → lower altitude (monotonicity). z2/z16 ratio > 1000 (≈ 2^14 ≈ 16384).
        /// </summary>
        [Test]
        public void AltitudeForZoom_Monotonic_HigherZoomLowerAltitude()
        {
            double altZ2  = CameraPoseMath.AltitudeForZoom(2.0,  TestViewportHeight, TestFovDeg);
            double altZ16 = CameraPoseMath.AltitudeForZoom(16.0, TestViewportHeight, TestFovDeg);

            Assert.Greater(altZ2, altZ16, "Higher zoom → lower altitude.");
            Assert.Greater(altZ2 / altZ16, 1000.0, "z2/z16 altitude ratio must be > 1000 (≈ 2^14).");
        }

        /// <summary>
        /// ZoomForAltitude is the inverse of AltitudeForZoom (round-trip tolerance 0.1%).
        /// </summary>
        [Test]
        public void ZoomForAltitude_IsInverseOfAltitudeForZoom()
        {
            for (double zoom = 1.0; zoom <= 16.0; zoom += 3.0)
            {
                double alt       = CameraPoseMath.AltitudeForZoom(zoom, TestViewportHeight, TestFovDeg);
                double roundTrip = ZoomForAltitude(alt, TestViewportHeight, TestFovDeg);
                Assert.AreEqual(zoom, roundTrip, zoom * 0.001,
                    $"ZoomForAltitude(AltitudeForZoom({zoom})) must round-trip back to {zoom}.");
            }
        }

        // ── Pose math — overhead at pitch=0 ──────────────────────────────────────────────────────

        /// <summary>
        /// At tilt=0: camera position is (0, altitude, 0) — directly above; forward is (0, -1, 0).
        /// The up-vector is derived from heading (non-degenerate even at pitch=0).
        /// </summary>
        [Test]
        public void Pose_PitchZero_CameraOverhead_LooksDown()
        {
            double altitude = 10000.0;
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                out double3 pos, out double3 fwd, out double3 up);

            Assert.AreEqual(0.0,      pos.x, 1e-6, "At pitch=0, X must be 0 (directly above).");
            Assert.AreEqual(altitude, pos.y, 1e-6, "At pitch=0, Y must equal altitude.");
            Assert.AreEqual(0.0,      pos.z, 1e-6, "At pitch=0, Z must be 0 (directly above).");

            Assert.AreEqual( 0.0, fwd.x, 1e-6, "Forward X must be 0 at pitch=0 heading=0.");
            Assert.AreEqual(-1.0, fwd.y, 1e-6, "Forward Y must be -1 (looking straight down).");
            Assert.AreEqual( 0.0, fwd.z, 1e-6, "Forward Z must be 0 at pitch=0 heading=0.");
        }

        /// <summary>
        /// At pitch=0, bearing changes the camera up-vector direction (deterministic north-up).
        /// The up-vector at heading=0 must differ from that at heading=90.
        /// </summary>
        [Test]
        public void Pose_PitchZero_BearingAffectsUpVector()
        {
            double altitude = 10000.0;

            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0),  Angle.FromDegrees(0.0), out _, out _, out double3 upNorth);
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(90.0), Angle.FromDegrees(0.0), out _, out _, out double3 upEast);

            // The up-vectors should differ (heading-derived, not fixed world-up).
            double diffX = Math.Abs(upNorth.x - upEast.x);
            double diffZ = Math.Abs(upNorth.z - upEast.z);
            bool different = diffX + diffZ > 0.1;
            Assert.IsTrue(different,
                $"Up-vector must rotate with heading at pitch=0. " +
                $"up(heading=0)=({upNorth.x:F3},{upNorth.y:F3},{upNorth.z:F3}) " +
                $"up(heading=90)=({upEast.x:F3},{upEast.y:F3},{upEast.z:F3}).");
        }

        /// <summary>
        /// At tilt=45, camera must be above origin (pos.Y > 0) and tilted (fwd.Y in (-1, 0)).
        /// </summary>
        [Test]
        public void Pose_Pitch45_TiltsTowardHorizon_StaysAbove()
        {
            double altitude = 10000.0;
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(45.0),
                out double3 pos, out double3 fwd, out _);

            Assert.Greater(pos.y, 0.0, "Camera must be above origin (pos.y > 0) at tilt=45.");
            Assert.Less(fwd.y,    0.0, "Forward Y must be negative (pointing downward component) at tilt=45.");
            Assert.Greater(fwd.y, -1.0, "Forward Y must be > -1 (tilted, not straight down) at tilt=45.");
        }

        /// <summary>
        /// REGRESSION (tilt vertical-flip): the camera up-vector must keep the sky up at every tilt —
        /// <c>up.y &gt; 0</c> across a tilt sweep. The pre-fix pose set <c>up.y = -sinT &lt; 0</c>, so as
        /// tilt → 90 the up-vector pointed straight down and the rendered world appeared on top. This was
        /// </summary>
        [Test]
        public void Pose_UpVector_KeepsSkyUp_AcrossTiltSweep()
        {
            double altitude = 10000.0;
            foreach (double t in new[] { 0.0, 30.0, 45.0, 60.0, 89.0 })
            foreach (double h in new[] { 0.0, 90.0, 200.0, 359.0 })
            {
                CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(h), Angle.FromDegrees(t),
                    out _, out _, out double3 up);
                Assert.Greater(up.y, -1e-9,
                    $"up.y must be ≥ 0 (sky up) at tilt={t}, heading={h}; got {up.y:F4} " +
                    "(negative ⇒ the vertical-flip bug is back).");
                // up must stay unit-length (the closed form has no normalization step).
                double upLen = math.sqrt(up.x * up.x + up.y * up.y + up.z * up.z);
                Assert.AreEqual(1.0, upLen, 1e-9, $"up must be unit at tilt={t}, heading={h}.");
            }
        }

        /// <summary>
        /// REGRESSION: at tilt=90 the camera looks along the horizon with world-up exactly preserved.
        /// At heading=0 it looks toward +Z (north) from the south side, up = (0,1,0).
        /// </summary>
        [Test]
        public void Pose_Tilt90_LooksAtHorizon_SkyUp()
        {
            double altitude = 10000.0;
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(90.0),
                out double3 pos, out double3 fwd, out double3 up);

            Assert.AreEqual(0.0, pos.x, 1e-3, "At heading=0 the camera has no east/west offset (pos.x ≈ 0).");
            Assert.Less(pos.z, 0.0, "At tilt=90 heading=0 the camera must sit SOUTH of the look-at (pos.z < 0).");
            Assert.AreEqual(0.0, pos.y, 1e-3, "At tilt=90 the camera is at ground level (pos.y ≈ 0).");

            Assert.AreEqual( 0.0, fwd.x, 1e-9);
            Assert.AreEqual( 0.0, fwd.y, 1e-9, "Forward must be horizontal at tilt=90.");
            Assert.AreEqual( 1.0, fwd.z, 1e-9, "Forward must point north (+Z) at tilt=90 heading=0.");

            Assert.AreEqual(0.0, up.x, 1e-9);
            Assert.AreEqual(1.0, up.y, 1e-9, "Up must be world-up (0,1,0) at tilt=90.");
            Assert.AreEqual(0.0, up.z, 1e-9);
        }

        /// <summary>
        /// REGRESSION: with tilt&gt;0 at heading=0 the camera looks NORTH (forward +Z) from the south
        /// side (pos.z &lt; 0), so the bearing-0 view recedes toward north at the top of the screen
        /// (MapLibre convention). The pre-fix pose had the opposite Z sign (camera north, looking south).
        /// </summary>
        [Test]
        public void Pose_TiltPositive_Heading0_OrbitsToSouth_LooksNorth()
        {
            double altitude = 10000.0;
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(45.0),
                out double3 pos, out double3 fwd, out _);

            Assert.Less(pos.z,    0.0, "Camera must orbit to the SOUTH (pos.z < 0) at heading=0 tilt=45.");
            Assert.Greater(fwd.z, 0.0, "Forward must have a NORTH component (fwd.z > 0) at heading=0 tilt=45.");
            Assert.Greater(pos.y, 0.0, "Camera stays above the ground at tilt=45.");
        }

        // ── Heading interpolation: shortest-angle ─────────────────────────────────────────────────

        [Test]
        public void LerpHeading_ShortestPath_AcrossZero()
        {
            // 350 → 10: shortest is +20 (not −340)
            double half = Angle.LerpShortest(Angle.FromDegrees(350.0), Angle.FromDegrees(10.0), 0.5).Degrees;
            // At t=0.5, diff=+20, heading = 350+10 = 360 ≡ 0
            bool nearZero = half < 5.0 || half > 355.0;
            Assert.IsTrue(nearZero, $"350→10 at t=0.5 should be near 0°. Got {half:F2}°.");
        }

        [Test]
        public void LerpHeading_ShortestPath_Full()
        {
            double full = Angle.LerpShortest(Angle.FromDegrees(350.0), Angle.FromDegrees(10.0), 1.0).Degrees;
            Assert.AreEqual(10.0, full, 1e-6, "350→10 at t=1.0 must land at 10°.");
        }

        // ── Heading normalization ─────────────────────────────────────────────────────────────────

        [Test]
        public void CameraProperties_HeadingNormalized_To0_360()
        {
            var p = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, -45.0, 0);
            Assert.AreEqual(315.0, p.Heading.Degrees, 1e-9, "Negative heading must wrap to [0,360).");

            var q = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 370.0, 0);
            Assert.AreEqual(10.0, q.Heading.Degrees, 1e-9, "Heading > 360 must wrap to [0,360).");
        }

        // ── Pan Y-sign regression ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pins the "content follows cursor" pan direction through the anchored-pan API.
        /// Screen convention: +x right, +y up, origin bottom-left (Unity mouse position).
        /// MapController does not negate delta.y — the anchored pan handles direction.
        ///
        /// This test pins: cursor moves UP (+y in +y-up convention) → grabbed ground appears ABOVE centre
        /// → camera centre moves SOUTH (lat decreases) so the grabbed point follows the cursor.
        /// </summary>
        [Test]
        public void D6a_PanYSign_DragUp_NewIS_MovesCenterSouth()
        {
            var v = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 4.0, 0, 0);
            IProjection proj   = new WebMercatorProjection();
            var         vp     = new double2(1920.0, 1080.0);
            var         centre = new double2(960.0, 540.0);

            // Grab the earth point at the screen centre.
            GeoCoordinate3D grabbed = proj.ScreenToGround(centre, vp, v);
            // Cursor moves UP (+y): grabbed point needs to appear above centre → centre moves SOUTH.
            var cursorNow = centre + new double2(0.0, 30.0);

            var patch = MapRenderer.App.View.ViewInput.ApplyPan(proj, v, grabbed, cursorNow, vp);

            Assert.Less(patch.Latitude.Value, v.LookAt.Latitude,
                "D6a: cursor UP in +y-up convention must move the LookAt centre SOUTH " +
                "(grabbed point glues to cursor above centre; content follows cursor correctly).");
        }

        /// <summary>
        /// Complementary: cursor moves DOWN (−y in +y-up convention) → centre moves NORTH.
        /// </summary>
        [Test]
        public void D6a_PanYSign_DragDown_NewIS_MovesCenterNorth()
        {
            var v = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 4.0, 0, 0);
            IProjection proj   = new WebMercatorProjection();
            var         vp     = new double2(1920.0, 1080.0);
            var         centre = new double2(960.0, 540.0);

            GeoCoordinate3D grabbed = proj.ScreenToGround(centre, vp, v);
            // Cursor moves DOWN (−y): grabbed point appears below centre → centre moves NORTH.
            var cursorNow = centre - new double2(0.0, 30.0);

            var patch = MapRenderer.App.View.ViewInput.ApplyPan(proj, v, grabbed, cursorNow, vp);

            Assert.Greater(patch.Latitude.Value, v.LookAt.Latitude,
                "D6a: cursor DOWN in +y-up convention must move the LookAt centre NORTH.");
        }

        // ── Tilt-Y sign convention ──────────────────────────────────────────────────────────────────
        //
        // The sensitivity multiply (pitchSensitivity = 0.3) is applied by the caller before the delta
        // reaches the seam, as MapController does when building a TiltBy intent.

        /// <summary>
        /// Pins the tilt-Y sign convention — drag-UP tilts the camera toward overhead
        /// (pitch DECREASES). The new Input System reports <c>delta.y &gt; 0</c> for an upward mouse
        /// move; <c>MapController</c> negates it before building the <see cref="MapRenderer.App.View.GestureIntent.TiltBy"/>
        /// intent, and <c>ApplyTiltDelta</c> adds <c>dy·sensitivity</c> to the current tilt.
        ///
        /// <para>So: +Y drag (drag up) → negate → TiltBy(dy = -positive * sensitivity) → tilt DECREASES.
        /// This is the headless pin for that direction; <c>MapController</c>'s comment and call site must
        /// agree (the final direction is a maintainer play-test call, noted in the stage).</para>
        ///
        /// <para>Starts from a non-zero tilt so the "pitch decreased" assertion is not vacuously
        /// clamped at 0.</para>
        /// </summary>
        [Test]
        public void D6_TiltYSign_DragUp_NewIS_TiltsTowardOverhead()
        {
            // Start tilted (30°) so a decrease is observable (not clamped at 0).
            var v = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, heading: 0.0, tilt: 30.0);

            // New IS: delta.y = +20 (drag up). MapController negates → dy_for_seam = -20.
            // Sensitivity 0.3 is applied by the caller before the intent is built (as in MapController).
            double newIsDeltaY = 20.0;
            double viewInputDy = -newIsDeltaY;

            var patch = MapRenderer.App.View.ViewInput.ApplyTiltDelta(
                v, tiltDeltaDeg: viewInputDy * 0.3, maxPitch: 60.0);

            Assert.Less(patch.Tilt.Value, v.Tilt.Degrees,
                "D6: a +Y drag (upward mouse move in the new Input System), after sign flip at the " +
                "MapController translator, must tilt the camera TOWARD overhead (pitch decreases). " +
                "Failure means MapController.Update passes the wrong sign to ViewInput.ApplyTiltDelta " +
                "(drag direction does not match the documented overhead-on-drag-up convention).");
        }

        /// <summary>
        /// Complementary: drag DOWN (delta.y = -20) → tilt toward horizon (pitch increases).
        /// Symmetric check for the same sign-flip convention.
        /// </summary>
        [Test]
        public void D6_TiltYSign_DragDown_NewIS_TiltsTowardHorizon()
        {
            var v = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, heading: 0.0, tilt: 30.0);

            // New IS: delta.y = -20 (drag down). Negate → +20 for the seam.
            double newIsDeltaY = -20.0;
            double viewInputDy = -newIsDeltaY;

            var patch = MapRenderer.App.View.ViewInput.ApplyTiltDelta(
                v, tiltDeltaDeg: viewInputDy * 0.3, maxPitch: 60.0);

            Assert.Greater(patch.Tilt.Value, v.Tilt.Degrees,
                "D6: a -Y drag (downward mouse move in the new Input System), after sign flip, must tilt " +
                "the camera TOWARD the horizon (pitch increases).");
        }

        // ── Test-local oracle ────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Zoom from altitude — the inverse of <see cref="CameraPoseMath.AltitudeForZoom"/>, kept HERE
        /// rather than in Core, where it had no production caller. Zoom is canonical and altitude
        /// derived, so the reverse conversion is only ever a test's question. Written against
        /// EarthConstants/WebMercator directly rather than through the forward function's own constants:
        /// an inverse derived independently is a STRONGER oracle than one that could drift alongside it.
        /// </summary>
        private static double ZoomForAltitude(double altitudeMetres, double viewportHeightPx, double verticalFovDeg)
        {
            double halfFovRad     = Angle.FromDegrees(verticalFovDeg * 0.5).Radians;
            double metersPerPixel = (2.0 * altitudeMetres * math.tan(halfFovRad)) / viewportHeightPx;
            // altitude = (vpH * mpp) / (2 * tan(fov/2))  →  mpp = altitude*2*tan(fov/2)/vpH
            // mpp = EarthCirc / (TilePx * 2^zoom)  →  zoom = log2(EarthCirc / (TilePx * mpp))
            if (metersPerPixel <= 0) return 0;
            return math.log2(EarthConstants.EquatorialCircumferenceMetres / (WebMercator.TilePixelSize * metersPerPixel));
        }
    }

// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference. Tests the two-way slider reconcile.



    [TestFixture]
    public class CameraSliderBindingTests
    {
        private static CameraProperties Cam(double zoom, double heading = 0.0, double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0 }, zoom, heading, tilt);

        // Idle fields/baseline that match the given camera exactly (same units everywhere now).
        private static SliderValues Idle(CameraProperties c)
            => new SliderValues { Zoom = c.Zoom, Tilt = c.Tilt.Degrees, Heading = c.Heading.Degrees };

        // ── Oscillation stability under feedback ─────────────────────────────────────────────────────
        [Test]
        public void B1_NoOscillation_WhenCameraSteady()
        {
            CameraProperties camera = Cam(zoom: 8.0, heading: 30.0, tilt: 20.0);
            SliderValues fields   = Idle(camera);
            SliderValues baseline = Idle(camera);

            for (int i = 0; i < 100; i++)
            {
                ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in camera);

                Assert.IsFalse(r.HasPatch, $"iteration {i}: a steady camera must emit no patch");
                Assert.That(r.Display.Zoom,    Is.EqualTo(8.0).Within(1e-4),  $"iteration {i}: Zoom drifted");
                Assert.That(r.Display.Tilt,    Is.EqualTo(20.0).Within(1e-3), $"iteration {i}: Tilt drifted");
                Assert.That(r.Display.Heading, Is.EqualTo(30.0).Within(1e-3), $"iteration {i}: Heading drifted");

                // Feed the loop back (the live two-way binding).
                fields   = r.Display;
                baseline = r.Baseline;
            }
        }

        // ── THE decisive test: does NOT fight a self-moving camera ───────────────────────────────────
        [Test]
        public void B2_DoesNotFightSelfMovingCamera()
        {
            CameraProperties before = Cam(zoom: 8.0, heading: 30.0, tilt: 20.0);
            SliderValues fields   = Idle(before);   // user idle
            SliderValues baseline = Idle(before);

            // The camera moves on its own (scroll-zoom + heading change), the panel having done nothing.
            CameraProperties after = Cam(zoom: 12.0, heading: 75.0, tilt: 20.0);

            ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in after);

            Assert.IsFalse(r.HasPatch, "a field-vs-camera guard would emit a stale revert-patch here");
            Assert.That(r.Display.Zoom,    Is.EqualTo(12.0).Within(1e-9), "Zoom must follow the camera");
            Assert.That(r.Display.Heading, Is.EqualTo(75.0).Within(1e-9), "Heading must follow the camera");
            Assert.That(r.Display.Tilt,    Is.EqualTo(20.0).Within(1e-9), "Tilt unchanged → follows the camera");
        }

        // ── Slider→camera survives ConstrainedAngle Wrap exactly ─────────────────────────────────────
        [Test]
        public void B3_HeadingEdit_WrapsExactly()
        {
            CameraProperties camera = Cam(zoom: 5.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = Idle(camera);

            SliderValues over = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = 370.0 };
            ReconcileResult r1 = CameraSliderBinding.Reconcile(in over, in baseline, in camera);
            Assert.IsTrue(r1.Patch.Heading.HasValue);
            Assert.That(r1.Patch.Heading.Value, Is.EqualTo(370.0), "raw value is passed to the patch");
            Assert.That(r1.Display.Heading,     Is.EqualTo(10.0),  "370 wraps to 10 exactly");

            SliderValues under = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = -10.0 };
            ReconcileResult r2 = CameraSliderBinding.Reconcile(in under, in baseline, in camera);
            Assert.That(r2.Display.Heading, Is.EqualTo(350.0), "-10 wraps to 350 exactly");
        }

        // ── Slider→camera survives ConstrainedAngle Clamp ────────────────────────────────────────────
        [Test]
        public void B4_TiltEdit_ClampsExactly()
        {
            CameraProperties camera = Cam(zoom: 5.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = Idle(camera);

            SliderValues over = new SliderValues { Zoom = 5.0, Tilt = 95.0, Heading = 0.0 };
            ReconcileResult r1 = CameraSliderBinding.Reconcile(in over, in baseline, in camera);
            Assert.That(r1.Display.Tilt, Is.EqualTo(90.0), "95 clamps to 90");

            SliderValues under = new SliderValues { Zoom = 5.0, Tilt = -5.0, Heading = 0.0 };
            ReconcileResult r2 = CameraSliderBinding.Reconcile(in under, in baseline, in camera);
            Assert.That(r2.Display.Tilt, Is.EqualTo(0.0), "-5 clamps to 0");
        }

        // ── Per-field isolation (no stomp) ───────────────────────────────────────────────────────────
        [Test]
        public void B5_PerFieldIsolation_OnlyEditedFieldEmitted()
        {
            // Baseline says zoom 5; the live camera is at zoom 10 (a concurrent camera change). The Zoom
            // field still matches its baseline (idle), so Zoom must NOT be emitted despite the camera diff.
            CameraProperties camera = Cam(zoom: 10.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = 0.0 };
            SliderValues fields   = new SliderValues { Zoom = 5.0, Tilt = 0.0, Heading = 90.0 };

            ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in camera);

            Assert.IsTrue(r.Patch.Heading.HasValue, "the edited Heading must be emitted");
            Assert.IsFalse(r.Patch.Zoom.HasValue, "the idle Zoom must NOT be emitted (no stomp)");
            Assert.IsFalse(r.Patch.Tilt.HasValue, "the idle Tilt must NOT be emitted");
        }

        // ── A Zoom edit is emitted as canonical zoom ─────────────────────────────────────────────────
        [Test]
        public void B6_ZoomEdit_EmittedAsCanonicalZoom()
        {
            CameraProperties camera = Cam(zoom: 8.0, heading: 0.0, tilt: 0.0);
            SliderValues baseline = Idle(camera);

            SliderValues fields = new SliderValues { Zoom = 11.5, Tilt = 0.0, Heading = 0.0 };
            ReconcileResult r = CameraSliderBinding.Reconcile(in fields, in baseline, in camera);

            Assert.IsTrue(r.Patch.Zoom.HasValue, "an edited Zoom must be emitted");
            Assert.That(r.Patch.Zoom.Value, Is.EqualTo(11.5).Within(1e-9), "Zoom is passed through as canonical zoom");
            Assert.That(r.Display.Zoom, Is.EqualTo(11.5).Within(1e-9));
        }
    }
}
