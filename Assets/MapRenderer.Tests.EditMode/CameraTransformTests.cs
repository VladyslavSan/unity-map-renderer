// Unity EditMode only — tests MapController.ApplyCameraTransform and AltitudeForZoom (S42).
// Cannot live in Tools/core-tests (needs real Camera / UnityEngine types).
//
// Teeth covered:
//   Tooth 1 (DECISIVE): pitch=0 → camera above origin (y>0) looking straight down (Dot(fwd,down)>0.99)
//   Tooth 2: higher zoom → lower altitude; z2 altitude >> z16 altitude; pinned against D2 formula.
//   Tooth 3: farClipPlane > camera.position.y at low zoom (world not clipped).
//   D1 sign guard: pitch>0 tilts toward horizon; bearing rotates about +Y.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;
using MapController = MapRenderer.Unity.Rendering.Map.Controller;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
namespace MapRenderer.Tests
{
    [TestFixture]
    public class CameraTransformTests
    {
        // Shared deterministic viewport height so formula results are reproducible across machines.
        private const float TestViewportHeight = 1080f;
        private const float TestFovDeg         = 60f;

        private static CameraProperties Cam(double lon, double lat, double zoom,
                                            double heading = 0.0, double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, heading, tilt);

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a MapController + Camera pair wired together. Caller is responsible for
        /// DestroyImmediate(rootGo) and DestroyImmediate(camGo) in a finally block.
        /// </summary>
        private static (MapController ctrl, Camera cam, GameObject rootGo, GameObject camGo)
            CreatePair()
        {
            var rootGo = new GameObject("MapRoot_Test");
            rootGo.AddComponent<MapView>();
            var ctrl = rootGo.AddComponent<MapController>();

            var camGo = new GameObject("Camera_Test");
            var cam   = camGo.AddComponent<Camera>();
            // Deterministic viewport: the camera IS the viewport now (ViewportPx.y drives the altitude
            // formula), so a fixed-height RenderTexture reproduces the old ReferenceViewportHeightPx=1080.
            cam.targetTexture = new RenderTexture((int)TestViewportHeight, (int)TestViewportHeight, 0);

            ctrl.Camera                 = cam;
            ctrl.AltitudeMultiplier     = 1f;

            return (ctrl, cam, rootGo, camGo);
        }

        // ── Tooth 1 (DECISIVE) — overhead at pitch 0 ─────────────────────────────────────────────

        /// <summary>
        /// S42 tooth 1 (DECISIVE): at pitch=0 the camera must be directly above the origin (y>0)
        /// and look straight down (Vector3.Dot(forward, down) > 0.99).
        ///
        /// Fails on the pre-S42 code-base where Quaternion.Euler(90-pitch,…)*up at pitch=0 produces
        /// offset=(0,0,H), placing the camera at y=0 looking horizontally.
        /// </summary>
        [Test]
        public void PitchZero_CameraOverhead_LooksDown()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            try
            {
                var view = Cam(0.0, 0.0, 8.0, heading: 0.0, tilt: 0.0);
                ctrl.ApplyCameraTransform(view);

                Assert.Greater(cam.transform.position.y, 0f,
                    "Camera must be above the origin (position.y > 0) at pitch=0.");

                float dot = Vector3.Dot(cam.transform.forward, Vector3.down);
                Assert.Greater(dot, 0.99f,
                    $"Camera must look straight down at pitch=0. Dot(forward, down) = {dot:F4}, expected > 0.99.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Tooth 2 — zoom → altitude monotonic and magnitude-pinned ─────────────────────────────

        /// <summary>
        /// S42 tooth 2: higher zoom → lower altitude (y). Low zoom (~2) yields altitude orders of
        /// magnitude larger than high zoom (~16). Both are pinned against the D2 formula via the
        /// public static AltitudeForZoom method.
        /// </summary>
        [Test]
        public void HigherZoom_LowerAltitude_Monotonic_AndPinnedAgainstFormula()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            try
            {
                var viewLow  = Cam(0.0, 0.0, 2.0);
                var viewHigh = Cam(0.0, 0.0, 16.0);

                ctrl.ApplyCameraTransform(viewLow);
                float yLow = cam.transform.position.y;

                ctrl.ApplyCameraTransform(viewHigh);
                float yHigh = cam.transform.position.y;

                // Monotonicity: higher zoom → lower altitude.
                Assert.Greater(yLow, yHigh,
                    $"Zoom 2 altitude ({yLow:F1} m) must be greater than zoom 16 altitude ({yHigh:F1} m).");

                // Orders-of-magnitude difference: 2^14 ≈ 16384×
                float ratio = yLow / yHigh;
                Assert.Greater(ratio, 1000f,
                    $"z2/z16 altitude ratio must be > 1000 (2^14 ≈ 16384). Got {ratio:F1}.");

                // Pin against D2 formula (use the same AltitudeForZoom static that the runtime uses,
                // with relative tolerance to avoid float-precision drift on different machines).
                float expectedLow  = MapController.AltitudeForZoom(2.0,  TestViewportHeight, TestFovDeg);
                float expectedHigh = MapController.AltitudeForZoom(16.0, TestViewportHeight, TestFovDeg);

                // Relative tolerance: 0.1% — the formula is deterministic float; main risk is a
                // factor-of-2 or radians-vs-degrees error, not float rounding.
                float tolLow  = expectedLow  * 0.001f;
                float tolHigh = expectedHigh * 0.001f;
                Assert.AreEqual(expectedLow,  yLow,  tolLow,
                    $"z2 altitude {yLow:E4} must match AltitudeForZoom result {expectedLow:E4} (0.1% tol).");
                Assert.AreEqual(expectedHigh, yHigh, tolHigh,
                    $"z16 altitude {yHigh:E4} must match AltitudeForZoom result {expectedHigh:E4} (0.1% tol).");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>
        /// S42 tooth 2 (reinforcement): tests AltitudeForZoom in isolation against the hand-computed
        /// D2 value for zoom=10 / height=1080 / FOV=60°. Catches factor-of-2 or radians errors in
        /// the formula without needing a live camera.
        ///
        /// Hand computation:
        ///   metersPerPixel = 40075016.686 / (256 * 2^10) = 40075016.686 / 262144 ≈ 152.874
        ///   halfFovRad     = 30° * π/180 ≈ 0.52360
        ///   tan(halfFov)   ≈ 0.57735
        ///   altitude       = (1080 * 152.874) / (2 * 0.57735) ≈ 143053.7
        /// </summary>
        [Test]
        public void AltitudeForZoom_MatchesD2Formula_HandComputed()
        {
            const double zoom         = 10.0;
            const float  height       = 1080f;
            const float  fov          = 60f;

            // Hand-computed reference (double arithmetic to avoid float error in the reference itself).
            const double earthCirc    = 40075016.686;
            const double tileSize     = 256.0;
            double metersPerPixel     = earthCirc / (tileSize * System.Math.Pow(2.0, zoom));
            double halfFovRad         = fov * 0.5 * System.Math.PI / 180.0;
            float  expected           = (float)((height * metersPerPixel) / (2.0 * System.Math.Tan(halfFovRad)));

            float actual = MapController.AltitudeForZoom(zoom, height, fov);

            // Relative tolerance 0.01%: both compute the same float expression; only binary rounding matters.
            float tol = expected * 0.0001f;
            Assert.AreEqual(expected, actual, tol,
                $"AltitudeForZoom({zoom}, {height}, {fov}) = {actual:E6}, expected {expected:E6}.");
        }

        // ── Tooth 3 — clip planes contain the view at low zoom ────────────────────────────────────

        /// <summary>
        /// S42 tooth 3: at low zoom the far clip plane must exceed the camera's altitude so the world
        /// is not clipped. Also asserts near &lt; far and near &gt; 0.
        /// </summary>
        [Test]
        public void LowZoom_FarClipContainsView()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            try
            {
                var view = Cam(0.0, 0.0, 2.0);
                ctrl.ApplyCameraTransform(view);

                float altitude = cam.transform.position.y;

                Assert.Greater(cam.farClipPlane, altitude,
                    $"farClipPlane ({cam.farClipPlane:E4}) must be > camera altitude ({altitude:E4}) at zoom 2.");

                Assert.Greater(cam.nearClipPlane, 0f,
                    "nearClipPlane must be positive.");

                Assert.Less(cam.nearClipPlane, cam.farClipPlane,
                    "nearClipPlane must be less than farClipPlane.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── D1 sign guard — pitch tilts toward horizon, bearing rotates about +Y ─────────────────

        /// <summary>
        /// D1 sign guard: at pitch=45 the camera must still be above the origin (y>0) and must be
        /// tilted (Dot(forward,down) in (0, 0.99)) — not overhead (0.99) and not horizontal (≤0).
        /// This pins the pitch sign: a sign inversion would set the camera below the horizon.
        /// </summary>
        [Test]
        public void PitchNonZero_TiltsTowardHorizon_CameraStillAbove()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            try
            {
                var view = Cam(0.0, 0.0, 8.0, heading: 0.0, tilt: 45.0);
                ctrl.ApplyCameraTransform(view);

                Assert.Greater(cam.transform.position.y, 0f,
                    "Camera must still be above the origin at pitch=45.");

                float dot = Vector3.Dot(cam.transform.forward, Vector3.down);
                Assert.Greater(dot, 0f,
                    $"Camera forward must have a downward component at pitch=45. Dot={dot:F4}.");
                Assert.Less(dot, 0.99f,
                    $"Camera must NOT be straight down at pitch=45 (should be tilted). Dot={dot:F4}.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>
        /// D1 sign guard — bearing: at pitch=0 the camera is directly overhead, so bearing changes
        /// the camera's "up" direction (LookRotation) but does NOT move the camera's position (rotating
        /// (0,altitude,0) around Y is a no-op). At pitch>0, bearing does move the camera laterally.
        ///
        /// This test pins the behavior at pitch=45: changing bearing from 0 to 90 must produce a
        /// different x/z position while keeping the camera above the origin (y > 0).
        /// </summary>
        [Test]
        public void BearingWithPitch_CameraOrbitsLaterally_YStaysPositive()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            try
            {
                // Reference: bearing=0, pitch=45 → camera is above+in-front of origin
                var viewNorth = Cam(0.0, 0.0, 8.0, heading:  0.0, tilt: 45.0);
                ctrl.ApplyCameraTransform(viewNorth);
                float xNorth = cam.transform.position.x;
                float zNorth = cam.transform.position.z;
                float yNorth = cam.transform.position.y;

                // Rotate bearing 90°, same pitch
                var viewEast = Cam(0.0, 0.0, 8.0, heading: 90.0, tilt: 45.0);
                ctrl.ApplyCameraTransform(viewEast);
                float xEast = cam.transform.position.x;
                float zEast = cam.transform.position.z;
                float yEast = cam.transform.position.y;

                // y stays positive (camera above origin) in both cases.
                Assert.Greater(yNorth, 0f, "Camera must be above origin at bearing=0 pitch=45.");
                Assert.Greater(yEast,  0f, "Camera must be above origin at bearing=90 pitch=45.");

                // The x/z position must differ between the two bearings (bearing orbits the camera).
                float posDiff = Mathf.Abs(xEast - xNorth) + Mathf.Abs(zEast - zNorth);
                Assert.Greater(posDiff, yNorth * 0.1f,
                    $"Bearing change from 0→90 at pitch=45 must move the camera laterally. " +
                    $"Δ(x+z) = {posDiff:F2}, threshold = {yNorth * 0.1f:F2}.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Camera is set to perspective ──────────────────────────────────────────────────────────

        /// <summary>
        /// ApplyCameraTransform must force the camera to perspective mode, regardless of the scene
        /// asset's initial setting (MapDemo.unity has orthographic:1).
        /// </summary>
        [Test]
        public void ApplyCameraTransform_SetsCameraToPerspective()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            try
            {
                cam.orthographic = true; // simulate scene-asset default
                var view = Cam(0.0, 0.0, 8.0);
                ctrl.ApplyCameraTransform(view);

                Assert.IsFalse(cam.orthographic,
                    "ApplyCameraTransform must set Camera.orthographic = false.");
                Assert.AreEqual(TestFovDeg, cam.fieldOfView, 0.001f,
                    "Camera.fieldOfView must match VerticalFovDeg.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
