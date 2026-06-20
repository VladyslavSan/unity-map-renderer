using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Fast, Unity-free pipeline tests over the committed fixture: decode -> assemble -> earcut, with
    /// area conservation. Mirrors the pure-logic part of the Unity FullPipelineTests (the MeshBuilder
    /// assertions stay Unity-only). Primary purpose: guard the disjoint-hole regression — a mis-nested
    /// spatially-disjoint ring would re-inflate triangulated area. Runs via `dotnet test` in ~1s.
    /// </summary>
    public class CorePipelineTests
    {
        // Walk up from the test assembly to find the repo's committed fixture.
        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                "sample-tile.bytes not found walking up from " + AppContext.BaseDirectory);
        }

        private static double AbsArea(List<double2> r) => SignedArea.AbsArea(r);

        private static double TriArea(double2[] v, int[] idx)
        {
            double t = 0;
            for (int i = 0; i < idx.Length; i += 3)
            {
                var a = v[idx[i]]; var b = v[idx[i + 1]]; var c = v[idx[i + 2]];
                t += Math.Abs(0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)));
            }
            return t;
        }

        private static double CrossS(double2 a, double2 b, double2 p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static bool Crosses(double2 p0, double2 p1, double2 q0, double2 q1)
        {
            double d1 = CrossS(q0, q1, p0), d2 = CrossS(q0, q1, p1);
            double d3 = CrossS(p0, p1, q0), d4 = CrossS(p0, p1, q1);
            return (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                    ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)));
        }

        private static bool SelfIntersects(List<double2> ring)
        {
            int n = ring.Count;
            if (n < 4) return false;
            for (int i = 0; i < n; i++)
            {
                double2 a0 = ring[i], a1 = ring[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    if (ring[i].x == ring[j].x && ring[i].y == ring[j].y) return true;
                    if (Crosses(a0, a1, ring[j], ring[(j + 1) % n])) return true;
                }
            }
            return false;
        }

        [Test]
        public void Decode_CountriesLayer_Has239Features()
        {
            var tile = MvtDecoder.Decode(LoadFixture());
            var layer = tile.GetLayer("countries");
            Assert.IsNotNull(layer);
            Assert.AreEqual(239, layer.Features.Count);
        }

        [Test]
        public void AllCleanHoledPolygons_ConserveArea()
        {
            var tile = MvtDecoder.Decode(LoadFixture());
            var layer = tile.GetLayer("countries");
            int checkedCount = 0, failed = 0;
            double worstRel = 0;
            foreach (var f in layer.Features)
            {
                if (f.GeometryType != MvtGeometryType.Polygon) continue;
                foreach (var poly in PolygonAssembler.Assemble(MvtGeometry.Decode(f.Geometry)))
                {
                    if (poly.Holes == null || poly.Holes.Count == 0) continue;
                    if (SelfIntersects(poly.Outer)) continue;
                    bool clean = true;
                    foreach (var h in poly.Holes) if (SelfIntersects(h)) { clean = false; break; }
                    if (!clean) continue;

                    double outerA = AbsArea(poly.Outer);
                    double holeSum = 0; foreach (var h in poly.Holes) holeSum += AbsArea(h);
                    double expected = outerA - holeSum;
                    if (expected <= 1.0) continue;

                    var res = Earcut.Triangulate(poly.Outer, poly.Holes);
                    if (res.Indices.Length < 3) continue;
                    double rel = Math.Abs(TriArea(res.Vertices, res.Indices) - expected) / expected;
                    checkedCount++;
                    if (rel > worstRel) worstRel = rel;
                    if (rel >= 0.01) failed++;
                }
            }
            Assert.Greater(checkedCount, 0, "Expected at least one clean holed polygon to validate.");
            Assert.AreEqual(0, failed,
                $"{failed}/{checkedCount} clean holed polygons failed area conservation (worstRel={worstRel:F4}). " +
                "A spatially-disjoint hole mis-nested by PolygonAssembler re-inflates the area.");
        }
    }
}
