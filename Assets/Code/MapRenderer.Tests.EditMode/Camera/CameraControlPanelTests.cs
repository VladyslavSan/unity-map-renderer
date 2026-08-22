// Unity EditMode only — proves the S72 CameraControlPanel shuttle actually wires its serialized floats
// through CameraSliderBinding.Reconcile to the live camera's write seam. Cannot live in Tools/core-tests
// (needs the MonoBehaviour + MapView + CameraSystem rig). The reconcile *logic* is pinned headless by
// CameraSliderBindingTests; this asserts the MonoBehaviour glue (field → patch → Apply → field write-back).

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.App;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class CameraControlPanelTests
    {
        private const float TestViewportHeight = 1080f;
        private const float TestFovDeg         = 60f;

        private static (MapView view, GameObject rootGo, GameObject camGo)
            CreateRig(CameraProperties initial)
        {
            var rootGo = new GameObject("MapView_Test");
            var view   = rootGo.AddComponent<MapView>();

            var camGo  = new GameObject("Camera_Test");
            var cam    = camGo.AddComponent<Camera>();

            view.SetCamera(new MapCamera(cam, initial));
            return (view, rootGo, camGo);
        }

        [Test]
        public void HeadingSliderEdit_ReachesLiveCamera()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0);
            var (view, rootGo, camGo) = CreateRig(initial);
            try
            {
                var panel = rootGo.AddComponent<CameraControlPanel>();
                panel.Map = view;

                // Frame 1: seeds the baseline from the camera and pulls the sliders to it (no edit yet).
                panel.Tick();
                Assert.That(view.Camera.CurrentProperties.Heading.Degrees, Is.EqualTo(0.0).Within(1e-6),
                    "seeding frame must not move the camera");

                // The user drags Heading to 90; next Tick must push it through to the live camera.
                panel.Heading = 90f;
                panel.Tick();
                Assert.That(view.Camera.CurrentProperties.Heading.Degrees, Is.EqualTo(90.0).Within(1e-3),
                    "a Heading slider edit must reach the live camera via the write seam");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void DebugReadouts_FollowTheCamera()
        {
            var initial = new CameraProperties(
                new GeoCoordinate3D { Latitude = 52.52, Longitude = 13.405 }, zoom: 10.0, heading: 0.0, tilt: 0.0);
            var (view, rootGo, camGo) = CreateRig(initial);
            try
            {
                var panel = rootGo.AddComponent<CameraControlPanel>();
                panel.Map = view;
                panel.Tick();

                double expectedMetres = MapRenderer.Core.View.Camera.CameraPoseMath.AltitudeForZoom(
                    10.0, view.Camera.ViewportPx.y, view.Camera.CurrentProperties.VerticalFovDeg);

                Assert.That(panel.Distance,  Is.EqualTo(expectedMetres).Within(1.0),
                    "Distance readout must be the metres view of the camera's zoom.");
                Assert.That(panel.Latitude,  Is.EqualTo(52.52).Within(1e-9),  "Latitude readout follows the look-at.");
                Assert.That(panel.Longitude, Is.EqualTo(13.405).Within(1e-9), "Longitude readout follows the look-at.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void UnwiredPanel_NoOpsCleanly()
        {
            var rootGo = new GameObject("Panel_Test");
            try
            {
                var panel = rootGo.AddComponent<CameraControlPanel>();
                // Map is null (edit-mode / pre-wire): Tick must not throw.
                Assert.DoesNotThrow(() => panel.Tick());
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }
    }
}
