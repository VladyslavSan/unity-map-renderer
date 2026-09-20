using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
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
    /// Direct teeth against <see cref="RibbonJob"/> (3D, Burst) over a flat centerline (points on the XZ
    /// plane, <c>up = +Y</c>, via <see cref="FlatRibbon"/>). Through UMR-173 this file held the
    /// differential-parity oracle against a managed 2D reference tessellator; the reference is retired and
    /// the first-principles properties it asserted moved to <c>RibbonJobGeometryTests</c>. What remains
    /// here are the teeth that were always direct assertions on the job's own output, not comparisons —
    /// they name themselves as such in their own doc comments below.
    /// </summary>
    [TestFixture]
    public class LineRibbonJobTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // ── Oracle harness ──────────────────────────────────────────────────────────────────────

        private static (LineRibbonVertex[] verts, int[] indices) RunJob(
            double2[] pts, JoinType join, CapType cap, double miterLimit, int roundSegments,
            double roundLimit = 1.05)
            => FlatRibbon.Build(pts, join, cap, miterLimit, roundSegments, roundLimit);

        // ── Degenerate / smoke ───────────────────────────────────────────────────────────────────

        [Test]
        public void LessThanTwoPoints_ProducesZeroOutput()
        {
            var (jv, ji) = RunJob(new[] { new double2(5, 5) }, JoinType.Miter, CapType.Butt, 2.0, 4);
            Assert.AreEqual(0, jv.Length, "< 2 points → 0 verts.");
            Assert.AreEqual(0, ji.Length, "< 2 points → 0 indices.");
        }

        // ── line-round-limit: shallow round joins collapse to miter ────────────────────────────────

        /// <summary>Mixed-regime fixture: a shallow (20°, collapses to miter) join followed by a sharp (90°,
        /// fan preserved) join in the SAME polyline.</summary>
        private static readonly double2[] MixedRoundLimitFixture =
        {
            new double2(0, 0),
            new double2(10, 0),
            new double2(19.396926207859085, 3.4202014332566878),  // 20° turn — shallow, collapses to miter
            new double2(15.976724774602397, 12.817127641115771),  // further 90° turn — sharp, fan preserved
        };

        /// <summary>
        /// Direct assertion on the raw Job output. Exact counts worked out by hand from
        /// <see cref="MixedRoundLimitFixture"/>'s emission shape — see the inline breakdown below.
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
        /// Direct analytic assertion on the Burst side: the inner-join vertex at a 90° left turn (bevel)
        /// sits at the analytic intersection of the two concave offset lines — hand-computed here, not
        /// read from a managed arm.
        /// </summary>
        [Test]
        public void InnerJoin_Bevel_90LeftTurn_Across_MatchesAnalyticIntersection()
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

        // ── Fixture-wide robustness over real line geometry (geolines layer) ────────────────────────

        /// <summary>
        /// Runs <see cref="RibbonJob"/> over every real line path in the fixture's geolines layer and
        /// asserts basic geometric health: finite positions, an index count that is a multiple of 3, and
        /// a flat-mapping's out-of-plane component (<c>Across.y</c>) staying zero. No longer a
        /// differential comparison — the managed reference it once ran against is retired.
        /// </summary>
        [Test]
        public void Fixture_Geolines_RibbonJob_ProducesHealthyGeometry()
        {
            Assert.IsTrue(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");
            // IR C1 P3: the command streams come from the bytes (MvtFixtureStreams), not off a decoded
            // feature — this must not share its input path with production's decoder.
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

                    var (jv, ji) = RunJob(path.ToArray(), JoinType.Miter, CapType.Butt, 2.0, 4);
                    string label = $"geolines#{fi}";

                    Assert.AreEqual(0, ji.Length % 3, $"{label}: index count must be a multiple of 3.");
                    foreach (var v in jv)
                    {
                        Assert.IsFalse(double.IsNaN(v.Position.x) || double.IsNaN(v.Position.y) || double.IsNaN(v.Position.z),
                            $"{label}: Position must be finite.");
                        Assert.IsFalse(double.IsNaN(v.Across.x) || double.IsNaN(v.Across.y) || double.IsNaN(v.Across.z),
                            $"{label}: Across must be finite.");
                        Assert.AreEqual(0.0, v.Across.y, 1e-9, $"{label}: flat mapping must keep Across.y == 0.");
                        Assert.AreEqual(0.0, v.Position.y, 1e-9, $"{label}: flat mapping must keep Position.y == 0.");
                    }
                    pathsChecked++;
                }
            }

            Assert.Greater(pathsChecked, 0, "expected at least one geoline path to build");
        }
    }
}
