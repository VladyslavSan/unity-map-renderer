// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
// Non-obvious why: this is its own file because `using MapRenderer.Core.Style.Line;` brings a second
// `StyleLayer` into scope, which collides with the bare one in StyleTests.cs (CS0104).

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
    /// <see cref="LineOffset"/>: perpendicular band-center shift, sign/symmetry,
    /// zoom coupling, width independence, join cleanliness, and shader structure guard. The CPU teeth run
    /// <see cref="LineOffset.Displace"/> over hand-derived <see cref="FlatRibbonStations"/>, not a Burst
    /// builder, so they stay in the core-tests loop; a greppable assertion guards the HLSL mirror.
    /// </summary>
    [TestFixture]
    public class LineOffsetTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>One ribbon vertex station: position, extrusion normal, side, and arc-length distance —
        /// the four fields <see cref="LineOffset.Displace"/> and this file's teeth need.</summary>
        private readonly struct Station
        {
            public readonly double2 Position;
            public readonly double2 Normal;
            public readonly float Side;
            public readonly double DistanceAlong;

            public Station(double2 position, double2 normal, float side, double distanceAlong)
            {
                Position = position;
                Normal = normal;
                Side = side;
                DistanceAlong = distanceAlong;
            }
        }

        /// <summary>The two fixtures this file needs, built directly from the ribbon definition (unit
        /// perpendiculars for a straight station; the miter-factor-scaled bisector at a corner) rather
        /// than through a tessellator/job.</summary>
        private static class FlatRibbonStations
        {
            /// <summary>Straight horizontal line (0,0)→(10,0): two stations, unit perpendicular
            /// normals ±(0,1).</summary>
            public static Station[] StraightLine() => new[]
            {
                new Station(new double2(0, 0),  new double2(0, 1),  +1f, 0.0),
                new Station(new double2(0, 0),  new double2(0, -1), -1f, 0.0),
                new Station(new double2(10, 0), new double2(0, 1),  +1f, 10.0),
                new Station(new double2(10, 0), new double2(0, -1), -1f, 10.0),
            };

            /// <summary>Miter join station of a single 90° left corner (0,0)→(0,10)→(10,10). The corner's
            /// normal is the bisector SCALED by the miter factor (1/cos45° = √2): (-1,1) left / (1,-1)
            /// right — not a unit vector, matching the ribbon builder's miter contract.</summary>
            public static (Station left, Station right) Join() => (
                new Station(new double2(0, 10), new double2(-1, 1), +1f, 10.0),
                new Station(new double2(0, 10), new double2(1, -1), -1f, 10.0));
        }

        /// <summary>
        /// Returns (original, displaced) band centers for every station (left/right vertex pair at the
        /// same DistanceAlong) of the straight-line fixture: the midpoints of the two vertices before and
        /// after Displace. StraightSegment_BandCenterShiftsPerpendicularByOffsetM checks that their
        /// difference is perpendicular with magnitude offsetM.
        /// </summary>
        private static List<(double2 original, double2 displaced)> BandCentersForStraightLine(double offsetM)
        {
            var byDistance = new Dictionary<double, (Station left, Station right)>();
            foreach (var s in FlatRibbonStations.StraightLine())
            {
                double d = Math.Round(s.DistanceAlong, 10); // bucket by distance
                if (!byDistance.TryGetValue(d, out var pair))
                    pair = default;
                if (s.Side > 0) pair.left  = s;
                else            pair.right = s;
                byDistance[d] = pair;
            }

            var results = new List<(double2, double2)>();
            foreach (var (_, pair) in byDistance)
            {
                double2 origCenter = (pair.left.Position + pair.right.Position) * 0.5;

                double2 leftDisp  = pair.left.Position  + LineOffset.Displace(pair.left.Normal,  pair.left.Side,  offsetM);
                double2 rightDisp = pair.right.Position + LineOffset.Displace(pair.right.Normal, pair.right.Side, offsetM);
                double2 displCenter = (leftDisp + rightDisp) * 0.5;

                results.Add((origCenter, displCenter));
            }

            return results;
        }

        // Convenience overload that returns only the displaced centers (for the sign/symmetry teeth).
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

        // ── DECISIVE: perpendicular shift ────────────────────────────────────────────────────

        [Test]
        public void StraightSegment_BandCenterShiftsPerpendicularByOffsetM()
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

        // ── sign + symmetry ──────────────────────────────────────────────────────────────────

        [Test]
        public void Sign_PlusNAndMinusNOffsetToOppositeSides()
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
        public void ZeroOffset_DisplacementIsExactlyZero()
        {
            // offset=0 must produce zero displacement (byte-equal to no-offset control).
            foreach (var v in FlatRibbonStations.StraightLine())
            {
                double2 disp = LineOffset.Displace(v.Normal, v.Side, 0.0);
                Assert.That(disp.x, Is.EqualTo(0.0),
                    "Zero offset must produce exact zero displacement (x).");
                Assert.That(disp.y, Is.EqualTo(0.0),
                    "Zero offset must produce exact zero displacement (y).");
            }
        }

        // ── zoom-coupled px→m conversion ─────────────────────────────────────────────────────

        [Test]
        public void OffsetMeters_ScalesLinearlyWithMetersPerPixel_WhenPixelMode()
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
        public void WidthAndOffset_ShareSamePxToMRatio_AtTwoZooms()
        {
            // Width and offset must produce the same ratio at two zoom levels.
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
        public void OffsetMeters_InMeterMode_IsIdentity()
        {
            // When widthIsPixels=false, offset is already in meters — metersPerPixel is ignored.
            double offsetM_val = 7.5;
            double mpp = 999.0; // large mpp should have no effect in meter mode

            double result1 = LineOffset.OffsetMeters(offsetM_val, mpp, widthIsPixels: false);
            Assert.That(result1, Is.EqualTo(offsetM_val).Within(1e-15),
                "In meter mode, OffsetMeters must return the offset unchanged.");
        }

        // ── width-independent ────────────────────────────────────────────────────────────────

        [Test]
        public void WidthIndependent_BandCenterShiftDoesNotChangeWithWidth()
        {
            // Band center shift = offsetM, regardless of widthM: width scales the extrusion magnitude,
            // not the offset displacement.
            double offsetM = 4.0;

            // Find a station (e.g., DistanceAlong == 0).
            // Both vertices at dist=0 contribute to band center.
            Station left  = default, right = default;
            bool foundLeft = false, foundRight = false;
            foreach (var v in FlatRibbonStations.StraightLine())
            {
                if (Math.Abs(v.DistanceAlong) < 1e-9)
                {
                    if (v.Side > 0) { left  = v; foundLeft  = true; }
                    else            { right = v; foundRight = true; }
                }
            }
            Assert.IsTrue(foundLeft && foundRight, "Expected a station at DistanceAlong=0.");

            // The shader applies width as `unitDir * miter * outerM`; the offset term
            // `normal * side * offsetM` has no outerM, so here the shift stays |offsetM| at any width.

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

        // ── joins survive ────────────────────────────────────────────────────────────────────

        [Test]
        public void MiterJoin_OffsetCenterlinesMeetAtJoin()
        {
            // A 90° miter corner at moderate offset gives one finite band center at the join.
            // Limitation: the raw miter factor grows without bound on a sharp corner;
            // line-miter-limit caps the shift (LineOffset.cs).
            double offsetM = 2.0;

            // The miter join station of a 90° left corner: the miter normal is at 45° (bisecting the
            // corner), scaled by the miter factor — see FlatRibbonStations.Join.
            var (joinLeft, joinRight) = FlatRibbonStations.Join();

            // Apply displacement.
            double2 leftDisp  = LineOffset.Displace(joinLeft.Normal,  joinLeft.Side,  offsetM);
            double2 rightDisp = LineOffset.Displace(joinRight.Normal, joinRight.Side, offsetM);
            double2 center    = (leftDisp + rightDisp) * 0.5;

            // The band center should be a finite point (no NaN/Inf — join did not blow up).
            Assert.IsFalse(double.IsNaN(center.x) || double.IsNaN(center.y),
                "Miter join band center must not be NaN at moderate offset.");
            Assert.IsFalse(double.IsInfinity(center.x) || double.IsInfinity(center.y),
                "Miter join band center must not be Infinite at moderate offset.");

            // A 90° miter extends the shift by 1/cos(45°) ≈ √2, so the expected magnitude is ≈ 2.828;
            // the assertion only bounds it.
            double centerMag = Len(center);
            Assert.That(centerMag, Is.LessThan(10.0 * offsetM),
                $"Miter join center shift should be bounded at moderate offset. Got: {centerMag}. " +
                "Note: large-offset sharp corners can blow up (MapLibre parity limitation — see LineOffset.cs).");
        }

        // ── greppable: shader uses sideAndDist.x in offset term ──────────────────────────────

        [Test]
        public void Shader_OffsetTermUsesSideAndDistX_NotWidenSymmetrically()
        {
            // Non-obvious why: offset folded into outerM widens both sides symmetrically; the CPU teeth
            // stay green while the GPU is wrong. The offset term lives once, in Line_VertexExtrude.hlsl.
            string hlsl = File.ReadAllText(EngineFreeShaderPaths.ResolveMapShaderPath("Line_VertexExtrude.hlsl"));

            // The offset term must multiply by sideAndDist.x (the per-vertex side), so it shifts the band
            // CENTER rather than widening the half-width.
            Assert.That(hlsl, Does.Contain("sideAndDist.x * (miter * _LineOffset * pxToWorld)"),
                "Line_VertexExtrude.hlsl offset term must multiply by sideAndDist.x " +
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
