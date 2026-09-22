// Globe/GlobeTests.cs — globe fill/line subdivision, winding, tangent and placement teeth (EditMode).
//
// GlobeTileSelectorTests.cs stays its own file (fast lane, csproj-registered). No using/alias
// collision across these nine: the two files that alias Fill = MapRenderer.Core.Style.Fill
// (GlobeFillTangentTests, GlobeFillWindingTests) are the only ones that use it bare; the two files that
// only import the MapRenderer.Jobs.Fill namespace never write a bare Fill.X.
//
// Contents:
//   GlobeCameraInteractionTests              — SphericalProjection ScreenToGround/GroundToScreen against Unity's own camera, plus the anchored-pan pin over a simulated drag (S91-C).
//   GlobeFillBandTests                       — the boundary band through curvature subdivision: band quads conform to the interior they border.
//   GlobeFillSubdividerTests                 — the globe fill subdivider Burst job refines flat earcut triangles onto the sphere and passes flat Mercator straight through (S91-C).
//   GlobeFillTangentTests                    — the fill Tangent stream carries the projection's per-vertex surface east, not a constant +X (S91-C).
//   GlobeFillWindingTests                    — the fill front face points out of the surface on both globe and Mercator, so one Cull Back mode fits both.
//   GlobeLineSubdivisionTests                — falsifiable teeth for globe line curvature subdivision: arc-proportional densification, chord fidelity (S91-C).
//   GlobeLineWindingTests                    — the line ribbon's front face points out of the surface on both globe and Mercator builds.
//   GlobePlacementTests                      — numerical handedness teeth for camera-relative globe placement via the ENU-rebased camera orbit (S91-C).
//   RightHandedSphereProjectionWindingTests  — a throwaway right-handed sphere projection's line ribbon still winds the same as flat Mercator (S100).

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.App;
using MapRenderer.App.View;
using MapRenderer.Unity.View.Camera;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Fill;
using System.IO;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geometry;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;
using MapRenderer.Unity.View;


namespace MapRenderer.Tests.Globe
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeCameraInteractionTests — SphericalProjection ScreenToGround/GroundToScreen vs. Unity's own camera, plus the anchored-pan pin
    // ───────────────────────────────────────────────────────────────────────────────────

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

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeFillBandTests — the boundary band through curvature subdivision — band quads conform to the interior
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class GlobeFillBandTests
    {
        private const double Extent = 4096.0;

        /// <summary>A whole-world tile, so the shared edge subtends far more than the ~3 degree split
        /// threshold and the subdivider genuinely recurses. A tile that never splits would make every
        /// assertion below vacuous.</summary>
        private static readonly TileId WholeWorld = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>The shared edge runs along <c>x = 0</c>, i.e. in LATITUDE, from +85 degrees to -85. The
        /// tile's <c>y = 0</c> edge looks like the obvious choice and is useless: on a z0 tile it runs from
        /// longitude -180 to +180, which is the SAME meridian, so both endpoints share a surface up, the
        /// edge subtends zero angle and is never marked. Measured, not assumed — the first form of this
        /// fixture used that edge and its own non-vacuity precondition caught it.</summary>
        private const double SharedEdgeX = 0.0;

        /// <summary>The outward miter every band outer vertex in these fixtures carries — unit length, so a
        /// vertex's expected <c>|band.xy|</c> is numerically its own <c>side</c>.</summary>
        private static readonly float3 OuterBand = new float3(0f, -1f, 1f);

        /// <summary>One subdivision run's output, disposed by the caller.</summary>
        private readonly struct Run : System.IDisposable
        {
            /// <summary>The subdivided vertices, in traversal order.</summary>
            public readonly NativeList<GlobeFillVertex> Vertices;

            /// <param name="vertices">The subdivided vertices.</param>
            /// <param name="indices">The sequential index list, held only so it can be disposed.</param>
            public Run(NativeList<GlobeFillVertex> vertices, NativeList<int> indices)
            {
                Vertices = vertices;
                _indices = indices;
            }

            private readonly NativeList<int> _indices;

            public void Dispose() { Vertices.Dispose(); _indices.Dispose(); }
        }

        /// <summary>Subdivides one hand-built vertex/triangle set through the production dispatcher.</summary>
        /// <param name="tileVerts">Tile-space vertices.</param>
        /// <param name="bands">The band attribute of each vertex, same length as <paramref name="tileVerts"/>.</param>
        /// <param name="triangles">Triangle indices into <paramref name="tileVerts"/>.</param>
        /// <returns>The completed run.</returns>
        private static Run Subdivide(double2[] tileVerts, float3[] bands, int[] triangles)
        {
            var verts = new NativeList<double2>(tileVerts.Length, Allocator.Persistent);
            var band  = new NativeList<float3>(bands.Length, Allocator.Persistent);
            var tris  = new NativeList<int>(triangles.Length, Allocator.Persistent);
            var feat  = new NativeList<int>(tileVerts.Length, Allocator.Persistent);
            foreach (double2 v in tileVerts) verts.Add(v);
            foreach (float3 b in bands) band.Add(b);
            foreach (int t in triangles) tris.Add(t);
            for (int i = 0; i < tileVerts.Length; i++) feat.Add(0);

            var outVerts = new NativeList<GlobeFillVertex>(256, Allocator.Persistent);
            var outIndices = new NativeList<int>(256, Allocator.Persistent);
            JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                new SphericalProjection(), verts, tris, feat, band,
                WholeWorld, Extent, double3.zero,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad,
                GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices,
                GlobeFillSubdivideDispatch.DefaultMaxTotalVertices,
                outVerts, outIndices, default);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            verts.Dispose(); band.Dispose(); tris.Dispose(); feat.Dispose();
            return new Run(outVerts, outIndices);
        }

        /// <summary>The interior triangle alone — the reference arm.</summary>
        /// <returns>Its subdivision.</returns>
        private static Run InteriorOnly() => Subdivide(
            new[] { new double2(0, 0), new double2(0, Extent), new double2(Extent, Extent) },
            new[] { float3.zero, float3.zero, float3.zero },
            new[] { 0, 1, 2 });

        /// <summary>The same interior triangle plus one band quad on its <c>x = 0</c> edge, in the layout
        /// <c>FillBandJob</c> emits: inner and outer at the SAME tile coordinate, inner carrying
        /// <c>(0,0,0)</c>, and the two triangles <c>(inner_i, outer_i, outer_j)</c> /
        /// <c>(inner_i, outer_j, inner_j)</c>.</summary>
        /// <returns>Its subdivision.</returns>
        private static Run InteriorPlusBand() => Subdivide(
            new[]
            {
                new double2(0, 0), new double2(0, Extent), new double2(Extent, Extent), // interior
                new double2(0, 0), new double2(0, 0),                                   // band inner/outer @ P0
                new double2(0, Extent), new double2(0, Extent),                         // band inner/outer @ P1
            },
            new[] { float3.zero, float3.zero, float3.zero, float3.zero, OuterBand, float3.zero, OuterBand },
            new[] { 0, 1, 2, /* quad */ 3, 4, 6, 3, 6, 5 });

        // ── The refusal gate: the interior is untouched, and the band conforms to it ────────────────────

        /// <summary>
        /// Subdividing the interior triangle produces bit-identical output whether or not band quads are in
        /// the same input — the band appends, it never perturbs. And the band's own share of the output
        /// splits the shared edge at exactly the same tile coordinates the interior does, which is the
        /// conformance claim that lets the band ride through subdivision at all.
        ///
        /// <para>RED-verify: reverse the band quad's two triangles into non-degenerate tile positions (give
        /// the outer vertices a real tile offset) and the shared-edge sets diverge; the split points stop
        /// agreeing because the marks stop being computed from the same endpoints.</para>
        /// </summary>
        [Test]
        public void ABandQuadSplitsTheSharedEdgeExactlyWhereTheInteriorDoes()
        {
            using Run reference = InteriorOnly();
            using Run banded = InteriorPlusBand();

            Assert.Greater(reference.Vertices.Length, 3,
                "precondition: the whole-world tile must actually subdivide, or every assertion here is " +
                "vacuous.");
            Assert.Greater(banded.Vertices.Length, reference.Vertices.Length,
                "the band quad must contribute vertices of its own.");

            // The subdivider drains its stack per source triangle, so the interior triangle's whole subtree
            // is a PREFIX of the banded run's output.
            for (int i = 0; i < reference.Vertices.Length; i++)
            {
                GlobeFillVertex a = reference.Vertices[i], b = banded.Vertices[i];
                Assert.AreEqual(a.Tile.x, b.Tile.x, 0.0, $"interior vertex {i}: Tile.x must be bit-identical");
                Assert.AreEqual(a.Tile.y, b.Tile.y, 0.0, $"interior vertex {i}: Tile.y must be bit-identical");
                Assert.AreEqual(a.World.x, b.World.x, 0.0, $"interior vertex {i}: World.x must be bit-identical");
                Assert.AreEqual(0.0, b.Band.z, 0.0,
                    $"interior vertex {i} carries a band coordinate. The interior is never displaced.");
            }

            // Conformance: the split points along the shared x = 0 edge, taken from the interior's share and
            // from the band's share, must be the same set.
            SortedSet<double> interiorSplits = SharedEdgeSplits(reference.Vertices, 0, reference.Vertices.Length);
            SortedSet<double> bandSplits = SharedEdgeSplits(banded.Vertices, reference.Vertices.Length, banded.Vertices.Length);

            Assert.Greater(interiorSplits.Count, 2,
                "precondition: the shared edge must genuinely split, or the sets below agree trivially.");
            Assert.That(bandSplits, Is.EquivalentTo(interiorSplits),
                "the band quad's split of the shared edge does not match the interior's. A mark is a " +
                "function of an edge's two endpoints alone, and a band quad's long edge has the same two " +
                "endpoints as the interior edge it abuts — so any divergence here is a T-junction, a visible " +
                "crack between the fill and its own band at low zoom.");
        }

        /// <summary>Distinct tile-y coordinates of every vertex on the shared <c>x = 0</c> edge, over one
        /// slice of the output.</summary>
        /// <param name="verts">The subdivided vertices.</param>
        /// <param name="from">First index of the slice.</param>
        /// <param name="to">One past the last index of the slice.</param>
        /// <returns>The distinct y coordinates found.</returns>
        private static SortedSet<double> SharedEdgeSplits(NativeList<GlobeFillVertex> verts, int from, int to)
        {
            var ys = new SortedSet<double>();
            for (int i = from; i < to; i++)
                if (verts[i].Tile.x == SharedEdgeX) ys.Add(verts[i].Tile.y);
            return ys;
        }

        // ── The band does not perturb the interior's subdivision ───────────────────────────────────────

        /// <summary>Builds the z0 countries fixture on the sphere, with or without the boundary band, and
        /// returns the vertices of its INTERIOR triangles only, in triangle order.
        ///
        /// <para><b>The interior/band discriminator is per-TRIANGLE, and it is exact.</b> A band leaf
        /// triangle always retains at least one vertex with <c>side &gt; 0</c>: <c>side</c> is affine over a
        /// source triangle, so its zero set is a LINE (the band's inner edge), and a sub-triangle with all
        /// three vertices on a line would be degenerate — subdivision produces none. So "all three vertices
        /// carry side 0" identifies interior triangles with no false positives.</para></summary>
        /// <param name="suppressBand">True to build with no band geometry at all.</param>
        /// <param name="interior">Vertices of the interior triangles, in triangle order.</param>
        /// <param name="totalVertices">The whole mesh's vertex count, band included.</param>
        /// <param name="bounds">The built mesh's bounds.</param>
        private static void BuildGlobeFixture(
            bool suppressBand, out Vector3[] interior, out int totalVertices, out Bounds bounds)
        {
            using var bag = new ObjectDisposalBag();
            var (go, material) = FillSceneHelper.BuildFillGo(
                projection: new SphericalProjection(), suppressBoundaryBand: suppressBand);
            bag.Track(go);
            bag.Track(material);
            {
                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                var band = new List<Vector3>();
                mesh.GetUVs(3, band);

                var kept = new List<Vector3>(vertices.Length);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (band[a].z != 0f || band[b].z != 0f || band[c].z != 0f) continue;
                    kept.Add(vertices[a]); kept.Add(vertices[b]); kept.Add(vertices[c]);
                }

                interior = kept.ToArray();
                totalVertices = mesh.vertexCount;
                bounds = mesh.bounds;
            }
        }

        /// <summary>
        /// The band changes nothing about the interior, on the real z0 countries fixture: the interior
        /// triangles' vertex stream is IDENTICAL, element for element, with and without the band.
        ///
        /// <para><b>Why this compares interior-to-interior and not stream-to-stream.</b> Subdivision
        /// re-emits every vertex in traversal order and <c>FillBandJob</c> interleaves each feature's band
        /// triangles after that feature's own interior triangles, so the two whole streams are interleaved,
        /// not nested. An in-order positional match over the raw streams would be a SUBSEQUENCE test, and a
        /// band vertex sits on a ring vertex — a position the interior also carries — so it could advance
        /// the match pointer and pass for the wrong reason. Filtering to interior triangles first (see
        /// <see cref="BuildGlobeFixture"/> for why that discriminator is exact) makes this a plain equality.
        /// Do not restore the subsequence form.</para>
        ///
        /// <para><b>Why this is stronger than a budget check.</b> The subdivider's only cross-triangle
        /// coupling is its budget test, so this property is what a correct budget split BUYS; asserting it
        /// directly holds no matter where any ceiling later sits. It is also the property the rendered
        /// six-pixel failure violated: a shared budget let band vertices starve the interior, triangles past
        /// the cutoff emitted flat, and a thin feature moved off the pixels it had covered.</para>
        ///
        /// <para>RED-verify: point <c>GlobeFillSubdivideJob</c>'s <c>overBudget</c> back at
        /// <c>OutVerts.Length &gt;= InteriorBudget</c> (the shared budget this replaced) and it fails,
        /// naming the first interior vertex that moved.</para>
        /// </summary>
        [Test]
        public void TheBandLeavesTheCurvedArmsInteriorAndBoundsAlone()
        {
            BuildGlobeFixture(true, out Vector3[] hard, out int hardTotal, out Bounds hardBounds);
            BuildGlobeFixture(false, out Vector3[] banded, out int bandedTotal, out Bounds bandedBounds);

            TestContext.WriteLine(
                $"[curved arm] band-free total={hardTotal} banded total={bandedTotal} " +
                $"interior band-free={hard.Length} interior banded={banded.Length} " +
                $"interior budget={GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices} " +
                $"total ceiling={GlobeFillSubdivideDispatch.DefaultMaxTotalVertices}");

            Assert.Greater(bandedTotal, hardTotal,
                "precondition: the banded build must actually carry band geometry.");
            Assert.Greater(banded.Length, 0, "precondition: the filter must find interior triangles.");
            Assert.Less(bandedTotal, GlobeFillSubdivideDispatch.DefaultMaxTotalVertices,
                $"the banded curved build ({bandedTotal}) reached the TOTAL allocation ceiling. That ceiling " +
                "is a backstop, not a working limit; hitting it on the shipped fixture means it is sized " +
                "wrong or the arm's cost has changed.");

            // The band's one-pixel displacement happens in the VERTEX SHADER, so a band vertex sits on its
            // ring vertex CPU-side and the mesh bounds cannot legitimately grow. If they do, the displacement
            // has moved out of the shader and the mechanism has changed underneath us — and any fixture that
            // fits a camera per mesh is then measuring the transform rather than the band.
            Assert.AreEqual(hardBounds, bandedBounds,
                $"the two builds' bounds differ (hard={hardBounds}, banded={bandedBounds}).");

            Assert.AreEqual(hard.Length, banded.Length,
                $"the interior emitted a different number of vertices with the band present " +
                $"({hard.Length} without, {banded.Length} with). Band geometry must never reach geometry " +
                "that is not its own — the subdivider's budget test is the only path by which it can.");
            for (int i = 0; i < hard.Length; i++)
                if (hard[i] != banded[i])
                    Assert.Fail(
                        $"interior vertex {i} moved when the band was added: {hard[i]} -> {banded[i]}. " +
                        "The band starved the interior's subdivision budget and triangles past the cutoff " +
                        "emitted flat.");
        }

        /// <summary>
        /// The curved arm's INTERIOR keeps real headroom under its own subdivision budget on the shipped
        /// fixture. Past that budget the subdivider forces triangles to emit flat regardless of their marks,
        /// so exhausting it is a silent quality regression — the globe's fills facet — with no error and no
        /// failing test anywhere else.
        ///
        /// <para><b>The fence is 95%, and here is the whole trade.</b> Measured when it was written:
        /// 164 535 of 200 000, or 82.3%. It is not set at the measurement, because a fence at the
        /// measurement is one no regression fails. It is not set at 100% either, because past the budget the
        /// subdivider emits flat regardless of marks and the globe facets SILENTLY — the fence has to fire
        /// while there is still something to do about it.
        /// <b>What it will NOT catch: interior growth under ~25 500 vertices passes silently.</b> That is the
        /// cost of 95% over a tighter 90%, stated so the number does not read as arbitrary and so the next
        /// reader moves it deliberately or not at all.</para>
        ///
        /// <para>Getting 164 535 down is <b>UMR-103</b>'s job, not this fence's — this one only stops it
        /// getting worse. The claim used to live as prose in <c>DefaultMaxInteriorVertices</c>' own doc
        /// ("never reached in production"), where it was already 18% from false and nothing was measuring
        /// it.</para>
        /// </summary>
        [Test]
        public void TheCurvedArmsInteriorKeepsHeadroomUnderItsBudget()
        {
            BuildGlobeFixture(true, out Vector3[] hard, out _, out _);

            const double fence = 0.95;
            int ceiling = (int)(GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices * fence);
            TestContext.WriteLine($"[curved arm] band-free interior={hard.Length} fence={ceiling}");
            Assert.Less(hard.Length, ceiling,
                $"the curved arm's band-free interior is {hard.Length} vertices, past {fence:P0} of its " +
                $"{GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices} subdivision budget — it measured " +
                "164535 (82.3%) when this fence was written, so it has grown since. Past the budget " +
                "itself the subdivider emits flat and the globe's fills facet, silently. Raising the budget " +
                "to clear this fence is only correct if the extra peak allocation is affordable — it is not " +
                "a way to make the fence pass. Bringing the count itself down is tracked as UMR-103.");
        }

        // ── The attribute survives the lerp, and stays a displacement times a coverage coordinate ──────

        /// <summary>
        /// Across every subdivided band vertex the two halves of the attribute stay consistent:
        /// <c>|band.xy| == side</c> (these fixtures use a unit miter), <c>side</c> stays inside
        /// <c>[0,1]</c>, and at least one vertex lands STRICTLY between the two — the midpoint of a split
        /// inner→outer edge, which is what proves the subdivider interpolates the attribute rather than
        /// dropping or duplicating it.
        ///
        /// <para>The three assertions are not redundant. Dropping the band from the midpoint construction
        /// entirely leaves every vertex at <c>side</c> 0 or 1 with <c>|xy|</c> to match, so the ratio
        /// assertion stays GREEN and only the strictly-between one reds. Interpolating only one half reds
        /// the ratio.</para>
        ///
        /// <para>RED-verify: make the midpoint take one endpoint's band instead of their average (only the
        /// strictly-between assertion reds), then make it average only <c>xy</c> and take <c>z</c> from an
        /// endpoint (the ratio assertion reds).</para>
        /// </summary>
        [Test]
        public void SubdividingABandEdgeInterpolatesBothHalvesOfTheAttribute()
        {
            using Run banded = InteriorPlusBand();

            int strictlyBetween = 0;
            for (int i = 0; i < banded.Vertices.Length; i++)
            {
                float3 band = banded.Vertices[i].Band;
                Assert.IsFalse(math.any(math.isnan(band)), $"vertex {i}: the band attribute must never be NaN.");
                Assert.That(band.z, Is.InRange(0.0, 1.0),
                    $"vertex {i}: side must stay inside [0,1] — the band is one pixel wide and that is not " +
                    "a knob a midpoint may widen.");
                Assert.AreEqual(band.z, math.length(band.xy), 1e-12,
                    $"vertex {i}: |band.xy| must track side. The displacement and the coverage coordinate " +
                    "are interpolated together; a midpoint that lerps one and not the other puts the ramp " +
                    "somewhere other than where its geometry is.");
                if (band.z > 1e-9 && band.z < 1.0 - 1e-9) strictlyBetween++;
            }

            Assert.Greater(strictlyBetween, 0,
                "no subdivided vertex carries a side strictly between 0 and 1, so the subdivider is not " +
                "interpolating the band at all — it is copying an endpoint's value. The band's inner→outer " +
                "edges DO get split on this fixture; a run with only 0s and 1s means the attribute was " +
                "dropped from the midpoint construction.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeFillSubdividerTests — the globe fill subdivider Burst job — refines flat earcut triangles onto the sphere
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeFillSubdividerTests
    {
        private const double Extent = 4096;

        // One full-tile triangle → schedule the Burst subdivide job into NativeLists (caller disposes).
        private static void Run(IProjection proj, TileId id, int maxDepth, int budget,
            out NativeList<GlobeFillVertex> verts, out NativeList<int> idx)
        {
            var tileVerts = new NativeList<double2>(3, Allocator.Persistent);
            tileVerts.Add(new double2(0, 0)); tileVerts.Add(new double2(Extent, 0)); tileVerts.Add(new double2(0, Extent));
            var tris = new NativeList<int>(3, Allocator.Persistent); tris.Add(0); tris.Add(1); tris.Add(2);
            var feat = new NativeList<int>(3, Allocator.Persistent); feat.Add(0); feat.Add(0); feat.Add(0);
            var band = new NativeList<float3>(3, Allocator.Persistent);
            band.Add(float3.zero); band.Add(float3.zero); band.Add(float3.zero);

            verts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            idx   = new NativeList<int>(64, Allocator.Persistent);
            JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                proj, tileVerts, tris, feat, band, id, Extent, new double3(0, 0, 0),
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, maxDepth, budget,
                GlobeFillSubdivideDispatch.DefaultMaxTotalVertices, verts, idx, default);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            tileVerts.Dispose(); tris.Dispose(); feat.Dispose(); band.Dispose();
        }

        [Test]
        public void Globe_Subdivides_AndKeepsEveryVertexOnTheSphere()
        {
            Run(new SphericalProjection(), new TileId { Z = 3, X = 3, Y = 3 },
                GlobeFillSubdivideDispatch.DefaultMaxDepth, GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices,
                out var v, out var idx);
            try
            {
                Assert.Greater(v.Length, 3, "a 45° globe triangle must subdivide past the single flat triangle");
                // vertex sharing: OutVerts (v) is now the UNIQUE count and OutIndices
                // (idx) the EMITTED count — every 1→4 split's 3 midpoints are each shared by 3 of its 4
                // children (GlobeFillVertexKey), so a single-triangle subdivision this deep MUST show real
                // sharing, not just "no more than" the emitted count.
                Assert.Less(v.Length, idx.Length, "a multi-level split must produce SOME shared split-edge vertices");
                Assert.AreEqual(0, idx.Length % 3, "indices form whole triangles");

                double R = EarthConstants.A;
                for (int i = 0; i < v.Length; i++)
                {
                    GlobeFillVertex fv = v[i]; // origin = 0 ⇒ World IS the ECEF position
                    Assert.AreEqual(R, math.length(fv.World), R * 1e-6, $"vertex {i} on the sphere (midpoints re-projected)");
                    Assert.AreEqual(1.0, math.length(fv.Up),   1e-6, "up unit");
                    Assert.AreEqual(1.0, math.length(fv.East), 1e-6, "east unit");
                    Assert.AreEqual(0.0, math.dot(fv.Up, fv.East), 1e-6, "east ⊥ up (valid TBN per refined vertex)");
                }
            }
            finally { v.Dispose(); idx.Dispose(); }
        }

        [Test]
        public void Mercator_IsFlat_PassesThroughWithoutSubdivision()
        {
            Run(new WebMercatorProjection(), new TileId { Z = 0, X = 0, Y = 0 },
                GlobeFillSubdivideDispatch.DefaultMaxDepth, GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices,
                out var v, out var idx);
            try { Assert.AreEqual(3, v.Length, "a flat projection (constant up) must NOT subdivide"); }
            finally { v.Dispose(); idx.Dispose(); }
        }

        [Test]
        public void Budget_BoundsTheOutput_NoLowZoomExplosion()
        {
            // depth 8 unbounded ≈ 4^8·3 ≈ 196k verts for ONE whole-globe triangle; the 2 000 budget must cap it.
            Run(new SphericalProjection(), new TileId { Z = 0, X = 0, Y = 0 }, 8, 2000, out var v, out var idx);
            try
            {
                // vertex sharing: the budget counts EMITTED vertices (idx.Length, one
                // OutIndices.Add per Emit call) — v (OutVerts, the unique count) is always <= idx.Length, so
                // asserting on v no longer pins the bound that actually exists: sharing made it strictly
                // easier to pass without the budget doing any more work. Assert on idx.Length instead.
                Assert.Less(idx.Length, 20000, "the vertex budget must prevent the low-zoom subdivision explosion");
            }
            finally { v.Dispose(); idx.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeFillTangentTests — the fill Tangent stream carries the projection's per-vertex surface east
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeFillTangentTests : BaseTestFixture
    {

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
        protected override void OnTearDown()
        {
            try { TestDecodedTiles.DisposeAll(); }
            finally { base.OnTearDown(); }
        }
        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""Test"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>IR C1 P3: the fixture is decoded at the SAME z0 address every test builds at, and the
        /// layer (which owns its buffer) is returned instead of a detached feature list.</summary>
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static (ITileLayer layer, List<SelectedTileFeature> selection, Fill.PaintProperties paint) Setup()
        {
            var mvtTile   = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, SampleTileFixture.Bytes()));
            var fillLayer = MinimalStyle().Layers[0];
            var paint     = ((Fill.StyleLayer)fillLayer).Paint;
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must have the 'countries' layer");
            var selected  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0);
            Assert.Greater(selected.Count, 0);
            return (mvtLayer, selected, paint);
        }

        [Test]
        public void GlobeFill_Tangent_IsPerVertexEast_OrthonormalToNormal()
        {
            var (layer, selection, paint) = Setup();

            Mesh mesh = Track(TestTileMeshBuilder.BuildFillFromLayer(layer, selection, paint, 0.0, FixtureTile, new SphericalProjection()));
            Assert.IsNotNull(mesh, "globe fill must produce geometry");
            Vector4[] tan = mesh.tangents;
            Vector3[] nrm = mesh.normals;
            Assert.Greater(tan.Length, 0);
            Assert.AreEqual(nrm.Length, tan.Length);

            var first = new float3(tan[0].x, tan[0].y, tan[0].z);
            bool varies = false;
            int stride = math.max(1, tan.Length / 3000); // sample — the subdivided globe mesh can be large
            for (int i = 0; i < tan.Length; i += stride)
            {
                var t = new float3(tan[i].x, tan[i].y, tan[i].z);
                var n = new float3(nrm[i].x, nrm[i].y, nrm[i].z);
                Assert.AreEqual(1f, math.length(t), 1e-3f, $"tangent {i} must be unit-length");
                Assert.AreEqual(0f, math.dot(t, n),  2e-3f, $"tangent {i} must be ⊥ the surface normal");
                Assert.AreEqual(1f, tan[i].w, "bitangent sign must be +1");
                if (math.length(t - first) > 1e-2f) varies = true;
            }
            // The globe's east rotates over the sphere — a constant +X (the bug) would make this false.
            Assert.IsTrue(varies, "globe fill tangents must VARY per vertex (not a constant tangent)");
        }

        [Test]
        public void GlobeFill_IsSubdivided_ForCurvature_MercatorIsNot()
        {
            var (layer, selection, paint) = Setup(); // z0 tile spans the globe → heavy chording without C-3

            // Band-free on BOTH arms: this comparison is about SUBDIVISION, and both arms now emit an
            // outward boundary band whose vertex counts would otherwise contribute to the ratio below.
            Mesh flat  = Track(TestTileMeshBuilder.BuildFillFromLayer(
                layer, selection, paint, 0.0, FixtureTile, null, suppressBoundaryBand: true));
            Mesh globe = Track(TestTileMeshBuilder.BuildFillFromLayer(
                layer, selection, paint, 0.0, FixtureTile, new SphericalProjection(), suppressBoundaryBand: true));
            Assert.IsNotNull(flat); Assert.IsNotNull(globe);

            // C-3 refines flat earcut triangles onto the sphere → strictly more TRIANGLES than the flat
            // build, while Mercator stays exactly the un-subdivided earcut output.
            // vertex sharing: was asserted on vertexCount, which sharing now shrinks
            // (fewer unique vertices for the same triangle set) — triangle/index count is what subdivision
            // actually grows, and sharing never touches it (every leaf triangle still emits exactly 3 indices,
            // shared storage or not), so it stays the right, sharing-invariant signal for "did it subdivide".
            Assert.Greater(globe.triangles.Length, flat.triangles.Length * 2,
                "globe fill must subdivide for curvature (many more triangles than the flat Mercator build)");
        }

        [Test]
        public void MercatorFill_Tangent_StaysConstantPlusX()
        {
            var (layer, selection, paint) = Setup();

            Mesh mesh = Track(TestTileMeshBuilder.BuildFillFromLayer(layer, selection, paint, 0.0, FixtureTile, null)); // null ⇒ WebMercator
            Assert.IsNotNull(mesh);
            Vector4[] tan = mesh.tangents;
            Assert.Greater(tan.Length, 0);
            foreach (var t in tan)
                Assert.AreEqual(new Vector4(1f, 0f, 0f, 1f), t, "Mercator fill tangent must stay constant +X (byte-identical)");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeFillWindingTests — the fill front face points out of the surface on both globe and Mercator
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeFillWindingTests : BaseTestFixture
    {

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
        protected override void OnTearDown()
        {
            try { TestDecodedTiles.DisposeAll(); }
            finally { base.OnTearDown(); }
        }
        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""Test"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        /// <summary>IR C1 P3: the fixture is decoded at the SAME z0 address every test builds at, and the
        /// layer (which owns its buffer) is returned instead of a detached feature list.</summary>
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static (ITileLayer layer, List<SelectedTileFeature> selection, Fill.PaintProperties paint) Setup()
        {
            var mvtTile   = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, SampleTileFixture.Bytes()));
            var fillLayer = MinimalStyle().Layers[0];
            var paint     = ((Fill.StyleLayer)fillLayer).Paint;
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must have the 'countries' layer");
            var selected  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0);
            Assert.Greater(selected.Count, 0);
            return (mvtLayer, selected, paint);
        }

        /// <summary>Tally the sign of the angle between each triangle's geometric face normal
        /// (cross(b-a, c-a)) and its outward surface normal; returns (dominantSign, uniformity ∈ [0.5,1]).
        /// Skips edge-on slivers: the 1→4 midpoint subdivision on the globe produces thin triangles whose
        /// face normal is numerical noise (≈⊥ the surface). A well-formed sub-triangle spans ≤3° of curvature,
        /// so its face normal is within a few degrees of the surface normal (cosine≈+1); a genuinely BACK-facing
        /// triangle reads cosine≈−1 — both survive the |cosine|>0.5 gate, only the noise is dropped.</summary>
        private static (int sign, double uniformity, int counted) WindingSign(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            Vector3[] n = mesh.normals;
            int[]     t = mesh.triangles;
            Assert.Greater(n.Length, 0, "mesh must carry surface normals");

            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                float3 va = v[t[i]], vb = v[t[i + 1]], vc = v[t[i + 2]]; // Vector3→float3 at the mesh boundary
                float3 g = math.cross(vb - va, vc - va);  // right-handed face normal
                float3 nn = n[t[i]];
                float gm = math.length(g), nm = math.length(nn);
                if (gm <= 0f || nm <= 0f) continue;
                // Skip near-degenerate NEEDLE slivers (high aspect ratio): the globe fill's edge-conforming
                // subdivision leaves antimeridian-spanning earcut slivers (an earcut concern, not a winding
                // one — ~0.08% of z0 fill area) whose geometric face normal is likewise numerical noise, but
                // not edge-on enough to trip the |cos|<0.5 gate below. thinness = 2·area/longestEdge² =
                // gm/longestEdge²; well-formed ≈0.4+, a needle ≈<0.02. This keeps the check measuring
                // MVT/earcut orientation consistency on WELL-FORMED triangles (its stated intent) — it does
                // NOT lower the 0.99 uniformity bar.
                float longestSq = math.max(math.lengthsq(vb - va), math.max(math.lengthsq(vc - vb), math.lengthsq(va - vc)));
                if (longestSq > 0f && gm / longestSq < 0.02f) continue; // needle sliver — face-normal noise
                float cos = math.dot(g, nn) / (gm * nm);  // angle between face normal and surface up
                if (math.abs(cos) < 0.5f) continue;       // edge-on sliver (subdivision noise) — skip
                if (cos > 0f) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate triangles to measure");
            int sign = pos >= neg ? 1 : -1;
            double uniformity = (double)math.max(pos, neg) / counted;
            return (sign, uniformity, counted);
        }

        [Test]
        public void GlobeFill_WindsSameAsMercator_RelativeToSurfaceNormal()
        {
            var (layer, selection, paint) = Setup(); // z0 countries: globe path fully subdivides (curvature)

            Mesh flat  = Track(TestTileMeshBuilder.BuildFillFromLayer(layer, selection, paint, 0.0, FixtureTile, null)); // WebMercator
            Mesh globe = Track(TestTileMeshBuilder.BuildFillFromLayer(layer, selection, paint, 0.0, FixtureTile, new SphericalProjection()));
            Assert.IsNotNull(flat,  "Mercator fill must produce geometry");
            Assert.IsNotNull(globe, "globe fill must produce geometry");

            var (mSign, mUnif, mN) = WindingSign(flat);
            var (gSign, gUnif, gN) = WindingSign(globe);

            var w = TestContext.Out;
            w.WriteLine($"Mercator: sign={mSign,2}  uniformity={mUnif:0.0000}  triangles={mN}");
            w.WriteLine($"Globe   : sign={gSign,2}  uniformity={gUnif:0.0000}  triangles={gN}");
            w.Flush();

            // Each build must be near-uniform (MVT/earcut winding is deterministic — no mixed orientation).
            Assert.Greater(mUnif, 0.99, "Mercator fill winding is not uniform (mixed-orientation triangles)");
            Assert.Greater(gUnif, 0.99, "globe fill winding is not uniform (mixed-orientation triangles)");

            // ABSOLUTE invariant (post the GPU-boundary winding reversal in StyledFillTileBuilder): each emitted
            // triangle's right-handed face normal must ALIGN with the outward surface normal (sign +1) — the
            // front face genuinely points OUT of the surface. That is exactly the orientation stock Cull Back
            // (shipped MapFill.mat _Cull:2) keeps for the camera-facing surface. Same convention as the
            // LayerOrderSnapshotTests fill quad: {0,2,1,0,3,2} with a +Y normal ⇒ dot(cross_RH, up) = +1, which
            // renders under Cull Back; the old {0,1,2} gave −1 and needed the compensating Cull Front. A sign of
            // −1 here means the reversal was dropped and the render is inverted (front-renders-as-back). This
            // catches the inversion that the earlier relative-only guard could not — it stayed green while both
            // sides flipped together.
            Assert.AreEqual(1, mSign, "Mercator fill front face must point OUT of the surface (Unity-front under stock Cull Back)");
            Assert.AreEqual(1, gSign, "globe fill front face must point OUT of the surface (Unity-front under stock Cull Back)");
            // Consistency: globe must wind the SAME as Mercator relative to its outward normal, so one cull mode
            // is correct for both projections.
            Assert.AreEqual(mSign, gSign,
                "globe fill winds OPPOSITE to Mercator relative to the surface normal — back-face culling that " +
                "shows Mercator would hide the near hemisphere on the globe (the reported glitch).");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeLineSubdivisionTests — falsifiable teeth for the globe line curvature subdivision
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeLineSubdivisionTests
    {
        // Unit ECEF surface normal at (lon,lat) degrees — the per-point "up" the subdivider measures arcs from.
        private static double3 Normal(double lonDeg, double latDeg)
        {
            double lam = lonDeg * math.PI_DBL / 180.0;
            double phi = latDeg * math.PI_DBL / 180.0;
            double cp = math.cos(phi);
            return new double3(cp * math.cos(lam), cp * math.sin(lam), math.sin(phi));
        }

        private static System.Collections.Generic.List<double2> Subdivide(double2 a, double2 b, double3 upA, double3 upB)
            => LineCurvatureSubdivision.Subdivide(
                new[] { a, b }, new[] { upA, upB }, SphericalProjection.MaxCurveSegmentRad);

        [Test]
        public void LongArc_IsDensifiedProportionalToSpan()
        {
            // 90° arc (equator, 0°→90° lon) at ~2°/step ⇒ ≈ 45 sub-segments ⇒ ≈ 46 points.
            int c90 = Subdivide(new double2(0, 0), new double2(4096, 0), Normal(0, 0), Normal(90, 0)).Count;
            Assert.GreaterOrEqual(c90, 45, "90° arc must split into ~45 sub-segments");
            Assert.LessOrEqual(c90, 47, "…and not wildly more (≈ 90°/2°)");

            // 30° arc ⇒ ≈ 15 sub-segments — strictly fewer than the 90° case (proportional to span).
            int c30 = Subdivide(new double2(0, 0), new double2(4096, 0), Normal(0, 0), Normal(30, 0)).Count;
            Assert.Less(c30, c90, "a shorter arc must produce fewer points");
            Assert.GreaterOrEqual(c30, 15, "30° arc must split into ~15 sub-segments");
        }

        [Test]
        public void ShortArc_IsNotSubdivided()
        {
            // 1° arc < the 2° threshold ⇒ no subdivision ⇒ just the 2 endpoints.
            int c = Subdivide(new double2(10, 20), new double2(30, 20), Normal(0, 0), Normal(1, 0)).Count;
            Assert.AreEqual(2, c, "a segment under the arc threshold stays a single chord (2 points)");
        }

        [Test]
        public void SubPoints_LieOnTheTileSpaceChord_EndpointsPreserved()
        {
            var a = new double2(100, 200);
            var b = new double2(900, 600);
            var sub = Subdivide(a, b, Normal(0, 0), Normal(60, 0));
            int c = sub.Count;

            Assert.AreEqual(a, sub[0], "first point preserved");
            Assert.AreEqual(b, sub[c - 1], "last point preserved");
            // Every sub-point is the linear interpolation a→b (collinear, monotone in x).
            for (int i = 1; i < c; i++)
            {
                double t = (sub[i].x - a.x) / (b.x - a.x);
                double2 expected = a + (b - a) * t;
                Assert.AreEqual(expected.y, sub[i].y, 1e-6, $"sub-point {i} off the chord");
                Assert.Greater(t, (sub[i - 1].x - a.x) / (b.x - a.x) - 1e-9, "monotone along the chord");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeLineWindingTests — the line ribbon's front face points out of the surface on both globe and Mercator
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeLineWindingTests : BaseTestFixture
    {
        // z6 and z9 boundary_3 fixtures — winding is independent of curvature subdivision, so either zoom works.
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void GlobeLine_WindsSameAsMercator_RelativeToSurfaceNormal(string fixture, int z, int x, int y)
        {
            var id = new TileId { Z = z, X = x, Y = y };

            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json")));
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            // These two struct literals bind to BuildLineFromLayer<TProj>, not the IProjection-typed
            // overload — see that overload's own doc note.
            Mesh flat  = Track(TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, layer.Paint, layer.Layout, z, id, new WebMercatorProjection()));
            Mesh globe = Track(TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, layer.Paint, layer.Layout, z, id, new SphericalProjection()));
            Assert.IsNotNull(flat,  "Mercator line must produce geometry");
            Assert.IsNotNull(globe, "globe line must produce geometry");

            var (mSign, mUnif, mN) = RibbonWindingSign(flat);
            var (gSign, gUnif, gN) = RibbonWindingSign(globe);

            var w = TestContext.Out;
            w.WriteLine($"[{fixture}] Mercator: sign={mSign,2} uniformity={mUnif:0.0000} tris={mN}");
            w.WriteLine($"[{fixture}] Globe   : sign={gSign,2} uniformity={gUnif:0.0000} tris={gN}");
            w.Flush();

            Assert.Greater(mUnif, 0.99, "Mercator ribbon winding is not uniform");
            Assert.Greater(gUnif, 0.99, "globe ribbon winding is not uniform");
            // ABSOLUTE invariant (post the GPU-boundary winding reversal in StyledLineTileBuilder): the extruded
            // ribbon's front face points OUT of the surface (sign +1), the orientation stock Cull Back (shipped
            // MapLine.mat _Cull:2) keeps for the camera-facing side. Sign −1 means the reversal was dropped and
            // the ribbon renders inverted. Closes the hole the relative-only check left (both sides could flip
            // together and stay green).
            Assert.AreEqual(1, mSign, $"{fixture}: Mercator ribbon front face must point OUT (Unity-front under stock Cull Back)");
            Assert.AreEqual(1, gSign, $"{fixture}: globe ribbon front face must point OUT (Unity-front under stock Cull Back)");
            Assert.AreEqual(mSign, gSign,
                $"{fixture}: globe line ribbon winds OPPOSITE to Mercator relative to the surface normal — " +
                "back-face culling that shows Mercator would hide the near hemisphere on the globe.");
        }

        /// <summary>Reconstruct the shader-extruded ribbon (pos + across·W — the shader bakes the per-side sign
        /// INTO `across`/extrudeN, so there is NO ·side here; `side` is AA-only) and tally the sign of the angle
        /// between each triangle's face normal and its surface normal. W is adaptive PER TRIANGLE — a small
        /// fraction of the local centerline edge — so the ribbon stays locally thin and never folds at a sharp
        /// turn (a fixed absolute W folds, and folds the globe's subdivided-shorter segments differently). This
        /// is index-order-sensitive (the reversed winding is exactly what the flip fixes). Join/cap fans, whose
        /// three verts share one centerline point (edge≈0), are skipped — their winding is ambiguous.</summary>
        internal static (int sign, double uniformity, int counted) RibbonWindingSign(Mesh mesh)
        {
            Vector3[] p   = mesh.vertices;
            Vector3[] nrm = mesh.normals;
            var across = new List<Vector3>(); mesh.GetUVs(0, across); // TexCoord0 = across (per-side sign baked in)
            int[] t = mesh.triangles;
            Assert.AreEqual(p.Length, across.Count, "across stream must be present");

            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                float3 pa = p[t[i]], pb = p[t[i + 1]], pc = p[t[i + 2]]; // Vector3→float3 at the mesh boundary
                // Local scale = longest centerline edge; w = 5% of it keeps the extruded ribbon thin (no fold).
                float d = math.max(math.distance(pa, pb), math.max(math.distance(pb, pc), math.distance(pc, pa)));
                if (d < 1e-4f) continue; // join/cap fan collapsed to one centerline point — ambiguous, skip
                float w = 0.05f * d;
                float3 ea = pa + (float3)across[t[i]]     * w;
                float3 eb = pb + (float3)across[t[i + 1]] * w;
                float3 ec = pc + (float3)across[t[i + 2]] * w;
                float3 g   = math.cross(eb - ea, ec - ea);      // extruded face normal
                float3 n   = nrm[t[i]];
                float gm = math.length(g), nm = math.length(n);
                if (gm <= 0f || nm <= 0f) continue;
                float cos = math.dot(g, n) / (gm * nm);
                if (math.abs(cos) < 0.5f) continue; // edge-on sliver
                if (cos > 0f) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate ribbon triangles to measure");
            int sign = pos >= neg ? 1 : -1;
            double uniformity = (double)math.max(pos, neg) / counted;
            return (sign, uniformity, counted);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobePlacementTests — numerical handedness teeth for camera-relative globe placement
    // ───────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>A curved, RIGHT-handed projection: un-swapped ECEF (det(TangentBasis) = +1), the mirror of
    /// <see cref="SphericalProjection"/>'s left-handed swap. Only the geometry surface is real (the line builder
    /// uses <see cref="ProjectPoint"/> + <see cref="MaxRefineAngleRad"/>); the camera-interaction members throw.</summary>
    public readonly struct RightHandedSphereProjection : IProjection
    {
        public const double Radius = EarthConstants.A;

        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);

            // Radial normal (unit), and the surface point at Radius — NO axis swap (raw ECEF is render space).
            double upX = cosPhi * cosLam, upY = cosPhi * sinLam, upZ = sinPhi;
            return new ProjectedPoint
            {
                World = new double3(upX * Radius, upY * Radius, upZ * Radius),
                Up    = new double3(upX,          upY,          upZ),
            };
        }

        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;
        public double3 UpAt(in GeoCoordinate geo)    => ProjectPoint(geo).Up;

        public float3x3 TangentBasisAt(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);
            double3 up   = new double3(cosPhi * cosLam, cosPhi * sinLam, sinPhi);
            double3 east = new double3(-sinLam, cosLam, 0.0);
            double3 north = math.cross(up, east);
            // No axis swap → right-handed ENU (det +1). c0=east, c1=up, c2=north.
            return new float3x3((float3)east, (float3)up, (float3)north);
        }

        public double MetersPerUnit    => 1.0;
        public double MaxRefineAngleRad => SphericalProjection.MaxCurveSegmentRad; // curved — same tolerance as the sphere

        public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
        {
            renderCentre = default; radius = 0.0; return false; // unused by the line builder
        }

        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera)
            => throw new System.NotSupportedException("RightHandedSphereProjection is a geometry-only test double.");
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera)
            => throw new System.NotSupportedException("RightHandedSphereProjection is a geometry-only test double.");
        public double ClampValidLatitude(double latitudeDegrees) => math.clamp(latitudeDegrees, -90.0, 90.0);
        public bool IsFinitePlanarWorld => false;
        public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in CameraProperties camera)
            => throw new System.NotSupportedException("RightHandedSphereProjection is a geometry-only test double.");
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RightHandedSphereProjectionWindingTests — a throwaway right-handed sphere projection still winds like flat Mercator
    // ───────────────────────────────────────────────────────────────────────────────────

    public class RightHandedSphereProjectionWindingTests : BaseTestFixture
    {
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void RightHandedCurvedLine_WindsSameAsMercator_RelativeToSurfaceNormal(string fixture, int z, int x, int y)
        {
            var id = new TileId { Z = z, X = x, Y = y };

            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json")));
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            // These two struct literals bind to BuildLineFromLayer<TProj>, not the IProjection-typed
            // overload (this is tooth (g)'s own point for the second: RightHandedSphereProjection is never
            // registered with Burst) — see that overload's own doc note.
            Mesh flat  = Track(TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, layer.Paint, layer.Layout, z, id, new WebMercatorProjection()));
            Mesh rh    = Track(TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, layer.Paint, layer.Layout, z, id, new RightHandedSphereProjection()));
            Assert.IsNotNull(flat, "Mercator line must produce geometry");
            Assert.IsNotNull(rh,   "right-handed curved line must produce geometry (managed ProjectPoint path)");

            var (mSign, mUnif, _) = GlobeLineWindingTests.RibbonWindingSign(flat);
            var (rSign, rUnif, _) = GlobeLineWindingTests.RibbonWindingSign(rh);

            Assert.Greater(mUnif, 0.99, "Mercator ribbon winding is not uniform");
            Assert.Greater(rUnif, 0.99, "right-handed curved ribbon winding is not uniform");
            Assert.AreEqual(mSign, rSign,
                $"{fixture}: a RIGHT-handed curved projection winds OPPOSITE to Mercator — winding is being decided " +
                "by curvature/handedness, not derived from cross(along, up). This is the S100 shortcut the tooth forbids.");
        }
    }
}
