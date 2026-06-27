// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
//
// Tests S45 CameraProperties, CameraPropertiesUpdate (patch semantics), CameraSystem (animation +
// interpolation + instant path), CameraPoseMath (altitude formula + inverse + pose + D6 fixes).

using System;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class CameraPropertiesTests
    {
        private const double TestViewportHeight = 1080.0;
        private const double TestFovDeg         = 60.0;

        // ── Patch semantics (S45 acceptance tooth 3) ─────────────────────────────────────────────

        /// <summary>
        /// A patch with only Tilt set changes ONLY tilt; all other fields (Zoom, Heading, LookAt) hold.
        /// </summary>
        [Test]
        public void Patch_TiltOnly_ChangesOnlyTilt()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 13.4, Latitude = 52.5, Altitude = 0 }, zoom: 10.0, heading: 45.0, tilt: 0.0);

            var patch = new CameraPropertiesUpdate { Tilt = 25.0 };
            CameraProperties result = patch.ApplyTo(initial, TestViewportHeight, TestFovDeg);

            Assert.AreEqual(25.0, result.Tilt.Degrees,    1e-9, "Tilt must be updated to 25.");
            Assert.AreEqual(10.0, result.Zoom,           1e-9, "Zoom must be unchanged.");
            Assert.AreEqual(45.0, result.Heading.Degrees, 1e-9, "Heading must be unchanged.");
            Assert.AreEqual(13.4, result.LookAt.Longitude,   1e-9, "Lon must be unchanged.");
            Assert.AreEqual(52.5, result.LookAt.Latitude,   1e-9, "Lat must be unchanged.");
        }

        /// <summary>
        /// Zoom and Distance must set the same canonical state (round-trip equal via D1).
        /// </summary>
        [Test]
        public void Patch_ZoomAndDistance_SameCanonicalState()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0, tilt: 0);

            double targetZoom = 10.0;
            double targetAlt  = CameraPoseMath.AltitudeForZoom(targetZoom, TestViewportHeight, TestFovDeg);

            var patchZoom = new CameraPropertiesUpdate { Zoom = targetZoom };
            var patchDist = new CameraPropertiesUpdate { Distance = targetAlt };

            CameraProperties fromZoom = patchZoom.ApplyTo(initial, TestViewportHeight, TestFovDeg);
            CameraProperties fromDist = patchDist.ApplyTo(initial, TestViewportHeight, TestFovDeg);

            // Round-trip tolerance: inverse is not exact to double precision but must be well within 0.1%.
            Assert.AreEqual(fromZoom.Zoom, fromDist.Zoom, targetZoom * 0.001,
                "Setting Zoom and setting the equivalent Distance must produce the same canonical zoom.");
        }

        /// <summary>Empty patch is a no-op.</summary>
        [Test]
        public void Patch_Empty_IsNoOp()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 1.0, Latitude = 2.0, Altitude = 3.0 }, zoom: 7.5, heading: 30.0, tilt: 15.0);

            var patch = new CameraPropertiesUpdate();
            CameraProperties result = patch.ApplyTo(initial, TestViewportHeight, TestFovDeg);

            Assert.AreEqual(initial.LookAt.Longitude, result.LookAt.Longitude, 1e-9);
            Assert.AreEqual(initial.LookAt.Latitude, result.LookAt.Latitude, 1e-9);
            Assert.AreEqual(initial.Zoom,        result.Zoom,       1e-9);
            Assert.AreEqual(initial.Heading.Degrees, result.Heading.Degrees, 1e-9);
            Assert.AreEqual(initial.Tilt.Degrees,   result.Tilt.Degrees,   1e-9);
        }

        // ── Animation interpolation (S45 acceptance tooth 2) ─────────────────────────────────────

        /// <summary>
        /// Apply({Zoom=target}, Duration=D) then Advance(D/2) lands halfway in ZOOM-SPACE.
        /// </summary>
        [Test]
        public void Animation_HalfwayAdvance_LandsHalfwayInZoomSpace()
        {
            double startZoom  = 4.0;
            double targetZoom = 12.0;
            double D          = 2.0; // seconds

            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, startZoom, 0, 0),
                TestViewportHeight, TestFovDeg);

            sys.Apply(new CameraPropertiesUpdate { Zoom = targetZoom }, new CameraAnimation(D));
            Assert.IsTrue(sys.IsAnimating, "Animation must be in flight.");

            // Advance by half the duration.
            sys.Update(D / 2.0);

            // At t=0.5, smoothStep(0.5) = 0.5^2 * (3 - 2*0.5) = 0.25 * 2 = 0.5
            // So the eased t is also 0.5 at t_linear=0.5 for smooth-step.
            double expectedZoom = startZoom + (targetZoom - startZoom) * 0.5;

            Assert.AreEqual(expectedZoom, sys.CurrentProperties.Zoom, 0.01,
                $"At D/2, zoom must be halfway between {startZoom} and {targetZoom} in zoom-space.");
            Assert.IsTrue(sys.IsAnimating, "Animation must still be in flight at halfway.");
        }

        /// <summary>
        /// Advance(D) reaches target and IsAnimating clears.
        /// </summary>
        [Test]
        public void Animation_FullAdvance_ReachesTargetAndClears()
        {
            double startZoom  = 2.0;
            double targetZoom = 14.0;
            double D          = 1.0;

            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, startZoom, 0, 0),
                TestViewportHeight, TestFovDeg);

            sys.Apply(new CameraPropertiesUpdate { Zoom = targetZoom }, new CameraAnimation(D));
            sys.Update(D); // full duration

            Assert.IsFalse(sys.IsAnimating, "IsAnimating must clear after full duration.");
            Assert.AreEqual(targetZoom, sys.CurrentProperties.Zoom, 1e-9,
                "Zoom must exactly equal target after full duration.");
        }

        /// <summary>
        /// Heading eases the short way across the 360° wrap: 350° → 10° takes +20°, not −340°.
        /// </summary>
        [Test]
        public void Animation_HeadingShortestPath_AcrossWrap()
        {
            double startHeading  = 350.0;
            double targetHeading = 10.0;
            double D             = 1.0;

            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, startHeading, 0),
                TestViewportHeight, TestFovDeg);

            sys.Apply(new CameraPropertiesUpdate { Heading = targetHeading }, new CameraAnimation(D));
            sys.Update(D); // full duration

            // Must reach 10° by the +20° path.
            Assert.AreEqual(10.0, sys.CurrentProperties.Heading.Degrees, 1.0,
                "Heading must ease 350→10 via the short +20° path, landing at 10°.");
        }

        /// <summary>
        /// Heading eases the short way at the midpoint: 350° → 10° at t=0.5 should be near 0° (≡360°).
        /// </summary>
        [Test]
        public void Animation_HeadingShortestPath_MidpointNearZero()
        {
            double D = 2.0;
            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 350.0, 0),
                TestViewportHeight, TestFovDeg);

            sys.Apply(new CameraPropertiesUpdate { Heading = 10.0 }, new CameraAnimation(D));
            sys.Update(D / 2.0);

            // At t=0.5, eased t=0.5, heading = 350 + 20*0.5 = 360 ≡ 0.
            double heading = sys.CurrentProperties.Heading.Degrees;
            bool nearZeroOrFull = heading < 5.0 || heading > 355.0;
            Assert.IsTrue(nearZeroOrFull,
                $"At 50% progress, 350→10 heading must be near 0°/360° (short path). Got {heading:F2}°.");
        }

        // ── Instant path / no per-frame GC (S45 acceptance tooth 4) ──────────────────────────────

        /// <summary>
        /// Duration==0 path: Apply with Instant animation sets current props, IsAnimating stays false.
        /// (Structural review that the instant path takes no animation object.)
        /// </summary>
        [Test]
        public void InstantApply_SetsCurrentProps_NoAnimation()
        {
            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 0, 0),
                TestViewportHeight, TestFovDeg);

            Assert.IsFalse(sys.IsAnimating, "No animation on fresh CameraSystem.");

            sys.Apply(new CameraPropertiesUpdate { Zoom = 8.0 }, CameraAnimation.Instant);

            Assert.IsFalse(sys.IsAnimating, "No animation after instant Apply.");
            Assert.AreEqual(8.0, sys.CurrentProperties.Zoom, 1e-9, "Zoom must be set immediately.");
        }

        // ── Altitude formula (S45 non-regression, absorbed from CameraTransformTests) ─────────────

        /// <summary>
        /// CameraPoseMath.AltitudeForZoom matches the hand-computed D2 value at zoom 10.
        /// Hand: metersPerPixel = 40075016.686 / (256 * 2^10) ≈ 152.874; alt ≈ 143053.7
        /// </summary>
        [Test]
        public void AltitudeForZoom_MatchesHandComputedValue()
        {
            const double zoom   = 10.0;
            const double height = 1080.0;
            const double fov    = 60.0;

            const double earthCirc = 40075016.686;
            const double tileSize  = 256.0;
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
        /// ZoomForDistance is the inverse of AltitudeForZoom (round-trip tolerance 0.1%).
        /// </summary>
        [Test]
        public void ZoomForDistance_IsInverseOfAltitudeForZoom()
        {
            for (double zoom = 1.0; zoom <= 16.0; zoom += 3.0)
            {
                double alt       = CameraPoseMath.AltitudeForZoom(zoom, TestViewportHeight, TestFovDeg);
                double roundTrip = CameraPoseMath.ZoomForDistance(alt, TestViewportHeight, TestFovDeg);
                Assert.AreEqual(zoom, roundTrip, zoom * 0.001,
                    $"ZoomForDistance(AltitudeForZoom({zoom})) must round-trip back to {zoom}.");
            }
        }

        // ── Pose math — overhead at pitch=0 (D6b degeneracy fix) ─────────────────────────────────

        /// <summary>
        /// At tilt=0: camera position is (0, altitude, 0) — directly above; forward is (0, -1, 0).
        /// The up-vector is derived from heading (non-degenerate even at pitch=0).
        /// </summary>
        [Test]
        public void Pose_PitchZero_CameraOverhead_LooksDown()
        {
            double altitude = 10000.0;
            CameraPoseMath.ComputePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                out double3 pos, out double3 fwd, out double3 up);

            Assert.AreEqual(0.0,      pos.x, 1e-6, "At pitch=0, X must be 0 (directly above).");
            Assert.AreEqual(altitude, pos.y, 1e-6, "At pitch=0, Y must equal altitude.");
            Assert.AreEqual(0.0,      pos.z, 1e-6, "At pitch=0, Z must be 0 (directly above).");

            Assert.AreEqual( 0.0, fwd.x, 1e-6, "Forward X must be 0 at pitch=0 heading=0.");
            Assert.AreEqual(-1.0, fwd.y, 1e-6, "Forward Y must be -1 (looking straight down).");
            Assert.AreEqual( 0.0, fwd.z, 1e-6, "Forward Z must be 0 at pitch=0 heading=0.");
        }

        /// <summary>
        /// D6b: at pitch=0, bearing changes the camera up-vector direction (deterministic north-up).
        /// The up-vector at heading=0 must differ from that at heading=90.
        /// </summary>
        [Test]
        public void Pose_PitchZero_BearingAffectsUpVector()
        {
            double altitude = 10000.0;

            CameraPoseMath.ComputePose(altitude, Angle.FromDegrees(0.0),  Angle.FromDegrees(0.0), out _, out _, out double3 upNorth);
            CameraPoseMath.ComputePose(altitude, Angle.FromDegrees(90.0), Angle.FromDegrees(0.0), out _, out _, out double3 upEast);

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
            CameraPoseMath.ComputePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(45.0),
                out double3 pos, out double3 fwd, out _);

            Assert.Greater(pos.y, 0.0, "Camera must be above origin (pos.y > 0) at tilt=45.");
            Assert.Less(fwd.y,    0.0, "Forward Y must be negative (pointing downward component) at tilt=45.");
            Assert.Greater(fwd.y, -1.0, "Forward Y must be > -1 (tilted, not straight down) at tilt=45.");
        }

        // ── Heading interpolation: shortest-angle (D4) ────────────────────────────────────────────

        [Test]
        public void LerpHeading_ShortestPath_AcrossZero()
        {
            // 350 → 10: shortest is +20 (not −340)
            double half = CameraPoseMath.LerpHeadingShortest(350.0, 10.0, 0.5);
            // At t=0.5, diff=+20, heading = 350+10 = 360 ≡ 0
            bool nearZero = half < 5.0 || half > 355.0;
            Assert.IsTrue(nearZero, $"350→10 at t=0.5 should be near 0°. Got {half:F2}°.");
        }

        [Test]
        public void LerpHeading_ShortestPath_Full()
        {
            double full = CameraPoseMath.LerpHeadingShortest(350.0, 10.0, 1.0);
            Assert.AreEqual(10.0, full, 1e-6, "350→10 at t=1.0 must land at 10°.");
        }

        // ── D7 Interruption: new Apply replaces in-flight animation from current value ─────────────

        [Test]
        public void Interruption_ReplacesAnimation_StartsFromCurrentValue()
        {
            double startZoom  = 2.0;
            double midTarget  = 14.0;
            double D          = 4.0;

            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, startZoom, 0, 0),
                TestViewportHeight, TestFovDeg);

            // Start an animation toward zoom 14.
            sys.Apply(new CameraPropertiesUpdate { Zoom = midTarget }, new CameraAnimation(D));
            sys.Update(D / 4.0); // advance 25% → zoom ≈ 5.0 (smoothStep(0.25) = 0.15625 → 2+12*0.15625≈3.875)

            double zoomAtInterrupt = sys.CurrentProperties.Zoom;
            Assert.Greater(zoomAtInterrupt, startZoom, "Zoom must have increased from start.");
            Assert.Less(zoomAtInterrupt,    midTarget,  "Zoom must not have reached target yet.");

            // Interrupt: apply new animation from current position to zoom 1.
            sys.Apply(new CameraPropertiesUpdate { Zoom = 1.0 }, new CameraAnimation(1.0));
            Assert.IsTrue(sys.IsAnimating, "New animation must be in flight after interruption.");

            // The starting point of the new animation must be the current (interpolated) zoom.
            // Advancing a tiny bit: zoom must be very close to zoomAtInterrupt (not jump to startZoom).
            sys.Update(0.01);
            Assert.AreEqual(zoomAtInterrupt, sys.CurrentProperties.Zoom, 0.1,
                "After interruption, zoom must start from current interpolated value (not the original start).");
        }

        // ── Completion callback ───────────────────────────────────────────────────────────────────

        [Test]
        public void OnAnimationComplete_CalledOnce_WhenAnimationFinishes()
        {
            int callCount = 0;
            var sys = new CameraSystem(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 5.0, 0, 0),
                TestViewportHeight, TestFovDeg);
            sys.OnAnimationComplete = () => callCount++;

            sys.Apply(new CameraPropertiesUpdate { Zoom = 10.0 }, new CameraAnimation(1.0));
            sys.Update(1.0); // completes the animation

            Assert.AreEqual(1, callCount, "OnAnimationComplete must be called exactly once.");
            Assert.IsFalse(sys.IsAnimating, "IsAnimating must clear after completion.");
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

        // ── D6a pan Y-sign regression (S45 tooth 5, updated for S63 anchored pan) ────────────────

        /// <summary>
        /// D6a: pins the "content follows cursor" pan direction using the S63 anchored-pan API.
        /// Screen convention: +x right, +y up, origin bottom-left (Unity mouse position).
        /// MapController (S63) no longer negates delta.y — the anchored pan handles direction correctly.
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

            var patch = MapRenderer.Core.View.ViewInput.ApplyPan(proj, v, grabbed, cursorNow, vp);

            Assert.Less(patch.Latitude.Value, v.LookAt.Latitude,
                "D6a (S63): cursor UP in +y-up convention must move the LookAt centre SOUTH " +
                "(grabbed point glues to cursor above centre; content follows cursor correctly).");
        }

        /// <summary>
        /// D6a complementary: cursor moves DOWN (−y in +y-up convention) → centre moves NORTH.
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

            var patch = MapRenderer.Core.View.ViewInput.ApplyPan(proj, v, grabbed, cursorNow, vp);

            Assert.Greater(patch.Latitude.Value, v.LookAt.Latitude,
                "D6a (S63): cursor DOWN in +y-up convention must move the LookAt centre NORTH.");
        }

        // ── D6 tilt-Y sign convention (S50 tooth 6) ──────────────────────────────────────────────

        /// <summary>
        /// D6 (S50): pins the right-drag tilt-Y sign convention — the same sign-flip class as the D6a pan.
        ///
        /// <para><b>Chosen convention (maintainer default, per the S50 ticket):</b> drag-UP tilts the
        /// camera toward overhead (pitch DECREASES), matching "content follows cursor." The new Input
        /// System reports <c>delta.y &gt; 0</c> for an upward mouse move; <c>MapController</c> negates it
        /// before calling <see cref="MapRenderer.Core.View.ViewInput.ApplyTilt"/> (exactly like pan), and
        /// <c>ApplyTilt</c> adds <c>dy·sensitivity</c> to the current tilt.</para>
        ///
        /// <para>So: +Y drag (drag up) → negate → ApplyTilt(dy = -positive) → tilt DECREASES.
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

            // New IS: delta.y = +20 (drag up). MapController negates → dy_for_ViewInput = -20.
            double newIsDeltaY = 20.0;
            double viewInputDy = -newIsDeltaY;

            var patch = MapRenderer.Core.View.ViewInput.ApplyTilt(
                v, dxPixels: 0.0, dyPixels: viewInputDy,
                bearingSensitivity: 0.3, pitchSensitivity: 0.3, maxPitch: 60.0);

            Assert.Less(patch.Tilt.Value, v.Tilt.Degrees,
                "D6: a +Y drag (upward mouse move in the new Input System), after sign flip at the " +
                "MapController translator, must tilt the camera TOWARD overhead (pitch decreases). " +
                "Failure means MapController.Update passes the wrong sign to ViewInput.ApplyTilt " +
                "(drag direction does not match the documented overhead-on-drag-up convention).");
        }

        /// <summary>
        /// D6 complementary: drag DOWN (new IS: delta.y = -20) → tilt toward horizon (pitch increases).
        /// Symmetric check for the same sign-flip convention.
        /// </summary>
        [Test]
        public void D6_TiltYSign_DragDown_NewIS_TiltsTowardHorizon()
        {
            var v = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, heading: 0.0, tilt: 30.0);

            // New IS: delta.y = -20 (drag down). Negate → +20 for ViewInput.
            double newIsDeltaY = -20.0;
            double viewInputDy = -newIsDeltaY;

            var patch = MapRenderer.Core.View.ViewInput.ApplyTilt(
                v, dxPixels: 0.0, dyPixels: viewInputDy,
                bearingSensitivity: 0.3, pitchSensitivity: 0.3, maxPitch: 60.0);

            Assert.Greater(patch.Tilt.Value, v.Tilt.Degrees,
                "D6: a -Y drag (downward mouse move in the new Input System), after sign flip, must tilt " +
                "the camera TOWARD the horizon (pitch increases).");
        }
    }
}
