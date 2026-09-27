// Unity EditMode only — tests MapCamera's device-pixel-ratio and viewport-guard behaviour, plus the
// Controller entry point that constructs it.

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapController = MapRenderer.App.Controller;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapCamera = MapRenderer.Unity.Rendering.Map.MapCamera;
namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class CameraTransformTests : BaseTestFixture
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

        // ── Engine level — MapCamera frames the logical viewport ──────────────────

        /// <summary>
        /// <see cref="MapCamera"/> frames the LOGICAL viewport (vp ÷ DPR).
        /// At tilt=0/heading=0 the camera sits directly overhead, so <c>position.y ≡ altitude</c>. DPR=2 puts
        /// the camera at HALF the DPR=1 altitude (2× closer ⇒ map 2× bigger); DPR=1 is bit-identical to the
        /// raw <see cref="CameraPoseMath.AltitudeForZoom"/> — the guard that the change never leaked into the
        /// DPR=1 path that every other camera test runs on.
        /// </summary>
        [Test]
        public void MapCamera_DevicePixelRatio_HalvesAltitudeAt2x_IdenticalAt1x()
        {
            var camGo = Track(new GameObject("Camera_DprTest"));
            var cam   = camGo.AddComponent<Camera>();
            cam.targetTexture = new RenderTexture((int)TestViewportHeight, (int)TestViewportHeight, 0);
            {
                var view = Cam(0.0, 0.0, 8.0);
                double expected1 = CameraPoseMath.AltitudeForZoom(view.Zoom, cam.pixelHeight,       view.VerticalFovDeg);
                double expected2 = CameraPoseMath.AltitudeForZoom(view.Zoom, cam.pixelHeight / 2.0, view.VerticalFovDeg);

                // Each ctor seeds the transform via SyncToCamera, so position.y is set on construction.
                var dpr1 = new MapCamera(cam, view, 1f, null, 1.0);
                float y1 = cam.transform.position.y;
                var dpr2 = new MapCamera(cam, view, 1f, null, 2.0);
                float y2 = cam.transform.position.y;

                Assert.AreEqual(expected1, y1, expected1 * 1e-5, "DPR=1 altitude == raw AltitudeForZoom(vp)");
                Assert.AreEqual(expected2, y2, expected2 * 1e-5, "DPR=2 altitude == AltitudeForZoom(vp/2)");
                Assert.AreEqual(y1 / 2.0, y2, y1 * 1e-4, "DPR=2 altitude is HALF the DPR=1 altitude");
            }
        }

        /// <summary>
        /// At a non-positive <c>DevicePixelRatio</c>, <see cref="MapCamera.ViewportLogicalPx"/> must fall back to
        /// the dpr-1 value instead of returning ±∞. Nothing enforces a positive
        /// <c>MapViewConfig.DevicePixelRatio</c>, and an infinite logical viewport disables symbol collision and
        /// NaN-poisons the tile selector's frustum. RED against an unguarded <c>ViewportPx / DevicePixelRatio</c>.
        /// </summary>
        [Test]
        public void MapCamera_ViewportLogicalPx_IsFiniteAtNonPositiveDevicePixelRatio()
        {
            var camGo = Track(new GameObject("Camera_DprGuardTest"));
            var cam   = camGo.AddComponent<Camera>();
            cam.targetTexture = new RenderTexture((int)TestViewportHeight, (int)TestViewportHeight, 0);
            {
                var view = Cam(0.0, 0.0, 8.0);
                foreach (double ratio in new[] { 0.0, -2.0 })
                {
                    var mapCamera = new MapCamera(cam, view, 1f, null, ratio);
                    double2 logical = mapCamera.ViewportLogicalPx;

                    Assert.IsFalse(double.IsInfinity(logical.x) || double.IsNaN(logical.x)
                                || double.IsInfinity(logical.y) || double.IsNaN(logical.y),
                        $"ViewportLogicalPx must stay finite at a ratio of {ratio} — an infinite logical " +
                        "viewport makes label collision reject nothing and NaN-poisons the tile selector.");
                    Assert.AreEqual(mapCamera.ViewportPx.x, logical.x, 0.0,
                        $"a ratio of {ratio} degrades to 1, so the logical viewport IS the physical one.");
                    Assert.AreEqual(mapCamera.ViewportPx.y, logical.y, 0.0,
                        $"a ratio of {ratio} degrades to 1, so the logical viewport IS the physical one.");
                }

                // The positive control: the guard must not have flattened the real ratios.
                var dpr2 = new MapCamera(cam, view, 1f, null, 2.0);
                Assert.AreEqual(dpr2.ViewportPx.y / 2.0, dpr2.ViewportLogicalPx.y, 0.0,
                    "a usable ratio still divides — exactly, not via a reciprocal.");
            }
        }

        // ── Controller entry point smoke test ─────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="MapController.ApplyCameraTransform"/> only constructs a <see cref="MapCamera"/> with
        /// <see cref="MapController.AltitudeMultiplier"/>; every pose/formula/clip/perspective claim is
        /// pinned directly against <c>MapCamera</c> in CameraSystemUnityTests.cs and CameraStateTests.cs.
        /// This is the one thing only the Controller path catches: those direct tests all use a
        /// multiplier of 1, so a dropped multiplier would pass every one of them.
        /// </summary>
        [Test]
        public void ApplyCameraTransform_AppliesAltitudeMultiplierAndPerspective()
        {
            var (ctrl, cam, rootGo, camGo) = CreatePair();
            Track(rootGo);
            Track(camGo);
                ctrl.AltitudeMultiplier = 2f;
                cam.orthographic = true; // simulate scene-asset default
                var view = Cam(0.0, 0.0, 8.0);
                ctrl.ApplyCameraTransform(view);

                double expected = CameraPoseMath.AltitudeForZoom(view.Zoom, TestViewportHeight, TestFovDeg)
                    * ctrl.AltitudeMultiplier;
                Assert.AreEqual(expected, cam.transform.position.y, expected * 0.001,
                    $"ApplyCameraTransform must scale altitude by AltitudeMultiplier ({ctrl.AltitudeMultiplier}).");

                Assert.IsFalse(cam.orthographic,
                    "ApplyCameraTransform must set Camera.orthographic = false.");
        }
    }
}
