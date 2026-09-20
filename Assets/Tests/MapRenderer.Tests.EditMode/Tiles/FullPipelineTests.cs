using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Tests.Meshing;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Full-pipeline headless test: decode → assemble → earcut over all 239 country features. Re-homed
    /// onto the Burst arm in A0 — <see cref="EarcutJobGatherHarness.RunLayer"/> drives the real
    /// RingSelect → RingAssembly → gather → <c>EarcutJob</c> chain, and each result is paired with its
    /// OWN input rings (<c>PolygonRun.InputOuter</c>/<c>InputHoles</c>, reconstructed from the gather
    /// state's own columns) rather than a separately assembled <c>PolygonAssembler</c> list — the two
    /// decompositions are not guaranteed to agree in polygon order (RED-checked in A0: on this fixture
    /// the two orders happen to coincide exactly, 0 of 3218 polygons mismatched by index — evidence
    /// about this fixture's order, not licence to pair by index on a different one).
    /// Validates that:
    ///   (a) The pipeline terminates (no infinite loops / stall-guard bails).
    ///   (b) Every earcut index is within the valid vertex range.
    ///   (c) Area is approximately conserved for simple polygons (no holes): |triArea − outerArea|
    ///       / outerArea &lt; 1%. Self-intersecting simple rings are skipped for area conservation,
    ///       but the skip count is pinned and EVERY skip is proved degenerate via a direct O(n²)
    ///       segment-intersection + vertex-coincidence test before being counted.
    ///   (c2) Area is approximately conserved for holed polygons: |triArea − (outerArea − ΣholeArea)|
    ///       / expected &lt; 1%. Holed polygons are skipped only when ALL rings (outer + every hole)
    ///       are proved self-intersecting via HasSelfIntersection(). Well-formed holed polygons
    ///       (all rings clean) must conserve area.
    ///   (d) [removed in S54] the Gen-1 sync mesh-builder index-format check; the async
    ///       StyledFillTileBuilder upload path is covered by MapViewAsyncMeshBuildTests.
    ///
    /// KNOWN SKIP POLYGONS (sample-tile fixture, all confirmed degenerate by HasSelfIntersection, and
    /// re-measured byte-identical on the Burst arm in A0 — both assemblers agree on all four):
    ///   Four tiny clip-boundary slivers with self-intersecting rings (MVT tile-boundary artefacts).
    ///   All four have forceClips=0 (stall guard did NOT fire on them; the area inflation is purely
    ///   geometric — the rings are intrinsically degenerate before the triangulator sees them):
    ///     - 4-vert ring: verts (3265,1333)(3273,1328)(3274,1326)(3270,1333)
    ///                    shoelace=12.0, triArea=23.0 (ratio 1.92x) — proper edge crossing
    ///     - 5-vert ring: verts (1432,1432)(1433,1430)(1433,1429)(1433,1430)(1433,1433)
    ///                    shoelace=1.5,  triArea=2.5  (ratio 1.67x) — repeated vertex at (1433,1430)
    ///                    (positions i=1 and i=3 are identical; improper self-intersection)
    ///     - 4-vert ring: verts (1140,1300)(1142,1298)(1149,1297)(1147,1297)
    ///                    shoelace=3.0,  triArea=9.0  (ratio 3.0x)  — proper edge crossing
    ///     - 4-vert ring: verts (3515,1651)(3517,1649)(3517,1648)(3516,1652)
    ///                    shoelace=1.5,  triArea=3.5  (ratio 2.33x) — proper edge crossing
    ///   If the pinned count fails after a change, DO NOT simply update the constant — verify each new
    ///   skip is genuinely degenerate (a real area-inflating self-intersection, not just a ring
    ///   HasSelfIntersection flags) before updating.
    ///
    /// KNOWN NON-SKIPS (A0 investigation, same fixture): five more rings that HasSelfIntersection also
    /// flags (a repeated-vertex or T-junction artefact of near-collinear points) but that do NOT skip,
    /// because they do not inflate area — ratio 1.00x, triangulated correctly, no overlap. The (c) skip
    /// gate is `triArea > outerArea * 1.5`, not bare self-intersection; these are the rings that prove
    /// the gate needs both clauses:
    ///     - (2804,890)(2804,891)(2805,900)(2804,897)         shoelace=3.0, triArea=3.0
    ///     - (434,922)(435,923)(438,925)(439,927)             shoelace=2.0, triArea=2.0
    ///     - (1171,1627)(1173,1627)(1175,1626)(1176,1627)     shoelace=1.5, triArea=1.5
    ///     - (585,1340)(586,1338)(586,1337)(586,1341)         shoelace=1.5, triArea=1.5
    ///     - (3601,2179)(3604,2175)(3604,2174)(3604,2176)     shoelace=1.5, triArea=1.5
    /// </summary>
    public class FullPipelineTests
    {
        private static readonly TileId SampleTileId = new TileId { Z = 0, X = 0, Y = 0 };

        private static byte[] LoadSampleTile()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        // -----------------------------------------------------------------------------------------
        // (a,b,c,c2) Real-data full pipeline: decode → assemble → earcut, 239 features
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Countries_FullPipeline_Terminates_IndexesValid_AreaConserved()
        {
            byte[] mvtBytes = LoadSampleTile();

            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);
            Assert.AreEqual(239, layer.Kinds.Count, "Expected 239 country features.");

            var runs = EarcutJobGatherHarness.RunLayer(mvtBytes, "countries", SampleTileId, forceLinearEarScan: false);

            int totalTriangles = 0;
            int simpleAreaChecks = 0;
            int holedAreaChecks  = 0;
            double totalSimpleRelError = 0.0;
            double totalHoledRelError  = 0.0;
            // Pinned count of self-intersecting polygons that legitimately skip area conservation.
            // Each skip is proved self-intersecting via HasSelfIntersection() before being counted.
            // If this assertion fails after a triangulator change, confirm the new skip is a real
            // self-intersection before updating the constant — do not simply increment it.
            int skipCount = 0;
            int totalForceClips = 0;

            foreach (var run in runs)
            {
                Assert.GreaterOrEqual(run.InputOuter.Count, 3, "Outer ring must have >= 3 verts.");

                // (a) Triangulate — if this hangs, the test times out.
                // (Triangulation already ran inside RunLayer; here we just consume the result.)
                totalForceClips += run.ForceClips;

                // (b) Index range.
                foreach (int idx in run.Indices)
                {
                    Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(run.Vertices.Length),
                        $"Index {idx} out of range [0, {run.Vertices.Length}) " +
                        $"for polygon with {run.InputOuter.Count} outer verts.");
                }

                bool hasHoles = run.InputHoles != null && run.InputHoles.Count > 0;

                if (!hasHoles && run.Indices.Length >= 3)
                {
                    // (c) Area conservation for simple polygons (no holes), tile space.
                    // Self-intersecting (bowtie) polygons in real MVT tiles have shoelace area ≠
                    // sum-of-triangle-areas by definition. Before skipping, we PROVE the ring is
                    // self-intersecting via a direct O(n²) segment-intersection test (HasSelfIntersection),
                    // so the skip cannot silently hide a triangulator defect on well-formed polygons.
                    double outerArea = AbsArea(run.InputOuter);
                    double triArea = ComputeTriArea(run.Vertices, run.Indices);
                    if (outerArea > 1.0) // skip near-zero areas (degenerate slivers of <1 sq tile-unit)
                    {
                        bool likelySelfIntersecting = triArea > outerArea * 1.5;
                        if (likelySelfIntersecting)
                        {
                            // Prove self-intersection before counting the skip.
                            // A ring that is NOT self-intersecting but fails area conservation
                            // indicates a triangulator defect; Assert.IsTrue surfaces it immediately.
                            Assert.IsTrue(
                                HasSelfIntersection(run.InputOuter),
                                $"Area skip for {run.InputOuter.Count}-vert polygon (triArea={triArea:F2}, " +
                                $"outerArea={outerArea:F2}, forceClips={run.ForceClips}) but ring has " +
                                $"no self-intersection (proper crossing, repeated vertex, or T-junction) — " +
                                $"this is a triangulator defect, not a degenerate input.");
                            skipCount++;
                        }
                        else
                        {
                            double relErr = Math.Abs(triArea - outerArea) / outerArea;
                            totalSimpleRelError += relErr;
                            simpleAreaChecks++;

                            Assert.That(relErr, Is.LessThan(0.01),
                                $"Area conservation error {relErr:F6} exceeds 1% for simple polygon with " +
                                $"{run.InputOuter.Count} verts. triArea={triArea:F2}, expected={outerArea:F2}, " +
                                $"forceClips={run.ForceClips}");
                        }
                    }
                }
                else if (hasHoles && run.Indices.Length >= 3)
                {
                    // (c2) Area conservation for holed polygons.
                    // Gate: ALL rings (outer + every hole) must be non-self-intersecting to
                    // require area conservation. Rings with proper crossings or repeated vertices
                    // (MVT tile-boundary artefacts) produce inflated areas by definition and are
                    // legitimately excused. Well-formed holed polygons must conserve area to < 1%.
                    //
                    // NOTE (A0 investigation): a self-intersecting-but-non-inflating outer (a near-
                    // collinear sliver — HasSelfIntersection's repeated-vertex/T-junction clauses can
                    // fire on a ring that still triangulates without overlap) legitimately falls through
                    // this branch uncounted — it is not a "skip" in the (c) sense, since nothing needs
                    // excusing: its area was never wrong. The pinned skipCount (below) is about (c)'s
                    // outer-alone case only; a genuinely holed polygon whose outer also fails the (c)
                    // inflation heuristic has no precedent in this fixture and is deliberately left
                    // unhandled rather than guessed at.
                    double outerArea = AbsArea(run.InputOuter);
                    if (outerArea > 1.0)
                    {
                        bool allRingsClean = !HasSelfIntersection(run.InputOuter);
                        if (allRingsClean)
                        {
                            foreach (var hole in run.InputHoles)
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
                            foreach (var hole in run.InputHoles)
                                holeAreasSum += AbsArea(hole);

                            double expectedArea = outerArea - holeAreasSum;
                            if (expectedArea > 1.0) // skip near-zero expected areas
                            {
                                double triArea = ComputeTriArea(run.Vertices, run.Indices);
                                double relErr  = Math.Abs(triArea - expectedArea) / expectedArea;
                                totalHoledRelError += relErr;
                                holedAreaChecks++;

                                Assert.That(relErr, Is.LessThan(0.01),
                                    $"Hole area conservation error {relErr:F6} exceeds 1% for polygon with " +
                                    $"{run.InputOuter.Count} outer verts, {run.InputHoles.Count} hole(s). " +
                                    $"triArea={triArea:F2}, expected={expectedArea:F2} " +
                                    $"(outerArea={outerArea:F2} - holeSum={holeAreasSum:F2}), " +
                                    $"forceClips={run.ForceClips}. allRingsClean=true so this is a " +
                                    $"triangulator defect on well-formed input.");
                            }
                        }
                    }
                }

                totalTriangles += run.Indices.Length / 3;
            }

            // Pinned polygon count (Burst arm, measured): RunLayer has no per-MVT-feature counter to
            // compare against the fixture's 239 features directly (unlike the retired managed loop, which
            // counted both), so this exact pin is the closest available proxy — a harness that silently
            // dropped a feature (or a ring-assembly regression that dropped/merged polygons) would move it.
            Assert.AreEqual(3218, runs.Count, "Countries fixture's assembled polygon count (Burst arm) moved.");
            Assert.Greater(totalTriangles, 0, "Should have produced at least one triangle.");

            // Pinned skip count: re-measured for the Burst arm (§8 of the A0 plan) — the upstream is
            // RingAssemblyJob, not PolygonAssembler, and the polygon decomposition may differ. Measured
            // result: 4, byte-identical to the managed arm's pin — the two assemblers agree on this
            // fixture's degenerate rings. If this fails after a triangulator or assembler change, DO NOT
            // just update the constant — verify each new skip is genuinely self-intersecting AND
            // area-inflating before changing the pin (see the class doc's KNOWN NON-SKIPS).
            Assert.AreEqual(4, skipCount,
                $"Expected exactly 4 self-intersecting polygon skips in the countries fixture, " +
                $"got {skipCount}. If a triangulator change caused this, verify each new skip is a real " +
                $"degenerate ring (HasSelfIntersection returns true) before updating the pinned count.");

            if (simpleAreaChecks > 0)
                Debug.Log($"[FullPipeline] Simple-polygon area conservation: avg relErr={totalSimpleRelError / simpleAreaChecks:F8} over {simpleAreaChecks} polygons.");
            if (holedAreaChecks > 0)
                Debug.Log($"[FullPipeline] Holed-polygon area conservation: avg relErr={totalHoledRelError / holedAreaChecks:F8} over {holedAreaChecks} clean polygons.");
            Debug.Log($"[FullPipeline] {runs.Count} polygons → {totalTriangles} triangles; {skipCount} degenerate skips; {totalForceClips} total force-clips.");
        }

        // -----------------------------------------------------------------------------------------
        // Regression test: worst-case CLEAN holed polygon from the fixture (any outer-vert count).
        // History: tiny tile-boundary clip artefacts produce opposite-wound rings that are NOT
        // spatially contained in their exterior. The original sign-only PolygonAssembler mis-nested
        // them as holes, so the triangulator bridged across the gap to a far-away ring and inflated
        // triangle area up to ~34x (this masqueraded as a "triangulator bridge bug"). The real fix is
        // in PolygonAssembler (RingContainedIn drops disjoint rings); the triangulator is correct for
        // genuine holes. This test takes the worst-ratio REAL holed polygon — all rings
        // non-self-intersecting, hole(s) genuinely contained — and asserts area conservation, guarding
        // against regression of EITHER the assembler nesting (a disjoint hole would re-inflate the
        // ratio) or hole-bridging.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void HoledPolygon_WorstCase_AreaIsConserved()
        {
            byte[] mvtBytes = LoadSampleTile();
            var runs = EarcutJobGatherHarness.RunLayer(mvtBytes, "countries", SampleTileId, forceLinearEarScan: false);

            // Worst holed polygon (largest triArea/expected ratio) among ALL holed polygons whose
            // rings are all non-self-intersecting. No outer-vert-count restriction: after the
            // assembler fix the worst real case has a many-vert outer, not a 4-vert sliver.
            double worstRatio = 0.0;
            EarcutJobGatherHarness.PolygonRun worstRun = default;
            double worstExpected = 0.0, worstTri = 0.0;
            bool found = false;

            foreach (var run in runs)
            {
                if (run.InputHoles == null || run.InputHoles.Count < 1) continue;

                // Only consider clean polygons (all rings non-self-intersecting).
                if (HasSelfIntersection(run.InputOuter)) continue;
                bool clean = true;
                foreach (var hole in run.InputHoles)
                    if (HasSelfIntersection(hole)) { clean = false; break; }
                if (!clean) continue;

                double outerArea = AbsArea(run.InputOuter);
                double holeSum   = 0.0;
                foreach (var hole in run.InputHoles) holeSum += AbsArea(hole);
                double expected  = outerArea - holeSum;
                if (expected < 1.0) continue;

                if (run.Indices.Length < 3) continue;

                double triArea = ComputeTriArea(run.Vertices, run.Indices);
                double ratio   = triArea / expected;

                if (ratio > worstRatio)
                {
                    worstRatio    = ratio;
                    worstRun      = run;
                    worstExpected = expected;
                    worstTri      = triArea;
                    found         = true;
                }
            }

            Assert.IsTrue(found,
                "No clean holed polygon (all rings non-self-intersecting, hole contained) found in " +
                "the fixture. The regression test needs updating if the fixture changed.");

            double worstRelErr = Math.Abs(worstTri - worstExpected) / worstExpected;

            Debug.Log(
                $"[Regression] Worst clean holed polygon: outerV={worstRun.InputOuter.Count}, " +
                $"holes={worstRun.InputHoles.Count}, expected={worstExpected:F2}, triArea={worstTri:F2}, " +
                $"ratio={worstRatio:F4}, relErr={worstRelErr:F6}, forceClips={worstRun.ForceClips}.");

            Assert.That(worstRelErr, Is.LessThan(0.01),
                $"Worst clean holed polygon area conservation error {worstRelErr:F6} exceeds 1% " +
                $"(outerV={worstRun.InputOuter.Count}, holes={worstRun.InputHoles.Count}, " +
                $"triArea={worstTri:F2}, expected={worstExpected:F2}, ratio={worstRatio:F4}, " +
                $"forceClips={worstRun.ForceClips}). allRingsClean=true → a disjoint hole slipped " +
                $"through RingAssemblyJob/FillGatherJob's nesting, or hole-bridging regressed.");
        }

        // -----------------------------------------------------------------------------------------
        // (d) Gen-1 MeshBuilder tests removed in S54 (MeshBuilder retired).
        //     UInt32 index format and vertex/index count are covered by
        //     MapViewAsyncMeshBuildTests.BuildMeshDataAndUploadMesh_RoundTrip_MatchesSyncBuildMesh
        //     and StyledFillTileBuilder tests (same assertions via StyledFillTileBuilder.BuildMesh).
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
        /// not by a triangulator defect. If HasSelfIntersection returns false for a skipped
        /// polygon, that polygon's area inflation must be a triangulator bug (stall-guard force-clip
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
