// GlobeCameraInteractionTests (S91-C) — validates SphericalProjection.ScreenToGround / GroundToScreen against
// Unity's OWN camera (the ground truth for handedness/fov/aspect), then tests the actual anchored-pan PIN over
// a simulated multi-frame drag — the check the advisor flagged as the one that discriminates (a round-trip
// alone would pass and prove nothing). The globe's render-space model: look-at at the render origin (+Y up),
// the sphere centred at (0, −R, 0); a screen ray hits it and the grabbed point pins under the cursor as the
// per-frame ApplyPan fixed-point converges.

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    public class GlobeCameraInteractionTests
    {
        private static readonly double2 Vp = new double2(800, 600);

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        // Builds a real Unity camera posed EXACTLY as the renderer does (MapCamera.SyncToCamera).
        private static (GameObject go, Camera cam, RenderTexture rt) BuildUnityCamera(in CameraProperties c)
        {
            double alt = CameraPoseMath.AltitudeForZoom(c.Zoom, Vp.y, c.VerticalFovDeg);
            CameraPoseMath.ComputeRelativePose(alt, c.Heading.Value, c.Tilt.Value,
                out double3 pos, out double3 fwd, out double3 up);

            var rt = new RenderTexture((int)Vp.x, (int)Vp.y, 24);
            var go = new GameObject("UnityCamRef");
            var cam = go.AddComponent<Camera>();
            cam.targetTexture     = rt;
            cam.aspect            = (float)(Vp.x / Vp.y);
            cam.fieldOfView       = (float)c.VerticalFovDeg;
            cam.nearClipPlane     = 0.1f;
            cam.farClipPlane      = (float)(alt + 4.0 * SphericalProjection.Radius);
            cam.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            cam.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));
            cam.enabled = false;
            return (go, cam, rt);
        }

        // The render-scene position of a geodetic point (what the renderer places its vertex at): rebase the
        // projected ECEF into the look-at ENU frame — rebase = transpose(TangentBasisAt(lookAt)).
        private static Vector3 RenderPos(SphericalProjection proj, in CameraProperties c, GeoCoordinate g)
        {
            double3  sceneOrigin = proj.Project(new GeoCoordinate { Latitude = c.LookAt.Latitude, Longitude = c.LookAt.Longitude });
            double3  rel = proj.Project(g) - sceneOrigin;
            float3x3 b   = proj.TangentBasisAt(new GeoCoordinate { Latitude = c.LookAt.Latitude, Longitude = c.LookAt.Longitude });
            double3  rs  = new double3(
                math.dot(new double3(b.c0.x, b.c0.y, b.c0.z), rel),
                math.dot(new double3(b.c1.x, b.c1.y, b.c1.z), rel),
                math.dot(new double3(b.c2.x, b.c2.y, b.c2.z), rel));
            return new Vector3((float)rs.x, (float)rs.y, (float)rs.z);
        }

        [Test]
        public void GroundToScreen_MatchesUnityCameraProjection()
        {
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 3.0);
            var (go, cam, rt) = BuildUnityCamera(in c);
            try
            {
                // Points on the near hemisphere around the look-at.
                foreach (var g in new[]
                {
                    new GeoCoordinate { Latitude = 20, Longitude = 12 }, // the look-at itself → screen centre
                    new GeoCoordinate { Latitude = 25, Longitude = 12 },
                    new GeoCoordinate { Latitude = 20, Longitude = 18 },
                    new GeoCoordinate { Latitude = 15, Longitude =  6 },
                })
                {
                    Vector3 unity = cam.WorldToScreenPoint(RenderPos(proj, in c, g));
                    double2 core  = proj.GroundToScreen(
                        new GeoCoordinate3D { Latitude = g.Latitude, Longitude = g.Longitude, Altitude = 0 }, Vp, c);
                    Assert.AreEqual(unity.x, core.x, 1.5, $"screen.x at {g.Latitude},{g.Longitude}");
                    Assert.AreEqual(unity.y, core.y, 1.5, $"screen.y at {g.Latitude},{g.Longitude}");
                    Assert.Greater(unity.z, 0f, "point must be in front of the camera");
                }
            }
            finally { Cleanup(go, rt); }
        }

        [Test]
        public void ScreenToGround_HitProjectsBackToTheSamePixel()
        {
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 3.0);
            var (go, cam, rt) = BuildUnityCamera(in c);
            try
            {
                // Pixels near the centre (surely on the globe disc).
                foreach (var px in new[]
                {
                    new double2(400, 300), new double2(460, 340), new double2(360, 260), new double2(430, 250),
                })
                {
                    GeoCoordinate3D g = proj.ScreenToGround(px, Vp, c);
                    // Through Unity's projection the recovered ground point must land back on px.
                    Vector3 back = cam.WorldToScreenPoint(RenderPos(proj, in c,
                        new GeoCoordinate { Latitude = g.Latitude, Longitude = g.Longitude }));
                    Assert.AreEqual(px.x, back.x, 1.5, "ScreenToGround→screen.x round-trip");
                    Assert.AreEqual(px.y, back.y, 1.5, "ScreenToGround→screen.y round-trip");
                }
            }
            finally { Cleanup(go, rt); }
        }

        [Test]
        public void AnchoredPan_ConvergesToPin_Interior()
        {
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 3.0);

            // Grab an off-centre point, then request it be dragged to a different cursor position. The Controller
            // captures the grabbed ground ONCE and calls ApplyPan every frame — a fixed-point iteration that
            // converges to the exact pin. Simulate that here.
            var pStart = new double2(360, 260);
            var pTarget = new double2(470, 350);
            GeoCoordinate3D grabbed = proj.ScreenToGround(pStart, Vp, c);

            for (int frame = 0; frame < 20; frame++)
            {
                CameraPropertiesUpdate patch = ViewInput.ApplyPan(proj, c, grabbed, pTarget, Vp);
                c = new CameraProperties(
                    new GeoCoordinate3D { Latitude = patch.Latitude.Value, Longitude = patch.Longitude.Value, Altitude = 0 },
                    c.Zoom, c.Heading.Degrees, c.Tilt.Degrees);
            }

            double2 grabbedNow = proj.GroundToScreen(grabbed, Vp, c);
            Assert.AreEqual(pTarget.x, grabbedNow.x, 2.0, "grabbed point must pin under the target cursor (x)");
            Assert.AreEqual(pTarget.y, grabbedNow.y, 2.0, "grabbed point must pin under the target cursor (y)");
        }

        [Test]
        public void GlobePin_UnderDpr2_HoldsWithLogicalSeam_DriftsWithPhysical()
        {
            // S92 T-GLOBE-PIN-DPI (D3): under DPR=2 the render camera frames the LOGICAL viewport (vp ÷ DPR,
            // MapCamera D1), so GroundToScreen(·, vpLogical) IS the render (that primitive is pinned against
            // Unity's own camera by GroundToScreen_MatchesUnityCameraProjection). The interaction seam
            // (Controller S92 D3) divides BOTH cursor and viewport by DPR before calling the projection, so the
            // grabbed point re-renders exactly under the cursor. A version that reconstructs with the PHYSICAL
            // viewport (skipping the ÷DPR) puts the camera at 2× the render altitude → the point drifts off.
            const double dpr = 2.0;
            double2 vpPhysical = new double2(800, 600);
            double2 vpLogical  = vpPhysical / dpr; // 400×300 — what the render frames and the seam uses
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 3.0);

            // An off-centre physical cursor (drift is zero at the exact centre — the discriminator needs offset).
            double2 cursorPhysical = new double2(520, 400);

            // CORRECT seam (D3): convert cursor + viewport to logical, so reconstruction shares the render's
            // basis. Grab the ground under the cursor, then re-render it — it must land back under the cursor.
            double2         cursorLogical = cursorPhysical / dpr;
            GeoCoordinate3D grabbed       = proj.ScreenToGround(cursorLogical, vpLogical, c);
            double2         rendered      = proj.GroundToScreen(grabbed, vpLogical, c) * dpr; // logical → physical
            Assert.AreEqual(cursorPhysical.x, rendered.x, 1.0, "pin holds under DPR=2 with the logical seam (x)");
            Assert.AreEqual(cursorPhysical.y, rendered.y, 1.0, "pin holds under DPR=2 with the logical seam (y)");

            // BUG: feed the PHYSICAL cursor + viewport to the projection while the render stays logical. Same
            // NDC ray, but the reconstructed altitude (from vpPhysical.y) is 2× the render's → the grabbed
            // point, re-rendered, drifts off the cursor. This is the interaction the stage says not to skip.
            GeoCoordinate3D grabbedBug  = proj.ScreenToGround(cursorPhysical, vpPhysical, c);
            double2         renderedBug = proj.GroundToScreen(grabbedBug, vpLogical, c) * dpr;
            double          drift       = math.length(renderedBug - cursorPhysical);
            Assert.Greater(drift, 3.0,
                "reconstructing with the physical viewport (no ÷DPR) drifts the pin off the cursor under DPR≠1");
        }

        [Test]
        public void ScreenToGround_NeverNaN_IncludingPastTheLimb()
        {
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 3.0);
            // A far corner pixel is past the globe's silhouette; ScreenToGround must clamp, not NaN/throw.
            foreach (var px in new[] { new double2(5, 5), new double2(795, 595), new double2(400, 300) })
            {
                GeoCoordinate3D g = proj.ScreenToGround(px, Vp, c);
                Assert.IsFalse(double.IsNaN(g.Latitude) || double.IsNaN(g.Longitude), $"NaN at pixel {px.x},{px.y}");
                Assert.IsTrue(g.Latitude >= -90.0 && g.Latitude <= 90.0, "latitude in range");
            }
        }

        private static double AngleDeg(SphericalProjection p, GeoCoordinate3D a, GeoCoordinate3D b)
        {
            double3 ua = p.ProjectPoint(new GeoCoordinate { Latitude = a.Latitude, Longitude = a.Longitude }).Up;
            double3 ub = p.ProjectPoint(new GeoCoordinate { Latitude = b.Latitude, Longitude = b.Longitude }).Up;
            return math.acos(math.clamp(math.dot(ua, ub), -1.0, 1.0)) * 180.0 / math.PI_DBL;
        }

        [Test]
        public void GlobePanSolve_StepIsAlwaysBounded_NoSpin()
        {
            // The rotation solve steps by acos(C·G) ≤ π BY CONSTRUCTION, so no drag — at any zoom, any cursor,
            // including past the limb — can rotate the look-at more than 180° in a step (the anti-spin guarantee).
            var proj = new SphericalProjection();
            foreach (double zoom in new[] { 0.0, 0.5, 1.0 })
            {
                var c = Cam(12, 20, zoom);
                var grabbed = proj.ScreenToGround(new double2(430, 320), Vp, c);
                foreach (var cursor in new[]
                {
                    new double2(405, 305), new double2(300, 200), new double2(700, 500), new double2(10, 10),
                })
                {
                    GeoCoordinate3D newLookAt = proj.PanLookAtForGrab(grabbed, cursor, Vp, c);
                    Assert.IsFalse(double.IsNaN(newLookAt.Latitude) || double.IsNaN(newLookAt.Longitude),
                        $"NaN at zoom {zoom}, cursor {cursor.x},{cursor.y}");
                    Assert.LessOrEqual(AngleDeg(proj, c.LookAt, newLookAt), 180.5,
                        $"a single pan step must rotate ≤ 180° (zoom {zoom}, cursor {cursor.x},{cursor.y})");
                }
            }
        }

        [Test]
        public void GlobePanSolve_ConvergesTowardPin_Interior()
        {
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 3.0);
            var pStart  = new double2(360, 260);
            var pTarget = new double2(470, 350);
            GeoCoordinate3D grabbed = proj.ScreenToGround(pStart, Vp, c);

            double startErr = math.length(proj.GroundToScreen(grabbed, Vp, c) - pTarget);
            for (int frame = 0; frame < 30; frame++)
            {
                GeoCoordinate3D newLookAt = proj.PanLookAtForGrab(grabbed, pTarget, Vp, c);
                c = new CameraProperties(newLookAt, c.Zoom, c.Heading.Degrees, c.Tilt.Degrees);
            }
            double endErr = math.length(proj.GroundToScreen(grabbed, Vp, c) - pTarget);

            // The grabbed point must end up much closer to the target cursor than it started (the pin), which
            // also confirms the rotation direction (C→G) is correct — a wrong sign would drive it away.
            Assert.Less(endErr, startErr, "pan must move the grabbed point TOWARD the cursor");
            Assert.Less(endErr, 4.0, "grabbed point converges under the target cursor (px)");
        }

        [Test]
        public void OldAffinePan_CanSpinAtLowZoom_WhichTheSolveFixes()
        {
            // Documents the reported bug: at low zoom the affine ViewInput.ApplyPan (planar trick) diverges near
            // the limb and the look-at accumulates many full turns; the bounded solve above cannot.
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 0.0);
            // A grabbed point far from the look-at renders near the tiny disc's limb.
            var grabbedGeo = new GeoCoordinate3D { Latitude = 20, Longitude = 12 + 78 };
            double2 pStart = proj.GroundToScreen(grabbedGeo, Vp, c);
            double2 cursor = pStart + new double2(6, 6); // a MINOR drag

            var cAffine = c;
            double affineTravel = 0; double prevLon = cAffine.LookAt.Longitude;
            for (int i = 0; i < 30; i++)
            {
                var patch = ViewInput.ApplyPan(proj, cAffine, grabbedGeo, cursor, Vp);
                affineTravel += math.abs(Angle.LerpShortest(
                    Angle.FromDegrees(prevLon), Angle.FromDegrees(patch.Longitude.Value), 1.0).Degrees - prevLon);
                prevLon = patch.Longitude.Value;
                cAffine = new CameraProperties(
                    new GeoCoordinate3D { Latitude = patch.Latitude.Value, Longitude = patch.Longitude.Value, Altitude = 0 },
                    cAffine.Zoom, cAffine.Heading.Degrees, cAffine.Tilt.Degrees);
            }

            // The bounded solve over the same drag stays within a modest total.
            var cSolve = c;
            double solveTravel = 0; prevLon = cSolve.LookAt.Longitude;
            var prevLA = cSolve.LookAt;
            for (int i = 0; i < 30; i++)
            {
                GeoCoordinate3D nla = proj.PanLookAtForGrab(grabbedGeo, cursor, Vp, cSolve);
                solveTravel += AngleDeg(proj, prevLA, nla);
                prevLA = nla;
                cSolve = new CameraProperties(nla, cSolve.Zoom, cSolve.Heading.Degrees, cSolve.Tilt.Degrees);
            }

            TestContext.WriteLine($"affine lon travel={affineTravel:F0}°, bounded solve travel={solveTravel:F0}°");
            Assert.Less(solveTravel, 360.0, "the bounded solve must not spin (total rotation < one turn)");
        }

        [Test]
        public void GlobePan_CursorDraggedOffGlobe_FreezesAndNeverSpins()
        {
            // The reported second case: click ON the earth, drag the cursor OFF it, and hold. There is no ground
            // point under an off-globe cursor, so the pan must FREEZE (hold the look-at) — not chase the clamped
            // silhouette forever. At zoom 0 the globe is a tiny disc, so a far-corner cursor is well off it.
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 0.0);
            GeoCoordinate3D grabbed = proj.ScreenToGround(new double2(400, 300), Vp, c); // dead-centre → a real hit
            var offGlobe = new double2(5, 5);

            GeoCoordinate3D startLookAt = c.LookAt;
            double travel = 0; var prev = c.LookAt;
            for (int frame = 0; frame < 60; frame++)
            {
                GeoCoordinate3D nla = proj.PanLookAtForGrab(grabbed, offGlobe, Vp, c);
                travel += AngleDeg(proj, prev, nla);
                prev = nla;
                c = new CameraProperties(nla, c.Zoom, c.Heading.Degrees, c.Tilt.Degrees);
            }

            Assert.Less(travel, 1e-6, "cursor off the globe must FREEZE the pan (no rotation, no spin)");
            Assert.AreEqual(startLookAt.Longitude, c.LookAt.Longitude, 1e-9, "look-at longitude held");
            Assert.AreEqual(startLookAt.Latitude,  c.LookAt.Latitude,  1e-9, "look-at latitude held");
        }

        [Test]
        public void GlobePan_SlowDragThroughTheLimb_DoesNotSpin()
        {
            // The reported corner case: slowly drag the cursor from inside the earth, through the edge, and out.
            // Right at the limb the surface is edge-on and the pin is singular; the grazing guard must freeze the
            // pan there rather than churn the look-at. A legitimate pan from centre toward the limb rotates the
            // globe at most ~one hemisphere; a spin would blow far past a single turn.
            var proj = new SphericalProjection();
            var c    = Cam(12, 20, 0.0); // zoom 0 → the whole limb is on screen
            GeoCoordinate3D grabbed = proj.ScreenToGround(new double2(400, 300), Vp, c); // centre → a real hit

            double total = 0; var prev = c.LookAt;
            for (int i = 0; i <= 240; i++) // sweep the cursor slowly rightward from centre out past the limb
            {
                var cursor = new double2(400 + i * 0.75, 300); // 400 → 580 px
                GeoCoordinate3D nla = proj.PanLookAtForGrab(grabbed, cursor, Vp, c);
                total += AngleDeg(proj, prev, nla);
                prev = nla;
                c = new CameraProperties(nla, c.Zoom, c.Heading.Degrees, c.Tilt.Degrees);
            }

            Assert.Less(total, 200.0, "a slow drag through the limb must pan, not spin (≤ ~one hemisphere)");
        }

        private static void Cleanup(GameObject go, RenderTexture rt)
        {
            Object.DestroyImmediate(go);
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
        }
    }
}
