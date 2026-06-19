using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Full-pipeline headless test: decode → assemble → earcut over all 239 country features.
    /// Validates that:
    ///   (a) The pipeline terminates (no infinite loops / stall-guard bails).
    ///   (b) Every earcut index is within the valid vertex range (checked via Result.Vertices).
    ///   (c) Area is approximately conserved for simple polygons (no holes): |triArea − outerArea|
    ///       / outerArea &lt; 1%. Self-intersecting simple rings are skipped for area conservation,
    ///       but the skip count is pinned at the known value (4) and EVERY skip is proved degenerate
    ///       via a direct O(n²) segment-intersection + vertex-coincidence test before being counted.
    ///   (c2) Area is approximately conserved for holed polygons: |triArea − (outerArea − ΣholeArea)|
    ///       / expected &lt; 1%. Holed polygons are skipped only when ALL rings (outer + every hole)
    ///       are proved self-intersecting via HasSelfIntersection(). Well-formed holed polygons
    ///       (all rings clean) must conserve area.
    ///   (d) MeshBuilder produces a mesh with UInt32 index format.
    ///
    /// KNOWN SKIP POLYGONS (sample-tile fixture, all confirmed degenerate by HasSelfIntersection):
    ///   Four tiny clip-boundary slivers with self-intersecting rings (MVT tile-boundary artefacts).
    ///   All four have forceClips=0 (stall guard did NOT fire on them; the area inflation is purely
    ///   geometric — the rings are intrinsically degenerate before earcut sees them):
    ///     - 4-vert ring: verts (3265,1333)(3273,1328)(3274,1326)(3270,1333)
    ///                    shoelace=12.0, triArea=23.0 (ratio 1.92x) — proper edge crossing
    ///     - 5-vert ring: verts (1432,1432)(1433,1430)(1433,1429)(1433,1430)(1433,1433)
    ///                    shoelace=1.5,  triArea=2.5  (ratio 1.67x) — repeated vertex at (1433,1430)
    ///                    (positions i=1 and i=3 are identical; improper self-intersection)
    ///     - 4-vert ring: verts (1140,1300)(1142,1298)(1149,1297)(1147,1297)
    ///                    shoelace=3.0,  triArea=9.0  (ratio 3.0x)  — proper edge crossing
    ///     - 4-vert ring: verts (3515,1651)(3517,1649)(3517,1648)(3516,1652)
    ///                    shoelace=1.5,  triArea=3.5  (ratio 2.33x) — proper edge crossing
    ///   If the pinned count (4) fails after a change, DO NOT simply update the constant —
    ///   verify each new skip is genuinely degenerate before updating.
    /// </summary>
    public class FullPipelineTests
    {
        // -----------------------------------------------------------------------------------------
        // (a,b,c,c2) Real-data full pipeline: decode → assemble → earcut, 239 features
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Countries_FullPipeline_Terminates_IndexesValid_AreaConserved()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");

            var mvtTile = MvtDecoder.Decode(File.ReadAllBytes(path));
            var layer = mvtTile.GetLayer("countries");
            Assert.IsNotNull(layer);
            Assert.AreEqual(239, layer.Features.Count, "Expected 239 country features.");

            int featureCount = 0;
            int polygonCount = 0;
            int totalTriangles = 0;
            int simpleAreaChecks = 0;
            int holedAreaChecks  = 0;
            double totalSimpleRelError = 0.0;
            double totalHoledRelError  = 0.0;
            // Pinned count of self-intersecting polygons that legitimately skip area conservation.
            // Each skip is proved self-intersecting via HasSelfIntersection() before being counted.
            // If this assertion fails after an earcut change, confirm the new skip is a real
            // self-intersection before updating the constant — do not simply increment it.
            int skipCount = 0;
            int totalForceClips = 0;

            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon) continue;

                var rings = MvtGeometry.Decode(feature.Geometry);
                Assert.IsNotNull(rings);

                var polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    Assert.IsNotNull(polygon.Outer, "Outer ring must not be null.");
                    Assert.GreaterOrEqual(polygon.Outer.Count, 3, "Outer ring must have >= 3 verts.");

                    // (a) Triangulate — if this hangs, the test times out.
                    var result = Earcut.Triangulate(polygon.Outer, polygon.Holes);

                    // Accumulate force-clip count (stall-guard firings) for summary log.
                    totalForceClips += result.ForceClips;

                    // (b) Index range — use result.Vertices.Length (no reconstruction needed).
                    foreach (int idx in result.Indices)
                    {
                        Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                            $"Index {idx} out of range [0, {result.Vertices.Length}) " +
                            $"for polygon with {polygon.Outer.Count} outer verts.");
                    }

                    bool hasHoles = polygon.Holes != null && polygon.Holes.Count > 0;

                    if (!hasHoles && result.Indices.Length >= 3)
                    {
                        // (c) Area conservation for simple polygons (no holes), tile space.
                        // Self-intersecting (bowtie) polygons in real MVT tiles have shoelace area ≠
                        // sum-of-triangle-areas by definition. Before skipping, we PROVE the ring is
                        // self-intersecting via a direct O(n²) segment-intersection test (HasSelfIntersection),
                        // so the skip cannot silently hide earcut defects on well-formed polygons.
                        double outerArea = AbsArea(polygon.Outer);
                        double triArea = ComputeTriArea(result.Vertices, result.Indices);
                        if (outerArea > 1.0) // skip near-zero areas (degenerate slivers of <1 sq tile-unit)
                        {
                            bool likelySelfIntersecting = triArea > outerArea * 1.5;
                            if (likelySelfIntersecting)
                            {
                                // Prove self-intersection before counting the skip.
                                // A ring that is NOT self-intersecting but fails area conservation
                                // indicates an earcut defect; Assert.IsTrue surfaces it immediately.
                                Assert.IsTrue(
                                    HasSelfIntersection(polygon.Outer),
                                    $"Area skip for {polygon.Outer.Count}-vert polygon (triArea={triArea:F2}, " +
                                    $"outerArea={outerArea:F2}, forceClips={result.ForceClips}) but ring has " +
                                    $"no self-intersection (proper crossing, repeated vertex, or T-junction) — " +
                                    $"this is an earcut defect, not a degenerate input.");
                                skipCount++;
                            }
                            else
                            {
                                double relErr = Math.Abs(triArea - outerArea) / outerArea;
                                totalSimpleRelError += relErr;
                                simpleAreaChecks++;

                                Assert.That(relErr, Is.LessThan(0.01),
                                    $"Area conservation error {relErr:F6} exceeds 1% for simple polygon with " +
                                    $"{polygon.Outer.Count} verts. triArea={triArea:F2}, expected={outerArea:F2}, " +
                                    $"forceClips={result.ForceClips}");
                            }
                        }
                    }
                    else if (hasHoles && result.Indices.Length >= 3)
                    {
                        // (c2) Area conservation for holed polygons.
                        // Gate: ALL rings (outer + every hole) must be non-self-intersecting to
                        // require area conservation. Rings with proper crossings or repeated vertices
                        // (MVT tile-boundary artefacts) produce inflated areas by definition and are
                        // legitimately excused. Well-formed holed polygons must conserve area to < 1%.
                        double outerArea = AbsArea(polygon.Outer);
                        if (outerArea > 1.0)
                        {
                            // Check all rings for self-intersection.
                            bool allRingsClean = !HasSelfIntersection(polygon.Outer);
                            if (allRingsClean && polygon.Holes != null)
                            {
                                foreach (var hole in polygon.Holes)
                                {
                                    if (HasSelfIntersection(hole))
                                    {
                                        allRingsClean = false;
                                        break;
                                    }
                                }
                            }

                            if (allRingsClean)
                            {
                                double holeAreasSum = 0.0;
                                if (polygon.Holes != null)
                                    foreach (var hole in polygon.Holes)
                                        holeAreasSum += AbsArea(hole);

                                double expectedArea = outerArea - holeAreasSum;
                                if (expectedArea > 1.0) // skip near-zero expected areas
                                {
                                    double triArea = ComputeTriArea(result.Vertices, result.Indices);
                                    double relErr  = Math.Abs(triArea - expectedArea) / expectedArea;
                                    totalHoledRelError += relErr;
                                    holedAreaChecks++;

                                    Assert.That(relErr, Is.LessThan(0.01),
                                        $"Hole area conservation error {relErr:F6} exceeds 1% for polygon with " +
                                        $"{polygon.Outer.Count} outer verts, " +
                                        $"{(polygon.Holes == null ? 0 : polygon.Holes.Count)} hole(s). " +
                                        $"triArea={triArea:F2}, expected={expectedArea:F2} " +
                                        $"(outerArea={outerArea:F2} - holeSum={holeAreasSum:F2}), " +
                                        $"forceClips={result.ForceClips}. allRingsClean=true so this is an " +
                                        $"earcut defect on well-formed input.");
                                }
                            }
                        }
                    }

                    totalTriangles += result.Indices.Length / 3;
                    polygonCount++;
                }
                featureCount++;
            }

            Assert.AreEqual(239, featureCount, "Should have processed all 239 polygon features.");
            Assert.Greater(polygonCount, 0, "Should have assembled at least one polygon.");
            Assert.Greater(totalTriangles, 0, "Should have produced at least one triangle.");

            // Pinned skip count: exactly 4 self-intersecting clip-boundary slivers in the fixture.
            // All 4 confirmed degenerate via HasSelfIntersection (3 with proper edge crossings,
            // 1 with a repeated vertex (1433,1430) at positions i=1,j=3 in a 5-vert ring).
            // All 4 have forceClips=0 — the stall guard did NOT fire on them.
            // If this fails after an earcut or assembler change, DO NOT just update the constant —
            // verify each new skip is genuinely self-intersecting before changing the pin.
            Assert.AreEqual(4, skipCount,
                $"Expected exactly 4 self-intersecting polygon skips in the countries fixture, " +
                $"got {skipCount}. If an earcut change caused this, verify each new skip is a real " +
                $"degenerate ring (HasSelfIntersection returns true) before updating the pinned count.");

            if (simpleAreaChecks > 0)
                UnityEngine.Debug.Log($"[FullPipeline] Simple-polygon area conservation: avg relErr={totalSimpleRelError / simpleAreaChecks:F8} over {simpleAreaChecks} polygons.");
            if (holedAreaChecks > 0)
                UnityEngine.Debug.Log($"[FullPipeline] Holed-polygon area conservation: avg relErr={totalHoledRelError / holedAreaChecks:F8} over {holedAreaChecks} clean polygons.");
            UnityEngine.Debug.Log($"[FullPipeline] {featureCount} features → {polygonCount} polygons → {totalTriangles} triangles; {skipCount} degenerate skips; {totalForceClips} total force-clips.");
        }

        // -----------------------------------------------------------------------------------------
        // Regression test: worst-case CLEAN holed polygon from the fixture (any outer-vert count).
        // History: tiny tile-boundary clip artefacts produce opposite-wound rings that are NOT
        // spatially contained in their exterior. The original sign-only PolygonAssembler mis-nested
        // them as holes, so Earcut bridged across the gap to a far-away ring and inflated triangle
        // area up to ~34x (this masqueraded as an "earcut bridge bug"). The real fix is in
        // PolygonAssembler (RingContainedIn drops disjoint rings); Earcut is correct for genuine
        // holes. This test takes the worst-ratio REAL holed polygon — all rings non-self-intersecting,
        // hole(s) genuinely contained — and asserts area conservation, guarding against regression of
        // EITHER the assembler nesting (a disjoint hole would re-inflate the ratio) or Earcut bridging.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void HoledPolygon_WorstCase_AreaIsConserved()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");

            var mvtTile = MvtDecoder.Decode(File.ReadAllBytes(path));
            var layer = mvtTile.GetLayer("countries");
            Assert.IsNotNull(layer);

            // Worst holed polygon (largest triArea/expected ratio) among ALL holed polygons whose
            // rings are all non-self-intersecting. No outer-vert-count restriction: after the
            // assembler fix the worst real case has a many-vert outer, not a 4-vert sliver.
            double   worstRatio    = 0.0;
            Polygon  worstPolygon  = null;
            Earcut.Result worstResult = default;
            double   worstExpected = 0.0, worstTri = 0.0;

            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon) continue;

                var rings    = MvtGeometry.Decode(feature.Geometry);
                var polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    if (polygon.Holes == null || polygon.Holes.Count < 1) continue;

                    // Only consider clean polygons (all rings non-self-intersecting).
                    if (HasSelfIntersection(polygon.Outer)) continue;
                    bool clean = true;
                    foreach (var hole in polygon.Holes)
                        if (HasSelfIntersection(hole)) { clean = false; break; }
                    if (!clean) continue;

                    double outerArea = AbsArea(polygon.Outer);
                    double holeSum   = 0.0;
                    foreach (var hole in polygon.Holes) holeSum += AbsArea(hole);
                    double expected  = outerArea - holeSum;
                    if (expected < 1.0) continue;

                    var res = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (res.Indices.Length < 3) continue;

                    double triArea = ComputeTriArea(res.Vertices, res.Indices);
                    double ratio   = triArea / expected;

                    if (ratio > worstRatio)
                    {
                        worstRatio    = ratio;
                        worstPolygon  = polygon;
                        worstResult   = res;
                        worstExpected = expected;
                        worstTri      = triArea;
                    }
                }
            }

            Assert.IsNotNull(worstPolygon,
                "No clean holed polygon (all rings non-self-intersecting, hole contained) found in " +
                "the fixture. The regression test needs updating if the fixture changed.");

            double worstRelErr = Math.Abs(worstTri - worstExpected) / worstExpected;

            UnityEngine.Debug.Log(
                $"[Regression] Worst clean holed polygon: outerV={worstPolygon.Outer.Count}, " +
                $"holes={worstPolygon.Holes.Count}, expected={worstExpected:F2}, triArea={worstTri:F2}, " +
                $"ratio={worstRatio:F4}, relErr={worstRelErr:F6}, forceClips={worstResult.ForceClips}.");

            Assert.That(worstRelErr, Is.LessThan(0.01),
                $"Worst clean holed polygon area conservation error {worstRelErr:F6} exceeds 1% " +
                $"(outerV={worstPolygon.Outer.Count}, holes={worstPolygon.Holes.Count}, " +
                $"triArea={worstTri:F2}, expected={worstExpected:F2}, ratio={worstRatio:F4}, " +
                $"forceClips={worstResult.ForceClips}). allRingsClean=true → a disjoint hole slipped " +
                $"through PolygonAssembler, or Earcut hole-bridging regressed.");
        }

        // -----------------------------------------------------------------------------------------
        // (d) MeshBuilder: UInt32 index format
        // -----------------------------------------------------------------------------------------

        [Test]
        public void MeshBuilder_ProducesUInt32Mesh()
        {
            var builder = new MeshBuilder();
            builder.AddFeature(
                new float3[] { new float3(0,0,0), new float3(1,0,0), new float3(0,0,1) },
                new int[] { 0, 1, 2 });

            var mesh = builder.Build();
            Assert.IsNotNull(mesh);
            Assert.AreEqual(IndexFormat.UInt32, mesh.indexFormat);
            Assert.AreEqual(3, mesh.vertexCount);
            Assert.AreEqual(3, mesh.triangles.Length);
        }

        [Test]
        public void MeshBuilder_VertexAndIndexCountsMatch()
        {
            var builder = new MeshBuilder();
            builder.AddFeature(
                new float3[] { new float3(0,0,0), new float3(1,0,0), new float3(0,0,1) },
                new int[] { 0, 1, 2 });
            builder.AddFeature(
                new float3[] { new float3(2,0,0), new float3(3,0,0), new float3(2,0,1), new float3(3,0,1) },
                new int[] { 0, 1, 2, 0, 2, 3 });

            Assert.AreEqual(7, builder.VertexCount);
            Assert.AreEqual(9, builder.IndexCount);

            var mesh = builder.Build();
            Assert.AreEqual(7, mesh.vertexCount);
            Assert.AreEqual(9, mesh.triangles.Length);
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// O(n²) test for degenerate / self-intersecting rings. A ring is considered degenerate
        /// (and a legitimate skip for area conservation) if any of the following holds:
        ///   (1) Proper edge crossing: two non-adjacent edges cross in their interiors (bowtie).
        ///   (2) Repeated vertex: any two non-adjacent vertices share the same coordinates
        ///       (creates backtracking overlap that inflates triangle-area vs shoelace-area).
        ///   (3) Vertex-on-non-adjacent-edge: a vertex lies strictly on a non-adjacent edge
        ///       (T-junction degenerate case).
        /// Returns true iff the ring is degenerate by any of these criteria.
        /// Used to prove that each area-check skip is caused by a genuinely degenerate input,
        /// not by an earcut triangulation defect. If HasSelfIntersection returns false for a skipped
        /// polygon, that polygon's area inflation must be an earcut bug (stall-guard force-clip
        /// producing garbage triangles on a well-formed ring).
        /// </summary>
        private static bool HasSelfIntersection(List<double2> ring)
        {
            int n = ring.Count;
            if (n < 3) return false;

            // (2) Repeated vertices: any pair of non-adjacent vertices with identical coordinates.
            // Adjacent vertices (i, i+1) sharing coords would be a zero-length edge, which is
            // degenerate but handled separately; here we detect the topologically-significant case
            // of non-adjacent vertex coincidence that creates backtracking overlap.
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent (wrap-around), skip
                    if (ring[i].x == ring[j].x && ring[i].y == ring[j].y)
                        return true; // repeated vertex → degenerate ring
                }
            }

            if (n < 4) return false; // need at least 4 verts for edge tests

            // (1) Proper edge crossing: non-adjacent edges cross in their interiors.
            // (3) Vertex-on-non-adjacent-edge: checked inside the edge-pair loop below.
            for (int i = 0; i < n; i++)
            {
                double2 a0 = ring[i];
                double2 a1 = ring[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent (wrap-around), skip
                    double2 b0 = ring[j];
                    double2 b1 = ring[(j + 1) % n];
                    // (1) Proper crossing.
                    if (SegmentsProperlyIntersect(a0, a1, b0, b1))
                        return true;
                    // (3) Vertex of edge a on edge b, or vertex of edge b on edge a.
                    if (PointOnSegment(b0, a0, a1) || PointOnSegment(b1, a0, a1) ||
                        PointOnSegment(a0, b0, b1) || PointOnSegment(a1, b0, b1))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true iff segments (p0,p1) and (q0,q1) properly intersect (interiors cross;
        /// shared endpoints are NOT counted as intersections).
        /// </summary>
        private static bool SegmentsProperlyIntersect(double2 p0, double2 p1, double2 q0, double2 q1)
        {
            double d1 = CrossScalar(q0, q1, p0);
            double d2 = CrossScalar(q0, q1, p1);
            double d3 = CrossScalar(p0, p1, q0);
            double d4 = CrossScalar(p0, p1, q1);
            // Proper intersection: each segment straddles the other's line.
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;
            return false;
        }

        /// <summary>
        /// Returns true iff point p lies strictly on segment (a,b) — including at the endpoints.
        /// Uses the collinearity + bounding-box test.
        /// </summary>
        private static bool PointOnSegment(double2 p, double2 a, double2 b)
        {
            // Must be collinear: cross product == 0.
            double cross = CrossScalar(a, b, p);
            if (Math.Abs(cross) > 1e-10) return false;
            // Must be within the bounding box of the segment.
            return p.x >= Math.Min(a.x, b.x) && p.x <= Math.Max(a.x, b.x) &&
                   p.y >= Math.Min(a.y, b.y) && p.y <= Math.Max(a.y, b.y);
        }

        /// <summary>Cross product of (b-a) × (p-a): sign indicates which side of line ab p is on.</summary>
        private static double CrossScalar(double2 a, double2 b, double2 p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static double AbsArea(List<double2> ring)
        {
            if (ring == null || ring.Count < 3) return 0.0;
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += (b.x - a.x) * (b.y + a.y);
            }
            return Math.Abs(area) * 0.5;
        }

        private static double ComputeTriArea(double2[] verts, int[] indices)
        {
            double total = 0.0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                double2 a = verts[indices[i]];
                double2 b = verts[indices[i + 1]];
                double2 c = verts[indices[i + 2]];
                total += Math.Abs(0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)));
            }
            return total;
        }
    }
}
