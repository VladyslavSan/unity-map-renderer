// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine, NativeArray, or MonoBehaviour references.
//
// S61 projection math tests:
//   T3: Ecef is genuinely 3D (pole, antipodal, chord, tangent-frame varies with position).
//   T4-numeric: batch Forward equals scalar Forward element-wise (exact), for both modules.
//   T6: WebMercator.Forward(geo).xz == FromLonLat(lon,lat) within 1e-6 m.
//   TangentBasis orthonormality for both modules.

using System;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Projection
{
    [TestFixture]
    public class ProjectionMathTests
    {
        // ── Constants ────────────────────────────────────────────────────────────────────────────

        private const double A = Ecef.A;              // 6378137.0
        private const double B = A * (1.0 - Ecef.F); // semi-minor b ≈ 6356752.314
        private const double Tol1m = 1.0;            // 1 metre tolerance
        private const double TolParity = 1e-6;       // Mercator parity tolerance

        // ── T3 — Ecef is genuinely 3D ─────────────────────────────────────────────────────────

        /// <summary>
        /// T3a: North pole (lat=90, lon=0, alt=0) → render ≈ (0, b, 0), tol ≤ 1 m.
        /// Mercator diverges at 90°; only a genuinely 3D impl can pass this.
        /// Render-space axis-swap: (X_ecef, Z_ecef, Y_ecef) — at pole, X=0, Y=0, Z=b → render=(0,b,0).
        /// </summary>
        [Test]
        public void Ecef_Pole_NearSemiMinor()
        {
            var geo = new GeoCoordinate3D { Longitude = 0.0, Latitude = 90.0, Altitude = 0.0 };
            double3 r = Ecef.Forward(geo);

            Assert.That(r.x, Is.EqualTo(0.0).Within(Tol1m), "Pole x must be ~0");
            Assert.That(r.y, Is.EqualTo(B).Within(Tol1m),   $"Pole y must be ~b={B:F3}");
            Assert.That(r.z, Is.EqualTo(0.0).Within(Tol1m), "Pole z must be ~0");
        }

        /// <summary>
        /// T3b: Antipodal longitudes — Forward(0,0,0)≈(A,0,0) and Forward(180,0,0)≈(−A,0,0).
        /// Opposite x sign; |sum| ≤ 1 m. A flat/planar impl puts both at +x.
        /// At equator: X_ecef = A*cos(0) = A; for lon=180: X_ecef = A*cos(0)*cos(π) = −A.
        /// Axis-swap: render = (X_ecef, Z_ecef, Y_ecef); at equator Z_ecef=0, Y_ecef=0.
        /// </summary>
        [Test]
        public void Ecef_Antipodal_OppositeX()
        {
            double3 p0   = Ecef.Forward(new GeoCoordinate3D { Longitude = 0.0,   Latitude = 0.0, Altitude = 0.0 });
            double3 p180 = Ecef.Forward(new GeoCoordinate3D { Longitude = 180.0, Latitude = 0.0, Altitude = 0.0 });

            // Each magnitude ≈ A
            Assert.That(Math.Abs(p0.x),   Is.EqualTo(A).Within(Tol1m), "lon=0 |x| must be ~A");
            Assert.That(Math.Abs(p180.x),  Is.EqualTo(A).Within(Tol1m), "lon=180 |x| must be ~A");

            // Opposite signs: sum ≈ 0
            Assert.That(Math.Abs(p0.x + p180.x), Is.LessThanOrEqualTo(Tol1m),
                $"Antipodal x sum must cancel: {p0.x} + {p180.x}");
        }

        /// <summary>
        /// T3c: Equatorial 90° chord — |Forward(0,0,0) − Forward(90,0,0)| ≈ A·√2 ≈ 9.02e6 m, tol ≤ 1 m.
        /// At lon=0: render x=A, z=0. At lon=90: render x=0, z=A. Chord = sqrt(A^2+A^2) = A*sqrt(2).
        /// </summary>
        [Test]
        public void Ecef_EquatorialChord_ApproxASqrt2()
        {
            double3 p0  = Ecef.Forward(new GeoCoordinate3D { Longitude = 0.0,  Latitude = 0.0, Altitude = 0.0 });
            double3 p90 = Ecef.Forward(new GeoCoordinate3D { Longitude = 90.0, Latitude = 0.0, Altitude = 0.0 });

            // Component-wise subtraction (shim has no double3 minus operator)
            double dx = p0.x - p90.x;
            double dy = p0.y - p90.y;
            double dz = p0.z - p90.z;
            double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double expected = A * Math.Sqrt(2.0);

            Assert.That(chord, Is.EqualTo(expected).Within(Tol1m),
                $"Equatorial 90° chord must be ~A√2={expected:F3}, got {chord:F3}");
        }

        /// <summary>
        /// T3d: Tangent frame varies with position (globe) vs constant (plane).
        /// Ecef.TangentBasis(0,0) Up≈(1,0,0); TangentBasis(0,90) Up≈(0,1,0).
        /// WebMercator.TangentBasis(any) constant Up=(0,1,0), East=(1,0,0).
        /// Both bases orthonormal (dot products on columns).
        /// </summary>
        [Test]
        public void TangentBasis_EcefVariesWebMercatorConstant()
        {
            // --- Ecef at (lon=0, lat=0): Up should be ~(1,0,0) in render space ---
            // ECEF up at origin: (cosφ·cosλ, cosφ·sinλ, sinφ) = (1,0,0)
            // axis-swap (X,Y,Z)→(X,Z,Y): render-up = (1, 0, 0)
            float3x3 b00 = Ecef.TangentBasis(new GeoCoordinate { Longitude = 0.0, Latitude = 0.0 });
            float3 up00 = b00.c1; // c1 = Up column
            Assert.That(up00.x, Is.EqualTo(1f).Within(1e-5f), "Ecef(0,0) Up.x≈1");
            Assert.That(up00.y, Is.EqualTo(0f).Within(1e-5f), "Ecef(0,0) Up.y≈0");
            Assert.That(up00.z, Is.EqualTo(0f).Within(1e-5f), "Ecef(0,0) Up.z≈0");

            // --- Ecef at (lon=0, lat=90): Up should be ~(0,1,0) in render space ---
            // ECEF up at north pole: (cosφ·cosλ, cosφ·sinλ, sinφ) = (0,0,1) (φ=90° → sinφ=1)
            // axis-swap: render-up = (X_ecef=0, Z_ecef=1, Y_ecef=0) = (0,1,0)
            float3x3 b090 = Ecef.TangentBasis(new GeoCoordinate { Longitude = 0.0, Latitude = 90.0 });
            float3 up090 = b090.c1; // c1 = Up column
            Assert.That(up090.x, Is.EqualTo(0f).Within(1e-5f), "Ecef(0,90) Up.x≈0");
            Assert.That(up090.y, Is.EqualTo(1f).Within(1e-5f), "Ecef(0,90) Up.y≈1");
            Assert.That(up090.z, Is.EqualTo(0f).Within(1e-5f), "Ecef(0,90) Up.z≈0");

            // --- WebMercator tangent basis is constant ---
            float3x3 wm00  = WebMercator.TangentBasis(new GeoCoordinate { Longitude = 0.0, Latitude = 0.0 });
            float3x3 wm45  = WebMercator.TangentBasis(new GeoCoordinate { Longitude = 45.0, Latitude = 45.0 });
            float3 eastWm  = wm00.c0; // c0 = East
            float3 upWm    = wm00.c1; // c1 = Up
            Assert.That(eastWm.x, Is.EqualTo(1f).Within(1e-5f), "WebMercator East.x=1");
            Assert.That(eastWm.y, Is.EqualTo(0f).Within(1e-5f), "WebMercator East.y=0");
            Assert.That(eastWm.z, Is.EqualTo(0f).Within(1e-5f), "WebMercator East.z=0");
            Assert.That(upWm.x,   Is.EqualTo(0f).Within(1e-5f), "WebMercator Up.x=0");
            Assert.That(upWm.y,   Is.EqualTo(1f).Within(1e-5f), "WebMercator Up.y=1");
            Assert.That(upWm.z,   Is.EqualTo(0f).Within(1e-5f), "WebMercator Up.z=0");

            // Constant: both points give the same Up
            float3 upWm45 = wm45.c1;
            Assert.That(upWm45.x, Is.EqualTo(upWm.x).Within(1e-5f), "WebMercator Up is constant (x)");
            Assert.That(upWm45.y, Is.EqualTo(upWm.y).Within(1e-5f), "WebMercator Up is constant (y)");
            Assert.That(upWm45.z, Is.EqualTo(upWm.z).Within(1e-5f), "WebMercator Up is constant (z)");

            // --- Orthonormality of both bases ---
            AssertOrthonormal(b00,  "Ecef(0,0)");
            AssertOrthonormal(b090, "Ecef(0,90)");
            AssertOrthonormal(wm00, "WebMercator");
        }

        // ── T6 — Mercator parity ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// T6: WebMercator.Forward(geo).xz equals pre-refactor FromLonLat(lon,lat) at
        /// equator, ±45°, and near the 85.05° limit — tolerance ≤ 1e-6 m.
        /// </summary>
        [Test]
        public void WebMercator_ForwardXZ_MatchesFromLonLat()
        {
            double[] lons = { 0.0, -180.0, 45.0, 13.404954 };
            double[] lats = { 0.0, 45.0, -45.0, 85.0 };

            foreach (double lon in lons)
            {
                foreach (double lat in lats)
                {
                    double3 fwd = WebMercator.Forward(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0.0 });
                    double2 ref2 = WebMercator.FromLonLat(new GeoCoordinate3D { Longitude = lon, Latitude = lat });

                    Assert.That(fwd.x, Is.EqualTo(ref2.x).Within(TolParity),
                        $"Forward.x vs FromLonLat.x at lon={lon}, lat={lat}");
                    Assert.That(fwd.z, Is.EqualTo(ref2.y).Within(TolParity),
                        $"Forward.z vs FromLonLat.y at lon={lon}, lat={lat}");
                }
            }
        }

        // ── T4-numeric — Batch equals scalar (exact) ────────────────────────────────────────────

        /// <summary>
        /// T4 (numeric part): WebMercator.Forward(span,dst) equals scalar Forward element-wise (exact).
        /// Caller-owned managed array is used (no NativeArray here — that's the Unity-only test).
        /// </summary>
        [Test]
        public void WebMercator_BatchEqualsScalar_Exact()
        {
            var points = new GeoCoordinate3D[]
            {
                new GeoCoordinate3D { Longitude = 0.0,   Latitude = 0.0,   Altitude = 0.0 },
                new GeoCoordinate3D { Longitude = 45.0,  Latitude = 45.0,  Altitude = 100.0 },
                new GeoCoordinate3D { Longitude = -90.0, Latitude = -30.0, Altitude = 0.0 },
                new GeoCoordinate3D { Longitude = 13.4,  Latitude = 52.5,  Altitude = 0.0 },
                new GeoCoordinate3D { Longitude = 0.0,   Latitude = 85.0,  Altitude = 0.0 },
            };

            var dst = new double3[points.Length];
            WebMercator.Forward(
                new ReadOnlySpan<GeoCoordinate3D>(points),
                new Span<double3>(dst));

            for (int i = 0; i < points.Length; i++)
            {
                double3 scalar = WebMercator.Forward(points[i]);
                Assert.AreEqual(scalar.x, dst[i].x, 0.0,
                    $"WebMercator batch.x[{i}] must equal scalar");
                Assert.AreEqual(scalar.y, dst[i].y, 0.0,
                    $"WebMercator batch.y[{i}] must equal scalar");
                Assert.AreEqual(scalar.z, dst[i].z, 0.0,
                    $"WebMercator batch.z[{i}] must equal scalar");
            }
        }

        /// <summary>
        /// T4 (numeric part): Ecef.Forward(span,dst) equals scalar Forward element-wise (exact).
        /// </summary>
        [Test]
        public void Ecef_BatchEqualsScalar_Exact()
        {
            var points = new GeoCoordinate3D[]
            {
                new GeoCoordinate3D { Longitude = 0.0,   Latitude = 0.0,   Altitude = 0.0 },
                new GeoCoordinate3D { Longitude = 90.0,  Latitude = 45.0,  Altitude = 0.0 },
                new GeoCoordinate3D { Longitude = -90.0, Latitude = -30.0, Altitude = 1000.0 },
                new GeoCoordinate3D { Longitude = 0.0,   Latitude = 90.0,  Altitude = 0.0 },   // pole
                new GeoCoordinate3D { Longitude = 180.0, Latitude = 0.0,   Altitude = 0.0 },   // antipodal
            };

            var dst = new double3[points.Length];
            Ecef.Forward(
                new ReadOnlySpan<GeoCoordinate3D>(points),
                new Span<double3>(dst));

            for (int i = 0; i < points.Length; i++)
            {
                double3 scalar = Ecef.Forward(points[i]);
                Assert.AreEqual(scalar.x, dst[i].x, 0.0,
                    $"Ecef batch.x[{i}] must equal scalar");
                Assert.AreEqual(scalar.y, dst[i].y, 0.0,
                    $"Ecef batch.y[{i}] must equal scalar");
                Assert.AreEqual(scalar.z, dst[i].z, 0.0,
                    $"Ecef batch.z[{i}] must equal scalar");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Asserts that all three columns of a float3x3 form an orthonormal frame.</summary>
        private static void AssertOrthonormal(float3x3 m, string label)
        {
            float3 c0 = m.c0; float3 c1 = m.c1; float3 c2 = m.c2;
            const float tol = 1e-5f;

            // Each column unit length
            Assert.That(Len(c0), Is.EqualTo(1.0f).Within(tol), $"{label}: c0 not unit length");
            Assert.That(Len(c1), Is.EqualTo(1.0f).Within(tol), $"{label}: c1 not unit length");
            Assert.That(Len(c2), Is.EqualTo(1.0f).Within(tol), $"{label}: c2 not unit length");

            // Mutual orthogonality
            Assert.That(Dot(c0, c1), Is.EqualTo(0.0f).Within(tol), $"{label}: c0·c1 ≠ 0");
            Assert.That(Dot(c0, c2), Is.EqualTo(0.0f).Within(tol), $"{label}: c0·c2 ≠ 0");
            Assert.That(Dot(c1, c2), Is.EqualTo(0.0f).Within(tol), $"{label}: c1·c2 ≠ 0");
        }

        private static float Len(float3 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        private static float Dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    }
}
