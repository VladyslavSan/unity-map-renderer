using System;
using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-side validator for the polygon triangulation stage (decode → assemble → earcut). Checks that a
    /// triangulation faithfully reproduces its source polygon-with-holes, independent of projection (it works
    /// in flat tile space). It is deliberately rasterisation-based, not area-only: a thin folded sliver has
    /// ~zero area but is visible, so an area check alone would pass a torn mesh.
    ///
    /// Invariants (see docs/mesh-triangulation-robustness-design.md §3):
    ///   • ForceClips == 0        — the triangulator never gave up and emitted garbage;
    ///   • WindingFlips == 0      — no inverted (folded) triangle;
    ///   • AreaRelError small     — Σ tri area ≈ Σ(outer − holes);
    ///   • MismatchPct small      — rasterised coverage matches the even-odd source fill, so no phantom holes
    ///                              (source-inside-but-untriangulated) and no spill (triangulated-but-outside).
    /// </summary>
    public static class MeshCoverageValidator
    {
        public readonly struct Report
        {
            public readonly int    Polygons, Triangles, ForceClips, WindingFlips;
            public readonly double AreaExpected, AreaActual, AreaRelError;
            public readonly int    PolyCells, MissingCells, ExtraCells;   // rasterised
            public readonly double MismatchPct;
            public readonly string AsciiMap;

            public Report(int polygons, int triangles, int forceClips, int windingFlips,
                          double areaExpected, double areaActual,
                          int polyCells, int missingCells, int extraCells, string asciiMap)
            {
                Polygons = polygons; Triangles = triangles; ForceClips = forceClips; WindingFlips = windingFlips;
                AreaExpected = areaExpected; AreaActual = areaActual;
                AreaRelError = areaExpected > 1.0 ? math.abs(areaActual - areaExpected) / areaExpected : 0.0;
                PolyCells = polyCells; MissingCells = missingCells; ExtraCells = extraCells;
                MismatchPct = polyCells > 0 ? 100.0 * (missingCells + extraCells) / polyCells : 0.0;
                AsciiMap = asciiMap;
            }

            /// <summary>The triangulation faithfully reproduces the source polygons.</summary>
            public bool Passes(double areaEps = 0.01, double mismatchEps = 1.0)
                => ForceClips == 0 && WindingFlips == 0 && AreaRelError <= areaEps && MismatchPct <= mismatchEps;

            public string Summary =>
                $"polys={Polygons} tris={Triangles} forceClips={ForceClips} windingFlips={WindingFlips} " +
                $"areaRel={AreaRelError:P2} coverageMismatch={MismatchPct:F2}% " +
                $"(missing={MissingCells} extra={ExtraCells} of {PolyCells})";
        }

        /// <summary>
        /// Validate an ALREADY-triangulated result against its ground-truth polygon rings — the caller
        /// triangulates (via the Burst <c>EarcutJob</c>, directly or through the production fill path)
        /// and supplies both the triangles and <paramref name="forceClips"/> (the triangulator's own
        /// clean-drop count); this validator only checks the result, so it works for any triangulation
        /// source.
        ///
        /// Winding-flip detection uses ONE global majority sign across every triangle in
        /// <paramref name="tris"/>: the triangulator normalises every polygon's outer ring to the same
        /// CCW-on-screen convention, so a whole tile's triangles share one winding sign on clean output —
        /// a flip anywhere is a fold. This is the only option available here since the caller's flat
        /// triangle list carries no per-polygon boundary markers.
        /// </summary>
        public static Report ValidateTriangulation(
            IReadOnlyList<Polygon> groundTruthPolys, List<(double2 a, double2 b, double2 c)> tris,
            int forceClips, int extent, int rasterN = 192)
        {
            double expected = 0;
            var rings = new List<List<double2>>();
            foreach (var poly in groundTruthPolys)
            {
                if (poly.Outer == null || poly.Outer.Count < 3) continue;
                double holeSum = 0;
                rings.Add(poly.Outer);
                if (poly.Holes != null)
                    foreach (var h in poly.Holes) { holeSum += SignedArea.AbsArea(h); rings.Add(h); }
                expected += math.max(0.0, SignedArea.AbsArea(poly.Outer) - holeSum);
            }

            double actual = 0;
            int pos = 0, neg = 0;
            var triSigns = new List<int>(tris.Count);
            foreach (var (a, b, c) in tris)
            {
                double s2 = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                actual += math.abs(0.5 * s2);
                int sign = s2 > 1e-9 ? 1 : s2 < -1e-9 ? -1 : 0;
                triSigns.Add(sign);
                if (sign > 0) pos++; else if (sign < 0) neg++;
            }
            int majority = pos >= neg ? 1 : -1;
            int windingFlips = 0;
            foreach (int s in triSigns) if (s != 0 && s != majority) windingFlips++;

            var (missing, extra, polyCells, ascii) = CoverageDiff(rings, tris, extent, rasterN);
            return new Report(groundTruthPolys.Count, tris.Count, forceClips, windingFlips,
                               expected, actual, polyCells, missing, extra, ascii);
        }

        // Ground truth: even-odd fill over ALL rings (outer+holes) — nesting-agnostic, so it is the true
        // geometric coverage regardless of how the assembler classified rings. Rendered: union of triangles.
        private static (int missing, int extra, int polyCells, string ascii)
            CoverageDiff(List<List<double2>> rings, List<(double2 a, double2 b, double2 c)> tris, int extent, int N)
        {
            var inPoly = new bool[N * N];
            var inTri  = new bool[N * N];
            double cell = (double)extent / N;

            var xs = new List<double>(64);
            for (int r = 0; r < N; r++)
            {
                double yc = (r + 0.5) * cell;
                xs.Clear();
                foreach (var ring in rings)
                {
                    int n = ring.Count;
                    for (int i = 0; i < n; i++)
                    {
                        double2 a = ring[i], b = ring[(i + 1) % n];
                        if ((a.y <= yc && b.y > yc) || (b.y <= yc && a.y > yc))
                            xs.Add(a.x + (yc - a.y) / (b.y - a.y) * (b.x - a.x));
                    }
                }
                if (xs.Count < 2) continue;
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int c0 = (int)math.ceil(xs[k] / cell - 0.5);
                    int c1 = (int)math.floor(xs[k + 1] / cell - 0.5);
                    if (c0 < 0) c0 = 0;
                    if (c1 >= N) c1 = N - 1;
                    for (int c = c0; c <= c1; c++) inPoly[r * N + c] = true;
                }
            }

            foreach (var (a, b, c) in tris)
            {
                double minX = math.min(a.x, math.min(b.x, c.x)), maxX = math.max(a.x, math.max(b.x, c.x));
                double minY = math.min(a.y, math.min(b.y, c.y)), maxY = math.max(a.y, math.max(b.y, c.y));
                int cc0 = (int)math.floor(minX / cell), cc1 = (int)math.ceil(maxX / cell);
                int rr0 = (int)math.floor(minY / cell), rr1 = (int)math.ceil(maxY / cell);
                if (cc0 < 0) cc0 = 0; if (cc1 >= N) cc1 = N - 1;
                if (rr0 < 0) rr0 = 0; if (rr1 >= N) rr1 = N - 1;
                for (int r = rr0; r <= rr1; r++)
                {
                    double yc = (r + 0.5) * cell;
                    for (int cx = cc0; cx <= cc1; cx++)
                    {
                        int idx = r * N + cx;
                        if (inTri[idx]) continue;
                        if (PointInTri(a, b, c, (cx + 0.5) * cell, yc)) inTri[idx] = true;
                    }
                }
            }

            int missing = 0, extra = 0, polyCells = 0;
            for (int i = 0; i < N * N; i++)
            {
                if (inPoly[i]) { polyCells++; if (!inTri[i]) missing++; }
                else if (inTri[i]) extra++;
            }
            return (missing, extra, polyCells, Ascii(inPoly, inTri, N));
        }

        private static string Ascii(bool[] inPoly, bool[] inTri, int N)
        {
            int cols = 80, rowsA = 40;
            var art = new StringBuilder();
            for (int ar = 0; ar < rowsA; ar++)
            {
                for (int ac = 0; ac < cols; ac++)
                {
                    int r0 = ar * N / rowsA, r1 = (ar + 1) * N / rowsA;
                    int c0 = ac * N / cols,  c1 = (ac + 1) * N / cols;
                    bool m = false, x = false, ok = false;
                    for (int r = r0; r < r1; r++)
                        for (int c = c0; c < c1; c++)
                        {
                            int i = r * N + c;
                            if (inPoly[i] && !inTri[i]) m = true;
                            else if (!inPoly[i] && inTri[i]) x = true;
                            else if (inPoly[i]) ok = true;
                        }
                    art.Append(m ? 'M' : x ? 'X' : ok ? '#' : '.');
                }
                art.Append('\n');
            }
            return art.ToString();
        }

        private static bool PointInTri(double2 a, double2 b, double2 c, double px, double py)
        {
            double d1 = (b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x);
            double d2 = (c.x - b.x) * (py - b.y) - (c.y - b.y) * (px - b.x);
            double d3 = (a.x - c.x) * (py - c.y) - (a.y - c.y) * (px - c.x);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }
}
