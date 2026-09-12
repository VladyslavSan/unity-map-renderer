// Unity EditMode only — NativeArray/NativeList, Burst jobs, UnityEngine.Application. NOT registered in
// core-tests.csproj.
//
// SHARED between the wall-job stage and the line-graph stage — whichever lands first creates this file; the
// second adds to it. Both probes pin one thing each: that the managed (boxed IProjection / plain-loop) path
// and its Burst-compiled twin (TileToGeoJob / ProjectPointsJob<TProj>) agree — WITHIN THE BOUND
// job-scheduling-design.md §8 stage 5 measured, per field, not a flat tolerance — in the double domain.
// Neither probe bounds the float streams a
// caller derives afterwards (cast, normalize, cross) — see the wall stage's own A2 for that composite
// reading.
//
// REVISION (2026-09-04, E5 ruled): these were plain bitwise comparisons and reded on arrival — the ruling's
// own measurement showed the managed/Burst boundary is NOT bit-exact on every field. Each probe now carries
// the SAME two-part shape as A2: bitwise on the fields the measured table records at 0 ULP (Longitude;
// WebMercator World.x/y and Up.x/y/z — all LINEAR in the inputs), and that field's own measured bound
// elsewhere (Latitude: 3; WebMercator World.z: 4; Spherical World.x/y/z: 4/1/4; Spherical Up.x/y/z: 2/1/3 —
// all downstream of a transcendental: atan/sinh for Latitude and its dependents, sin/cos for the spherical
// arm). A flat bound across every field would stop the linear ones pinning anything — they are exactly the
// fields a genuine formula error would move. Never widen a bound past its measured figure (that is E5,
// closed) — a red here means either a real regression or the measurement no longer describes this hardware.
//
// A green here is NOT reassuring by default: ProjectPointsJob<TProj> runs under OptimizeFor.Performance
// (Burst's relaxed float mode). Bit-identity on a field the table records as diverging (any transcendental-
// dependent field) is POSITIVE EVIDENCE Burst did not run, not evidence the two agree — the inverse of what
// the old bitwise-everywhere shape implied. Burst_IsEnabled_OrTheProjectionProbesAreVacuous below is the
// direct check; CompileSynchronously = true also falls back to managed IL SILENTLY on a Burst compile
// failure (FillGraphBurstProbeTests's own precedent), so the log's Burst-error grep is part of the verdict
// too, not just the toggle.
//
// RED-verified by perturbing one arm alone, run and reverted by hand — never left in this file:
//   (i)  feed one arm Extent + 1.0 (a COARSE injection — a single-ULP TileCoords bump cannot red this probe;
//        see the wall plan §5(b) for the magnitude analysis)
//   (ii) swap one arm's projection instance for the other projection type

using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Projection;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class ProjectionManagedVersusBurstTests
    {
        private static readonly IProjection[] Projections =
        {
            new WebMercatorProjection(),
            new SphericalProjection(),
        };

        // ── The Burst precondition — job-scheduling-design.md's own register, FillMeshGraphStructureTests'
        // JobDebugger_IsEnabled_… precedent. No injection of its own: RED-verify by hand, toggling
        // Jobs ▸ Burst ▸ Enable Compilation off. ─────────────────────────────────────────────────────────
        [Test]
        public void Burst_IsEnabled_OrTheProjectionProbesAreVacuous()
        {
            Assert.IsTrue(BurstCompiler.Options.EnableBurstCompilation,
                "Jobs ▸ Burst ▸ Enable Compilation is OFF — TileToGeoJob/ProjectPointsJob execute their " +
                "managed IL directly under [BurstCompile], so the two probes below compare one managed " +
                "method against itself and prove nothing while this reads false.");
        }

        // ── (i) TileToGeoJob.GeoAt vs the Burst job. Projection-independent — both projections exercise the ─
        // ── same code path here, which is expected (see the file header). ────────────────────────────────
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void TileToGeo_ManagedGeoAt_MatchesBurstJob(string fixture, int z, int x, int y)
        {
            foreach (IProjection proj in Projections)
                RunTileToGeoProbe(fixture, z, x, y, proj);
        }

        private static void RunTileToGeoProbe(string fixture, int z, int x, int y, IProjection proj)
        {
            var id = new TileId { Z = z, X = x, Y = y };
            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            MvtLayer layer = tile.GetLayer("boundary");
            Assert.IsNotNull(layer, "the \"boundary\" MVT source-layer (backing the boundary_3 style layer) must be present in this fixture");

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED — owned by `tile`, not disposed here
            int n = geometry.VertexCount;
            Assert.Greater(n, 0, $"expected boundary_3 ring vertices in {fixture} ({proj.GetType().Name})");

            var tileCoords = new NativeArray<double2>(n, Allocator.TempJob);
            var burstGeo   = new NativeArray<GeoCoordinate>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++) tileCoords[i] = geometry.Vertices[i];

                var managedGeo = new GeoCoordinate[n];
                for (int i = 0; i < n; i++)
                    managedGeo[i] = TileToGeoJob.GeoAt(geometry.Tile, geometry.Extent, tileCoords[i]);

                new TileToGeoJob
                {
                    Tile = geometry.Tile, Extent = geometry.Extent,
                    TileCoords = tileCoords, OutGeo = burstGeo,
                }.Run(n);

                for (int i = 0; i < n; i++)
                {
                    // Longitude is LINEAR in the inputs (a single scale + offset) — bit-exact, E5's table.
                    AssertBitEqualDouble(managedGeo[i].Longitude, burstGeo[i].Longitude, fixture, proj, i, "Longitude");
                    // Latitude is downstream of atan/sinh — bounded at its measured worst case.
                    AssertUlpBound(managedGeo[i].Latitude, burstGeo[i].Latitude, LatitudeMaxUlp, fixture, proj, i, "Latitude");
                }
            }
            finally
            {
                tileCoords.Dispose();
                burstGeo.Dispose();
            }
        }

        // ── (ii) IProjection.ProjectPoint vs ProjectPointsJob<TProj>. Compared as double3, NO float3 cast — ─
        // ── a managed↔Burst difference below a float ulp would otherwise compare equal and this probe would ─
        // ── report green while measuring nothing. This is the ONE sanctioned .Schedule in the wall stage: ──
        // ── this [Test] body runs on the main thread (where scheduling is legal), and A0 runs before A1.0 ──
        // ── reinstates the off-main .Run() entry point this probe's own subject does not need yet. ────────
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void ProjectPoint_Managed_MatchesProjectPointsJob(string fixture, int z, int x, int y)
        {
            foreach (IProjection proj in Projections)
                RunProjectPointProbe(fixture, z, x, y, proj);
        }

        private static void RunProjectPointProbe(string fixture, int z, int x, int y, IProjection proj)
        {
            var id = new TileId { Z = z, X = x, Y = y };
            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            MvtLayer layer = tile.GetLayer("boundary");
            Assert.IsNotNull(layer, "the \"boundary\" MVT source-layer (backing the boundary_3 style layer) must be present in this fixture");

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED
            int n = geometry.VertexCount;
            Assert.Greater(n, 0, $"expected boundary_3 ring vertices in {fixture} ({proj.GetType().Name})");

            var geo = new GeoCoordinate[n];
            for (int i = 0; i < n; i++)
                geo[i] = TileToGeoJob.GeoAt(geometry.Tile, geometry.Extent, geometry.Vertices[i]);

            // Both arms MUST use the same origin — one local, passed to both, so a mismatch here can never
            // be mistaken for the managed-vs-Burst divergence this probe exists to catch.
            double3 origin = double3.zero;

            var managedWorld = new double3[n];
            var managedUp    = new double3[n];
            for (int i = 0; i < n; i++)
            {
                ProjectedPoint pp = proj.ProjectPoint(in geo[i]);
                managedWorld[i] = pp.World - origin;
                managedUp[i]    = pp.Up;
            }

            using var points  = new NativeList<GeoCoordinate>(n, Allocator.TempJob);
            using var world   = new NativeList<double3>(n, Allocator.TempJob);
            using var normals = new NativeList<double3>(n, Allocator.TempJob);
            points.ResizeUninitialized(n);
            world.ResizeUninitialized(n);
            normals.ResizeUninitialized(n);
            NativeArray<GeoCoordinate> pointsW = points.AsArray();
            for (int i = 0; i < n; i++) pointsW[i] = geo[i];

            ProjectionDispatch.Schedule(proj, origin, points, world, normals, default).Complete();

            // E5's bound is per-projection AND per-component — WebMercator's World.x/y and every Up
            // component are linear (bit-exact); everything else is downstream of sin/cos or sinh/atan.
            bool mercator = proj is WebMercatorProjection;
            ulong worldXMaxUlp = mercator ? 0UL : SphericalWorldXMaxUlp;
            ulong worldYMaxUlp = mercator ? 0UL : SphericalWorldYMaxUlp;
            ulong worldZMaxUlp = mercator ? WebMercatorWorldZMaxUlp : SphericalWorldZMaxUlp;
            ulong upXMaxUlp    = mercator ? 0UL : SphericalUpXMaxUlp;
            ulong upYMaxUlp    = mercator ? 0UL : SphericalUpYMaxUlp;
            ulong upZMaxUlp    = mercator ? 0UL : SphericalUpZMaxUlp;

            for (int i = 0; i < n; i++)
            {
                AssertUlpBound(managedWorld[i].x, world[i].x,   worldXMaxUlp, fixture, proj, i, "World.x");
                AssertUlpBound(managedWorld[i].y, world[i].y,   worldYMaxUlp, fixture, proj, i, "World.y");
                AssertUlpBound(managedWorld[i].z, world[i].z,   worldZMaxUlp, fixture, proj, i, "World.z");
                AssertUlpBound(managedUp[i].x,     normals[i].x, upXMaxUlp,   fixture, proj, i, "Up.x");
                AssertUlpBound(managedUp[i].y,     normals[i].y, upYMaxUlp,   fixture, proj, i, "Up.y");
                AssertUlpBound(managedUp[i].z,     normals[i].z, upZMaxUlp,   fixture, proj, i, "Up.z");
            }
        }

        // ── E5's measured bounds (job-scheduling-design.md §8 stage 5's invariant block) — worst case across ─
        // ── both boundary fixtures, both projections, measured 2026-09-04 against these two exact jobs. ─────
        private const ulong LatitudeMaxUlp           = 3;
        private const ulong WebMercatorWorldZMaxUlp  = 4;
        private const ulong SphericalWorldXMaxUlp    = 4;
        private const ulong SphericalWorldYMaxUlp    = 1;
        private const ulong SphericalWorldZMaxUlp    = 4;
        private const ulong SphericalUpXMaxUlp       = 2;
        private const ulong SphericalUpYMaxUlp       = 1;
        private const ulong SphericalUpZMaxUlp       = 3;

        // ── Bitwise comparison — asulong so a NaN or a signed zero cannot pass as equal. Used only for the ──
        // ── 0-ULP (linear) fields; a genuine bit divergence there is a formula error, not float noise. ─────
        private static void AssertBitEqualDouble(
            double managed, double burst, string fixture, IProjection proj, int index, string label)
        {
            ulong m = math.asulong(managed), b = math.asulong(burst);
            Assert.AreEqual(m, b,
                $"{label}[{index}] diverges (expected bit-exact, E5's table) — {fixture} ({proj.GetType().Name}): " +
                $"managed=0x{m:X16} ({managed:R}) burst=0x{b:X16} ({burst:R})");
        }

        // ── Per-field ULP-distance bound, using the standard IEEE-754 total-order mapping (never a raw bit- ─
        // ── pattern subtraction, which is wrong across zero and across sign) — Bruce Dawson's AlmostEqualUlps
        // ── idiom: push positive doubles into the upper half of the ulong range, negative ones (bit-inverted)
        // ── into the lower half, so |orderedA - orderedB| is the true ULP distance regardless of sign. ──────
        private static ulong ToUlpOrder(double d)
        {
            long bits = System.BitConverter.DoubleToInt64Bits(d);
            ulong ubits = unchecked((ulong)bits);
            return (ubits & 0x8000000000000000UL) != 0 ? ~ubits : (ubits | 0x8000000000000000UL);
        }

        private static ulong UlpDistance(double a, double b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }

        private static void AssertUlpBound(
            double managed, double burst, ulong maxUlp, string fixture, IProjection proj, int index, string label)
        {
            ulong delta = UlpDistance(managed, burst);
            Assert.LessOrEqual(delta, maxUlp,
                $"{label}[{index}] exceeds its measured {maxUlp}-ULP bound (E5, job-scheduling-design.md §8 " +
                $"stage 5) — {fixture} ({proj.GetType().Name}): managed=0x{math.asulong(managed):X16} " +
                $"({managed:R}) burst=0x{math.asulong(burst):X16} ({burst:R}) delta={delta} ULP");
        }
    }
}
