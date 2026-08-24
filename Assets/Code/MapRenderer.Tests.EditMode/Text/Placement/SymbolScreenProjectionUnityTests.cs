// Unity EditMode only — needs a real UnityEngine.Camera / Camera.WorldToScreenPoint. Cross-checks
// SymbolScreenProjection (Tools/core-tests-verified pure math) against the REAL camera pipeline it's
// meant to reproduce. NOT registered in core-tests.csproj.

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T2 (Core-vs-real-camera pin): a synthetic world anchor's <see cref="SymbolScreenProjection.TryProjectAnchor"/>
    /// pixel must MATCH the real <see cref="Camera.WorldToScreenPoint"/> pixel for the SAME
    /// (origin-relative) local position, and a behind-camera anchor must be culled by both.
    /// </summary>
    [TestFixture]
    public class SymbolScreenProjectionUnityTests
    {
        private const int ViewportWidth = 800;
        private const int ViewportHeight = 600;

        private static (MapCamera mapCamera, GameObject camGo) BuildCamera(double tiltDeg = 0.0)
        {
            var camGo = new GameObject("SymbolProjection_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(ViewportWidth, ViewportHeight, 0);

            var lookAt = new GeoCoordinate3D { Latitude = 52.52, Longitude = 13.405, Altitude = 0.0 };
            var props = new CameraProperties(lookAt, zoom: 10.0, heading: 0.0, tilt: tiltDeg);
            var mapCamera = new MapCamera(uCam, props); // ctor calls SyncToCamera

            return (mapCamera, camGo);
        }

        [Test]
        public void TryProjectAnchor_MatchesRealCameraWorldToScreenPoint()
        {
            (MapCamera mapCamera, GameObject camGo) = BuildCamera();
            try
            {
                var lookAtSurface = new GeoCoordinate { Latitude = 52.52, Longitude = 13.405 };
                double3 sceneOriginRender = mapCamera.Projection.Project(lookAtSurface);

                // An anchor offset from the look-at -- projects away from dead-center, a non-degenerate check.
                var anchorGeo = new GeoCoordinate { Latitude = 52.521, Longitude = 13.406 };
                double3 renderPos = mapCamera.Projection.Project(anchorGeo);

                double3 local = renderPos - sceneOriginRender;
                var localUnity = new Vector3((float)local.x, (float)local.y, (float)local.z);
                Vector3 expectedScreen = mapCamera.Camera.WorldToScreenPoint(localUnity);

                float4x4 viewProj = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;

                bool ok = SymbolScreenProjection.TryProjectAnchor(
                    in renderPos, in sceneOriginRender, in viewProj, in viewportLogicalPx,
                    float3x3.identity, out float2 screenPx, out float depth);

                Assert.IsTrue(ok, "an anchor near the look-at, in front of an overhead camera, must not be culled");
                Assert.AreEqual(expectedScreen.x, screenPx.x, 0.5f,
                    $"SymbolScreenProjection.x ({screenPx.x:F2}) must match Camera.WorldToScreenPoint.x ({expectedScreen.x:F2})");
                Assert.AreEqual(expectedScreen.y, screenPx.y, 0.5f,
                    $"SymbolScreenProjection.y ({screenPx.y:F2}) must match Camera.WorldToScreenPoint.y ({expectedScreen.y:F2})");
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void TryProjectAnchor_BehindTiltedCamera_CulledLikeRealCamera()
        {
            // Tilt the camera so it has a real "behind" direction (at tilt=0 it looks straight down and
            // nothing at ground level is ever behind it).
            (MapCamera mapCamera, GameObject camGo) = BuildCamera(tiltDeg: 60.0);
            try
            {
                var lookAtSurface = new GeoCoordinate { Latitude = 52.52, Longitude = 13.405 };
                double3 sceneOriginRender = mapCamera.Projection.Project(lookAtSurface);

                // Exact, geometry-independent construction of a guaranteed-behind point: camera-relative
                // rendering places the Unity camera at `cameraPos` (render-relative to the look-at, which
                // sits at the origin) after SyncToCamera. A point 1.5x FURTHER than the camera itself,
                // along the SAME direction from the look-at, is on the far side of the camera from the
                // look-at -- i.e. behind it -- for ANY tilt/heading, with no lat/lon distance guessing.
                Vector3 cameraPosUnity = mapCamera.Camera.transform.position;
                var cameraPos = new double3(cameraPosUnity.x, cameraPosUnity.y, cameraPosUnity.z);
                double3 local = cameraPos * 1.5;
                double3 renderPos = sceneOriginRender + local;
                var localUnity = new Vector3((float)local.x, (float)local.y, (float)local.z);

                Vector4 clip = mapCamera.Camera.projectionMatrix * mapCamera.Camera.worldToCameraMatrix * new Vector4(localUnity.x, localUnity.y, localUnity.z, 1f);
                Assert.LessOrEqual(clip.w, 0f, "test precondition: 1.5x the camera's own render-relative position must be behind the real camera (clip.w <= 0)");

                float4x4 viewProj = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;

                bool ok = SymbolScreenProjection.TryProjectAnchor(
                    in renderPos, in sceneOriginRender, in viewProj, in viewportLogicalPx, float3x3.identity, out _, out _);

                Assert.IsFalse(ok, "an anchor behind the real (tilted) camera must be culled");
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
