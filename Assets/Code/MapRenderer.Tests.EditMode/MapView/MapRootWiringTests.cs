// Unity EditMode only — tests the MapHost.Wire() static entry point (S41 acceptance tooth 1).
// Verifies the wiring graph: MapController.Map != null, MapController.Camera == Camera.main,
// and the MapView is built over the camera after Wire(root, camera, ...).

using NUnit.Framework;
using UnityEngine;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.View.Camera;
using MapController   = MapRenderer.App.Controller;
using TouchController = MapRenderer.App.TouchController;
using MapHost = MapRenderer.App.MapHost;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
namespace MapRenderer.Tests.MapViews
{
    /// <summary>
    /// S41 wiring tests for <see cref="MapHost.Wire"/>.
    ///
    /// These tests fail on the pre-S41 code-base (where Map was set only in Play via GetComponent
    /// on the same Camera GO — never tested). They are the regression guard for the "unset Map
    /// reference" class of bug.
    /// </summary>
    [TestFixture]
    public class MapRootWiringTests
    {
        // ── (1) Happy-path wiring ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wire(root, camera, source, view) sets MapController.Camera == the supplied camera,
        /// MapController.Map != null, and builds the MapView over the camera.
        /// </summary>
        [Test]
        public void Wire_HappyPath_SetsControllerFieldsAndInitialisesMapView()
        {
            // Arrange: a MapRoot GO carrying MapView + MapController.
            var rootGo = new GameObject("MapRoot");
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();

            // A plain camera GO tagged MainCamera (so Camera.main resolves to it).
            var cameraGo = new GameObject("MainCamera");
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();

            // A minimal in-memory data source.
            var source = TestDataSource.Absent();
            var initialView = new CameraProperties(new GeoCoordinate3D { Longitude = 0.0, Latitude = 20.0, Altitude = 0 }, 2.0, 0, 0);

            try
            {
                // Act.
                MapHost.Wire(rootGo, cam, initialView);

                // Assert — tooth 1 of S41 acceptance:
                var ctrl    = rootGo.GetComponent<MapController>();
                var mapView = rootGo.GetComponent<MapView>();

                Assert.IsNotNull(ctrl.Map,    "MapController.Map must be set after Wire()");
                Assert.AreEqual(cam, ctrl.Camera,
                    "MapController.Camera must equal the camera passed to Wire()");
                Assert.IsNotNull(mapView.Camera,
                    "Wire must build the MapView over the camera (data loads separately via SetStyle)");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(cameraGo);
                source.Dispose();
            }
        }

        // ── (2) Missing camera — logs, no NRE ────────────────────────────────────────────────────

        /// <summary>
        /// Wire(root, null) must not throw. A MapCamera requires a real camera, so with none the MapView
        /// is not built — but MapController.Map is still set and nothing crashes.
        /// (In practice the scene always has a main camera; this only pins the graceful no-camera path.)
        /// </summary>
        [Test]
        public void Wire_NullCamera_DoesNotThrow_NoMapBuilt()
        {
            var rootGo = new GameObject("MapRoot");
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();
            var source = TestDataSource.Absent();

            try
            {
                // Must not throw.
                Assert.DoesNotThrow(
                    () => MapHost.Wire(rootGo, null, new CameraProperties(new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, 2, 0, 0)),
                    "Wire(root, null) must not throw even when the camera is missing.");

                var ctrl    = rootGo.GetComponent<MapController>();
                var mapView = rootGo.GetComponent<MapView>();

                Assert.IsNull(ctrl.Camera, "MapController.Camera must stay null when no camera is provided");
                Assert.IsNotNull(ctrl.Map,  "MapController.Map must still be set");
                Assert.IsNull(mapView.View,
                    "with no camera the MapView is not built (a MapCamera requires a real camera) — but no crash");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                source.Dispose();
            }
        }

        // ── (3) Missing MapView — logs, no NRE ───────────────────────────────────────────────────

        [Test]
        public void Wire_MissingMapView_DoesNotThrow()
        {
            var rootGo = new GameObject("MapRoot");
            // Deliberately omit MapView — Wire should log and return gracefully.

            try
            {
                Assert.DoesNotThrow(
                    () => MapHost.Wire(rootGo, null),
                    "Wire must not throw even when MapView is absent from the root.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        // ── (4) S74 — TouchController wired by Wire() ────────────────────────────────────────────

        /// <summary>
        /// S74: Wire() must add (or find) a <see cref="TouchController"/> on root and set its
        /// Map and Camera fields — locks the wiring so touch can't silently un-wire.
        /// </summary>
        [Test]
        public void Wire_HappyPath_WiresTouchController()
        {
            var rootGo = new GameObject("MapRoot");
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();

            var cameraGo = new GameObject("MainCamera");
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();

            var source      = TestDataSource.Absent();
            var initialView = new CameraProperties(
                new GeoCoordinate3D { Longitude = 0.0, Latitude = 0.0, Altitude = 0 }, 2.0, 0, 0);

            try
            {
                MapHost.Wire(rootGo, cam, initialView);

                var touch = rootGo.GetComponent<TouchController>();
                Assert.IsNotNull(touch,    "S74: Wire() must add TouchController to root");
                Assert.IsNotNull(touch.Map, "S74: TouchController.Map must be set after Wire()");
                Assert.AreEqual(cam, touch.camera,
                    "S74: TouchController.camera must equal the camera passed to Wire()");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(cameraGo);
                source.Dispose();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    }
}
