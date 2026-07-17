// Engine-free: no UnityEngine dependency. The stateless ProjectPoint uses only Unity.Mathematics, so Burst
// compiles it transitively when ProjectPointsJob calls SphericalProjection.ProjectPoint(...).

using Unity.Mathematics;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Simple-sphere globe projection: geodetic → ECEF on a sphere of radius <see cref="Radius"/>
    /// (eccentricity ignored). The strictly-better WGS-84 <c>EllipsoidalProjection</c> is a future drop-in (a
    /// new <see cref="IProjection"/> impl, zero pipeline edits). ECEF axes are swapped to render space
    /// <c>(X, Z, Y)</c> (docs §7); the surface up is the radial normal (exactly unit by construction).
    ///
    /// <para>Implements the SAME <see cref="IProjection"/> as <see cref="WebMercatorProjection"/> — the second
    /// implementation that proves the projection abstraction genuinely serves both planar and globe. The
    /// geometry surface (<see cref="ProjectPoint"/> / <see cref="Project"/> / <see cref="UpAt"/>) is real and
    /// Burst-callable; the camera-interaction methods (screen↔ground) need a 3D globe-camera pose that is a
    /// separate future stage and throw until then.</para>
    /// </summary>
    public readonly struct SphericalProjection : IProjection
    {
        /// <summary>The globe's sphere radius, metres. Fixed to the WGS-84 semi-major axis so the globe matches
        /// the Web Mercator world scale (<see cref="WebMercator.R"/>). Projections are stateless — the radius is
        /// a constant, not per-instance config.</summary>
        public const double Radius = EarthConstants.A;

        // ── Projection math — the single shared kernel (stateless struct → usable in Burst & managed) ──

        /// <summary>
        /// The spherical projection kernel: geodetic → render-space ECEF position on the sphere of
        /// <see cref="Radius"/> + the radial surface up. STATELESS — the only input is the geodetic point;
        /// uses only <c>Unity.Mathematics</c>, so Burst compiles it when
        /// <c>ProjectPointsJob&lt;SphericalProjection&gt;</c> calls it through the generic constraint; the OOP
        /// <see cref="Project"/>/<see cref="UpAt"/> call it too.
        /// </summary>
        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0; // longitude, radians
            double phi    = geo.Latitude  * math.PI_DBL / 180.0; // latitude, radians

            double sinPhi = math.sin(phi), cosPhi = math.cos(phi);
            double sinLam = math.sin(lambda), cosLam = math.cos(lambda);

            // Radial (geodetic) normal in ECEF — unit by construction (no normalize needed).
            double upX = cosPhi * cosLam, upY = cosPhi * sinLam, upZ = sinPhi;
            double rr  = Radius; // surface point (no elevation); elevated GeoCoordinate3D is a future path

            // Axis-swap ECEF (X,Y,Z) → render (X,Z,Y) (docs §7), matching Ecef.Forward. Built via the double3
            // constructor (no double3 arithmetic ops), matching Ecef.Forward and the core-tests shim.
            return new ProjectedPoint
            {
                World = new double3(upX * rr, upZ * rr, upY * rr),
                Up    = new double3(upX,      upZ,      upY),
            };
        }

        // ── Geometry (build side; docs §6) — conveniences forwarding to the kernel ─────────────────

        /// <inheritdoc/>
        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;

        /// <inheritdoc/>
        public double3 UpAt(in GeoCoordinate geo) => ProjectPoint(geo).Up;

        /// <inheritdoc/>
        public float3x3 TangentBasisAt(in GeoCoordinate geo) => Ecef.TangentBasis(geo); // render-space ENU at the point

        /// <inheritdoc/>
        public double MetersPerUnit => 1.0; // render units are ECEF metres

        /// <summary>Line-centerline subdivision tolerance: the max great-circle arc one centerline segment may
        /// span before it is split (~2°, sagitta ≈ 1 km — sub-pixel at whole-globe scale). Surfaced as the
        /// <see cref="MaxRefineAngleRad"/> policy so the builder subdivides generically, with no type check.</summary>
        public const double MaxCurveSegmentRad = 2.0 * math.PI_DBL / 180.0;

        /// <inheritdoc/>
        public double MaxRefineAngleRad => MaxCurveSegmentRad; // sphere — split chords that would sag off the surface

        /// <inheritdoc/>
        public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
        {
            // Render-space globe: sphere of Radius centred R below the look-at (look-at surface point at origin,
            // +Y radial), so the centre is R straight down. Same regardless of look-at (the rebase makes it so).
            renderCentre = new double3(0.0, -Radius, 0.0); radius = Radius; return true;
        }

        // ── Camera interaction (managed side) — globe orbit ray-cast (S91-C) ──────────────────────
        //
        // Reconstructs the SAME render-space camera the renderer builds (MapCamera.SyncToCamera: altitude from
        // AltitudeForZoom, orbit via CameraPoseMath.ComputeRelativePose, look-at at the render origin, +Y up) and the
        // look-at ENU frame the geometry is rebased into (S91-C Slice 1). The render-space globe is a sphere of
        // radius R centred at (0, −R, 0): the look-at surface point sits at the origin (+Y up), so the sphere
        // centre is R straight down. ScreenToGround casts the pixel ray at that sphere; GroundToScreen is the
        // exact inverse (perspective-project the rebased ECEF point). Consistency with the rendered camera is
        // what pins the grabbed point under the cursor across a drag (the anchored-pan fixed-point iteration).
        //
        // AltitudeMultiplier (MapCamera art-direction knob, not carried on CameraProperties) is assumed 1 — the
        // demo default; a non-default value would offset the reconstructed camera from the rendered one.

        // Incidence-cosine below which the surface is treated as edge-on (no stable pan anchor). ~0.12 ≈ 7° off
        // the limb — a thin band that, in screen space near the limb where dr/dθ→0, is only a few pixels wide.
        private const double LimbGrazingCosine = 0.12;

        private static double3 Sub(double3 a, double3 b)  => new double3(a.x - b.x, a.y - b.y, a.z - b.z);
        private static double3 Add(double3 a, double3 b)  => new double3(a.x + b.x, a.y + b.y, a.z + b.z);
        private static double3 Scale(double3 a, double s) => new double3(a.x * s, a.y * s, a.z * s);

        /// <summary>Reconstructs the render-space camera (position + forward/up/right) and the render-ECEF ENU
        /// basis at the look-at (east/up/north, double) plus the look-at's render-ECEF position (sceneOrigin).</summary>
        private static void ReconstructView(in CameraProperties cam, double2 vp,
            out double3 camPos, out double3 fwd, out double3 up, out double3 right,
            out double3 east, out double3 upR, out double3 north, out double3 sceneOrigin)
        {
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, vp.y, cam.VerticalFovDeg);
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value, out camPos, out fwd, out up);
            right = math.cross(up, fwd); // Unity left-handed screen basis: right = up × forward

            // Render-ECEF ENU basis at the look-at (double precision; axis-swap (X,Z,Y) matching ProjectPoint).
            double lam  = cam.LookAt.Longitude * math.PI_DBL / 180.0;
            double phi  = cam.LookAt.Latitude  * math.PI_DBL / 180.0;
            double sinP = math.sin(phi), cosP = math.cos(phi), sinL = math.sin(lam), cosL = math.cos(lam);
            double3 upE   = new double3(cosP * cosL, cosP * sinL, sinP);
            double3 eastE = new double3(-sinL, cosL, 0.0);
            double3 northE = math.cross(upE, eastE);
            east  = new double3(eastE.x,  eastE.z,  eastE.y);
            upR   = new double3(upE.x,    upE.z,    upE.y);
            north = new double3(northE.x, northE.z, northE.y);
            sceneOrigin = Scale(upR, Radius); // == ProjectPoint(lookAt).World
        }

        /// <inheritdoc/>
        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera)
            => CastGlobe(screenPx, viewportPx, in camera, out _);

        /// <summary>Casts the pixel ray at the render-space globe and returns the ground point. <paramref name="hit"/>
        /// is <c>true</c> when the ray actually intersects the sphere; on a miss (cursor past the limb) the result
        /// is clamped to the silhouette and <paramref name="hit"/> is <c>false</c> — callers that need a real
        /// anchor (the pan solve) must NOT rotate on a miss, or the grabbed point chases an unreachable target.</summary>
        private GeoCoordinate3D CastGlobe(double2 screenPx, double2 viewportPx, in CameraProperties camera, out bool hit)
        {
            ReconstructView(in camera, viewportPx, out double3 camPos, out double3 fwd, out double3 up,
                out double3 right, out double3 east, out double3 upR, out double3 north, out double3 sceneOrigin);

            // Pixel ray in render-scene space.
            double ndcX = (screenPx.x / viewportPx.x) * 2.0 - 1.0;
            double ndcY = (screenPx.y / viewportPx.y) * 2.0 - 1.0;
            double tanV = math.tan(camera.VerticalFovDeg * 0.5 * math.PI_DBL / 180.0);
            double tanH = tanV * (viewportPx.x / viewportPx.y);
            double3 dir = math.normalize(Add(fwd, Add(Scale(right, ndcX * tanH), Scale(up, ndcY * tanV))));

            // Intersect the render-space globe: centre (0, −R, 0), radius R.
            double3 centre = new double3(0.0, -Radius, 0.0);
            double3 oc = Sub(camPos, centre);
            double b  = math.dot(oc, dir);
            double cc = math.dot(oc, oc) - Radius * Radius;
            double disc = b * b - cc;
            hit = disc >= 0.0;

            double3 p;
            if (hit)
            {
                double sq = math.sqrt(disc);
                double t  = -b - sq;             // near hit
                if (t < 0.0) t = -b + sq;        // camera inside the sphere ⇒ far hit
                p = Add(camPos, Scale(dir, t));

                // Grazing guard: within a thin band inside the limb the surface is nearly edge-on, so the
                // anchored pin is numerically singular — GroundToScreen is hyper-sensitive there and a slow drag
                // through the edge churns the look-at (a residual spin). Incidence = −dot(normal, rayDir): 1 =
                // face-on, → 0 at the limb. Below the threshold there is no stable anchor, so report a miss and
                // let the pan freeze (the returned point stays valid for ScreenToGround, which ignores `hit`).
                double3 nrm = math.normalize(Sub(p, centre));
                if (-math.dot(nrm, dir) < LimbGrazingCosine) hit = false;
            }
            else
            {
                // Ray misses the globe (cursor past the limb) — clamp to the silhouette: the ray's closest
                // approach, pushed onto the sphere. The grabbed point can't pin here, but the result stays valid.
                double3 nearPt = Add(camPos, Scale(dir, -b));
                p = Add(centre, Scale(math.normalize(Sub(nearPt, centre)), Radius));
            }

            // render-scene → render-ECEF (ecef = p.x·east + p.y·up + p.z·north + sceneOrigin) → geodetic.
            double3 ecef = Add(sceneOrigin, Add(Scale(east, p.x), Add(Scale(upR, p.y), Scale(north, p.z))));
            double lat = math.asin(math.clamp(ecef.y / Radius, -1.0, 1.0)) * 180.0 / math.PI_DBL;
            double lon = math.atan2(ecef.z, ecef.x) * 180.0 / math.PI_DBL;
            return new GeoCoordinate3D { Latitude = lat, Longitude = lon, Altitude = 0.0 };
        }

        /// <inheritdoc/>
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera)
        {
            ReconstructView(in camera, viewportPx, out double3 camPos, out double3 fwd, out double3 up,
                out double3 right, out double3 east, out double3 upR, out double3 north, out double3 sceneOrigin);

            double3 ecef = ProjectPoint(new GeoCoordinate { Latitude = ground.Latitude, Longitude = ground.Longitude }).World;
            double3 rel  = Sub(ecef, sceneOrigin);
            // render-ECEF → render-scene: rebase·rel = (east·rel, up·rel, north·rel).
            double3 rs = new double3(math.dot(east, rel), math.dot(upR, rel), math.dot(north, rel));

            double3 d = Sub(rs, camPos);
            double viewX = math.dot(d, right);
            double viewY = math.dot(d, up);
            double viewZ = math.dot(d, fwd); // toward the scene (camera forward)

            double tanV = math.tan(camera.VerticalFovDeg * 0.5 * math.PI_DBL / 180.0);
            double tanH = tanV * (viewportPx.x / viewportPx.y);
            double ndcX = viewX / (viewZ * tanH);
            double ndcY = viewY / (viewZ * tanV);
            return new double2((ndcX * 0.5 + 0.5) * viewportPx.x, (ndcY * 0.5 + 0.5) * viewportPx.y);
        }

        /// <summary>
        /// Anchored globe pan via a BOUNDED rotation solve (S91-C) — the globe-correct replacement for the
        /// planar affine <c>ViewInput.ApplyPan</c>, which diverges on the globe near the limb (the screen↔ground
        /// Jacobian explodes there, so at low zoom a minor drag spins the earth). Returns the new look-at that
        /// brings <paramref name="grabbedGround"/> (captured at drag-start) toward <paramref name="cursorPx"/>.
        ///
        /// <para>The point currently under the cursor is <c>C = ScreenToGround(cursorPx)</c>; the grabbed point is
        /// <c>G</c>. Rotating the look-at by the rotation that maps C→G (as unit ECEF vectors) moves G under the
        /// cursor. The step is a rotation by <c>acos(C·G) ≤ π</c> — <b>bounded by construction</b>, so it can
        /// never spin; as the drag holds, C→G and the rotation → 0 (converges). The ENU rebase references the
        /// fixed north pole, so the pin is approximate (like the planar path), but it is always stable.</para>
        /// </summary>
        public GeoCoordinate3D PanLookAtForGrab(GeoCoordinate3D grabbedGround, double2 cursorPx, double2 viewportPx,
            in CameraProperties cam)
        {
            GeoCoordinate3D under = CastGlobe(cursorPx, viewportPx, in cam, out bool hit);
            // Cursor dragged off the globe: there is no ground point to pin the grab under, so FREEZE the pan
            // (hold the look-at). Rotating toward the clamped silhouette instead makes the earth spin forever.
            if (!hit) return cam.LookAt;

            double3 g = ProjectPoint(new GeoCoordinate { Latitude = grabbedGround.Latitude, Longitude = grabbedGround.Longitude }).Up;
            double3 c = ProjectPoint(new GeoCoordinate { Latitude = under.Latitude, Longitude = under.Longitude }).Up;
            double3 l = ProjectPoint(new GeoCoordinate { Latitude = cam.LookAt.Latitude, Longitude = cam.LookAt.Longitude }).Up;

            double3 lRot = RotateFromTo(l, c, g); // rotate the look-at by the rotation mapping C→G
            double lat = math.asin(math.clamp(lRot.y, -1.0, 1.0)) * 180.0 / math.PI_DBL;
            double lon = math.atan2(lRot.z, lRot.x) * 180.0 / math.PI_DBL;
            return new GeoCoordinate3D { Latitude = lat, Longitude = lon, Altitude = 0.0 };
        }

        /// <summary>Rotates unit vector <paramref name="v"/> by the rotation that maps unit <paramref name="from"/>
        /// onto unit <paramref name="to"/> (Rodrigues). Identity when they are (anti)parallel.</summary>
        private static double3 RotateFromTo(double3 v, double3 from, double3 to)
        {
            double3 axis = math.cross(from, to);
            double s = math.length(axis);   // sin(angle)
            double cAng = math.dot(from, to); // cos(angle)
            if (s < 1e-12) return v;         // aligned ⇒ no rotation
            axis = new double3(axis.x / s, axis.y / s, axis.z / s);
            double3 axv = math.cross(axis, v);
            double d = math.dot(axis, v);
            // v·cos + (axis×v)·sin + axis·(axis·v)·(1−cos)
            return Add(Add(Scale(v, cAng), Scale(axv, s)), Scale(axis, d * (1.0 - cAng)));
        }

        /// <inheritdoc/>
        public double ClampValidLatitude(double latitudeDegrees) => math.clamp(latitudeDegrees, -90.0, 90.0);

        /// <inheritdoc/>
        public bool IsFinitePlanarWorld => false; // cyclic — the globe wraps, no edges to clamp

        /// <inheritdoc/>
        public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in CameraProperties camera)
            => camera.LookAt; // identity — nothing to clamp on a closed world
    }
}
