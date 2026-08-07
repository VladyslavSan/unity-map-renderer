// Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture), off-screen GPU render +
// CPU readback.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// Stage T — the shared tilt-measurement fixture's OWN acceptance teeth, and its two in-stage consumers:
//   T1 — the ruler itself (no GPU): TiltedGroundScene / GroundRuler foreshorten by cos(tilt) and agree with
//        the closed form at the look-at.
//   T2 — a rendered line band's apparent width matches the ruler under tilt (docs/line-rendering-design.md
//        §1's world-width model). SAME pose as S111 (TiltedGroundSceneConfig's defaults ARE S111's values,
//        by design) — what is new here is the FIXTURE (an east–west road, world-metre width, measured by a
//        column coverage integral), not the camera pose.
//   T3 — the general-direction ray-cut primitive (PixelCoverage.CoverageProfileAlongRay /
//        HalfCrossingDistancePx) resolves the three join types' silhouette reach under tilt — the
//        measurement `docs/line-rendering-design.md` §3 item 4 (grazing incidence) will need, though THIS
//        tooth measures at the look-at, not grazing.
//   T4 — a REAL production-path label (LabelPlacementSystem.Tick) does not foreshorten under tilt today
//        (viewport-pitch-aligned), and — the point of building this at all — the fixture can tell that
//        apart from what a MAP-aligned label would read. Wired before P3 (`pitch-alignment: map`) exists.
//
// ONE FILE, BOTH CONSUMERS ON PURPOSE: splitting it would hide the very thing this stage exists to
// demonstrate — TiltedGroundScene is CONTENT-AGNOSTIC and serves a line/join consumer and a label consumer
// from the SAME harness. T4's glyph/LabelPlacementSystem arm lives here, under Visual/, rather than under
// Text/Placement/, for exactly that reason: it is a TILT-FIXTURE tooth first, a label tooth second.
//
// COMMON TO ALL FOUR: the scene renders at 55°, the value two green fixtures (LineProbeSymmetrySnapshotTests,
// LineDashSnapshotTests) already use. NO tooth here asserts an absolute pixel count — every assertion is
// ratio-to-oracle (measured / GroundRuler projection ≈ 1) or ratio-across-tilts (q(55°) / q(0°) against a
// derivation that is not the render) — docs/line-rendering-design.md §4 records why: four stages were
// reverted for asserting "the band is exactly N device pixels".
//
// NO METRE LITERALS: every world size here is `k · scene.MetresPerDevicePixel` — a bare metre literal is
// sub-pixel at this pose (zoom 8 / lat 30 puts one device px at ~300 m) and would render as nothing.
//
// THE MANDATED INJECTION (§7 of the design): forcing TiltDegrees = 0 inside TiltedGroundScene.Create must
// turn every "reads 1.000 if the mechanism is absent" clause below RED. T1(a) and T1(c) are CONTROLS and are
// expected to stay green under that injection.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class TiltFixtureSelfTests
    {
        private const double TiltDeg = 55.0;

        /// <summary>Builds a throwaway, unrendered scene (any tilt — <c>MetresPerDevicePixel</c> is
        /// tilt-invariant, which is exactly T1(a)'s own claim) just to read the frame's ruler, so a fixture
        /// can size itself in device px before building any world geometry.</summary>
        private static double ProbeMetresPerDevicePixel()
        {
            using var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { LitAmbient = false });
            return scene.MetresPerDevicePixel;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T1 — the ruler itself. No GPU: WorldToScreenPoint uses the camera's projection matrix, no draw.
        //
        // FIVE independent [Test] methods, not one. Review finding (REQUIRED, caught before this stage
        // shipped): NUnit's Assert.That throws on first failure, so a single method running clauses in
        // sequence lets an early clause's exception SHADOW every later clause — under the §7 tilt=0
        // injection the convergence loop (which used to run first) threw immediately and clauses (b), (c)
        // and the closed-form cross-check never executed at all, so their "goes red under injection" claim
        // was inferred, not demonstrated. Splitting makes every clause independently RED-verifiable forever
        // — a future injection (or a future clause added at the top) cannot shadow anything, because NUnit
        // runs each [Test] method regardless of whether a sibling method failed.
        //
        // T1AreaSpan(mpp) is the one non-control span length shared by (b)/(c)/the closed-form check —
        // 30·mpp, chosen from the convergence measurement below (~0.08% predicted deviation from the closed
        // form, a >12× margin under this file's 1% bound). See Ruler_ForeshorteningDeviationFromClosedForm_
        // ConvergesAsSpanShrinks's XML doc for the full derivation of why 120·mpp (this design's original
        // choice) was too coarse.
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private const double T1SpanMultiplier = 30.0;

        /// <summary>
        /// <b>T1 — CONTROL.</b> Proves: <see cref="MapCamera.MetresPerDevicePixel"/> does not depend on
        /// tilt (the orbit radius it is built from does not). Does NOT prove anything about foreshortening
        /// itself — that is <see cref="Ruler_ForeshortensByCosTilt_AlongTheTiltAxis"/>'s job.
        ///
        /// <para>Expected to STAY GREEN under the §7 tilt=0 injection: both scenes would then read the SAME
        /// mpp, trivially.</para>
        /// </summary>
        [Test]
        public void Ruler_MetresPerDevicePixel_IsTiltInvariant()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double mpp0  = scene0.MetresPerDevicePixel;
            double mpp55 = scene55.MetresPerDevicePixel;

            Assert.That(math.abs(mpp55 / mpp0 - 1.0), Is.LessThan(1e-9),
                $"T1 (a): MetresPerDevicePixel must be tilt-invariant (the orbit radius is) — " +
                $"mpp(0°)={mpp0:R}, mpp(55°)={mpp55:R}.");
        }

        /// <summary>
        /// <b>T1 — CONVERGENCE PROOF</b> that the closed form <see cref="GroundRuler.ClosedFormAcrossAzimuthSpanPx"/>
        /// is a SMALL-SPAN limit, and that <see cref="T1SpanMultiplier"/>·mpp (the span every other T1 tooth
        /// below uses) sits safely inside it — a permanent, asserted property of the harness, not a one-off
        /// diagnostic.
        ///
        /// <para><b>Why this exists at all — L was shrunk from this design's original 120·mpp (§9 fork 4),
        /// not the tolerance widened.</b> The closed form <c>L/mpp·cosθ</c> is the INFINITESIMAL-span limit
        /// of the exact perspective divide; across a FINITE span the near and far ends sit at different
        /// depths, and the divide contributes a second-order-in-L correction. Solving the pinhole geometry
        /// exactly for a segment of length L centred at the look-at gives
        /// <c>span(θ,L)/span(0,L) = cosθ / (1 − (L/2d)²sin²θ)</c> (d = orbit radius =
        /// <c>|CameraRelativePosition|</c>) — i.e. the closed form is exact only as L→0, and the gap from
        /// cosθ grows as L². At L=120·mpp that gap measures 1.24% — OUTSIDE this file's 1% bound, wider than
        /// this design originally assumed — so L moved to <see cref="T1SpanMultiplier"/>=30·mpp (~0.08%
        /// measured), not the tolerance.</para>
        ///
        /// <para>Proves: the deviation from cosθ shrinks (roughly quarters per halving of L) as L shrinks —
        /// the falsifiable difference between diagnosing a real second-order term and tuning until green.
        /// Does NOT by itself prove foreshortening exists (a flat 0% at every L would also "converge" in the
        /// sense of not growing) — <see cref="Ruler_ForeshortensByCosTilt_AlongTheTiltAxis"/> is the
        /// DISCRIMINATOR for that, and it is a SEPARATE method precisely so this one's failure cannot hide
        /// it.</para>
        /// </summary>
        [Test]
        public void Ruler_ForeshorteningDeviationFromClosedForm_ConvergesAsSpanShrinks()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double mpp0  = scene0.MetresPerDevicePixel;
            double cos55 = Angle.FromDegrees(TiltDeg).Cos;

            int[] convergenceKs = { 120, 60, 30, 15 };
            double[] convergenceDeviationPercent = new double[convergenceKs.Length];
            for (int i = 0; i < convergenceKs.Length; i++)
            {
                double Lk = convergenceKs[i] * mpp0;
                double spanZ55k = GroundRuler.GroundSegmentSpanPx(
                    scene55.UnityCamera, double3.zero, new double2(0.0, 1.0), Lk);
                double spanZ0k = GroundRuler.GroundSegmentSpanPx(
                    scene0.UnityCamera, double3.zero, new double2(0.0, 1.0), Lk);
                double ratioK = spanZ55k / spanZ0k;
                convergenceDeviationPercent[i] = 100.0 * math.abs(ratioK / cos55 - 1.0);
                TestContext.WriteLine(
                    $"T1 convergence: L={convergenceKs[i]}·mpp ({Lk:F1} m): span55={spanZ55k:F4}px, " +
                    $"span0={spanZ0k:F4}px, ratio={ratioK:F6}, deviation from cos55°=" +
                    $"{convergenceDeviationPercent[i]:F4}%");
            }
            for (int i = 1; i < convergenceKs.Length; i++)
            {
                Assert.That(convergenceDeviationPercent[i], Is.LessThan(convergenceDeviationPercent[i - 1]),
                    $"T1 CONVERGENCE: deviation from cos55° must shrink as L shrinks — at L=" +
                    $"{convergenceKs[i]}·mpp it read {convergenceDeviationPercent[i]:F4}%, at L=" +
                    $"{convergenceKs[i - 1]}·mpp it read {convergenceDeviationPercent[i - 1]:F4}%. A flat or " +
                    "growing deviation as L shrinks would mean the second-order-term diagnosis above is " +
                    "WRONG, not that a smaller L is safe to use.");
            }
        }

        /// <summary>
        /// <b>T1 — DISCRIMINATOR.</b> Proves: the projective ruler
        /// (<see cref="GroundRuler.GroundSegmentSpanPx"/>) foreshortens by cos(tilt) along the tilt axis
        /// (world ẑ, heading 0) at <see cref="T1SpanMultiplier"/>·mpp. Does NOT prove anything about
        /// rendering, any shader, any material — nothing here touches a render.
        ///
        /// <para>READS 1.000 (not cos 55° = 0.5736) if the harness is silently at tilt 0 — a factor of
        /// 1.744.</para>
        /// </summary>
        [Test]
        public void Ruler_ForeshortensByCosTilt_AlongTheTiltAxis()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double L = T1SpanMultiplier * scene0.MetresPerDevicePixel;
            double cos55 = Angle.FromDegrees(TiltDeg).Cos;

            double spanZ55 = GroundRuler.GroundSegmentSpanPx(
                scene55.UnityCamera, double3.zero, new double2(0.0, 1.0), L);
            double spanZ0 = GroundRuler.GroundSegmentSpanPx(
                scene0.UnityCamera, double3.zero, new double2(0.0, 1.0), L);
            double ratioZ = spanZ55 / spanZ0;
            Assert.That(ratioZ, Is.EqualTo(cos55).Within(1).Percent,
                $"T1 (b): the tilt-axis span must foreshorten by cos 55° = {cos55:F6}; measured " +
                $"{ratioZ:F6} (span55={spanZ55:F3}px, span0={spanZ0:F3}px). If this exceeds 1%, SHRINK " +
                $"{nameof(T1SpanMultiplier)} and report both numbers — do not widen the tolerance.");
        }

        /// <summary>
        /// <b>T1 — CONTROL.</b> Proves: the perpendicular (world x̂) span does NOT foreshorten — so
        /// <see cref="Ruler_ForeshortensByCosTilt_AlongTheTiltAxis"/> caught a genuine per-axis tilt effect,
        /// not a global scale change. Does NOT prove anything about the tilt axis itself.
        ///
        /// <para>Expected to STAY GREEN under the §7 tilt=0 injection: with no foreshortening on EITHER axis,
        /// this ratio trivially stays 1.</para>
        /// </summary>
        [Test]
        public void Ruler_DoesNotForeshorten_AlongThePerpendicularAxis()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double L = T1SpanMultiplier * scene0.MetresPerDevicePixel;

            double spanX55 = GroundRuler.GroundSegmentSpanPx(
                scene55.UnityCamera, double3.zero, new double2(1.0, 0.0), L);
            double spanX0 = GroundRuler.GroundSegmentSpanPx(
                scene0.UnityCamera, double3.zero, new double2(1.0, 0.0), L);
            double ratioX = spanX55 / spanX0;
            Assert.That(ratioX, Is.EqualTo(1.0).Within(1).Percent,
                $"T1 (c): the perpendicular (x̂) span must NOT foreshorten; measured {ratioX:F6}.");
        }

        /// <summary>
        /// <b>T1 — closed-form cross-check.</b> Proves the projective ruler agrees with §1.2's closed form
        /// at the look-at, at <see cref="T1SpanMultiplier"/>·mpp. Does NOT prove anything off the look-at —
        /// the closed form is valid only there, which is why every OTHER T tooth uses the projective ruler
        /// instead of this closed form.
        ///
        /// <para>The ONE T1 tooth without the camera-positioning blind spot the rest of T shares: this is
        /// bare trigonometry on <c>mpp</c> and <c>Angle</c> — it never calls
        /// <c>CameraPoseMath.ComputeRelativePose</c>, so it would PASS a bug in that pose math (a wrong
        /// camera position that still yields a self-consistent projective ruler). Every other T tooth reads
        /// the live camera and would catch such a bug; this one exists to catch drift in the closed form
        /// itself, not in the camera.</para>
        /// </summary>
        [Test]
        public void Ruler_AgreesWithTheClosedForm_AtTheLookAt()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double L = T1SpanMultiplier * scene0.MetresPerDevicePixel;
            double mpp55 = scene55.MetresPerDevicePixel;

            double spanZ55 = GroundRuler.GroundSegmentSpanPx(
                scene55.UnityCamera, double3.zero, new double2(0.0, 1.0), L);
            double closedForm55 = GroundRuler.ClosedFormAcrossAzimuthSpanPx(L, mpp55, Angle.FromDegrees(TiltDeg));
            Assert.That(spanZ55, Is.EqualTo(closedForm55).Within(1).Percent,
                $"T1: the projective ruler ({spanZ55:F3}px) must agree with the closed form " +
                $"({closedForm55:F3}px) at the look-at.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T2 — a rendered line band's apparent width matches the ruler under tilt.
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        // §3.2: derived from mpp, never a bare metre literal. 260·mpp / 6·mpp is the same order as S111's
        // east–west road (80,000 m / 2,000 m) at this file's default pose, but stays correct at any zoom.
        private const double T2RoadHalfLengthMultiplier = 260.0;
        private const double T2RoadStationMultiplier    =   6.0;
        private const int    T2SweepHalfWidth           =  10; // ±10 columns — see LineProbeSymmetrySnapshotTests'
                                                                 // TiltedSweepHalfWidth for why not wider under tilt.

        private readonly struct RoadBandMeasurement
        {
            public readonly double MeasuredWidthPx;
            public readonly double RulerWidthPx;
            public RoadBandMeasurement(double measuredWidthPx, double rulerWidthPx)
            {
                MeasuredWidthPx = measuredWidthPx;
                RulerWidthPx    = rulerWidthPx;
            }
        }

        /// <summary>Rendered band width in device px on one screen COLUMN: the coverage integral down the
        /// whole column. The ray/column analogue of <c>LineProbeSymmetrySnapshotTests.MeasureRowWidthPx</c> —
        /// that one cuts a NORTH–SOUTH (receding) road with a horizontal row; this cuts an EAST–WEST road
        /// with a vertical column.</summary>
        private static double MeasureColumnWidthPx(byte[] pixels, int column, float3 background, int size)
        {
            float3 plateau = background;
            float  best    = 0f;
            for (int row = 0; row < size; row++)
            {
                float3 sample = PixelCoverage.SampleLinear(pixels, size, size, column, row);
                float  dist   = math.distancesq(sample, background);
                if (dist > best) { best = dist; plateau = sample; }
            }
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"T2 column {column}: nothing distinguishable from the background — the road did not " +
                "render here, so no width is measurable.");

            double sum = 0.0;
            for (int row = 0; row < size; row++)
                sum += PixelCoverage.CoverageAt(pixels, size, size, column, row, background, plateau);
            return sum;
        }

        /// <summary>Renders an east–west road (world ±X at z = 0) with a WORLD-metre width — its across-axis
        /// is world ±Z, the foreshortened direction at heading 0 — and measures the apparent band width as
        /// the coverage-integral-per-column, averaged over the centre ±<see cref="T2SweepHalfWidth"/>
        /// columns. Returns that measurement alongside the projective ruler's prediction for the SAME world
        /// width, on the SAME camera.</summary>
        private static RoadBandMeasurement MeasureRoadBand(double tiltDeg, double worldWidthM)
        {
            using var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { TiltDegrees = tiltDeg });
            using var snap  = new SnapshotRenderer(scene.Config.SizePx, scene.Config.SizePx);

            double mpp = scene.MetresPerDevicePixel;
            double roadHalfLengthM = T2RoadHalfLengthMultiplier * mpp;
            double roadStationM    = T2RoadStationMultiplier * mpp;

            var pts = new List<double2>();
            for (double x = -roadHalfLengthM; x <= roadHalfLengthM + 1e-6; x += roadStationM)
                pts.Add(new double2(x, 0.0));
            Mesh mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);

            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present — T2 measures ITS coverage.");
            var mat = new Material(shader) { name = "T2RoadFixtureMat" };
            mat.SetFloat(ShaderProperties.Line.PropertyId.Width, (float)worldWidthM);
            mat.SetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels, 0f);
            mat.SetColor(ShaderProperties.PropertyId.BaseColor, new Color(0.95f, 0.60f, 0.15f, 1f));
            mat.SetFloat(ShaderProperties.PropertyId.Opacity, 1f);

            // Preconditions, asserted not assumed (LineProbeSymmetrySnapshotTests.cs:138–159 is the template).
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.DashCount), Is.EqualTo(0f),
                "T2 precondition: _DashCount must be 0 — a dash boundary would be summed as a ribbon edge.");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels), Is.EqualTo(0f),
                "T2 precondition: _WidthIsPixels must be 0 — the styled-PIXEL width model is deliberately " +
                "OUT of this tooth; it uses world metres so §3.2's no-literal rule is meaningful.");
            Assert.That(mat.IsKeywordEnabled("_EDGE_ANTIALIASING_OFF"), Is.False,
                "T2 precondition: the AA straddle must be live.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_SOLID_CORE"), Is.False,
                "T2 precondition: _HAIRLINE_SOLID_CORE must be off.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_HARD"), Is.False,
                "T2 precondition: _HAIRLINE_HARD must be off.");

            var go = new GameObject("T2RoadFixture");
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            try
            {
                scene.Render(snap);

                // The road's centreline must project onto a single screen ROW (heading 0), or a silhouette
                // screen-y does not invert to a single world z — LineProbeSymmetrySnapshotTests.cs:243–247.
                Vector3 originSp = scene.UnityCamera.WorldToScreenPoint(Vector3.zero);
                Vector3 refSp    = scene.UnityCamera.WorldToScreenPoint(new Vector3(10_000f, 0f, 0f));
                Assert.That(originSp.y, Is.EqualTo(refSp.y).Within(0.05),
                    "T2 precondition: the road's centreline must project onto a single screen ROW.");

                float3 background = PixelCoverage.BackgroundLinear(
                    snap.RawPixels, scene.Config.SizePx, scene.Config.SizePx);

                int centreColumn = scene.Config.SizePx / 2;
                double sum = 0.0;
                int    count = 0;
                for (int c = centreColumn - T2SweepHalfWidth; c <= centreColumn + T2SweepHalfWidth; c++)
                {
                    sum += MeasureColumnWidthPx(snap.RawPixels, c, background, scene.Config.SizePx);
                    count++;
                }
                double measuredWidthPx = sum / count;

                // SYMMETRIC about the centreline — GroundSegmentSpanPx, not a one-sided probe (that IS the
                // S111 defect).
                double rulerWidthPx = GroundRuler.GroundSegmentSpanPx(
                    scene.UnityCamera, double3.zero, new double2(0.0, 1.0), worldWidthM);

                return new RoadBandMeasurement(measuredWidthPx, rulerWidthPx);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        /// <summary>
        /// <b>T2.</b> Proves: a rendered band's apparent extent equals the projection of its intended WORLD
        /// extent under tilt, and the coverage-integral measurement is calibrated against the ruler.
        ///
        /// <para>Does NOT prove the styled-PIXEL width model — deliberately excluded by using world metres.
        /// Nothing at depths other than the look-at row. Nothing about a spherical projection: this scene is
        /// Web-Mercator and the ground is the plane y = 0.</para>
        /// </summary>
        [Test]
        public void RenderedBandWidth_MatchesTheRuler_UnderTilt()
        {
            double mpp     = ProbeMetresPerDevicePixel();
            double worldWidthM = 120.0 * mpp;

            RoadBandMeasurement m55 = MeasureRoadBand(TiltDeg, worldWidthM);
            RoadBandMeasurement m0  = MeasureRoadBand(0.0, worldWidthM);

            // (a) calibration. ≠ 1 if the coverage integral is not measuring the band's apparent width.
            double ratioCalibration = m55.MeasuredWidthPx / m55.RulerWidthPx;
            Assert.That(ratioCalibration, Is.EqualTo(1.0).Within(3).Percent,
                $"T2 (a) calibration: measured {m55.MeasuredWidthPx:F3}px vs ruler {m55.RulerWidthPx:F3}px " +
                $"at 55° (ratio {ratioCalibration:F4}).");

            // (b) DISCRIMINATION. Reads 1.000 at tilt 0 — (a) alone cannot see this, because the ruler
            // foreshortens WITH the render, so their ratio stays 1 regardless of tilt. This clause is what
            // makes T2 a tilt tooth.
            //
            // KNOWN SYSTEMATIC BIAS in the comparand, not in the render: cos55° is T1's INFINITESIMAL-span
            // limit (see T1's convergence tooth), but this band is 120·mpp wide — the exact L at which T1
            // measured a 1.2439% departure from cos55°. So the expected value here is ~1.24% low by
            // construction, consuming roughly 41% of this clause's 3% budget before any real measurement
            // error. A reading up to ~2% off is this bias, not a render defect — read the message's own
            // numbers, don't assume the tolerance is slack. (Not corrected here: the fix belongs to the
            // comparand — cos55°/(1-(W/2d)²sin²55°) — a decision for every T-family tooth at once, filed as
            // a follow-up rather than changed per-tooth in this stage.)
            double cos55 = Angle.FromDegrees(TiltDeg).Cos;
            double ratioTilt = m55.MeasuredWidthPx / m0.MeasuredWidthPx;
            Assert.That(ratioTilt, Is.EqualTo(cos55).Within(3).Percent,
                $"T2 (b) DISCRIMINATION: measured(55°)/measured(0°) must equal cos 55° = {cos55:F6}; " +
                $"got {ratioTilt:F6} (measured55={m55.MeasuredWidthPx:F3}px, " +
                $"measured0={m0.MeasuredWidthPx:F3}px). NOTE: cos55° is the L→0 limit and this band is " +
                "120·mpp wide, so the comparand itself is ~1.24% low by construction (see T1's convergence " +
                "tooth) — a reading in that neighbourhood is this known bias, not necessarily a defect.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T3 — the general-direction ray-cut primitive resolves the three join types' silhouette reach.
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private readonly struct JoinReachMeasurement
        {
            public readonly double ReachPx;
            public readonly double RulerPx;
            public JoinReachMeasurement(double reachPx, double rulerPx)
            {
                ReachPx = reachPx;
                RulerPx = rulerPx;
            }
        }

        /// <summary>Builds the apex-V fixture SHAPE <c>LineAaSnapshotTests.BuildApexFixture</c> established
        /// (that file's own test is NOT touched — only its shape is reused here as a T subject): apex at the
        /// world origin (the look-at), arms opening toward world −Z so the OUTWARD (convex) bisector points
        /// world +Z — the foreshortened direction under this scene's heading-0 tilt. Renders one join type on
        /// one tilt, marches <see cref="PixelCoverage.CoverageProfileAlongRay"/> from the apex along the
        /// bisector, and returns both the measured reach and the SAME-CAMERA ruler for
        /// <paramref name="analyticReachMetres"/> (a one-sided <see cref="GroundRuler.ScreenSpanPx"/> — the
        /// reach is measured one-sided FROM a fixed point, not the symmetric-about-centre quantity
        /// <see cref="GroundRuler.GroundSegmentSpanPx"/> models).</summary>
        private static JoinReachMeasurement MeasureJoinReach(
            JoinType join, double tiltDeg, double armM, double widthM, double analyticReachMetres)
        {
            using var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { TiltDegrees = tiltDeg });
            using var snap  = new SnapshotRenderer(scene.Config.SizePx, scene.Config.SizePx);

            var pts = new List<double2>
            {
                new double2( armM, -armM),
                new double2( 0.0,   0.0),
                new double2(-armM, -armM),
            };
            Mesh mesh = SyntheticLineMesh.BuildFromPoints(pts, join, CapType.Butt);

            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present — T3 measures ITS silhouette.");
            var mat = new Material(shader) { name = "T3ApexFixtureMat" };
            mat.SetFloat(ShaderProperties.Line.PropertyId.Width, (float)widthM);
            mat.SetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels, 0f);
            mat.SetColor(ShaderProperties.PropertyId.BaseColor, new Color(0.95f, 0.60f, 0.15f, 1f));
            mat.SetFloat(ShaderProperties.PropertyId.Opacity, 1f);

            var go = new GameObject("T3ApexFixture");
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            try
            {
                scene.Render(snap);

                int size = scene.Config.SizePx;
                float3 background = PixelCoverage.BackgroundLinear(snap.RawPixels, size, size);

                double2 originPx = GroundRuler.ProjectPx(scene.UnityCamera, double3.zero);
                // Baseline is armM, not a 1 m step: at this pose (d ~135km) a 1 m step differences two
                // ~1.35e5-magnitude floats whose ulp is ~0.03 m — a several-percent budget on the step
                // itself, which can tilt the resolved bisector direction by 1-2° and drift the marched reach
                // by a few px. armM (~100·mpp, tens of km) keeps the float precision budget negligible.
                double2 dirPx = GroundRuler.ScreenDirection(
                    scene.UnityCamera, double3.zero, new double3(0.0, 0.0, armM));

                const int    steps  = 200; // generous: max analytic reach here is ~85px (1.41421·60·mpp / mpp).
                const double stepPx = 1.0;

                float3 plateau = PixelCoverage.PlateauAlongRay(
                    snap.RawPixels, size, size, originPx, dirPx, steps, stepPx, background);
                Assert.That(math.length(plateau - background), Is.GreaterThan(0.05f),
                    $"T3 {join} @ {tiltDeg}°: no covered sample found along the bisector ray — the fixture " +
                    "did not render where this tooth looks.");

                float[] profile = PixelCoverage.CoverageProfileAlongRay(
                    snap.RawPixels, size, size, originPx, dirPx, steps, stepPx, background, plateau);
                Assert.That(profile[0], Is.GreaterThan(0.9f),
                    $"T3 {join} @ {tiltDeg}°: the apex sample itself must be covered (got {profile[0]:F3}) " +
                    "— the fixture is not where this tooth thinks it is.");

                double reachPx = PixelCoverage.HalfCrossingDistancePx(profile, stepPx);
                double rulerPx = GroundRuler.ScreenSpanPx(
                    scene.UnityCamera, double3.zero, new double3(0.0, 0.0, analyticReachMetres));

                return new JoinReachMeasurement(reachPx, rulerPx);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        /// <summary>
        /// <b>T3.</b> Proves: the general-direction ray cut is valid under tilt; the three join types stay
        /// separable at 55°; the harness serves a line-GEOMETRY consumer, not only a band-width one.
        ///
        /// <para>Does NOT prove anything at GRAZING incidence — this is the look-at at 55°, and grazing is
        /// the open question <c>docs/line-rendering-design.md</c> §3 item 4 owns. Nothing about caps. Nothing
        /// about the short-segment fold régime (§3 item 5) — these arms are two orders above it.</para>
        /// </summary>
        [Test]
        public void JoinSilhouetteReach_MatchesTheRuler_UnderTilt()
        {
            double mpp    = ProbeMetresPerDevicePixel();
            double h      = 60.0 * mpp;
            double armM   = 100.0 * mpp;
            double widthM = 2.0 * h;

            var joins = new[] { JoinType.Miter, JoinType.Round, JoinType.Bevel };
            var analyticMultiplier = new Dictionary<JoinType, double>
            {
                [JoinType.Miter] = 1.41421,
                [JoinType.Round] = 1.00000,
                [JoinType.Bevel] = 0.70711,
            };

            var at55 = new Dictionary<JoinType, JoinReachMeasurement>();
            var at0  = new Dictionary<JoinType, JoinReachMeasurement>();
            foreach (JoinType join in joins)
            {
                double r = analyticMultiplier[join] * h;
                at55[join] = MeasureJoinReach(join, TiltDeg, armM, widthM, r);
                at0[join]  = MeasureJoinReach(join, 0.0,     armM, widthM, r);
            }

            // (a) calibration, per join. ≠ 1 if the ray cut is not measuring the silhouette. 6% (not T2's
            // 3%): the round join is a 4-segment chord approximation to the arc and the crossing estimator
            // is coarser than the coverage integral.
            foreach (JoinType join in joins)
            {
                double ratio = at55[join].ReachPx / at55[join].RulerPx;
                Assert.That(ratio, Is.EqualTo(1.0).Within(6).Percent,
                    $"T3 (a) {join}: reach(55°)={at55[join].ReachPx:F2}px, ruler={at55[join].RulerPx:F2}px, " +
                    $"ratio={ratio:F4}.");
            }

            // (b) DISCRIMINATION. At tilt 0 this ratio is 1.000 for every join.
            foreach (JoinType join in joins)
            {
                double bound = 0.75 * at0[join].ReachPx;
                Assert.That(at55[join].ReachPx, Is.LessThan(bound),
                    $"T3 (b) DISCRIMINATION {join}: reach(55°)={at55[join].ReachPx:F2}px must be < 0.75×" +
                    $"reach(0°)={bound:F2}px (reach(0°)={at0[join].ReachPx:F2}px).");
            }

            // (c) separability survives tilt — a hard floor, not a fudge: a join type degrading into another
            // must fail this however loose the absolute tolerances in (a) are.
            double hReachPx55 = at55[JoinType.Round].RulerPx; // round's analytic reach IS 1.0·h.
            double separabilityFloor = 0.15 * hReachPx55;
            Assert.That(at55[JoinType.Miter].ReachPx - at55[JoinType.Round].ReachPx, Is.GreaterThan(separabilityFloor),
                $"T3 (c): miter must out-reach round by more than {separabilityFloor:F2}px at 55° " +
                $"(miter {at55[JoinType.Miter].ReachPx:F2}px, round {at55[JoinType.Round].ReachPx:F2}px).");
            Assert.That(at55[JoinType.Round].ReachPx - at55[JoinType.Bevel].ReachPx, Is.GreaterThan(separabilityFloor),
                $"T3 (c): round must out-reach bevel by more than {separabilityFloor:F2}px at 55° " +
                $"(round {at55[JoinType.Round].ReachPx:F2}px, bevel {at55[JoinType.Bevel].ReachPx:F2}px).");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T4 — a REAL production-path label does not foreshorten under tilt, and the fixture can see that a
        // map-aligned one would. Wired before P3 (pitch-alignment: map) exists.
        // ═══════════════════════════════════════════════════════════════════════════════════════════
        //
        // The one-glyph bootstrap is WorldPointEmitRenderTests.BuildGlyphA, widened private → internal there
        // (test-code-bloat convention: widen and reuse, never duplicate-and-drag) rather than copied here.

        private readonly struct LabelArmResult
        {
            public readonly double  InkHeightPx;
            public readonly int     QuadCount;
            public readonly double2 AnchorScreenPx;
            public readonly double  MetresPerDevicePixel;
            public readonly double  MapAlignedExpectationPx;

            public LabelArmResult(double inkHeightPx, int quadCount, double2 anchorScreenPx,
                double metresPerDevicePixel, double mapAlignedExpectationPx)
            {
                InkHeightPx              = inkHeightPx;
                QuadCount                = quadCount;
                AnchorScreenPx           = anchorScreenPx;
                MetresPerDevicePixel     = metresPerDevicePixel;
                MapAlignedExpectationPx  = mapAlignedExpectationPx;
            }
        }

        /// <summary>Renders the glyph 'A' through the REAL <c>LabelPlacementSystem.Tick</c> — exactly
        /// <c>WorldPointEmitRenderTests.cs:104–116</c>'s recipe (double Tick: the collision verdict is
        /// harvested one Tick late) — anchored at <c>frame.SceneOriginRender</c> (the look-at itself, so it
        /// projects to screen centre at BOTH tilts and the two ink heights are comparable).
        ///
        /// <para>When <paramref name="inkHeight0PxForMapExpectation"/> is supplied (the 55° call), also
        /// computes the MAP-aligned expectation for a quad of that height: <c>GroundSegmentSpanPx</c> of a
        /// world-plane segment <c>inkHeight(0°) · mpp</c> metres tall, on THIS scene's own camera.</para></summary>
        private static LabelArmResult RenderLabelArm(
            double tiltDeg, GlyphAtlasTexture atlasTexture, TextLayoutResult layout,
            double? inkHeight0PxForMapExpectation)
        {
            var config = new TiltedGroundSceneConfig
            {
                TiltDegrees     = tiltDeg,
                BackgroundColor = Color.white, // WorldSymbolInkAnalysis.InkThreshold reads dark ink on white.
                LitAmbient      = false,       // the label arm needs no lit recipe.
            };
            using var scene = TiltedGroundScene.Create(config);
            using var snap  = new SnapshotRenderer(config.SizePx, config.SizePx);

            SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
            double3 anchorRender = frame.SceneOriginRender; // the look-at, per T4's screen-centre requirement.

            long tileKey = TestTileKeys.PackedContaining(config.LookAt.Surface, zoom: 14);
            var label = new LabelInstance
            {
                AnchorRender = anchorRender, Layout = layout, Paint = LabelPaint.Default,
                TextSizePx = 220f, SortKey = 0f, FeatureIndex = 0, TileKey = tileKey,
            };

            var system = new LabelPlacementSystem(scene.MapCam,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var plan = new TestSymbolPlan(scene.MapCam.Projection);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(new[] { label }), atlasTexture);
                system.Tick(in frame, plan.Build(new[] { label }), atlasTexture);
                int quadCount = system.LastQuadCount;

                scene.Render(snap);
                byte[] px = (byte[])snap.RawPixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, config.SizePx, config.SizePx);
                WorldSymbolInkAnalysis.AnalyzeInk(px, config.SizePx, config.SizePx,
                    out int minRow, out int maxRow, out _, out _, out _, out _, out int inkCount);
                Assert.That(inkCount, Is.GreaterThan(50),
                    $"T4 @ {tiltDeg}°: must render meaningful ink (not blank/GPU-context-failed).");

                double inkHeightPx = maxRow - minRow + 1;

                // The oracle projects the CAMERA-RELATIVE point the render actually lands at (Unity world
                // origin — the look-at, under camera-relative rendering), NOT `anchorRender` itself, which is
                // the PRE-RTC absolute render-space coordinate the shader never sees directly (SceneFrame's
                // Rebase/RTC cancels it out — see WorldPointEmitRenderTests' NEW-F1 tooth for the proof that
                // this cancellation is exact regardless of which real tile the label nominally belongs to).
                double2 anchorScreenPx = GroundRuler.ProjectPx(scene.UnityCamera, double3.zero);
                double  mpp            = scene.MetresPerDevicePixel;

                double mapAlignedExpectationPx = 0.0;
                if (inkHeight0PxForMapExpectation.HasValue)
                {
                    double quadHeightWorld = inkHeight0PxForMapExpectation.Value * mpp;
                    mapAlignedExpectationPx = GroundRuler.GroundSegmentSpanPx(
                        scene.UnityCamera, double3.zero, new double2(0.0, 1.0), quadHeightWorld);
                }

                return new LabelArmResult(inkHeightPx, quadCount, anchorScreenPx, mpp, mapAlignedExpectationPx);
            }
            finally
            {
                system.Dispose();
            }
        }

        /// <summary>Half of <see cref="TiltedGroundSceneConfig.SizePx"/>'s default (512) — the frame centre
        /// in device px, for T4 (a)'s screen-centre precondition.</summary>
        private const double LabelFrameCentrePx = 256.0;

        /// <summary>
        /// <b>T4.</b> Proves: the harness renders a PRODUCTION-PATH label at tilt, and resolves screen extent
        /// finely enough to separate a map-aligned expectation from a viewport-aligned measurement by ~74% —
        /// i.e. P3 will have a real acceptance criterion.
        ///
        /// <para>Does NOT prove anything about <c>pitch-alignment: map</c>, which is UNIMPLEMENTED — clause
        /// (b) is a NEGATIVE control, expected to INVERT once P3 lands. Nothing about the spherical <c>Up</c>
        /// P2 added: this scene is Web-Mercator (<c>Up</c> is the constant (0,1,0), <c>Rebase</c> is
        /// identity), so this arm cannot distinguish a correct per-anchor frame from a hard-coded one — that
        /// is <c>SymbolUpCarrierChainTests</c>' job, over <c>SphericalProjection</c>. Nothing about icons,
        /// collision or fade.</para>
        /// </summary>
        [Test]
        public void ViewportPitchAlignedLabel_DoesNotForeshorten_AndTheFixtureCanSeeThatItWould()
        {
            (GlyphAtlasTexture atlasTexture, TextLayoutResult layout) = WorldPointEmitRenderTests.BuildGlyphA();
            try
            {
                LabelArmResult r0  = RenderLabelArm(0.0,     atlasTexture, layout, null);
                LabelArmResult r55 = RenderLabelArm(TiltDeg, atlasTexture, layout, r0.InkHeightPx);

                // (a) precondition. 0 ⇒ the label is culled under tilt → escalate, §9 fork 1.
                Assert.That(r55.QuadCount, Is.EqualTo(1),
                    "T4 (a): the label must not be culled at 55° — LastQuadCount == 0 here means §9 fork 1 " +
                    "applies (STOP and report; do not lower the tilt or hand-build the mesh unreported).");
                double centreDistPx = math.length(
                    r55.AnchorScreenPx - new double2(LabelFrameCentrePx, LabelFrameCentrePx));
                Assert.That(centreDistPx, Is.LessThan(8.0),
                    $"T4 (a): the anchor must project within 8px of frame centre at 55° (measured " +
                    $"{centreDistPx:F2}px) — AnchorRender is the look-at exactly so the two tilts are " +
                    "screen-centre comparable.");

                // (b) today's behaviour: the current path is viewport-pitch-aligned, so it must not
                // foreshorten.
                double ratio = r55.InkHeightPx / r0.InkHeightPx;
                Assert.That(ratio, Is.EqualTo(1.0).Within(2).Percent,
                    $"T4 (b): inkHeight(55°)/inkHeight(0°) must be ≈1.000 (viewport-pitch-aligned does not " +
                    $"foreshorten); got {ratio:F4} (inkHeight55={r55.InkHeightPx}px, inkHeight0={r0.InkHeightPx}px).");

                // (c) THE FIXTURE CAN DISCRIMINATE. At tilt 0 the map expectation collapses onto the
                // measurement and this clause fails — the pitch-0-inertness guard P3 inherits.
                double deviation = math.abs(r55.MapAlignedExpectationPx - r55.InkHeightPx) / r55.InkHeightPx;
                Assert.That(deviation, Is.GreaterThan(0.30),
                    $"T4 (c) DISCRIMINATION: the map-ALIGNED expectation ({r55.MapAlignedExpectationPx:F2}px) " +
                    $"must differ from the measured viewport-aligned ink height ({r55.InkHeightPx:F2}px) by " +
                    $"more than 30% at 55° (measured {deviation:P1}), or the fixture cannot tell a map-aligned " +
                    "label from a viewport-aligned one once P3 lands.");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }
    }
}
#endif // UNITY_EDITOR
