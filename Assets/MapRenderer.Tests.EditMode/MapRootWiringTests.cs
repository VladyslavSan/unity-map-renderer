// Unity EditMode only — tests the MapRoot.Wire() static entry point (S41 acceptance tooth 1).
// Verifies the wiring graph: MapController.Map != null, MapController.Camera == Camera.main,
// and MapView.IsInitialised after Wire(root, camera, source, ...).

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.View;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S41 wiring tests for <see cref="MapRoot.Wire"/>.
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
        /// MapController.Map != null, and MapView.IsInitialised.
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
            var source = new FixtureSource();
            var initialView = new ViewState(0.0, 20.0, 2.0);

            try
            {
                // Act.
                MapRoot.Wire(rootGo, cam, source, initialView, ownsSource: false, style: null);

                // Assert — tooth 1 of S41 acceptance:
                var ctrl    = rootGo.GetComponent<MapController>();
                var mapView = rootGo.GetComponent<MapView>();

                Assert.IsNotNull(ctrl.Map,    "MapController.Map must be set after Wire()");
                Assert.AreEqual(cam, ctrl.Camera,
                    "MapController.Camera must equal the camera passed to Wire()");
                Assert.IsTrue(mapView.IsInitialised,
                    "MapView must be initialised (Initialise() called) after Wire()");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
                Object.DestroyImmediate(cameraGo);
                source.Dispose();
            }
        }

        // ── (2) Missing camera — logs, no NRE, still wires MapView ──────────────────────────────

        /// <summary>
        /// Wire(root, null) must not throw. MapView is still initialised; MapController.Camera
        /// remains null (no camera to drive, but no crash either — D3 graceful handling).
        /// </summary>
        [Test]
        public void Wire_NullCamera_DoesNotThrow_MapViewStillInitialised()
        {
            var rootGo = new GameObject("MapRoot");
            rootGo.AddComponent<MapView>();
            rootGo.AddComponent<MapController>();
            var source = new FixtureSource();

            try
            {
                // Must not throw.
                Assert.DoesNotThrow(
                    () => MapRoot.Wire(rootGo, null, source, new ViewState(0, 0, 2), ownsSource: false, style: null),
                    "Wire(root, null) must not throw even when the camera is missing.");

                var ctrl    = rootGo.GetComponent<MapController>();
                var mapView = rootGo.GetComponent<MapView>();

                Assert.IsNull(ctrl.Camera, "MapController.Camera must stay null when no camera is provided");
                Assert.IsNotNull(ctrl.Map,  "MapController.Map must still be set");
                Assert.IsTrue(mapView.IsInitialised, "MapView must still be initialised with a null camera");
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
                    () => MapRoot.Wire(rootGo, null),
                    "Wire must not throw even when MapView is absent from the root.");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Minimal in-memory IDataSource that immediately returns empty (no-data) responses.</summary>
        private sealed class FixtureSource : IDataSource
        {
            public TileEncoding Encoding => TileEncoding.Mvt;

            public System.Threading.Tasks.Task<TileResponse> FetchAsync(
                TileId id,
                System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(TileResponse.Absent(TileEncoding.Mvt));

            public void Dispose() { }
        }
    }
}
