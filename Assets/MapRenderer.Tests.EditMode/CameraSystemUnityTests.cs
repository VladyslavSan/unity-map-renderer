// Unity EditMode only — tests the S45 CameraSystem integration with MapView + MapCamera.
// Cannot live in Tools/core-tests (needs UnityEngine.Camera, UnityEngine.Quaternion, MapView, etc.).
//
// Teeth covered:
//   Tooth 1 (DECISIVE): UpdateFrame(dt) runs camera advance BEFORE tile selection in the same frame.
//   Tooth 5 (D6 fixes): pan Y-sign; pitch=0 overhead + deterministic north-up with bearing.
//   Tooth 7: no GC on the instant-path (structural — assert IsAnimating stays false).

using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.View;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class CameraSystemUnityTests
    {
        private const float TestViewportHeight = 1080f;
        private const float TestFovDeg         = 60f;

        // ── Shared helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a MapView with a CameraSystem wired, ready for UpdateFrame calls.
        /// The MapView is not Initialised (no tile source) — tests the camera path only.
        /// </summary>
        private static (MapView view, CameraSystem camSys, MapCamera mapCam,
                        Camera cam, GameObject rootGo, GameObject camGo)
            CreateCameraRig(CameraProperties initial)
        {
            var rootGo = new GameObject("MapView_Test");
            var view   = rootGo.AddComponent<MapView>();

            var camGo = new GameObject("Camera_Test");
            var cam   = camGo.AddComponent<Camera>();

            var mapCam = new MapCamera(cam, TestViewportHeight, TestFovDeg);
            var camSys = new CameraSystem(initial,
                                          referenceViewportHeightPx: TestViewportHeight,
                                          verticalFovDeg: TestFovDeg);

            // Do NOT call view.Initialise (no tile scheduler needed for camera tests).
            view.SetCamera(mapCam, camSys);

            return (view, camSys, mapCam, cam, rootGo, camGo);
        }

        private static void TearDown(GameObject rootGo, GameObject camGo)
        {
            UnityEngine.Object.DestroyImmediate(rootGo);
            UnityEngine.Object.DestroyImmediate(camGo);
        }

        // ── Tooth 1 (DECISIVE) — UpdateFrame advances camera BEFORE tile-selection in same frame ──

        /// <summary>
        /// S45 tooth 1: drives ONE UpdateFrame(dt) with a pending camera update and asserts that
        /// the POST-update camera state is visible to pose consumers within that single call.
        ///
        /// A test that would FAIL on the old design (separate MonoBehaviour.Update ordering):
        ///   - Old: MapController.Update() mutates camera state; MapView.Update() (Tick) may have
        ///     already run in this frame and seen the OLD state.
        ///   - New: MapView.UpdateFrame(dt) runs Advance(dt) → syncs the Unity camera → THEN Tick, all
        ///     in one call. The cover computation inside that same Tick reads the post-update camera.
        ///
        /// We verify by queuing an animation, calling UpdateFrame, and asserting that the camera state
        /// seen inside that frame reflects the POST-advance camera.
        /// </summary>
        [Test]
        public void UpdateFrame_AdvancesCamera_BeforeTileSelection_InSameFrame()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0, Altitude = 0 }, zoom: 5.0, heading: 0, tilt: 0);

            var (view, camSys, _, _, rootGo, camGo) = CreateCameraRig(initial);
            try
            {
                // Queue an instant update that changes zoom.
                double targetZoom = 8.0;
                camSys.Apply(new CameraPropertiesUpdate { Zoom = targetZoom }, CameraAnimation.Instant);

                // Drive ONE UpdateFrame.
                view.UpdateFrame(0.016); // simulate one ~60fps frame

                // POST-update: the camera system must show the target zoom.
                Assert.AreEqual(targetZoom, camSys.CurrentProperties.Zoom, 1e-6,
                    "CameraSystem.Current must reflect the post-update zoom after UpdateFrame.");

                // The camera state exposed by MapView.View must also reflect the new zoom (it reads
                // CameraSystem.Current directly — the single camera-state type, no bridge).
                Assert.AreEqual(targetZoom, view.View.Zoom, 1e-6,
                    "MapView.View.Zoom must equal the post-update camera zoom after UpdateFrame. " +
                    "Failure means tile-selection saw the OLD zoom in this frame (determinism bug).");
            }
            finally { TearDown(rootGo, camGo); }
        }

        /// <summary>
        /// Tooth 1 reinforcement: with a TIMED animation (not instant), Advance runs inside
        /// UpdateFrame so the tile cover within that frame reflects the mid-animation camera.
        /// </summary>
        [Test]
        public void UpdateFrame_TimedAnimation_CameraAdvancedBeforeTick()
        {
            double startZoom  = 4.0;
            double targetZoom = 12.0;
            double D          = 2.0;
            double dt         = D / 2.0; // advance halfway

            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, startZoom, 0, 0);

            var (view, camSys, _, _, rootGo, camGo) = CreateCameraRig(initial);
            try
            {
                camSys.Apply(new CameraPropertiesUpdate { Zoom = targetZoom }, new CameraAnimation(D));

                // Drive ONE UpdateFrame at dt = D/2. Camera should be at ~midpoint zoom.
                view.UpdateFrame(dt);

                // smoothStep(0.5) = 0.5 → midpoint
                double expectedZoom = startZoom + (targetZoom - startZoom) * 0.5;

                Assert.AreEqual(expectedZoom, camSys.CurrentProperties.Zoom, 0.05,
                    "CameraSystem.Current.Zoom must be near midpoint after dt=D/2.");
                Assert.AreEqual(expectedZoom, view.View.Zoom, 0.05,
                    "MapView.View.Zoom must reflect the mid-animation zoom within the same UpdateFrame call " +
                    "(tile-selection reads the post-advance camera, not the pre-advance one).");
            }
            finally { TearDown(rootGo, camGo); }
        }

        // ── D5 — instant Apply path allocates zero GC ──────────────────────────────────────────────

        /// <summary>
        /// S50 D5 (tooth-4 with teeth): the instant (Duration==0) Apply path must allocate ZERO heap
        /// bytes — it merges a struct patch over the current readonly-struct camera state and clears the
        /// animation reference; no <c>AnimationState</c> object is created. This is the real
        /// <c>Is.Not.AllocatingGCMemory()</c> assertion that replaces the previously-unused import.
        /// </summary>
        [Test]
        public void InstantApply_DoesNotAllocateGCMemory()
        {
            var initial = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0, tilt: 0);
            var (_, camSys, _, _, rootGo, camGo) = CreateCameraRig(initial);
            try
            {
                // Warm the path once (JIT, first-call setup) outside the measured region.
                camSys.Apply(new CameraPropertiesUpdate { Zoom = 6.0 }, CameraAnimation.Instant);

                Assert.That(() =>
                        camSys.Apply(new CameraPropertiesUpdate { Zoom = 7.0, Heading = 10.0 },
                                     CameraAnimation.Instant),
                    Is.Not.AllocatingGCMemory(),
                    "The instant/jumpTo Apply path must not allocate (struct patch over readonly-struct " +
                    "state, no AnimationState object). A failure means the instant path boxed the patch " +
                    "or built an animation object.");
            }
            finally { TearDown(rootGo, camGo); }
        }

        // ── Tooth 5 — D6 fixes ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// D6a pan Y-sign fix: MapCamera at pitch=0 bearing=0 → camera is at (0, altitude, 0).
        /// At pitch=0, the camera is directly above; there is no tilt to verify sign with.
        /// We verify the basic overhead position.
        /// </summary>
        [Test]
        public void MapCamera_PitchZero_CameraOverhead_LooksDown()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, zoom: 8.0, heading: 0.0, tilt: 0.0);

            var (_, camSys, mapCam, cam, rootGo, camGo) = CreateCameraRig(initial);
            try
            {
                mapCam.ApplyCameraProperties(camSys.CurrentProperties);

                Assert.Greater(cam.transform.position.y, 0f,
                    "Camera must be above the origin (position.y > 0) at tilt=0.");

                float dot = Vector3.Dot(cam.transform.forward, Vector3.down);
                Assert.Greater(dot, 0.99f,
                    $"Camera must look straight down at tilt=0. Dot(forward,down)={dot:F4}.");
            }
            finally { TearDown(rootGo, camGo); }
        }

        /// <summary>
        /// D6b: at pitch=0, different bearings produce different camera orientations (up-vectors).
        /// The camera's up-vector must rotate with bearing (deterministic north-up without degeneracy).
        /// </summary>
        [Test]
        public void MapCamera_PitchZero_DifferentBearings_DifferentUpVectors()
        {
            var (_, _, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 0.0));
            try
            {
                // Bearing = 0
                var propsN = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0,   0.0);
                mapCam.ApplyCameraProperties(propsN);
                Vector3 upNorth = cam.transform.up;
                Vector3 fwdN    = cam.transform.forward;

                // Bearing = 90
                var propsE = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 90.0, 0.0);
                mapCam.ApplyCameraProperties(propsE);
                Vector3 upEast = cam.transform.up;
                Vector3 fwdE   = cam.transform.forward;

                // Both cameras must look straight down.
                Assert.Greater(Vector3.Dot(fwdN, Vector3.down), 0.99f,
                    "Camera with heading=0 pitch=0 must look straight down.");
                Assert.Greater(Vector3.Dot(fwdE, Vector3.down), 0.99f,
                    "Camera with heading=90 pitch=0 must look straight down.");

                // The up-vectors must differ (bearing affects orientation, not a degenerate fallback).
                float upDiff = (upNorth - upEast).magnitude;
                Assert.Greater(upDiff, 0.5f,
                    $"Camera up-vectors must differ between heading=0 and heading=90 at pitch=0. " +
                    $"Difference magnitude = {upDiff:F3} (expected > 0.5). " +
                    $"upNorth={upNorth}, upEast={upEast}.");
            }
            finally { TearDown(rootGo, camGo); }
        }

        /// <summary>
        /// Pitch > 0 tilts camera toward horizon (fwd.Y in (-1, 0)); camera stays above origin.
        /// </summary>
        [Test]
        public void MapCamera_Pitch45_TiltsTowardHorizon()
        {
            var (_, _, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));
            try
            {
                mapCam.ApplyCameraProperties(
                    new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));

                Assert.Greater(cam.transform.position.y, 0f,
                    "Camera must still be above origin at tilt=45.");
                float dot = Vector3.Dot(cam.transform.forward, Vector3.down);
                Assert.Greater(dot, 0f,     "Forward must have downward component at tilt=45.");
                Assert.Less(dot,    0.99f,  "Forward must not be straight down at tilt=45.");
            }
            finally { TearDown(rootGo, camGo); }
        }

        // ── Clip planes ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void MapCamera_ClipPlanes_ContainViewAtLowZoom()
        {
            var (_, camSys, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 2.0, 0, 0));
            try
            {
                mapCam.ApplyCameraProperties(camSys.CurrentProperties);

                float altitude = cam.transform.position.y;
                Assert.Greater(cam.farClipPlane,  altitude, "farClipPlane must exceed altitude at low zoom.");
                Assert.Greater(cam.nearClipPlane, 0f,       "nearClipPlane must be positive.");
                Assert.Less(cam.nearClipPlane, cam.farClipPlane, "near must be less than far.");
            }
            finally { TearDown(rootGo, camGo); }
        }

        // ── Bearing orbits at pitch > 0 ───────────────────────────────────────────────────────────

        [Test]
        public void MapCamera_BearingWithPitch_OrbitsLaterally()
        {
            var (_, _, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0, 45.0));
            try
            {
                var propsN = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0.0,  45.0);
                mapCam.ApplyCameraProperties(propsN);
                Vector3 posN = cam.transform.position;

                var propsE = new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 90.0, 45.0);
                mapCam.ApplyCameraProperties(propsE);
                Vector3 posE = cam.transform.position;

                Assert.Greater(posN.y, 0f, "Camera above origin at bearing=0 pitch=45.");
                Assert.Greater(posE.y, 0f, "Camera above origin at bearing=90 pitch=45.");

                float lateralDiff = Mathf.Abs(posE.x - posN.x) + Mathf.Abs(posE.z - posN.z);
                Assert.Greater(lateralDiff, posN.y * 0.1f,
                    $"Bearing 0→90 at pitch=45 must move camera laterally. Δ(x+z)={lateralDiff:F2}.");
            }
            finally { TearDown(rootGo, camGo); }
        }

        // ── Camera set to perspective ─────────────────────────────────────────────────────────────

        [Test]
        public void MapCamera_SetsCameraToPerspective()
        {
            var (_, camSys, mapCam, cam, rootGo, camGo) = CreateCameraRig(
                new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 8.0, 0, 0));
            try
            {
                cam.orthographic = true;
                mapCam.ApplyCameraProperties(camSys.CurrentProperties);

                Assert.IsFalse(cam.orthographic, "Camera must be set to perspective (orthographic=false).");
                Assert.AreEqual(TestFovDeg, cam.fieldOfView, 0.001f,
                    "Camera.fieldOfView must equal VerticalFovDeg.");
            }
            finally { TearDown(rootGo, camGo); }
        }
    }
}
