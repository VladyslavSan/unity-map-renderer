// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S3: the globe far-side horizon cull — a polar-plane test, EXACT for on-surface points (globe symbol
    /// anchors). Teeth: near-side kept / far-side hidden, cross-checked against an INDEPENDENT ray-sphere
    /// oracle (deliberately NOT the horizon half-angle test — that is algebraically identical to
    /// <c>dot &lt; radius²</c> and would be a tautological check); the planar no-op (<c>globeRadiusSq &lt; 0</c>)
    /// is unconditional, no state read.
    /// </summary>
    [TestFixture]
    public class HorizonCullTests
    {
        [Test]
        public void LookAtAnchor_WithCameraOverhead_IsNotHidden()
        {
            // repAnchorRender == sceneOriginRender ⇒ rebased P = (0,0,0); cam = (0,altitude,0); centre = (0,-R,0).
            // dot(P-C, cam-C) = R·(altitude+R) > R² for any altitude > 0 ⇒ never hidden — the look-at itself,
            // directly under the camera, is always visible.
            const double r = 6378137.0, altitude = 500.0;
            var origin = new double3(0, 0, 0);
            var cam = new double3(0, altitude, 0);
            var centre = new double3(0, -r, 0);

            bool hidden = HorizonCull.IsHiddenBeyondHorizon(
                origin, origin, float3x3.identity, cam, centre, r * r);

            Assert.IsFalse(hidden, "the look-at anchor sits directly under the camera — never hidden");
        }

        [Test]
        public void PlanarProjection_NegativeRadiusSq_IsAlwaysUnhidden()
        {
            // globeRadiusSq < 0 ⇒ unconditional false, no state read — arbitrary/nonsensical geometry must not
            // flip the answer (this is the Mercator no-op path: WebMercatorProjection.TryGetHorizonOccluder
            // returns false, so the caller passes globeRadiusSq = -1 regardless of anchor/camera/centre).
            bool hidden = HorizonCull.IsHiddenBeyondHorizon(
                new double3(1e9, 1e9, 1e9), new double3(-1e9, -1e9, -1e9), float3x3.identity,
                new double3(0, 0, 0), new double3(0, 0, 0), -1.0);

            Assert.IsFalse(hidden, "globeRadiusSq < 0 must always be a no-op");
        }

        // ── Independent oracle: ray-sphere intersection — a genuinely different formula from the plane test ──
        //
        // repAnchorRender is exactly ON the occluding sphere (a projected geodetic point), so parametrizing the
        // ray from cam toward it as cam + t·(p − cam) always has a root at t = 1. Solve the quadratic for the
        // NEAR root: if it lands strictly before t = 1, the sphere itself blocks the direct line of sight to p
        // (occluded / far side); if t = 1 IS the near root, nothing blocks it (visible / near side). Ray-sphere
        // intersection is invariant under the rigid rebase transform, so running it in the SAME rebased frame
        // HorizonCull uses is a legitimate cross-check of a different arithmetic path over the same geometry —
        // not a restatement of the dot-product plane test.
        //
        // "t = 1 is the near root" is an EXACT identity for every visible point (not just those close to the
        // horizon) — the direct ray from cam to a visible p never touches the sphere before p itself. Comparing
        // the computed tNear to 1 is therefore comparing float32-seam noise (~1e-6, from the SAME
        // double→float narrowing HorizonCull's production seam performs) around an exact zero for the ENTIRE
        // visible hemisphere, so the tolerance below must clear that noise floor with margin — 1e-4 does, while
        // staying far under the genuine (non-noise) gap any point outside a ~1-2° collar of the true horizon
        // exhibits (the sweep below only samples points ≥5° from the analytic horizon angle for that reason).
        private const double OracleEpsilon = 1e-4;

        private static bool RaySphereOccluded(double3 cam, double3 p, double3 centre, double radius)
        {
            double3 d = p - cam;
            double3 oc = cam - centre;
            double a = math.dot(d, d);
            double b = 2.0 * math.dot(oc, d);
            double c = math.dot(oc, oc) - radius * radius;
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0) return false; // ray misses the sphere entirely (shouldn't happen — p is on it)
            double tNear = (-b - math.sqrt(disc)) / (2.0 * a);
            return tNear < 1.0 - OracleEpsilon;
        }

        // Shared globe rig: look-at at the equator/prime-meridian, camera straight overhead at 2R altitude
        // (horizon half-angle = acos(R/(R+altitude)) = acos(1/3) ≈ 70.53°) — heading/tilt = 0 keeps `pos` on
        // the +Y axis, matching TryGetHorizonOccluder's (0,-R,0) centre by construction.
        private static void BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender,
            out float3x3 rebase, out double3 camRelative, out double3 centreRelative, out double radiusSq)
        {
            proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 0.0, Longitude = 0.0 };
            double altitude = 2.0 * SphericalProjection.Radius;

            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0), Angle.FromDegrees(0),
                out camRelative, out _, out _);

            sceneOriginRender = proj.Project(lookAt);
            rebase = math.transpose(proj.TangentBasisAt(lookAt));
            proj.TryGetHorizonOccluder(out centreRelative, out double radius);
            radiusSq = radius * radius;
        }

        [Test]
        public void FarAndNearSideAnchors_AgreeWithIndependentRaySphereOracle()
        {
            BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender, out float3x3 rebase,
                out double3 camRelative, out double3 centreRelative, out double radiusSq);
            double radius = math.sqrt(radiusSq);

            // Analytic horizon angle for this rig (2R overhead): acos(R/(R+altitude)) = acos(1/3) ≈ 70.53°.
            // Points within a couple degrees of it sit on a near-tangent ray — genuinely ill-conditioned for
            // ANY numeric method, oracle included — so the parity sweep keeps a 5° collar around it; the
            // dedicated near-limb probe below tests just outside that collar on each side.
            double horizonDeg = math.acos(1.0 / 3.0) * 180.0 / math.PI_DBL;

            bool sawHidden = false, sawVisible = false;
            for (double lon = -180.0; lon <= 180.0; lon += 5.0)
            {
                if (math.abs(math.abs(lon) - horizonDeg) < 5.0) continue; // skip the near-tangent collar

                var geo = new GeoCoordinate { Latitude = 0.0, Longitude = lon };
                double3 anchorRender = proj.Project(geo);

                bool predicate = HorizonCull.IsHiddenBeyondHorizon(
                    anchorRender, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);

                // Rebase the anchor into the SAME frame the oracle needs — mirrors the seam inside HorizonCull.
                double3 local = anchorRender - sceneOriginRender;
                var localF = new float3((float)local.x, (float)local.y, (float)local.z);
                float3 rebasedF = math.mul(rebase, localF);
                var anchorRebased = new double3(rebasedF.x, rebasedF.y, rebasedF.z);

                bool oracle = RaySphereOccluded(camRelative, anchorRebased, centreRelative, radius);

                Assert.AreEqual(oracle, predicate, $"lon={lon}: plane test vs ray-sphere oracle disagree");
                if (predicate) sawHidden = true; else sawVisible = true;
            }

            Assert.IsTrue(sawHidden, "the sweep must include far-side (hidden) longitudes");
            Assert.IsTrue(sawVisible, "the sweep must include near-side (visible) longitudes");
        }

        [Test]
        public void NearLimbAnchors_JustInsideAndJustBeyondHorizon_AgreeWithOracle()
        {
            // A dedicated near-limb pair straddling the analytic horizon (≈70.53° for this 2R-overhead rig) —
            // just outside the ill-conditioned collar the sweep above skips, so both the plane test and the
            // independent ray-sphere oracle still resolve it cleanly, but only barely.
            BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender, out float3x3 rebase,
                out double3 camRelative, out double3 centreRelative, out double radiusSq);
            double radius = math.sqrt(radiusSq);
            double horizonDeg = math.acos(1.0 / 3.0) * 180.0 / math.PI_DBL;

            foreach (double lon in new[] { horizonDeg - 6.0, horizonDeg + 6.0 })
            {
                double3 anchorRender = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = lon });

                bool predicate = HorizonCull.IsHiddenBeyondHorizon(
                    anchorRender, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);

                double3 local = anchorRender - sceneOriginRender;
                var localF = new float3((float)local.x, (float)local.y, (float)local.z);
                float3 rebasedF = math.mul(rebase, localF);
                var anchorRebased = new double3(rebasedF.x, rebasedF.y, rebasedF.z);
                bool oracle = RaySphereOccluded(camRelative, anchorRebased, centreRelative, radius);

                Assert.AreEqual(oracle, predicate, $"near-limb lon={lon}");
            }

            bool justInside = HorizonCull.IsHiddenBeyondHorizon(
                proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = horizonDeg - 6.0 }),
                sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);
            bool justBeyond = HorizonCull.IsHiddenBeyondHorizon(
                proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = horizonDeg + 6.0 }),
                sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);

            Assert.IsFalse(justInside, "6° inside the horizon — still visible");
            Assert.IsTrue(justBeyond, "6° beyond the horizon — hidden");
        }

        [Test]
        public void Antipode_IsHidden_NearNeighbour_IsVisible()
        {
            BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender, out float3x3 rebase,
                out double3 camRelative, out double3 centreRelative, out double radiusSq);

            double3 antipode = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 180.0 });
            double3 neighbour = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 10.0 });

            Assert.IsTrue(HorizonCull.IsHiddenBeyondHorizon(
                antipode, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq),
                "the antipode is on the far side of the globe — hidden");
            Assert.IsFalse(HorizonCull.IsHiddenBeyondHorizon(
                neighbour, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq),
                "a 10° near-neighbour of the look-at is well within the horizon — visible");
        }
    }
}
