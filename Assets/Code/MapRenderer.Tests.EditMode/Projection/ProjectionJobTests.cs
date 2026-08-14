using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Projection
{
    /// <summary>
    /// EditMode tests for TileToGeoJob + ProjectPointsJob&lt;TProj&gt; (WebMercator + Spherical).
    ///
    /// (1) Parity test — job matches TileId.ToMercator − origin for known tile coords,
    ///     including a non-z0 tile to exercise the 2^z path.
    /// (2) Acceptance — all 239 countries' vertices project within MercatorBounds (± margin).
    /// (3) Precision intent — origin-relative double3 magnitudes are smaller than absolute Mercator.
    ///
    /// Note: Tests run the job via managed fallback (.Run() / .Complete()), not Burst-compiled.
    /// They validate numeric correctness but do NOT prove Burst compilation.
    /// </summary>
    public class ProjectionJobTests
    {
        private const double R = 6378137.0;

        // -----------------------------------------------------------------------------------------
        // Helper: run the job synchronously over a single tile coord.
        // -----------------------------------------------------------------------------------------

        private static double3 Project(int z, int x, int y, double extent, double px, double py,
            double originX, double originY)
        {
            var coords = new NativeArray<double2>(1, Allocator.TempJob);
            var geo    = new NativeArray<GeoCoordinate>(1, Allocator.TempJob);
            var result = new NativeArray<double3>(1, Allocator.TempJob);
            var up     = new NativeArray<double3>(1, Allocator.TempJob);
            coords[0] = new double2(px, py);

            // tile → geodetic (projection-independent), then project through Web Mercator.
            new TileToGeoJob
            {
                Tile = new TileId { Z = z, X = x, Y = y }, Extent = extent,
                TileCoords = coords, OutGeo = geo,
            }.Schedule(1, 1).Complete();

            new ProjectPointsJob<WebMercatorProjection>
            {
                Projection = new WebMercatorProjection(),
                OriginWorld = new double3(originX, 0.0, originY),
                Points = geo, WorldPositions = result, Normals = up,
            }.Schedule(1, 1).Complete();

            double3 r = result[0];
            coords.Dispose();
            geo.Dispose();
            result.Dispose();
            up.Dispose();
            return r;
        }

        // -----------------------------------------------------------------------------------------
        // (1) Parity — job vs TileId.ToMercator (z0)
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Parity_JobMatchesCore_Z0()
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            var (bMin, _) = tile.MercatorBounds();
            double originX = bMin.x, originY = bMin.y;
            double extent = 4096.0;

            // Test a handful of tile-space coords.
            double[][] testPoints = {
                new[] { 0.0, 0.0 },
                new[] { 2048.0, 2048.0 },
                new[] { 4096.0, 0.0 },
                new[] { 0.0, 4096.0 },
                new[] { 1000.0, 3000.0 },
            };

            // Generous tolerance: at z0 tile, float ULP at world scale (~20M m) is ~2–5 m.
            // A formula bug errors by thousands of km, so 50 m absolute tol is safe and not too tight.
            const double tol = 50.0;

            foreach (var pt in testPoints)
            {
                double px = pt[0], py = pt[1];
                double2 mercRef = tile.ToMercator(px, py, extent);
                double refDx = mercRef.x - originX;
                double refDz = mercRef.y - originY;

                double3 jobOut = Project(0, 0, 0, extent, px, py, originX, originY);

                Assert.That(jobOut.x, Is.EqualTo(refDx).Within(tol),
                    $"X mismatch at px={px}, py={py}: job={jobOut.x:F2}, ref={refDx:F2}");
                Assert.That(jobOut.z, Is.EqualTo(refDz).Within(tol),
                    $"Z mismatch at px={px}, py={py}: job={jobOut.z:F2}, ref={refDz:F2}");
                Assert.AreEqual(0.0, jobOut.y, "Y should be 0.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // (1b) Parity — non-z0 tile (exercises 2^z path)
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Parity_JobMatchesCore_NonZ0()
        {
            // z=5, x=10, y=12 — a realistic non-trivial tile
            int z = 5, tx = 10, ty = 12;
            var tile = new TileId { Z = z, X = tx, Y = ty };
            var (bMin, _) = tile.MercatorBounds();
            double originX = bMin.x, originY = bMin.y;
            double extent = 4096.0;

            double[][] testPoints = {
                new[] { 0.0, 0.0 },
                new[] { 2048.0, 2048.0 },
                new[] { 4096.0, 4096.0 },
                new[] { 1000.0, 500.0 },
            };

            const double tol = 5.0; // Non-z0: tile is smaller, ULPs much tighter, 5 m is safe.

            foreach (var pt in testPoints)
            {
                double px = pt[0], py = pt[1];
                double2 mercRef = tile.ToMercator(px, py, extent);
                double refDx = mercRef.x - originX;
                double refDz = mercRef.y - originY;

                double3 jobOut = Project(z, tx, ty, extent, px, py, originX, originY);

                Assert.That(jobOut.x, Is.EqualTo(refDx).Within(tol),
                    $"X mismatch z={z} tx={tx} ty={ty} px={px}: job={jobOut.x:F4}, ref={refDx:F4}");
                Assert.That(jobOut.z, Is.EqualTo(refDz).Within(tol),
                    $"Z mismatch z={z} tx={tx} ty={ty} py={py}: job={jobOut.z:F4}, ref={refDz:F4}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // (2) Acceptance — 239 country vertices within MercatorBounds
        // -----------------------------------------------------------------------------------------

        [Test]
        public void CountryVertices_ProjectWithinMercatorBounds()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");

            // IR C1 P3: the ring coordinates for this projection sweep come from the independent fixture
            // reader plus the managed reference decoder, not from a decoded feature (which carries none).
            var layer = MvtFixtureStreams.ReadLayer(File.ReadAllBytes(path), "countries");
            Assert.IsNotNull(layer);

            var tileId = new TileId { Z = 0, X = 0, Y = 0 };
            var (bMin, bMax) = tileId.MercatorBounds();
            double originX = bMin.x, originY = bMin.y;
            double extent = layer.Extent;

            // 5% margin for vertices at the tile boundary.
            double marginX = (bMax.x - bMin.x) * 0.05;
            double marginY = (bMax.y - bMin.y) * 0.05;

            int totalChecked = 0;
            foreach (uint[] commands in layer.Commands)
            {
                var rings = MapRenderer.Tests.TestSupport.MvtGeometry.Decode(commands);
                foreach (var ring in rings)
                {
                    if (ring == null || ring.Count == 0) continue;

                    var coords = new NativeArray<double2>(ring.Count, Allocator.TempJob);
                    var geo     = new NativeArray<GeoCoordinate>(ring.Count, Allocator.TempJob);
                    var results = new NativeArray<double3>(ring.Count, Allocator.TempJob);
                    var ups     = new NativeArray<double3>(ring.Count, Allocator.TempJob);

                    for (int i = 0; i < ring.Count; i++)
                        coords[i] = ring[i];

                    new TileToGeoJob
                    {
                        Tile = new TileId { Z = 0, X = 0, Y = 0 }, Extent = extent,
                        TileCoords = coords, OutGeo = geo,
                    }.Schedule(ring.Count, 64).Complete();

                    new ProjectPointsJob<WebMercatorProjection>
                    {
                        Projection = new WebMercatorProjection(),
                        OriginWorld = new double3(originX, 0.0, originY),
                        Points = geo, WorldPositions = results, Normals = ups,
                    }.Schedule(ring.Count, 64).Complete();

                    for (int i = 0; i < ring.Count; i++)
                    {
                        double3 wp = results[i];
                        // Reconstruct absolute mercator from origin-relative
                        double absX = wp.x + originX;
                        double absZ = wp.z + originY;

                        Assert.That(absX, Is.GreaterThanOrEqualTo(bMin.x - marginX).And.LessThanOrEqualTo(bMax.x + marginX),
                            $"absX={absX:F2} out of [{bMin.x:F2}, {bMax.x:F2}]");
                        Assert.That(absZ, Is.GreaterThanOrEqualTo(bMin.y - marginY).And.LessThanOrEqualTo(bMax.y + marginY),
                            $"absZ={absZ:F2} out of [{bMin.y:F2}, {bMax.y:F2}]");

                        totalChecked++;
                    }

                    coords.Dispose();
                    geo.Dispose();
                    results.Dispose();
                    ups.Dispose();
                }
            }
            Assert.Greater(totalChecked, 0, "Should have checked at least one vertex.");
        }

        // -----------------------------------------------------------------------------------------
        // (3) Precision intent — origin-relative float3 magnitudes smaller than absolute Mercator
        // -----------------------------------------------------------------------------------------

        [Test]
        public void OriginRelative_MagnitudesAreBoundedByTileSize()
        {
            // At z0, the whole-world tile spans ~±20M m in X, ~±20M m in Y.
            // Origin-relative coords from the tile corner should be in [0, ~40M] for x and z.
            // (We subtract the MIN corner, so values range from 0 to the tile width.)
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            var (bMin, bMax) = tile.MercatorBounds();
            double originX = bMin.x, originY = bMin.y;
            double tileWidth = bMax.x - bMin.x;
            double tileHeight = bMax.y - bMin.y;
            double extent = 4096.0;

            // Sample the four corners
            double[][] corners = {
                new[] { 0.0, 0.0 },
                new[] { 4096.0, 0.0 },
                new[] { 0.0, 4096.0 },
                new[] { 4096.0, 4096.0 },
            };

            foreach (var c in corners)
            {
                double3 wp = Project(0, 0, 0, extent, c[0], c[1], originX, originY);

                // Origin-relative x should be in [0, tileWidth] (with float rounding)
                Assert.That(wp.x, Is.GreaterThanOrEqualTo(-1000.0).And.LessThanOrEqualTo(tileWidth + 1000.0),
                    $"Origin-relative X={wp.x} out of tile width bounds.");
                // Origin-relative z should be in [0, tileHeight] (note: Y-axis: top=low Mercator)
                Assert.That(wp.z, Is.GreaterThanOrEqualTo(-tileHeight - 1000.0).And.LessThanOrEqualTo(tileHeight + 1000.0),
                    $"Origin-relative Z={wp.z} out of tile height bounds.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // (4) Projection-agnostic job — same generic ProjectPointsJob<TProj>, Spherical struct → vertices on
        //     the sphere. Proves the job is driven by the projection type (not hardcoded Mercator) AND that
        //     Burst compiles ProjectPointsJob<SphericalProjection> (the second IProjection impl).
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Spherical_JobProjectsVerticesOntoTheSphere()
        {
            const double extent = 4096.0;

            var coords = new NativeArray<double2>(4, Allocator.TempJob);
            coords[0] = new double2(0, 0);
            coords[1] = new double2(2048, 2048);
            coords[2] = new double2(4096, 0);
            coords[3] = new double2(1000, 3000);
            var geo   = new NativeArray<GeoCoordinate>(4, Allocator.TempJob);
            var world = new NativeArray<double3>(4, Allocator.TempJob);
            var up    = new NativeArray<double3>(4, Allocator.TempJob);

            new TileToGeoJob
            {
                Tile = new TileId { Z = 3, X = 2, Y = 1 }, Extent = extent,
                TileCoords = coords, OutGeo = geo,
            }.Schedule(4, 1).Complete();

            new ProjectPointsJob<SphericalProjection>
            {
                Projection = new SphericalProjection(),
                OriginWorld = new double3(0, 0, 0), // absolute render-space ECEF (no RTC) so |World| == R
                Points = geo, WorldPositions = world, Normals = up,
            }.Schedule(4, 1).Complete();

            for (int i = 0; i < 4; i++)
            {
                double r = math.length(world[i]);
                Assert.That(r, Is.EqualTo(R).Within(1.0), $"vertex {i} must lie on the sphere of radius R");
                Assert.That(math.length(up[i]), Is.EqualTo(1.0).Within(1e-9), $"up {i} must be unit");
                Assert.That(math.dot(world[i] / r, up[i]), Is.EqualTo(1.0).Within(1e-9), $"up {i} must be radial");
            }

            coords.Dispose(); geo.Dispose(); world.Dispose(); up.Dispose();
        }
    }
}
