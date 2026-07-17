// GlobePlacementTests (S91-C, sub-step b) — the numerical HANDEDNESS teeth for camera-relative globe
// placement, BEFORE any backend wiring. These are the falsifiable checks the visible PNG cannot give:
// a mirrored globe of green blobs reads the same to the eye, but east-lands-left fails an assertion here.
//
// The scheme (S91-C): the scene is rebased into the look-at's local ENU
// frame so ONE camera orbit (CameraPoseMath.ComputeRelativePose, look-at at the render origin, up=+Y) serves plane
// AND globe. A tile places at rotation = rebase, position = FloatingOrigin.TileToSceneRebased(...), where
// rebase = transpose(projection.TangentBasisAt(lookAt)).
//
// The traps these catch:
//   - det=+1 / orthonormality is necessary but NOT a handedness gate: transpose(B), B, and any column
//     permutation all keep det +1. So the real tooth is DIRECTIONAL — east must land screen-right, north
//     screen-up — which also disambiguates rebase = Bᵀ from the transpose bug rebase = B.
//   - the globe basis is a proper rotation (det +1) despite the ECEF→render reflection, so quaternion(rebase)
//     is a clean unit rotation (no handedness flip) — asserted below.

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    public class GlobePlacementTests
    {
        // A few generic look-at points (avoid only axis-aligned special cases where a bug could hide).
        private static readonly GeoCoordinate[] LookAts =
        {
            new GeoCoordinate { Latitude = 0.0,   Longitude = 0.0   },
            new GeoCoordinate { Latitude = 35.0,  Longitude = 20.0  },
            new GeoCoordinate { Latitude = -18.0, Longitude = 140.0 },
            new GeoCoordinate { Latitude = 60.0,  Longitude = -75.0 },
        };

        // ── Basis is a proper rotation (necessary, not sufficient) ─────────────────────────────────

        [Test]
        public void SphericalTangentBasis_IsOrthonormalProperRotation()
        {
            var proj = new SphericalProjection();
            foreach (var lookAt in LookAts)
            {
                float3x3 b = proj.TangentBasisAt(lookAt);

                // Orthonormal: B·Bᵀ = I.
                float3x3 shouldBeI = math.mul(b, math.transpose(b));
                AssertMatrixApprox(shouldBeI, float3x3.identity, 1e-5f, $"B·Bᵀ at {lookAt.Latitude},{lookAt.Longitude}");

                // Proper rotation (det = +1) — despite the ECEF→render axis-swap reflection, the (E,U,N)
                // column order restores right-handedness, so quaternion(rebase) is a clean unit rotation.
                Assert.AreEqual(1.0f, math.determinant(b), 1e-4f,
                    $"det(B) must be +1 (proper rotation) at {lookAt.Latitude},{lookAt.Longitude}");
            }
        }

        // ── The handedness tooth: rebase maps the ENU axes to the scene axes ───────────────────────
        // This is what det/orthonormality CANNOT give. rebase = Bᵀ must send the render-space East vector
        // (B.c0) to +X, Up (B.c1) to +Y, North (B.c2) to +Z. If someone used B instead of Bᵀ (the single
        // most likely bug), rebase·East ≠ +X and this fails.

        [Test]
        public void Rebase_MapsEnuAxesToSceneAxes()
        {
            var proj = new SphericalProjection();
            foreach (var lookAt in LookAts)
            {
                float3x3 b      = proj.TangentBasisAt(lookAt);
                float3x3 rebase = math.transpose(b);

                AssertVecApprox(math.mul(rebase, b.c0), new float3(1, 0, 0), 1e-5f, $"East→+X at {lookAt.Longitude}");
                AssertVecApprox(math.mul(rebase, b.c1), new float3(0, 1, 0), 1e-5f, $"Up→+Y at {lookAt.Longitude}");
                AssertVecApprox(math.mul(rebase, b.c2), new float3(0, 0, 1), 1e-5f, $"North→+Z at {lookAt.Longitude}");
            }
        }

        // ── End-to-end handedness through the REAL camera pose ─────────────────────────────────────
        // Project a point EAST of the look-at and one NORTH, rebase both into the scene frame, and push them
        // through the camera built from CameraPoseMath.ComputeRelativePose. In Unity view space (worldToCameraMatrix:
        // camera looks down −Z, +X right, +Y up) east must land +X and north must land +Y. Uses the camera's
        // view matrix only (no GPU) so it is deterministic in headless batch mode.

        [Test]
        public void GlobeCamera_EastIsScreenRight_NorthIsScreenUp()
        {
            var proj = new SphericalProjection();

            foreach (var lookAt in LookAts)
            {
                float3x3 rebase        = math.transpose(proj.TangentBasisAt(lookAt));
                double3  sceneOrigin   = proj.Project(lookAt);
                double   altitude      = 3.0 * SphericalProjection.Radius; // frame the globe (pose test, not zoom→alt)

                var camGo = BuildOrbitCamera(altitude, headingDeg: 0.0, tiltDeg: 0.0);
                try
                {
                    var cam = camGo.GetComponent<Camera>();

                    Vector3 vCenter = WorldToView(cam, SceneLocal(proj, sceneOrigin, rebase, lookAt));
                    Vector3 vEast   = WorldToView(cam, SceneLocal(proj, sceneOrigin, rebase,
                        new GeoCoordinate { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude + 5.0 }));
                    Vector3 vNorth  = WorldToView(cam, SceneLocal(proj, sceneOrigin, rebase,
                        new GeoCoordinate { Latitude = lookAt.Latitude + 5.0, Longitude = lookAt.Longitude }));

                    string at = $"lookAt {lookAt.Latitude},{lookAt.Longitude}";
                    // Look-at sits at the scene origin → dead centre of view (x≈0, y≈0).
                    Assert.Less(Mathf.Abs(vCenter.x), 1e3f, $"look-at not centred (x) — {at}");
                    Assert.Less(Mathf.Abs(vCenter.y), 1e3f, $"look-at not centred (y) — {at}");
                    // 5° east/north ≈ 5.5e5 m of view-space offset; a big, unambiguous margin.
                    Assert.Greater(vEast.x,  1e5f, $"east must land to the RIGHT (+X view) — {at}");
                    Assert.Greater(vNorth.y, 1e5f, $"north must land ABOVE (+Y view) — {at}");
                }
                finally
                {
                    Object.DestroyImmediate(camGo);
                }
            }
        }

        // ── Mercator reduces to today's flat translation-only placement ────────────────────────────

        [Test]
        public void TileToSceneRebased_Mercator_ReducesToTileLocalToScene()
        {
            var proj = new WebMercatorProjection();
            // Mercator tangent basis is constant identity ⇒ rebase = I ⇒ rotation = identity.
            float3x3 rebase = math.transpose(proj.TangentBasisAt(new GeoCoordinate { Latitude = 40, Longitude = 11 }));
            AssertMatrixApprox(rebase, float3x3.identity, 1e-6f, "Mercator rebase must be identity");

            var (tileMin, _) = new TileId { Z = 4, X = 5, Y = 6 }.MercatorBounds();
            double2 sceneMerc = new double2(1.2e6, -3.4e5);

            // Render-space origins: Mercator maps east→+X, height→+Y(=0), north→+Z.
            double3 tileOriginRender  = new double3(tileMin.x, 0.0, tileMin.y);
            double3 sceneOriginRender = new double3(sceneMerc.x, 0.0, sceneMerc.y);

            float3 rebased = FloatingOrigin.TileToSceneRebased(tileOriginRender, sceneOriginRender, rebase);
            float3 flat    = FloatingOrigin.TileLocalToScene(tileMin, sceneMerc);

            AssertVecApprox(rebased, flat, 1e-3f, "TileToSceneRebased must equal TileLocalToScene under Mercator");
        }

        // ── The tile containing the look-at places its look-at vertex at the scene origin ──────────
        // Since a mesh vertex renders at rebase·(project(v) − sceneOrigin), a vertex AT the look-at renders
        // at the origin. Verified via the transform composition: rebase·(project(v) − tileOrigin) [mesh-local]
        // + position [TileToSceneRebased] == 0 when v == lookAt.

        [Test]
        public void TileToSceneRebased_LookAtVertex_RendersAtSceneOrigin()
        {
            var proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 12.0, Longitude = -47.0 };

            float3x3 rebase        = math.transpose(proj.TangentBasisAt(lookAt));
            double3  sceneOrigin   = proj.Project(lookAt);
            double3  tileOrigin    = proj.Project(new GeoCoordinate { Latitude = -85.0, Longitude = -180.0 }); // some tile corner

            float3 position   = FloatingOrigin.TileToSceneRebased(tileOrigin, sceneOrigin, rebase);
            // The look-at vertex, baked mesh-local about tileOrigin, then rotated by rebase:
            float3 meshLocal  = math.mul(rebase, new float3(
                (float)(sceneOrigin.x - tileOrigin.x),
                (float)(sceneOrigin.y - tileOrigin.y),
                (float)(sceneOrigin.z - tileOrigin.z)));
            float3 rendered   = meshLocal + position;

            AssertVecApprox(rendered, float3.zero, 1e-1f, "look-at vertex must render at the scene origin");
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────────────

        private static float3 SceneLocal(IProjection proj, double3 sceneOrigin, float3x3 rebase, GeoCoordinate geo)
        {
            double3 world = proj.Project(geo);
            return math.mul(rebase, new float3(
                (float)(world.x - sceneOrigin.x),
                (float)(world.y - sceneOrigin.y),
                (float)(world.z - sceneOrigin.z)));
        }

        private static Vector3 WorldToView(Camera cam, float3 worldPos)
            => cam.worldToCameraMatrix.MultiplyPoint(new Vector3(worldPos.x, worldPos.y, worldPos.z));

        private static GameObject BuildOrbitCamera(double altitude, double headingDeg, double tiltDeg)
        {
            CameraPoseMath.ComputeRelativePose(altitude,
                Angle.FromDegrees(headingDeg), Angle.FromDegrees(tiltDeg),
                out double3 pos, out double3 fwd, out double3 up);

            var go  = new GameObject("GlobeOrbitCamera");
            var cam = go.AddComponent<Camera>();
            cam.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            cam.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));
            cam.orthographic  = false;
            cam.fieldOfView   = 35f;
            cam.nearClipPlane = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            cam.farClipPlane  =                  (float)CameraPoseMath.FarClip(altitude);
            cam.enabled       = false;
            return go;
        }

        private static void AssertVecApprox(float3 actual, float3 expected, float tol, string what)
        {
            Assert.AreEqual(expected.x, actual.x, tol, $"{what} (x)");
            Assert.AreEqual(expected.y, actual.y, tol, $"{what} (y)");
            Assert.AreEqual(expected.z, actual.z, tol, $"{what} (z)");
        }

        private static void AssertMatrixApprox(float3x3 actual, float3x3 expected, float tol, string what)
        {
            AssertVecApprox(actual.c0, expected.c0, tol, $"{what} c0");
            AssertVecApprox(actual.c1, expected.c1, tol, $"{what} c1");
            AssertVecApprox(actual.c2, expected.c2, tol, $"{what} c2");
        }
    }
}
