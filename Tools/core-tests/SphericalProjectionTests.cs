// SphericalProjectionTests — engine-free proof that IProjection genuinely serves a SECOND projection
// (the simple-sphere globe), not just Web Mercator. Runs via `dotnet test Tools/core-tests` in ~0.1s.
//
// Validates the stateless struct SphericalProjection's ProjectPoint math + the OOP IProjection forwarding:
//   - known geodetic points map to the expected render-space ECEF (axis-swap X,Z,Y);
//   - every surface point lies on the sphere (|World| == Radius) with a radial unit up;
//   - the OOP conveniences (Project/UpAt, via the boxed interface) equal the ProjectPoint kernel.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    public class SphericalProjectionTests
    {
        private const double R = SphericalProjection.Radius; // == EarthConstants.A

        private static ProjectedPoint Project(double lat, double lon)
            => new SphericalProjection().ProjectPoint(new GeoCoordinate { Latitude = lat, Longitude = lon });

        // The core-tests shim's double3/float3 have no arithmetic ops / length / dot — compute componentwise.
        private static double Len(double3 v) => math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        private static double Dot(double3 a, double3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        private static double LenF(float3 v) => math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        private static double DotF(float3 a, float3 b) => (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;

        // (lat,lon,alt) → expected render World (east=+X, up=+Y, north=+Z after the ECEF X,Z,Y swap).
        [TestCase(0.0, 0.0)]     // equator, prime meridian → +X
        [TestCase(90.0, 0.0)]    // north pole            → +Y
        [TestCase(0.0, 90.0)]    // equator, 90°E          → +Z
        public void KnownPoints_MapToExpectedAxes(double lat, double lon)
        {
            ProjectedPoint pp = Project(lat, lon);

            double3 expected =
                (lat == 90.0) ? new double3(0, R, 0) :
                (lon == 90.0) ? new double3(0, 0, R) :
                                new double3(R, 0, 0);

            Assert.That(pp.World.x, Is.EqualTo(expected.x).Within(1.0), "World.x");
            Assert.That(pp.World.y, Is.EqualTo(expected.y).Within(1.0), "World.y");
            Assert.That(pp.World.z, Is.EqualTo(expected.z).Within(1.0), "World.z");

            // Up is the radial unit normal (World / R).
            Assert.That(pp.Up.x, Is.EqualTo(expected.x / R).Within(1e-9), "Up.x");
            Assert.That(pp.Up.y, Is.EqualTo(expected.y / R).Within(1e-9), "Up.y");
            Assert.That(pp.Up.z, Is.EqualTo(expected.z / R).Within(1e-9), "Up.z");
        }

        [Test]
        public void EverySurfacePoint_LiesOnSphere_WithRadialUnitUp()
        {
            for (double lat = -80; lat <= 80; lat += 20)
            for (double lon = -180; lon <= 180; lon += 45)
            {
                ProjectedPoint pp = Project(lat, lon);

                double r = Len(pp.World);
                Assert.That(r, Is.EqualTo(R).Within(1e-3), $"|World| at ({lat},{lon}) must equal the radius");

                Assert.That(Len(pp.Up), Is.EqualTo(1.0).Within(1e-9), "Up must be unit");
                // Up parallel to the position vector (radial): dot(World, Up)/|World| == 1.
                Assert.That(Dot(pp.World, pp.Up) / r, Is.EqualTo(1.0).Within(1e-9), "Up must be radial");
            }
        }

        [Test]
        public void Oop_Project_MatchesKernel_AndUpIsRadial()
        {
            IProjection proj = new SphericalProjection(); // boxed struct (one box), radius = EarthConstants.A
            var geo = new GeoCoordinate { Latitude = 48.0, Longitude = 11.0 };

            double3 world = proj.Project(geo);
            double3 up    = proj.UpAt(geo);
            ProjectedPoint kernel = Project(geo.Latitude, geo.Longitude);

            Assert.That(world.x, Is.EqualTo(kernel.World.x).Within(1e-9), "OOP Project must equal the ProjectPoint kernel");
            Assert.That(world.y, Is.EqualTo(kernel.World.y).Within(1e-9));
            Assert.That(world.z, Is.EqualTo(kernel.World.z).Within(1e-9));
            Assert.That(Dot(world, up) / Len(world), Is.EqualTo(1.0).Within(1e-9), "UpAt must be radial");
        }

        [Test]
        public void TangentBasisAt_IsOrthonormal_AndUpColumnEqualsUpAt()
        {
            IProjection proj = new SphericalProjection();
            for (double lat = -70; lat <= 70; lat += 35)
            for (double lon = -150; lon <= 150; lon += 75)
            {
                var geo = new GeoCoordinate { Latitude = lat, Longitude = lon };
                float3x3 b = proj.TangentBasisAt(geo);
                float3 east = b.c0, up = b.c1, north = b.c2;

                // Orthonormal columns.
                Assert.That(LenF(east),  Is.EqualTo(1.0).Within(1e-5), "east unit");
                Assert.That(LenF(up),    Is.EqualTo(1.0).Within(1e-5), "up unit");
                Assert.That(LenF(north), Is.EqualTo(1.0).Within(1e-5), "north unit");
                Assert.That(DotF(east, up),    Is.EqualTo(0.0).Within(1e-5), "east ⟂ up");
                Assert.That(DotF(up, north),   Is.EqualTo(0.0).Within(1e-5), "up ⟂ north");
                Assert.That(DotF(east, north), Is.EqualTo(0.0).Within(1e-5), "east ⟂ north");

                // The up column is the surface up (radial) — matches UpAt.
                double3 upAt = proj.UpAt(geo);
                float3 upAtF = new float3((float)upAt.x, (float)upAt.y, (float)upAt.z);
                Assert.That(DotF(upAtF, up), Is.EqualTo(1.0).Within(1e-4), "up column == UpAt");
            }
        }

        [Test]
        public void Mercator_TangentBasisAt_IsIdentity()
        {
            IProjection proj = new WebMercatorProjection();
            float3x3 b = proj.TangentBasisAt(new GeoCoordinate { Latitude = 40, Longitude = -3 });
            Assert.That(DotF(b.c0, new float3(1, 0, 0)), Is.EqualTo(1.0).Within(1e-6), "east=+X");
            Assert.That(DotF(b.c1, new float3(0, 1, 0)), Is.EqualTo(1.0).Within(1e-6), "up=+Y");
            Assert.That(DotF(b.c2, new float3(0, 0, 1)), Is.EqualTo(1.0).Within(1e-6), "north=+Z");
        }

        [Test]
        public void ClampValidLatitude_UsesFullGeodeticRange()
        {
            var proj = new SphericalProjection();
            Assert.That(proj.ClampValidLatitude(89.9),  Is.EqualTo(89.9).Within(1e-12), "sphere has no Mercator pole limit");
            Assert.That(proj.ClampValidLatitude(120.0), Is.EqualTo(90.0).Within(1e-12));
            Assert.That(proj.ClampValidLatitude(-95.0), Is.EqualTo(-90.0).Within(1e-12));
        }
    }
}
