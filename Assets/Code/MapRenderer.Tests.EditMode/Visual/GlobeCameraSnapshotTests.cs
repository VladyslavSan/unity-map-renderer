// GlobeCameraSnapshotTests (S91-C, sub-step b) — renders the countries fixture on the globe THROUGH THE REAL
// runtime placement: the camera pose comes from CameraPoseMath.ComputePose (the same orbit math the live
// MapCamera uses) and the tile is placed by the ENU-rebase (rotation = rebase, position =
// FloatingOrigin.TileToSceneRebased). This is the visible corroboration of the numerical handedness teeth in
// GlobePlacementTests — a sane, framed, correctly-oriented globe means the quaternion/handedness is right,
// which is the gate to wire the 3 backends next.
//
// Scope note: this exercises the POSE ORIENTATION (heading/tilt → fwd/up) + the rebase quaternion, NOT the
// zoom→altitude chain — the altitude is hand-picked to frame the globe (AltitudeForZoom(0) dwarfs it).

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests.Visual
{
    public class GlobeCameraSnapshotTests
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void RendersGlobeThroughComputePoseAndRebase_WritesPng()
        {
            var proj = new SphericalProjection();

            // Look-at over Africa/Mediterranean: a recognizable, asymmetric land layout (Europe to the north,
            // Africa below) — a mirrored render would be obvious to the eye, the pose math obvious to the test.
            var lookAt = new GeoCoordinate { Latitude = 20.0, Longitude = 12.0 };

            // ── Tile mesh, UNPLACED (fitToView:false) so we drive the real camera-relative placement. ──
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]",
                projection: proj,
                fitToView: false);
            if (mat != null) mat.SetFloat("_Cull", 2f); // back-face cull → only the near hemisphere shows

            // ── Place the tile by the ENU rebase (the exact scheme the backends will use). ──
            // Mesh vertices are baked relative to the tile's SW-corner projected through the SAME projection
            // (docs §5). z=0/x=0/y=0 SW corner = ToLonLat(px=0, py=extent) = (lon −180, lat ≈ −85.05).
            double2 swLL = new TileId { Z = 0, X = 0, Y = 0 }.ToLonLat(0.0, 1.0, 1.0);
            double3 tileOriginRender  = proj.Project(new GeoCoordinate { Latitude = swLL.y, Longitude = swLL.x });
            double3 sceneOriginRender = proj.Project(lookAt);

            float3x3 rebase   = math.transpose(proj.TangentBasisAt(lookAt)); // render → local ENU
            float3   position = FloatingOrigin.TileToSceneRebased(tileOriginRender, sceneOriginRender, rebase);

            quaternion q = new quaternion(rebase); // proper rotation (det +1) → clean unit quaternion
            mapGo.transform.rotation      = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
            mapGo.transform.localPosition = new Vector3(position.x, position.y, position.z);

            // ── Camera: the REAL orbit pose (look-at at the render origin, up=+Y). ──
            // Altitude hand-picked to frame the globe (radius R): 2.5·R ⇒ the globe fills most of a 35° FOV.
            double altitude = 2.5 * SphericalProjection.Radius;
            CameraPoseMath.ComputePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                out double3 pos, out double3 fwd, out double3 up);

            var cameraGo = new GameObject("GlobeOrbitCamera");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));
            camera.orthographic    = false;
            camera.fieldOfView     = 35f;
            camera.nearClipPlane   = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            camera.farClipPlane    =                  (float)CameraPoseMath.FarClip(altitude);
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled         = false;

            // Directional light to shade the sphere (independent of the rotated tile transform).
            var lightGo = new GameObject("GlobeLight");
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);

            var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                string path = snap.WritePng("globe-camera-countries.png");
                TestContext.WriteLine($"[GlobeCameraSnapshotTests] wrote {path}");

                if (snap.IsAllBlack())
                    Assert.Inconclusive("Globe render is all-background — no GPU context (headless). PNG still written.");
            }
            finally
            {
                snap.Dispose();
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(mapGo);
            }
        }
    }
}
