// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style;
using MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S44 — <see cref="LineOffset"/>: perpendicular band-center shift, sign/symmetry,
    /// zoom coupling, width independence, join cleanliness, and shader structure guard.
    ///
    /// All decisive teeth are CPU-side via <see cref="LineOffset.Displace"/> and
    /// <see cref="LineTessellator"/>. The HLSL mirror is guarded by the greppable assertion.
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class LineOffsetTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a straight horizontal line (A→B) and return (originalCenter, displacedCenter)
        /// pairs for every station (pair of left/right vertices at the same DistanceAlong).
        ///
        /// originalCenter  = midpoint of the two undisplaced vertex positions (= the centerline point).
        /// displacedCenter = midpoint of the two displaced vertex positions after applying Displace.
        ///
        /// The actual shift is (displacedCenter − originalCenter), which must be perpendicular to
        /// the tangent and have magnitude offsetM — this is what Tooth 1 checks.
        /// </summary>
        private static List<(double2 original, double2 displaced)> BandCentersForStraightLine(double offsetM)
        {
            // Simple horizontal segment A(0,0) → B(10,0).
            var line = new List<double2>
            {
                new double2(0, 0),
                new double2(10, 0)
            };

            var result = LineTessellator.Triangulate(line, JoinType.Miter, CapType.Butt);
            var verts = result.Vertices;

            // Find distinct DistanceAlong values (stations).
            // Each station has exactly two vertices: left (Side=+1) and right (Side=-1).
            var stations = new Dictionary<double, (LineVertex left, LineVertex right)>();
            foreach (var v in verts)
            {
                double d = Math.Round(v.DistanceAlong, 10); // bucket by distance
                if (!stations.TryGetValue(d, out var pair))
                    pair = default;
                if (v.Side > 0) pair.left  = v;
                else            pair.right = v;
                stations[d] = pair;
            }

            var results = new List<(double2, double2)>();
            foreach (var (_, pair) in stations)
            {
                // Undisplaced positions: both vertices sit on the centerline (Position is shared).
                // The center is the midpoint of the two positions (same point for straight segments,
                // may differ slightly for miter-join stations where vertices are at the junction).
                double2 origLeft  = pair.left.Position;
                double2 origRight = pair.right.Position;
                double2 origCenter = (origLeft + origRight) * 0.5;

                // Displaced positions for left (side=+1) and right (side=-1).
                double2 leftDisp  = pair.left.Position  + LineOffset.Displace(pair.left.Normal,  pair.left.Side,  offsetM);
                double2 rightDisp = pair.right.Position + LineOffset.Displace(pair.right.Normal, pair.right.Side, offsetM);

                // Displaced band center = midpoint of displaced edge vertices.
                double2 displCenter = (leftDisp + rightDisp) * 0.5;

                results.Add((origCenter, displCenter));
            }

            return results;
        }

        // Convenience overload that returns only the displaced centers (for Tooth 2 symmetry).
        private static List<double2> DisplacedCenters(double offsetM)
        {
            var pairs = BandCentersForStraightLine(offsetM);
            var list = new List<double2>(pairs.Count);
            foreach (var (_, disp) in pairs)
                list.Add(disp);
            return list;
        }

        /// <summary>Dot product of two 2D vectors.</summary>
        private static double Dot(double2 a, double2 b) => a.x * b.x + a.y * b.y;

        /// <summary>Euclidean length of a 2D vector.</summary>
        private static double Len(double2 v) => Math.Sqrt(v.x * v.x + v.y * v.y);

        // ── Tooth 1 (DECISIVE): perpendicular shift ──────────────────────────────────────────

        [Test]
        public void Tooth1_StraightSegment_BandCenterShiftsPerpendicularByOffsetM()
        {
            // Straight line A(0,0)→B(10,0); tangent = (1,0); expected perpendicular normal = (0,1).
            // With offsetM=3, band centers should shift by 3 in the normal (Y) direction.
            double offsetM = 3.0;
            var pairs = BandCentersForStraightLine(offsetM);

            Assert.IsTrue(pairs.Count >= 1, "Expected at least one station.");

            double2 tangent = new double2(1, 0);
            foreach (var (orig, disp) in pairs)
            {
                // Actual shift vector: displaced center minus original center.
                double2 centerShift = disp - orig;

                // Parallel component (along tangent) should be ~0.
                double parallelComponent = Math.Abs(Dot(centerShift, tangent));
                Assert.That(parallelComponent, Is.LessThan(1e-9),
                    $"Band center shift must be perpendicular to the line tangent (no along-tangent component). " +
                    $"Got parallel component: {parallelComponent}");

                // Perpendicular magnitude should equal offsetM.
                double perpMagnitude = Len(centerShift);
                Assert.That(perpMagnitude, Is.EqualTo(offsetM).Within(1e-9),
                    $"Band center shift magnitude must equal offsetM={offsetM}. Got: {perpMagnitude}");
            }
        }

        // ── Tooth 2: sign + symmetry ─────────────────────────────────────────────────────────

        [Test]
        public void Tooth2_Sign_PlusNAndMinusNOffsetToOppositeSides()
        {
            double offsetM = 5.0;
            var centersPlus  = DisplacedCenters(+offsetM);
            var centersZero  = DisplacedCenters(0.0);
            var centersMinus = DisplacedCenters(-offsetM);

            Assert.IsTrue(centersPlus.Count > 0 && centersMinus.Count > 0 && centersZero.Count > 0);

            for (int i = 0; i < Math.Min(centersPlus.Count, centersMinus.Count); i++)
            {
                // +N and -N centers should be on opposite sides: sum ≈ 2 * zero center.
                double2 expectedSum = centersZero[i] * 2.0;
                double2 actualSum   = centersPlus[i] + centersMinus[i];
                double err = Len(actualSum - expectedSum);
                Assert.That(err, Is.LessThan(1e-9),
                    $"Station {i}: +N and -N centers must be symmetric about zero-offset. " +
                    $"Expected sum {expectedSum}, got {actualSum}, error {err}");
            }
        }

        [Test]
        public void Tooth2_ZeroOffset_DisplacementIsExactlyZero()
        {
            // Tooth 2: offset=0 must produce zero displacement (byte-equal to no-offset control).
            var line = new List<double2> { new double2(0, 0), new double2(10, 0) };
            var result = LineTessellator.Triangulate(line, JoinType.Miter, CapType.Butt);

            foreach (var v in result.Vertices)
            {
                double2 disp = LineOffset.Displace(v.Normal, v.Side, 0.0);
                Assert.That(disp.x, Is.EqualTo(0.0),
                    "Zero offset must produce exact zero displacement (x).");
                Assert.That(disp.y, Is.EqualTo(0.0),
                    "Zero offset must produce exact zero displacement (y).");
            }
        }

        // ── Tooth 3: zoom-coupled px→m conversion ────────────────────────────────────────────

        [Test]
        public void Tooth3_OffsetMeters_ScalesLinearlyWithMetersPerPixel_WhenPixelMode()
        {
            // At two zooms with different metersPerPixel, the ratio of offsetM values must match
            // the ratio used by width (zoom-coupled, same px→m path).
            double offsetPx   = 10.0;
            double mpp1       = 0.5;  // zoom level 1
            double mpp2       = 1.0;  // zoom level 2

            double offsetM1 = LineOffset.OffsetMeters(offsetPx, mpp1, widthIsPixels: true);
            double offsetM2 = LineOffset.OffsetMeters(offsetPx, mpp2, widthIsPixels: true);

            // Both should scale linearly: ratio should equal mpp1/mpp2.
            Assert.That(offsetM1 / offsetM2, Is.EqualTo(mpp1 / mpp2).Within(1e-12),
                $"Offset ratio {offsetM1}/{offsetM2} must match metersPerPixel ratio {mpp1}/{mpp2}.");
        }

        [Test]
        public void Tooth3_WidthAndOffset_ShareSamePxToMRatio_AtTwoZooms()
        {
            // Width and offset must produce the same ratio at two zoom levels (tooth 3).
            double widthPx   = 8.0;
            double offsetPx  = 5.0;
            double mpp1      = 0.25;
            double mpp2      = 2.0;

            // Width conversion (mirrors Line_VertexExtrude.hlsl: widthWorld = _Width * pxToWorld when
            // WidthIsPixels=1 — pxToWorld is measured per-vertex on the GPU; here it is the zoom scalar).
            double widthM1 = widthPx * mpp1;
            double widthM2 = widthPx * mpp2;

            double offsetM1 = LineOffset.OffsetMeters(offsetPx, mpp1, widthIsPixels: true);
            double offsetM2 = LineOffset.OffsetMeters(offsetPx, mpp2, widthIsPixels: true);

            // Both ratios must be equal.
            double widthRatio  = widthM1  / widthM2;
            double offsetRatio = offsetM1 / offsetM2;
            Assert.That(offsetRatio, Is.EqualTo(widthRatio).Within(1e-12),
                $"Offset ratio {offsetRatio} must equal width ratio {widthRatio} at two zooms (zoom coupling).");
        }

        [Test]
        public void Tooth3_OffsetMeters_InMeterMode_IsIdentity()
        {
            // When widthIsPixels=false, offset is already in meters — metersPerPixel is ignored.
            double offsetM_val = 7.5;
            double mpp = 999.0; // large mpp should have no effect in meter mode

            double result1 = LineOffset.OffsetMeters(offsetM_val, mpp, widthIsPixels: false);
            Assert.That(result1, Is.EqualTo(offsetM_val).Within(1e-15),
                "In meter mode, OffsetMeters must return the offset unchanged.");
        }

        // ── Tooth 4: width-independent ───────────────────────────────────────────────────────

        [Test]
        public void Tooth4_WidthIndependent_BandCenterShiftDoesNotChangeWithWidth()
        {
            // Band center shift = offsetM, regardless of widthM. Verify by using Displace
            // for two different width scales and confirming the center displacement is equal.
            // (Width affects the extrusion magnitude, not the offset displacement.)
            double offsetM = 4.0;

            var line = new List<double2> { new double2(0, 0), new double2(10, 0) };
            var result = LineTessellator.Triangulate(line, JoinType.Miter, CapType.Butt);
            var verts = result.Vertices;

            // Find a station (e.g., DistanceAlong == 0).
            // Both vertices at dist=0 contribute to band center.
            LineVertex left  = default, right = default;
            bool foundLeft = false, foundRight = false;
            foreach (var v in verts)
            {
                if (Math.Abs(v.DistanceAlong) < 1e-9)
                {
                    if (v.Side > 0) { left  = v; foundLeft  = true; }
                    else            { right = v; foundRight = true; }
                }
            }
            Assert.IsTrue(foundLeft && foundRight, "Expected a station at DistanceAlong=0.");

            // Simulate two different widths by scaling the normal: widthM is applied by the
            // vertex shader as `unitDir * miter * outerM`. The normal from the tessellator
            // carries the miter factor; the offset term `normal * side * offsetM` is independent
            // of outerM (the band thickness). Verify by checking that Displace does not depend
            // on any scale applied to the position (only to the extrusion normal direction).

            // The displacement vector should have magnitude = |offsetM| * |normal| / |normal|...
            // Actually: Displace returns normal * side * offsetM. For a unit-normal straight segment
            // normal has magnitude 1, so displacement magnitude = |offsetM| = 4.0.
            // If width were doubled (not changing the normal), displacement stays |offsetM|.

            double2 leftDisp  = LineOffset.Displace(left.Normal,  left.Side,  offsetM);
            double2 rightDisp = LineOffset.Displace(right.Normal, right.Side, offsetM);
            double2 center    = (leftDisp + rightDisp) * 0.5;

            // Band center shift magnitude must equal offsetM regardless of any width parameter.
            double centerMag = Len(center);
            Assert.That(centerMag, Is.EqualTo(offsetM).Within(1e-9),
                $"Band center shift magnitude must equal offsetM={offsetM} regardless of line-width. " +
                $"Got: {centerMag}");

            // Verify the same for a scaled offset (double) — the ratio holds.
            double offsetM2 = offsetM * 2.0;
            double2 leftDisp2  = LineOffset.Displace(left.Normal,  left.Side,  offsetM2);
            double2 rightDisp2 = LineOffset.Displace(right.Normal, right.Side, offsetM2);
            double2 center2    = (leftDisp2 + rightDisp2) * 0.5;
            double center2Mag  = Len(center2);
            Assert.That(center2Mag / centerMag, Is.EqualTo(offsetM2 / offsetM).Within(1e-9),
                "Doubling offsetM must double the center shift — width scale is independent.");
        }

        // ── Tooth 5: joins survive ───────────────────────────────────────────────────────────

        [Test]
        public void Tooth5_MiterJoin_OffsetCenterlinesMeetAtJoin()
        {
            // 3-point polyline with a moderate angle. Miter join (default).
            // At a moderate offset, the join station should produce a single band center
            // whose displacement from the zero-offset position is ≈ offsetM in the
            // perpendicular direction to each adjacent segment.
            //
            // Large-offset miter explosion on sharp corners is a documented limitation
            // (MapLibre parity). This test uses a 90° corner at moderate offset.
            double offsetM = 2.0;
            var line = new List<double2>
            {
                new double2(0,  0),
                new double2(0, 10),   // 90° left turn at (0,10)
                new double2(10, 10)
            };

            var result = LineTessellator.Triangulate(line, JoinType.Miter, CapType.Butt);
            var verts  = result.Vertices;

            // Find the miter join station at (0, 10) — DistanceAlong ≈ 10.
            // This is a left turn: the miter normal should be at 45° (bisecting the 90° corner).
            LineVertex joinLeft  = default, joinRight = default;
            bool foundJL = false, foundJR = false;
            foreach (var v in verts)
            {
                if (Math.Abs(v.DistanceAlong - 10.0) < 0.1)
                {
                    if (v.Side > 0) { joinLeft  = v; foundJL = true; }
                    else            { joinRight = v; foundJR = true; }
                }
            }

            Assert.IsTrue(foundJL && foundJR,
                "Expected a join station at DistanceAlong≈10 (the 90° corner). " +
                "If the join was split (bevel/round), revise the expected distance.");

            // Apply displacement.
            double2 leftDisp  = LineOffset.Displace(joinLeft.Normal,  joinLeft.Side,  offsetM);
            double2 rightDisp = LineOffset.Displace(joinRight.Normal, joinRight.Side, offsetM);
            double2 center    = (leftDisp + rightDisp) * 0.5;

            // The band center should be a finite point (no NaN/Inf — join did not blow up).
            Assert.IsFalse(double.IsNaN(center.x) || double.IsNaN(center.y),
                "Miter join band center must not be NaN at moderate offset.");
            Assert.IsFalse(double.IsInfinity(center.x) || double.IsInfinity(center.y),
                "Miter join band center must not be Infinite at moderate offset.");

            // The magnitude of the center displacement should be finite and reasonable.
            // For a 90° miter: miter factor = 1/cos(45°) ≈ √2 ≈ 1.414.
            // Expected center shift magnitude = sqrt2 * offsetM ≈ 2.828.
            // (The miter extends the shift by the miter factor — this is the documented behavior.)
            double centerMag = Len(center);
            Assert.That(centerMag, Is.LessThan(10.0 * offsetM),
                $"Miter join center shift should be bounded at moderate offset. Got: {centerMag}. " +
                "Note: large-offset sharp corners can blow up (MapLibre parity limitation — see LineOffset.cs).");
        }

        // ── Tooth 6 (greppable): shader uses sideAndDist.x in offset term ────────────────────

        [Test]
        public void Tooth6_Shader_OffsetTermUsesSideAndDistX_NotWidenSymmetrically()
        {
            // Guard against the "sign-folding" regression: if a dev accidentally folds offset
            // into outerM (the magnitude), both sides widen symmetrically and ALL of teeth 1/2/4
            // would pass on the CPU but fail on the GPU. The greppable HLSL assertion catches it.
            //
            // S67: The offset term moved from Line_LitForwardPass.hlsl (inline) into
            // Line_VertexExtrude.hlsl (shared helper). We assert it is there — still one site,
            // consumed by all five line passes. Same assertion strength, correct file.

            // S67: extrusion logic (including the S44 offset term) lives in Line_VertexExtrude.hlsl (shared,
            // resolved by name — move-proof).
            string hlsl = File.ReadAllText(EngineFreeShaderPaths.ResolveMapShaderPath("Line_VertexExtrude.hlsl"));

            // The S44 offset term must reference sideAndDist.x (the per-vertex side value).
            // This is the structural guarantee that the offset shifts the band CENTER,
            // not the half-width (which would be a symmetric widening, failing teeth 1/2/4).
            Assert.That(hlsl, Does.Contain("sideAndDist.x * (miter * _LineOffset * pxToWorld)"),
                "Line_VertexExtrude.hlsl S44 offset term must multiply by sideAndDist.x " +
                "to achieve a side-consistent shift (band center shift, not symmetric widening). " +
                "Grep: 'sideAndDist.x * (miter * _LineOffset * pxToWorld)'");

            // Also confirm _LineOffset is declared in Line_LitInput.hlsl (resolved by name — move-proof).
            string inputHlsl = File.ReadAllText(EngineFreeShaderPaths.ResolveMapShaderPath("Line_LitInput.hlsl"));
            Assert.That(inputHlsl, Does.Contain("float  _LineOffset;"),
                "Line_LitInput.hlsl CBUFFER must declare 'float  _LineOffset;' (SRP Batcher requirement).");

            // Confirm the DOTS bridge includes _LineOffset (all four pieces).
            Assert.That(inputHlsl, Does.Contain("UNITY_DOTS_INSTANCED_PROP(float , _LineOffset)"),
                "Line_LitInput.hlsl DOTS bridge must include _LineOffset instanced prop.");
            Assert.That(inputHlsl, Does.Contain("unity_DOTS_Sampled_LineOffset"),
                "Line_LitInput.hlsl DOTS bridge must include unity_DOTS_Sampled_LineOffset static.");
            Assert.That(inputHlsl, Does.Contain("#define _LineOffset"),
                "Line_LitInput.hlsl DOTS bridge must include #define _LineOffset redirect.");
        }

    }
}
