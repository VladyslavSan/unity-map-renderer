using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Mvt;
using MapRenderer.Jobs;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Differential oracle for <see cref="LineTessellationJob"/> (Burst) vs the managed reference
    /// <see cref="LineTessellator.Triangulate"/>. The two implementations must produce identical
    /// output for the same input — this is what makes the Burst port falsifiable (S89 D1).
    ///
    /// Parity is STRICT (bit-exact) for the arithmetic-only paths (miter/bevel joins, butt/square
    /// caps: only +,−,*,/,sqrt — all IEEE-correctly-rounded, so Burst and Mono agree exactly), and
    /// TIGHT-TOLERANCE for the transcendental paths (round join/cap use atan2/cos/sin, whose libm
    /// implementations may differ by a ULP between Burst and Mono — but the triangle topology, which
    /// is integer, must still match exactly).
    /// </summary>
    [TestFixture]
    public class LineTessellationJobTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // ── Oracle harness ──────────────────────────────────────────────────────────────────────

        private static (LineVertex[] verts, int[] indices) RunJob(
            double2[] pts, JoinType join, CapType cap, double miterLimit, int roundSegments)
        {
            int capV = LineTessellationJob.MaxVertexCount(pts.Length, roundSegments);
            int capI = LineTessellationJob.MaxIndexCount(pts.Length, roundSegments);

            var points = new NativeArray<double2>(pts.Length == 0 ? 1 : pts.Length, Allocator.TempJob);
            var outV   = new NativeArray<LineVertex>(capV == 0 ? 1 : capV, Allocator.TempJob);
            var outI   = new NativeArray<int>(capI == 0 ? 1 : capI, Allocator.TempJob);
            var vc     = new NativeArray<int>(1, Allocator.TempJob);
            var ic     = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < pts.Length; i++) points[i] = pts[i];

                new LineTessellationJob
                {
                    InputPoints    = points,
                    PointCount     = pts.Length,
                    Join           = join,
                    Cap            = cap,
                    MiterLimit     = miterLimit,
                    RoundSegments  = roundSegments,
                    OutVertices    = outV,
                    OutIndices     = outI,
                    OutVertexCount = vc,
                    OutIndexCount  = ic,
                }.Schedule().Complete();

                int nv = vc[0], ni = ic[0];
                var verts   = new LineVertex[nv];
                var indices = new int[ni];
                for (int i = 0; i < nv; i++) verts[i]   = outV[i];
                for (int i = 0; i < ni; i++) indices[i] = outI[i];
                return (verts, indices);
            }
            finally
            {
                points.Dispose(); outV.Dispose(); outI.Dispose(); vc.Dispose(); ic.Dispose();
            }
        }

        /// <summary>
        /// Assert the Burst job matches the managed reference for <paramref name="pts"/> under the
        /// given style. Counts + indices are always exact; vertex fields are exact when
        /// <paramref name="strict"/> (arithmetic-only), else within <c>1e-9</c> (transcendental).
        /// </summary>
        private static void AssertParity(
            double2[] pts, JoinType join, CapType cap, double miterLimit, int roundSegments,
            bool strict, string label)
        {
            var managed  = LineTessellator.Triangulate(pts, join, cap, miterLimit, roundSegments);
            var (jv, ji) = RunJob(pts, join, cap, miterLimit, roundSegments);

            Assert.AreEqual(managed.Vertices.Length, jv.Length, $"{label}: vertex count");
            Assert.AreEqual(managed.Indices.Length,  ji.Length, $"{label}: index count");

            for (int i = 0; i < ji.Length; i++)
                Assert.AreEqual(managed.Indices[i], ji[i], $"{label}: index[{i}]");

            for (int i = 0; i < jv.Length; i++)
            {
                LineVertex m = managed.Vertices[i], j = jv[i];
                // Side / WidthScale are set by pure assignment — always exact.
                Assert.AreEqual(m.Side,       j.Side,       $"{label}: v[{i}].Side");
                Assert.AreEqual(m.WidthScale, j.WidthScale, $"{label}: v[{i}].WidthScale");

                if (strict)
                {
                    Assert.AreEqual(m.Position.x,   j.Position.x,   $"{label}: v[{i}].Position.x");
                    Assert.AreEqual(m.Position.y,   j.Position.y,   $"{label}: v[{i}].Position.y");
                    Assert.AreEqual(m.Normal.x,     j.Normal.x,     $"{label}: v[{i}].Normal.x");
                    Assert.AreEqual(m.Normal.y,     j.Normal.y,     $"{label}: v[{i}].Normal.y");
                    Assert.AreEqual(m.DistanceAlong, j.DistanceAlong, $"{label}: v[{i}].DistanceAlong");
                }
                else
                {
                    Assert.AreEqual(m.Position.x,   j.Position.x,   1e-9, $"{label}: v[{i}].Position.x");
                    Assert.AreEqual(m.Position.y,   j.Position.y,   1e-9, $"{label}: v[{i}].Position.y");
                    Assert.AreEqual(m.Normal.x,     j.Normal.x,     1e-9, $"{label}: v[{i}].Normal.x");
                    Assert.AreEqual(m.Normal.y,     j.Normal.y,     1e-9, $"{label}: v[{i}].Normal.y");
                    Assert.AreEqual(m.DistanceAlong, j.DistanceAlong, 1e-9, $"{label}: v[{i}].DistanceAlong");
                }
            }
        }

        // ── Degenerate / smoke ───────────────────────────────────────────────────────────────────

        [Test]
        public void LessThanTwoPoints_ProducesZeroOutput()
        {
            var (jv, ji) = RunJob(new[] { new double2(5, 5) }, JoinType.Miter, CapType.Butt, 2.0, 4);
            Assert.AreEqual(0, jv.Length, "< 2 points → 0 verts.");
            Assert.AreEqual(0, ji.Length, "< 2 points → 0 indices.");
        }

        [Test]
        public void DuplicateConsecutivePoints_CollapsedIdentically()
        {
            // Managed collapses consecutive duplicates before tessellating; the job must too.
            var pts = new[]
            {
                new double2(0, 0), new double2(0, 0),
                new double2(10, 0), new double2(10, 0), new double2(20, 5),
            };
            AssertParity(pts, JoinType.Miter, CapType.Butt, 2.0, 4, strict: true, "dup-collapse");
        }

        // ── Arithmetic-only paths → STRICT bit-exact parity ────────────────────────────────────────

        [Test]
        public void TwoPoints_Butt_ExactParity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0) },
                            JoinType.Miter, CapType.Butt, 2.0, 4, strict: true, "2pt-butt");

        [Test]
        public void MiterJoin_ShallowCorner_ExactParity()
        {
            // Gentle bend → miter stays under the limit (no bevel fallback).
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(20, 3) };
            AssertParity(pts, JoinType.Miter, CapType.Butt, 4.0, 4, strict: true, "miter-shallow");
        }

        [Test]
        public void MiterJoin_SharpCorner_FallsBackToBevel_ExactParity()
        {
            // Near-hairpin → miter ratio exceeds the limit → the Miter path falls back to bevel.
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(1, 1) };
            AssertParity(pts, JoinType.Miter, CapType.Butt, 2.0, 4, strict: true, "miter->bevel");
        }

        [Test]
        public void BevelJoin_LeftTurn_ExactParity()
        {
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(20, 8) }; // CCW / left
            AssertParity(pts, JoinType.Bevel, CapType.Butt, 2.0, 4, strict: true, "bevel-left");
        }

        [Test]
        public void BevelJoin_RightTurn_ExactParity()
        {
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(20, -8) }; // CW / right
            AssertParity(pts, JoinType.Bevel, CapType.Butt, 2.0, 4, strict: true, "bevel-right");
        }

        [Test]
        public void SquareCap_ExactParity()
        {
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(20, 4) };
            AssertParity(pts, JoinType.Miter, CapType.Square, 4.0, 4, strict: true, "square-cap");
        }

        // ── Transcendental paths → topology exact, geometry tight-tolerance ─────────────────────────

        [Test]
        public void RoundJoin_BothTurns_ApproxParity()
        {
            AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, 8) },
                         JoinType.Round, CapType.Butt, 2.0, 4, strict: false, "round-join-left");
            AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, -8) },
                         JoinType.Round, CapType.Butt, 2.0, 4, strict: false, "round-join-right");
        }

        [Test]
        public void RoundCap_ApproxParity()
        {
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(20, 4) };
            AssertParity(pts, JoinType.Miter, CapType.Round, 4.0, 3, strict: false, "round-cap");
        }

        // ── Fixture-wide oracle over real line geometry (geolines layer) ────────────────────────────

        [Test]
        public void Fixture_Geolines_ExactParity_MiterButt()
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            var tile  = MvtDecoder.Decode(File.ReadAllBytes(FixturePath));
            var layer = tile.GetLayer("geolines");
            Assert.IsNotNull(layer, "geolines layer present in fixture");

            int pathsChecked = 0;
            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.LineString || feature.Geometry == null)
                    continue;

                List<List<double2>> paths = MvtGeometry.Decode(feature.Geometry);
                foreach (var path in paths)
                {
                    if (path.Count < 2) continue;
                    AssertParity(path.ToArray(), JoinType.Miter, CapType.Butt, 2.0, 4,
                                 strict: true, $"geolines#{feature.Id}");
                    pathsChecked++;
                }
            }

            Assert.Greater(pathsChecked, 0, "expected at least one geoline path to tessellate");
        }
    }
}
