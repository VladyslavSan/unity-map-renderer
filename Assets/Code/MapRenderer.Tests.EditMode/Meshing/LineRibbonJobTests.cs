using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Planar differential oracle for <see cref="RibbonJob"/> (3D, Burst) vs the managed 2D reference
    /// <see cref="LineTessellator.Triangulate"/>. Fed a FLAT centerline (points on the XZ plane, <c>up = +Y</c>),
    /// the 3D array builder must reproduce the managed ribbon: mapping flat 2D <c>(x, y) → 3D (x, 0, y)</c>,
    /// <c>Position → (x, 0, z)</c>, <c>Across → (nx, 0, nz)</c>, with <c>Position.y == 0</c> and <c>Across.y == 0</c>.
    ///
    /// <para>This is the safety net that needs no builder, no projection, and no GPU (S100). Parity is
    /// TIGHT-TOLERANCE, not bit-exact: the single no-branch 3D formulation reorders the same float ops (an extra
    /// normalize; the round arc swept in a local basis rather than global <c>atan2</c>), so vertices agree to
    /// ~1e-9 — pixel-identical — but not to the last bit. Chasing bit-exactness would require reintroducing the
    /// planar-special frame this stage deletes. Triangle topology (integer indices) must match EXACTLY.</para>
    /// </summary>
    [TestFixture]
    public class LineRibbonJobTests
    {
        private const double Eps = 1e-9;

        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // ── Oracle harness ──────────────────────────────────────────────────────────────────────

        private static (LineRibbonVertex[] verts, int[] indices) RunJob(
            double2[] pts, JoinType join, CapType cap, double miterLimit, int roundSegments,
            double roundLimit = 1.05)
        {
            int capV = RibbonJob.MaxVertexCount(pts.Length, roundSegments);
            int capI = RibbonJob.MaxIndexCount(pts.Length, roundSegments);

            var points = new NativeArray<double3>(pts.Length == 0 ? 1 : pts.Length, Allocator.TempJob);
            var ups    = new NativeArray<double3>(pts.Length == 0 ? 1 : pts.Length, Allocator.TempJob);
            var outV   = new NativeArray<LineRibbonVertex>(capV == 0 ? 1 : capV, Allocator.TempJob);
            var outI   = new NativeArray<int>(capI == 0 ? 1 : capI, Allocator.TempJob);
            var vc     = new NativeArray<int>(1, Allocator.TempJob);
            var ic     = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < pts.Length; i++)
                {
                    points[i] = new double3(pts[i].x, 0.0, pts[i].y); // flat centerline: 2D (x,y) → 3D (x,0,y)
                    ups[i]    = new double3(0.0, 1.0, 0.0);            // Mercator up = +Y
                }

                new RibbonJob
                {
                    Points         = points,
                    Ups            = ups,
                    PointCount     = pts.Length,
                    Join           = join,
                    Cap            = cap,
                    MiterLimit     = miterLimit,
                    RoundSegments  = roundSegments,
                    RoundLimit     = roundLimit,
                    OutVertices    = outV,
                    OutIndices     = outI,
                    OutVertexCount = vc,
                    OutIndexCount  = ic,
                }.Schedule().Complete();

                int nv = vc[0], ni = ic[0];
                var verts   = new LineRibbonVertex[nv];
                var indices = new int[ni];
                for (int i = 0; i < nv; i++) verts[i]   = outV[i];
                for (int i = 0; i < ni; i++) indices[i] = outI[i];
                return (verts, indices);
            }
            finally
            {
                points.Dispose(); ups.Dispose(); outV.Dispose(); outI.Dispose(); vc.Dispose(); ic.Dispose();
            }
        }

        /// <summary>Assert the 3D ribbon job matches the managed 2D reference for <paramref name="pts"/> under the
        /// given style. Counts + indices exact; vertex geometry within <see cref="Eps"/>; the extruded plane's
        /// out-of-plane component (Position.y / Across.y) must be zero.</summary>
        private static void AssertParity(
            double2[] pts, JoinType join, CapType cap, double miterLimit, int roundSegments, string label,
            double roundLimit = 1.05)
        {
            var managed  = LineTessellator.Triangulate(pts, join, cap, miterLimit, roundSegments, roundLimit);
            var (jv, ji) = RunJob(pts, join, cap, miterLimit, roundSegments, roundLimit);

            Assert.AreEqual(managed.Vertices.Length, jv.Length, $"{label}: vertex count");
            Assert.AreEqual(managed.Indices.Length,  ji.Length, $"{label}: index count");

            for (int i = 0; i < ji.Length; i++)
                Assert.AreEqual(managed.Indices[i], ji[i], $"{label}: index[{i}]");

            for (int i = 0; i < jv.Length; i++)
            {
                LineVertex m = managed.Vertices[i]; LineRibbonVertex j = jv[i];
                Assert.AreEqual(m.Side,       j.Side,       $"{label}: v[{i}].Side");        // pure assignment
                Assert.AreEqual(m.WidthScale, j.WidthScale, $"{label}: v[{i}].WidthScale");

                Assert.AreEqual(m.Position.x,   j.Position.x,   Eps, $"{label}: v[{i}].Position.x");
                Assert.AreEqual(m.Position.y,   j.Position.z,   Eps, $"{label}: v[{i}].Position.z (2D-y)");
                Assert.AreEqual(0.0,            j.Position.y,   Eps, $"{label}: v[{i}].Position.y must be 0");
                Assert.AreEqual(m.Normal.x,     j.Across.x,     Eps, $"{label}: v[{i}].Across.x");
                Assert.AreEqual(m.Normal.y,     j.Across.z,     Eps, $"{label}: v[{i}].Across.z (2D-y)");
                Assert.AreEqual(0.0,            j.Across.y,     Eps, $"{label}: v[{i}].Across.y must be 0");
                Assert.AreEqual(m.DistanceAlong, j.DistanceAlong, Eps, $"{label}: v[{i}].DistanceAlong");
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
            var pts = new[]
            {
                new double2(0, 0), new double2(0, 0),
                new double2(10, 0), new double2(10, 0), new double2(20, 5),
            };
            AssertParity(pts, JoinType.Miter, CapType.Butt, 2.0, 4, "dup-collapse");
        }

        // ── Straight / miter / bevel / caps ────────────────────────────────────────────────────────

        [Test]
        public void TwoPoints_Butt_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0) },
                            JoinType.Miter, CapType.Butt, 2.0, 4, "2pt-butt");

        [Test]
        public void MiterJoin_ShallowCorner_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, 3) },
                            JoinType.Miter, CapType.Butt, 4.0, 4, "miter-shallow");

        [Test]
        public void MiterJoin_SharpCorner_FallsBackToBevel_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(1, 1) },
                            JoinType.Miter, CapType.Butt, 2.0, 4, "miter->bevel");

        [Test]
        public void BevelJoin_LeftTurn_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, 8) },
                            JoinType.Bevel, CapType.Butt, 2.0, 4, "bevel-left");

        [Test]
        public void BevelJoin_RightTurn_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, -8) },
                            JoinType.Bevel, CapType.Butt, 2.0, 4, "bevel-right");

        [Test]
        public void SquareCap_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, 4) },
                            JoinType.Miter, CapType.Square, 4.0, 4, "square-cap");

        // ── Round join / cap (the local-basis arc sweep) ─────────────────────────────────────────────

        [Test]
        public void RoundJoin_BothTurns_Parity()
        {
            AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, 8) },
                         JoinType.Round, CapType.Butt, 2.0, 4, "round-join-left");
            AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, -8) },
                         JoinType.Round, CapType.Butt, 2.0, 4, "round-join-right");
        }

        [Test]
        public void RoundCap_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(20, 4) },
                            JoinType.Miter, CapType.Round, 4.0, 3, "round-cap");

        [Test]
        public void RoundCap_And_RoundJoin_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(18, 6), new double2(28, 6) },
                            JoinType.Round, CapType.Round, 2.0, 4, "round-both");

        // ── Inner-join miter clamp (bevel/round inner vertex now carries the miter factor) ──────────
        //
        // BevelJoin_LeftTurn_Parity / BevelJoin_RightTurn_Parity above use a 38.7° turn (factor 1.060) —
        // the UNclamped branch only, and weakly. MiterJoin_SharpCorner_FallsBackToBevel_Parity already
        // exercises the clamped branch via the miter→bevel fallback, but stays green whether both arms
        // agree at 1.0 (un-fixed) or 2.0 (fixed) — parity alone cannot tell the two apart. These two
        // teeth force the clamped branch explicitly, with the fixture that also proves it clamps managed-side
        // (LineTessellatorTests.InnerJoin_Bevel_150LeftTurn_ClampedBranch / ..._Round_150LeftTurn_...).

        [Test]
        public void InnerJoin_ClampedAngle_Bevel_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(-15.980762113533157, 15.0) },
                            JoinType.Bevel, CapType.Butt, 2.0, 4, "inner-clamp-bevel");

        [Test]
        public void InnerJoin_ClampedAngle_Round_Parity()
            => AssertParity(new[] { new double2(0, 0), new double2(10, 0), new double2(-15.980762113533157, 15.0) },
                            JoinType.Round, CapType.Butt, 2.0, 4, "inner-clamp-round");

        // Short-segment fold regime. Every OTHER parity fixture in this file uses ≥10-unit segments against
        // HalfWidth 2 — an order of magnitude clear of the regime where the inner-join quad provably inverts
        // (docs/line-rendering-design.md §3 item 5). LineTessellatorTests pins the boundary exactly, but on
        // the MANAGED oracle; without these cases the Burst producer — the one that actually ships — is
        // unexercised at the documented boundary, and a Jobs-only divergence there would ship green.
        // Same 90° fixture, S = 1.8 < S_crit = 2.0.
        [TestCase(JoinType.Miter, "fold-miter")]
        [TestCase(JoinType.Bevel, "fold-bevel")]
        [TestCase(JoinType.Round, "fold-round")]
        public void ShortSegment_FoldRegime_Parity(JoinType join, string label)
            => AssertParity(new[] { new double2(0, 0), new double2(1.8, 0), new double2(1.8, 10) },
                            join, CapType.Butt, 2.0, 4, label);

        // ── line-round-limit: shallow round joins collapse to miter (Burst twin) ────────────────────

        /// <summary>Mixed-regime fixture: a shallow (20°, collapses to miter) join followed by a sharp (90°,
        /// fan preserved) join in the SAME polyline — the combination the managed-only teeth in
        /// <c>LineTessellatorTests</c> don't cover, since they exercise one join per fixture.</summary>
        private static readonly double2[] MixedRoundLimitFixture =
        {
            new double2(0, 0),
            new double2(10, 0),
            new double2(19.396926207859085, 3.4202014332566878),  // 20° turn — shallow, collapses to miter
            new double2(15.976724774602397, 12.817127641115771),  // further 90° turn — sharp, fan preserved
        };

        [Test]
        public void RoundLimit_MixedShallowAndSharp_Parity()
            => AssertParity(MixedRoundLimitFixture, JoinType.Round, CapType.Butt, 2.0, 4, "round-limit-mixed",
                             roundLimit: 1.05);

        /// <summary>
        /// Direct assertion on the raw Job output (deliberately redundant with the parity tooth above and
        /// with <c>LineTessellatorTests.ShallowRoundJoin_CollapsesToMiter_MatchesMiterPathExactly</c>): the
        /// tooth that survives if <see cref="AssertParity"/> is ever loosened. Exact counts worked out by hand
        /// from <see cref="MixedRoundLimitFixture"/>'s emission shape — see the inline breakdown below.
        /// </summary>
        [Test]
        public void RoundLimit_ShallowCorner_JobEmitsMiterVertexCountDirectly()
        {
            var (jv, ji) = RunJob(MixedRoundLimitFixture, JoinType.Round, CapType.Butt, 2.0, 4, roundLimit: 1.05);

            // Verts: start cap(2) + shallow-join-as-miter(2, NOT the 7-vert fan) + sharp round join
            // (7: 1 inner + 1 arcStart + 4 fan intermediates + 1 arcEnd) + last seg(2) = 13.
            // Indices: start-cap→join1 connecting quad(6) + join1→join2 connecting quad(6, part of the round
            // join's own emission) + join2's 4 fan triangles(12) + join2's 1 closing fan triangle(3) +
            // last-seg quad(6) = 33. (Matches RightAngle_RoundJoin_ExactVertexCount's 11v/27i for a single
            // round join, plus this fixture's extra shallow-as-miter join: +2v/+6i.)
            Assert.AreEqual(13, jv.Length,
                $"Mixed shallow+sharp fixture: shallow join must contribute only 2 verts (miter), not a " +
                $"7-vert fan. Expected 13 total verts, got {jv.Length}.");
            Assert.AreEqual(33, ji.Length,
                $"Mixed shallow+sharp fixture: expected 33 total indices (no fan triangles at the shallow " +
                $"join). Got {ji.Length}.");
        }

        /// <summary>
        /// F1 fix — Burst twin of <c>LineTessellatorTests.RoundLimit_ExceedsMiterLimit_CascadesToBevel_
        /// NotUnboundedMiter</c>: roundLimit(3.0) and miterLimit(2.0) independently style-settable, corner
        /// f=2.5 sits between them, must cascade round→miter→bevel (not fall through to an unbounded
        /// ComputeMiterNormals spike). Parity-only is sufficient here — both producers share the SAME
        /// roundCollapsedToMiter dispatch shape, and index-count parity alone discriminates bevel (3 join
        /// verts) from an unbounded miter (2).
        /// </summary>
        [Test]
        public void RoundLimit_ExceedsMiterLimit_CascadesToBevel_Parity()
            => AssertParity(
                new[] { new double2(0, 0), new double2(10, 0), new double2(3.2, 7.332121111929344) },
                join: JoinType.Round, cap: CapType.Butt, miterLimit: 2.0, roundSegments: 4,
                label: "round-limit-exceeds-miter", roundLimit: 3.0);

        /// <summary>
        /// One direct analytic assertion on the Burst side (deliberately redundant with T1 ∧ parity):
        /// the tooth that survives if <see cref="AssertParity"/> is ever loosened. Same fixture as
        /// <c>LineTessellatorTests.InnerJoin_Bevel_90LeftTurn_Unclamped_MatchesConcaveOffsetLineIntersection</c>,
        /// mapped flat 2D (x,y) → 3D (x,0,y).
        /// </summary>
        [Test]
        public void InnerJoin_Bevel_90LeftTurn_Across_MatchesManagedAnalytic()
        {
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(10, 10) };
            var (jv, _) = RunJob(pts, JoinType.Bevel, CapType.Butt, 2.0, 4);

            double3 corner = new double3(10, 0, 0);
            int found = -1, count = 0;
            for (int i = 0; i < jv.Length; i++)
            {
                double3 pos = jv[i].Position;
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9 &&
                    math.abs(pos.z - corner.z) < 1e-9 && jv[i].Side == +1f) // left turn ⇒ concave Side == +1
                {
                    found = i;
                    count++;
                }
            }
            Assert.AreEqual(1, count, $"Expected exactly one inner-join vertex at the corner with Side=+1. Found {count}.");

            double3 across = jv[found].Across;
            Assert.AreEqual(-1.0, across.x, 1e-9, $"Across.x should be -1.0. Got {across.x:G17}.");
            Assert.AreEqual(0.0, across.y, 1e-9, $"Across.y should be 0.0. Got {across.y:G17}.");
            Assert.AreEqual(1.0, across.z, 1e-9, $"Across.z should be 1.0. Got {across.z:G17}.");

            double len = math.length(across);
            Assert.AreEqual(1.4142135623730951, len, 1e-9, $"|Across| should be √2 = 1.4142135623730951. Got {len:G17}.");
        }

        // ── Fixture-wide oracle over real line geometry (geolines layer) ────────────────────────────

        [Test]
        public void Fixture_Geolines_Parity_MiterButt()
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            // IR C1 P3: the command streams come from the bytes (MvtFixtureStreams), not off a decoded
            // feature — the ribbon oracle must not share its input path with production's decoder.
            var layer = MvtFixtureStreams.ReadLayer(File.ReadAllBytes(FixturePath), "geolines");
            Assert.IsNotNull(layer, "geolines layer present in fixture");

            int pathsChecked = 0;
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.LineString || layer.Commands[fi] == null)
                    continue;

                List<List<double2>> paths = MvtGeometry.Decode(layer.Commands[fi]);
                foreach (var path in paths)
                {
                    if (path.Count < 2) continue;
                    AssertParity(path.ToArray(), JoinType.Miter, CapType.Butt, 2.0, 4, $"geolines#{fi}");
                    pathsChecked++;
                }
            }

            Assert.Greater(pathsChecked, 0, "expected at least one geoline path to build");
        }
    }
}
