// Unity EditMode only — tests the merged MapCamera integration with MapView (S89).
// Cannot live in Tools/core-tests (needs UnityEngine.Camera, UnityEngine.Quaternion, MapView, etc.).
//
// Covered:
//   • MapView.Camera.CurrentProperties reflects the camera state (single camera-state type, no bridge).
//   • Instant Apply allocates zero GC (struct patch over the readonly-struct state).
//   • Pose / clip-plane / perspective correctness of the Unity camera transform (D6 fixes).

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class CameraSystemUnityTests : BaseTestFixture
    {
        private const float TestViewportHeight = 1080f;
        private const float TestFovDeg         = 60f;

        // ── Shared helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a MapView with a MapCamera wired, ready for camera tests. No tile source is wired —
        /// tests the camera path only.
        /// </summary>
        private static (MapView view, MapCamera mapCam, Camera cam, GameObject rootGo, GameObject camGo)
            CreateCameraRig(CameraProperties initial)
        {
            var rootGo = new GameObject("MapView_Test");
            var view   = rootGo.AddComponent<MapView>();

            var camGo = new GameObject("Camera_Test");
            var cam   = camGo.AddComponent<Camera>();

            var mapCam = new MapCamera(cam, initial);
            view.SetCamera(mapCam); // no tile source wired — no tile scheduler needed for camera tests

            return (view, mapCam, cam, rootGo, camGo);
        }

        // ── MapView.Camera.CurrentProperties reflects the camera state ──────────────────────────────

        /// <summary>
        /// Applying a patch to the camera is visible through <c>MapView.Camera.CurrentProperties</c> —
        /// MapView reads the camera's state directly (one camera-state type, no model↔binding bridge).
        /// </summary>
        [Test]
        public void CameraApply_UpdatesMapViewCurrentProperties()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0, Altitude = 0 }, zoom: 5.0, heading: 0, tilt: 0);

            var (view, mapCam, _, rootGo, camGo) = CreateCameraRig(initial);
            Track(rootGo);
            Track(camGo);
                mapCam.Apply(new CameraPropertiesUpdate { Zoom = 8.0 });

                Assert.AreEqual(8.0, view.Camera.CurrentProperties.Zoom, 1e-6,
                    "MapView.Camera.CurrentProperties must reflect the camera state after Apply.");
        }

        // ── Instant Apply path allocates zero GC ────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="MapCamera.Apply"/> merges a struct patch over the readonly-struct camera state and
        /// re-drives the transform (native calls, no managed allocation) — it must allocate ZERO heap bytes.
        /// </summary>
        [Test]
        public void InstantApply_DoesNotAllocateGCMemory()
        {
            var initial = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0, tilt: 0);
            var (_, mapCam, _, rootGo, camGo) = CreateCameraRig(initial);
            Track(rootGo);
            Track(camGo);
                // Warm the path once (JIT, first-call setup) outside the measured region.
                mapCam.Apply(new CameraPropertiesUpdate { Zoom = 6.0 });

                Assert.That(() => mapCam.Apply(new CameraPropertiesUpdate { Zoom = 7.0, Heading = 10.0 }),
                    Is.Not.AllocatingGCMemory(),
                    "Apply must not allocate (struct patch over readonly-struct state; native transform writes).");
        }

        // ── Pose — D6 fixes ─────────────────────────────────────────────────────────────────────────

        /// <summary>MapCamera at pitch=0 → camera overhead looking straight down.</summary>
        [Test]
        public void MapCamera_PitchZero_CameraOverhead_LooksDown()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom: 8.0, heading: 0.0, tilt: 0.0);

            var (_, mapCam, cam, rootGo, camGo) = CreateCameraRig(initial);
            Track(rootGo);
            Track(camGo);
                mapCam.SetProperties(initial);
                mapCam.SyncToCamera();

                Assert.Greater(cam.transform.position.y, 0f,
                    "Camera must be above the origin (position.y > 0) at tilt=0.");

                float dot = Vector3.Dot(cam.transform.forward, Vector3.down);
                Assert.Greater(dot, 0.99f,
                    $"Camera must look straight down at tilt=0. Dot(forward,down)={dot:F4}.");
        }

        /// <summary>
        /// D6b: at pitch=0, different bearings produce different camera orientations (up-vectors) —
        /// deterministic north-up without degeneracy.
        /// </summary>
        [Test]
        public void MapCamera_PitchZero_DifferentBearings_DifferentUpVectors()
        {
            var (_, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 0.0));
            Track(rootGo);
            Track(camGo);
                mapCam.SetProperties(new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 0.0));
                mapCam.SyncToCamera();
                Vector3 upNorth = cam.transform.up;
                Vector3 fwdN    = cam.transform.forward;

                mapCam.SetProperties(new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 90.0, 0.0));
                mapCam.SyncToCamera();
                Vector3 upEast = cam.transform.up;
                Vector3 fwdE   = cam.transform.forward;

                Assert.Greater(Vector3.Dot(fwdN, Vector3.down), 0.99f,
                    "Camera with heading=0 pitch=0 must look straight down.");
                Assert.Greater(Vector3.Dot(fwdE, Vector3.down), 0.99f,
                    "Camera with heading=90 pitch=0 must look straight down.");

                float upDiff = (upNorth - upEast).magnitude;
                Assert.Greater(upDiff, 0.5f,
                    $"Camera up-vectors must differ between heading=0 and heading=90 at pitch=0. " +
                    $"Difference magnitude = {upDiff:F3} (expected > 0.5). upNorth={upNorth}, upEast={upEast}.");
        }

        /// <summary>Pitch &gt; 0 tilts camera toward horizon (fwd.Y in (-1, 0)); camera stays above origin.</summary>
        [Test]
        public void MapCamera_Pitch45_TiltsTowardHorizon()
        {
            var (_, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));
            Track(rootGo);
            Track(camGo);
                mapCam.SetProperties(
                    new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));
                mapCam.SyncToCamera();

                Assert.Greater(cam.transform.position.y, 0f,
                    "Camera must still be above origin at tilt=45.");
                float dot = Vector3.Dot(cam.transform.forward, Vector3.down);
                Assert.Greater(dot, 0f,     "Forward must have downward component at tilt=45.");
                Assert.Less(dot,    0.99f,  "Forward must not be straight down at tilt=45.");
        }

        // ── Clip planes ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void MapCamera_ClipPlanes_ContainViewAtLowZoom()
        {
            var (_, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 2.0, 0, 0));
            Track(rootGo);
            Track(camGo);
                float altitude = cam.transform.position.y;
                Assert.Greater(cam.farClipPlane,  altitude, "farClipPlane must exceed altitude at low zoom.");
                Assert.Greater(cam.nearClipPlane, 0f,       "nearClipPlane must be positive.");
                Assert.Less(cam.nearClipPlane, cam.farClipPlane, "near must be less than far.");
        }

        // ── Bearing orbits at pitch > 0 ───────────────────────────────────────────────────────────

        [Test]
        public void MapCamera_BearingWithPitch_OrbitsLaterally()
        {
            var (_, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));
            Track(rootGo);
            Track(camGo);
                mapCam.SetProperties(new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));
                mapCam.SyncToCamera();
                Vector3 posN = cam.transform.position;

                mapCam.SetProperties(new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 90.0, 45.0));
                mapCam.SyncToCamera();
                Vector3 posE = cam.transform.position;

                Assert.Greater(posN.y, 0f, "Camera above origin at bearing=0 pitch=45.");
                Assert.Greater(posE.y, 0f, "Camera above origin at bearing=90 pitch=45.");

                float lateralDiff = Mathf.Abs(posE.x - posN.x) + Mathf.Abs(posE.z - posN.z);
                Assert.Greater(lateralDiff, posN.y * 0.1f,
                    $"Bearing 0→90 at pitch=45 must move camera laterally. Δ(x+z)={lateralDiff:F2}.");
        }

        // ── Stage U: floating-origin single owner ────────────────────────────────────────────────

        /// <summary>
        /// Stage U teeth: <see cref="MapCamera.CameraRelativePosition"/> is the single owner of the camera's
        /// pose relative to the floating origin — stored straight off <see cref="CameraPoseMath.ComputeRelativePose"/>'s
        /// <c>pos</c>, never read back off <c>Camera.transform.position</c>. A non-trivial pose (heading AND
        /// tilt both non-zero, so no axis degenerates to zero and a swapped/rounded value would show) proves:
        /// (1) <c>MapCamera.CameraRelativePosition</c> == the independently recomputed <c>ComputeRelativePose</c>
        /// <c>pos</c>, exactly (both are the same double3, no narrowing); (2) it matches
        /// <c>Camera.transform.position</c> within the float32-cast tolerance the transform assignment incurs;
        /// (3) <c>MapView.BuildSceneFrame</c> folds the SAME stored value into <c>SceneFrame.CameraRelativePosition</c>
        /// — no second pose computation, no round-trip. A shallow impl that re-derived or read the transform
        /// back would still pass every other test in this file but fail here.
        /// </summary>
        [Test]
        public void SyncToCamera_CameraRelativePosition_IsSingleOwner_NoRoundTrip()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 12.5, Latitude = 34.0, Altitude = 0 },
                zoom: 9.0, heading: 40.0, tilt: 25.0);

            var (view, mapCam, cam, rootGo, camGo) = CreateCameraRig(initial);
            Track(rootGo);
            Track(camGo);
                mapCam.SetProperties(initial);
                mapCam.SyncToCamera();

                // Independently recompute the expected relative pose the same way SyncToCamera does.
                double altitude = CameraPoseMath.AltitudeForZoom(
                    initial.Zoom, cam.pixelHeight, initial.VerticalFovDeg);
                CameraPoseMath.ComputeRelativePose(altitude, initial.Heading.Value, initial.Tilt.Value,
                    out double3 expectedPos, out _, out _);

                Assert.AreEqual(expectedPos, mapCam.CameraRelativePosition,
                    "MapCamera.CameraRelativePosition must equal ComputeRelativePose's pos exactly.");

                Assert.AreEqual((float)expectedPos.x, cam.transform.position.x, 1e-3f, "x vs. transform");
                Assert.AreEqual((float)expectedPos.y, cam.transform.position.y, 1e-3f, "y vs. transform");
                Assert.AreEqual((float)expectedPos.z, cam.transform.position.z, 1e-3f, "z vs. transform");

                // BuildSceneFrame folds the SAME stored value in — no second ComputeRelativePose call.
                var frame = view.View.BuildSceneFrame(mapCam.CurrentProperties);
                Assert.AreEqual(mapCam.CameraRelativePosition, frame.CameraRelativePosition,
                    "SceneFrame.CameraRelativePosition must be the same value MapCamera stored — single owner.");
        }

        // ── Camera set to perspective ─────────────────────────────────────────────────────────────

        [Test]
        public void MapCamera_SetsCameraToPerspective()
        {
            var (_, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0, 0));
            Track(rootGo);
            Track(camGo);
                cam.orthographic = true;
                mapCam.SetProperties(new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0, 0));
                mapCam.SyncToCamera();

                Assert.IsFalse(cam.orthographic, "Camera must be set to perspective (orthographic=false).");
                Assert.AreEqual(TestFovDeg, cam.fieldOfView, 0.001f,
                    "Camera.fieldOfView must equal VerticalFovDeg.");
        }
    }
}
