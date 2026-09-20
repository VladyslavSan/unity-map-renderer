// Tests for the TileLoadStressDriver debug harness: the pure motion math (ZoomAt triangle wave + LookAtAt
// circular orbit) and the wired live-plumbing (Tick pushes the swept zoom + orbited look-at onto the real
// MapCamera; disabled/unwired are clean no-ops).

using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.App;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TileLoadStressDriverTests
    {
        private const double Tol = 1e-9;

        // ── Pure zoom math: ZoomAt triangle wave ─────────────────────────────────────────────────────

        [Test]
        public void ZoomAt_PhaseZero_IsMin()
            => Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(0.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_HalfPeriod_IsMax()
            => Assert.AreEqual(14.0, TileLoadStressDriver.ZoomAt(4.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_FullPeriod_WrapsBackToMin()
            => Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(8.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_QuarterPeriod_IsMidpointRising()
            => Assert.AreEqual(9.0, TileLoadStressDriver.ZoomAt(2.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_ThreeQuarterPeriod_IsMidpointFalling()
            => Assert.AreEqual(9.0, TileLoadStressDriver.ZoomAt(6.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_NonPositivePeriod_PinsToMin()
        {
            Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(3.7, 4.0, 14.0, 0.0), Tol);
            Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(3.7, 4.0, 14.0, -5.0), Tol);
        }

        [Test]
        public void ZoomAt_ReversedRange_IsSwapped()
        {
            Assert.AreEqual(4.0,  TileLoadStressDriver.ZoomAt(0.0, 14.0, 4.0, 8.0), Tol);
            Assert.AreEqual(14.0, TileLoadStressDriver.ZoomAt(4.0, 14.0, 4.0, 8.0), Tol);
        }

        [Test]
        public void ZoomAt_StaysWithinRange_OverAFullSweep()
        {
            for (int i = 0; i <= 100; i++)
            {
                double z = TileLoadStressDriver.ZoomAt(i * 0.16, 4.0, 14.0, 8.0);
                Assert.GreaterOrEqual(z, 4.0 - Tol);
                Assert.LessOrEqual(z, 14.0 + Tol);
            }
        }

        // ── Pure pan math: LookAtAt circular orbit ───────────────────────────────────────────────────
        // Centre at the equator (cos(lat)=1) so the longitude scaling is 1 and the orbit is exact.

        [Test]
        public void LookAtAt_PhaseZero_IsDueEastOfCentre()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(0.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(0.0,  lat, 1e-9);
            Assert.AreEqual(10.0, lon, 1e-9);
        }

        [Test]
        public void LookAtAt_QuarterPeriod_IsDueNorth()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(2.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(10.0, lat, 1e-9);
            Assert.AreEqual(0.0,  lon, 1e-9);
        }

        [Test]
        public void LookAtAt_HalfPeriod_IsDueWest()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(4.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(0.0,   lat, 1e-9);
            Assert.AreEqual(-10.0, lon, 1e-9);
        }

        [Test]
        public void LookAtAt_FullPeriod_WrapsBackToStart()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(8.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(0.0,  lat, 1e-9);
            Assert.AreEqual(10.0, lon, 1e-9);
        }

        [Test]
        public void LookAtAt_NonPositivePeriodOrRadius_PinsToCentre()
        {
            var a = TileLoadStressDriver.LookAtAt(3.7, 52.52, 13.405, 0.05, 0.0);
            Assert.AreEqual(52.52,  a.latitude,  Tol);
            Assert.AreEqual(13.405, a.longitude, Tol);

            var b = TileLoadStressDriver.LookAtAt(3.7, 52.52, 13.405, 0.0, 12.0);
            Assert.AreEqual(52.52,  b.latitude,  Tol);
            Assert.AreEqual(13.405, b.longitude, Tol);
        }

        [Test]
        public void LookAtAt_AwayFromEquator_ScalesLongitudeByInvCosLat()
        {
            // At quarter period the orbit is due-north (lon == centre) regardless of latitude.
            var (lat, lon) = TileLoadStressDriver.LookAtAt(3.0, 52.52, 13.405, 0.05, 12.0);
            Assert.AreEqual(52.52 + 0.05, lat, 1e-9, "due north: centre lat + radius");
            Assert.AreEqual(13.405,       lon, 1e-9, "due north: longitude unchanged");

            // At phase 0 the longitude offset is radius / cos(lat) — larger than the raw radius at 52.52°N.
            var east = TileLoadStressDriver.LookAtAt(0.0, 52.52, 13.405, 0.05, 12.0);
            double expectedLon = 13.405 + 0.05 / math.cos(math.radians(52.52));
            Assert.AreEqual(expectedLon, east.longitude, 1e-9);
            Assert.Greater(east.longitude - 13.405, 0.05,
                "inverse-cos scaling must widen the longitude offset beyond the raw radius away from the equator");
        }

        // ── Wired live-plumbing: Tick drives the real camera zoom + look-at ──────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""stress"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        private static (GameObject go, MapView view, TileLoadStressDriver driver) WireDriver()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_StressDriver");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 14;
            view.WithTestCamera();
            view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

            var driver = go.AddComponent<TileLoadStressDriver>();
            driver.Map                = view;   // explicit — Start()'s self-wire doesn't run under the EditMode runner
            driver.MinZoom            = 3f;
            driver.MaxZoom            = 9f;
            driver.ZoomPeriodSeconds  = 8f;
            driver.CenterLatitude     = 0.0;    // equator ⇒ exact orbit arithmetic
            driver.CenterLongitude    = 0.0;
            driver.PanRadiusDegrees   = 10.0;
            driver.PanPeriodSeconds   = 8f;
            return (go, view, driver);
        }

        [Test]
        public void Tick_DrivesCameraZoomAndLookAt_AlongTheMotionCurves()
        {
            var (go, view, driver) = WireDriver();
            try
            {
                // Half period: zoom at MaxZoom, orbit due-west (lat centre, lon −radius).
                driver.Tick(4.0f);
                var cam = view.Camera.CurrentProperties;
                Assert.AreEqual(9.0,   cam.Zoom,               1e-3, "half period ⇒ MaxZoom");
                Assert.AreEqual(0.0,   cam.LookAt.Latitude,    1e-3, "half period ⇒ orbit latitude back at centre");
                Assert.AreEqual(-10.0, cam.LookAt.Longitude,   1e-3, "half period ⇒ orbit due-west of centre");

                // Another half period: zoom back to MinZoom, orbit due-east again (full lap).
                driver.Tick(4.0f);
                cam = view.Camera.CurrentProperties;
                Assert.AreEqual(3.0,  cam.Zoom,             1e-3, "full period ⇒ MinZoom");
                Assert.AreEqual(0.0,  cam.LookAt.Latitude,  1e-3);
                Assert.AreEqual(10.0, cam.LookAt.Longitude, 1e-3, "full period ⇒ orbit due-east of centre");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Tick_Disabled_LeavesCameraUntouched()
        {
            var (go, view, driver) = WireDriver();
            try
            {
                driver.SweepEnabled = false;
                var before = view.Camera.CurrentProperties;
                driver.Tick(4.0f);
                var after = view.Camera.CurrentProperties;
                Assert.AreEqual(before.Zoom,             after.Zoom,             Tol, "zoom must be untouched");
                Assert.AreEqual(before.LookAt.Latitude,  after.LookAt.Latitude,  Tol, "look-at must be untouched");
                Assert.AreEqual(before.LookAt.Longitude, after.LookAt.Longitude, Tol, "look-at must be untouched");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Tick_Unwired_NoOpsCleanly()
        {
            var go = new GameObject("StressDriver_Unwired");
            try
            {
                var driver = go.AddComponent<TileLoadStressDriver>();
                driver.Map = null;
                Assert.DoesNotThrow(() => driver.Tick(1.0f));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
